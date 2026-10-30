using System.Text.Json;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// Задача: hh сам подбирает смежные специальности (сап админ —
// но тоже IT админ и прочее); надо, чтобы и наш поиск так же работал.

// Как это работает: перед/после основного прохода ИИ-цепь называет 1-2
// смежных поисковых запроса («SAP администратор» -> «системный
// администратор»), каждый проходит лёгкий preview-проход по выдаче
// (BrowserEmulationPortalBase.SearchPreviewAsync: первая страница, без
// вкладок). Ровно то, что видно на странице вакансии hh — только
// источником «похожести» становится наш ИИ, а не JS-виджет hh (анонимный
// HTML виджет не отдаёт — проверено живым curl).

// ЭКОНОМИЯ: один короткий запрос на НОВОЕ слово поиска; ответ кэшируется в
// SettingsKV (TTL 30 дней) — повторные поиски того же слова ИИ не платят.
// Отказ ИИ не валит поиск: пусто = ищем только основной запрос.
// THREAD: последовательный прогон — состояния нет.
// ============================================================================
public sealed class RelatedQueriesService
{
    // Сколько смежных запросов брать: больше — дольше прогон (каждый —
    // первая страница выдачи с человек-темпом), меньше — скупо.
    public const int MaxRelated = 2;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly ILogService _log;
    private readonly TaskRoutingPolicy _chain;
    private readonly IApplicationStore? _store;

    public RelatedQueriesService(
        ILogService log,
        TaskRoutingPolicy chain,
        IApplicationStore? store = null)
    {
        _log = log;
        _chain = chain;
        _store = store;
    }

    // Возвращает 0..MaxRelated смежных запросов. НИКОГДА не бросает —
    // поиск должен идти даже если ИИ недоступен.
    public async Task<IReadOnlyList<string>> ExpandAsync(
        string query, CancellationToken ct = default)
    {
        var norm = query.Trim();
        // «*» — каталог целиком (смежное не имеет смысла); список через
        // запятую — юзер уже сам сказал, что хочет; короткое — шум.
        if (norm.Length == 0 || norm == "*" || norm.Contains(',')
            || Resume.GeoMatcher.Normalize(norm).Length < 3)
        {
            return Array.Empty<string>();
        }

        var cacheKey = CacheKey(norm);
        var cached = ReadCache(cacheKey);
        if (cached is { Count: > 0 })
        {
            _log.Log($"[поиск] смежные запросы (из кэша, ИИ не платили): "
                     + string.Join(" · ", cached));
            return cached;
        }

        try
        {
            var result = await _chain.RunAsync(AiTaskKind.QueryExpansion, new AiChatRequest(
                SystemPrompt: "Ты ассистент поиска вакансий. Отвечай ТОЛЬКО JSON-массивом "
                    + "коротких строк, без пояснений и без markdown-обёртки.",
                UserPrompt: $"Поисковый запрос вакансий: «{norm}». Назови {MaxRelated} близких "
                    + "по смыслу поисковых запросов — смежные специальности и синонимы "
                    + "(пример: «SAP администратор» -> «системный администратор»). "
                    + "Каждый запрос — короткая строка на русском, для той же местности. "
                    + "Ответ — JSON-массив строк.",
                // r22h: 512, а не «впритык» — урок живого лога 2026-10-30:
                // при 120 reasoning-модели (qwen3.5, gpt-oss) сжигают весь
                // бюджет на размышления, content пуст. Платим по факту
                // (списываются произведённые токены, а не лимит), а кэш 30
                // дней делает расход одноразовым на слово.
                MaxTokens: 512,
                Temperature: 0.3), ct).ConfigureAwait(false);
            if (result is null)
            {
                // цепь пуста/исчерпана — RunAsync уже назвал причины;
                // здесь фиксируем последствие для поиска (тихих исходов нет).
                _log.LogWarn("skip: смежные запросы не получены — причина: цепь ИИ недоступна; "
                             + "решение: ищу только основной запрос, повторю при следующем поиске");
                return Array.Empty<string>();
            }

            var related = ParseRelated(result.Content, norm);
            if (related.Count == 0)
            {
                _log.LogWarn("skip: смежные запросы не получены — причина: ИИ вернул не JSON-массив; "
                             + "решение: ищу только основной запрос, повторю попытку при следующем поиске");
                return related;
            }

            WriteCache(cacheKey, related);
            _log.LogSuccess($"[поиск] смежные запросы ({result.ProviderName}, "
                            + $"{result.Usage.TotalTokens} ток.): " + string.Join(" · ", related)
                            + " — каждый проверю лёгким проходом первой страницы");
            return related;
        }
        catch (OperationCanceledException)
        {
            throw; // Отмена — не сбой: наверх, поиск останавливается честно
        }
        catch (Exception ex)
        {
            // расширение — необязательная надстройка, падать нельзя.
            _log.LogError($"[поиск] смежные запросы не получены — {ex.GetType().Name}: {ex.Message}; "
                          + "решение: ищу только основной запрос");
            return Array.Empty<string>();
        }
    }

    // ---- парсинг ответа ИИ: JSON-массив строк (возможна ```json-обёртка) ----
    internal IReadOnlyList<string> ParseRelated(string content, string original)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Trim('`');
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                text = text[4..];
            }
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return Array.Empty<string>();
        }

        try
        {
            var items = JsonSerializer.Deserialize<string[]>(text[start..(end + 1)]);
            if (items is null)
            {
                return Array.Empty<string>();
            }

            var originalNorm = Resume.GeoMatcher.Normalize(original);
            return items
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().Trim('"', '«', '»', '.'))
                .Where(s => s.Length is >= 3 and <= 80)
                // Смежный запрос ≠ оригинал и не его кусок: «SAP»/«админ» при
                // запросе «SAP администратор» расширяют поиск, а не уточняют
                // смежное — такое «похожее» не нужно.
                .Where(s => !originalNorm.Contains(Resume.GeoMatcher.Normalize(s), StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRelated)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    // ---- кэш в SettingsKV: {"at":"ISO","queries":[...]} ----
    private string CacheKey(string query)
        => $"related.queries:{Resume.GeoMatcher.Normalize(query)}";

    private IReadOnlyList<string>? ReadCache(string key)
    {
        if (_store is null)
        {
            return null;
        }

        try
        {
            var raw = _store.GetSetting(key);
            if (raw is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            var at = doc.RootElement.GetProperty("at").GetDateTimeOffset();
            if (DateTimeOffset.Now - at > CacheTtl)
            {
                return null; // устарел — спрошу ИИ заново
            }

            return doc.RootElement.GetProperty("queries").EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return null; // битый кэш — как будто его нет
        }
    }

    private void WriteCache(string key, IReadOnlyList<string> related)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                queries = related,
            });
            _store.SetSetting(key, payload);
        }
        catch (Exception ex)
        {
            // Кэш — оптимизация, а не данные: промолчать нельзя, ронять нельзя.
            _log.LogWarn($"skip: кэш смежных запросов не записан — {ex.Message}; "
                         + "решение: ИИ спросят заново при следующем поиске");
        }
    }
}
