// FLOW: TaskRoutingPolicy -> CompleteAsync -> AiChatResult; расход уходит в ResponseCostCounter оркестратора
// LIFECYCLE: реализации — синглтоны DI (CompositionRoot); лимит-регистры персистятся в state/*.json
// STATE: reqLedger/tokenLedger (сутки/потрачено), _serialGate (одновременность); конфиг — readonly
// THREAD: регистры под своими _gate; _serialGate сериализует вызовы (ModelScope: 1 одновременный)
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// [НАШЕ] OpenAiCompatibleProvider — один класс на все OpenAI-совместимые
// эндпоинты продуктовой цепи (решение владельца 2026-10-21, карточка
// ai-providers-card-2026-10-21.md §6): ModelScope, SiliconFlow, ollama-local,
// lm-studio (Bionic). Паттерн Llm7Provider: локальные лимит-регистры + 429/402
// = AiProviderBusyException (фолбэк цепи) + громкий протокол каждого исхода.
//
// ПЕРВИОИСТОЧНИКИ ЛИМИТОВ (снято 2026-10-21, дословно в карточке §2–§4):
//   • ModelScope: «2,000 API Inference calls per day» на аккаунт, «200 calls
//     per model per day» максимум, «single-request concurrency for every
//     developer», сброс ежедневно UTC+8 00:00 (= 19:00 по Минску).
//     base: https://api-inference.modelscope.cn/v1/ — ключ ОБЯЗАТЕЛЕН.
//   • SiliconFlow: «The Rate Limits for free models are fixed» — чат
//     RPM 1000–10000, TPM 50 000–5 000 000; тир L0 (старт): RPM 1000 / TPM
//     40 000. Суточные TPD/RPD free-моделей в доках НЕ публикуются —
//     фиксируем фактически строками [cost]. base: https://api.siliconflow.com/v1/
//     (international; .cn совместим) — ключ ОБЯЗАТЕЛЕН.
//   • Ollama локально: «Local server requires no auth... unlimited (bounded
//     by local hardware)». base: http://localhost:11434/v1/ — без ключа,
//     безлимит; сервер не поднят = Busy с причиной (фолбэк цепи, не краш).
//   • LM Studio / Bionic: тот же контракт на http://localhost:1234/v1/.
//
// ДЕФОЛТНЫЕ МОДЕЛИ — НЕ ЖИВОЙ СНИМОК каталога (регистрация владельца
// ещё идёт). Каталоги ротируются: ProbeModelsAsync на старте сверяет модель
// с живым GET models и кричит при расхождении; точный id ставится env'ом
// AIJOB_MODELSCOPE_MODEL / AIJOB_SILICONFLOW_MODEL / AIJOB_OLLAMA_MODEL.
//
// КОНФИГ (env, паттерн llm7):
//   AIJOB_<ИМЯ>_API_KEY  — ключ (облачным обязателен, локальным игнорируется)
//   AIJOB_<ИМЯ>_MODEL    — id модели (иначе дефолт из константы профиля)
// ============================================================================
public sealed class OpenAiCompatibleProvider : IAiChatProvider
{
    // --- Профили (решение владельца 2026-10-21: ModelScope и SiliconFlow —
    //     в цепь; ollama-local и lm-studio — локальные ступени «для тех, у кого
    //     есть GPU»). Капы — из карточки §2–§4, без выдумок. ---
    public const string ModelScopeBaseUrl = "https://api-inference.modelscope.cn/v1/";
    public const string ModelScopeDefaultModel = "Qwen/Qwen3-32B";
    public const long ModelScopeDailyRequestCap = 2000; // на аккаунт, все модели суммарно

    public const string SiliconFlowBaseUrl = "https://api.siliconflow.com/v1/";
    public const string SiliconFlowDefaultModel = "Qwen/Qwen3-8B";

    public const string OllamaLocalBaseUrl = "http://localhost:11434/v1/";
    public const string LmStudioBaseUrl = "http://localhost:1234/v1/";

    public string Name { get; }
    public int QualityRank { get; }

    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly bool _requireApiKey;
    private readonly bool _isLocal;
    private readonly string _baseLabel;
    private readonly bool _ownsHttpClient;
    private readonly AiBudgetLedger? _reqLedger;   // капа в ЗАПРОСАХ/сутки (ModelScope)
    private readonly AiBudgetLedger? _tokenLedger; // капа в ТОКЕНАХ/сутки (если задана)
    private readonly SemaphoreSlim? _serialGate;   // одновременные запросы (ModelScope: 1)
    public bool IsKeyMissing { get; }

    /// <param name="dailyRequestCap">Дневная капа запросов (ModelScope: 2000); null — безлимит.</param>
    /// <param name="dailyTokenCap">Дневная капа токенов, если публична; null — не ведём.</param>
    /// <param name="maxConcurrent">Одновременных запросов (ModelScope: 1); null — без сериализации.</param>
    /// <param name="requireApiKey">Облачные — true (пустой ключ = Busy без HTTP), локальные — false.</param>
    public OpenAiCompatibleProvider(
        ILogService log,
        string name,
        string baseUrl,
        string? model = null,
        string? apiKey = null,
        string? modelEnvVar = null,
        string? apiKeyEnvVar = null,
        int qualityRank = 9,
        long? dailyRequestCap = null,
        long? dailyTokenCap = null,
        int? maxConcurrent = null,
        bool requireApiKey = true,
        string? requestLedgerPath = null,
        string? tokenLedgerPath = null,
        string? signupHint = null,
        HttpClient? httpClient = null)
    {
        _log = log;
        Name = name;
        QualityRank = qualityRank;
        _requireApiKey = requireApiKey;
        _isLocal = !requireApiKey;
        _ownsHttpClient = httpClient is null;
        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(120) };
            _baseLabel = _http.BaseAddress!.ToString();
        }
        else
        {
            _http = httpClient; // тесты/переиспользование: адрес задан снаружи
            _baseLabel = baseUrl;
        }

        _model = model ?? (modelEnvVar is null ? null : Environment.GetEnvironmentVariable(modelEnvVar))
                        ?? DefaultModelFor(name);
        var key = apiKey ?? (apiKeyEnvVar is null ? null : Environment.GetEnvironmentVariable(apiKeyEnvVar));
        IsKeyMissing = _requireApiKey && string.IsNullOrWhiteSpace(key);
        // Локальным ключ не нужен — Authorization не шлём вовсе (Ollama/LM Studio его игнорируют).
        _apiKey = IsKeyMissing || _isLocal ? "" : key!.Trim();

        if (IsKeyMissing)
        {
            // НИ-2: провайдер известен цепи, но вне её до ввода ключа — кричим причину и путь.
            _log.LogWarn($"skip: {Name} без ключа — причина: {apiKeyEnvVar ?? "ключ"} не задан; " +
                         $"решение: бесплатный ключ на {signupHint ?? "сайте провайдера"}, " +
                         $"после ввода провайдер сам встанет в цепь");
        }

        _reqLedger = dailyRequestCap is null ? null : new AiBudgetLedger(dailyRequestCap.Value, requestLedgerPath, log);
        _tokenLedger = dailyTokenCap is null ? null : new AiBudgetLedger(dailyTokenCap.Value, tokenLedgerPath, log);
        _serialGate = maxConcurrent is null ? null : new SemaphoreSlim(maxConcurrent.Value, maxConcurrent.Value);

        _log.Log($"[{Name}] провайдер готов: base={_baseLabel}, model={_model}, " +
                 $"капа: {(dailyRequestCap is null ? "запросы без лимита" : $"{dailyRequestCap:N0} запр/сутки")}" +
                 $"{(dailyTokenCap is null ? "" : $" / {dailyTokenCap:N0} ток/сутки")}, " +
                 $"ключ: {(IsKeyMissing ? "НЕТ (вне цепи)" : _isLocal ? "не требуется" : "токен")}" +
                 $"{(maxConcurrent is null ? "" : $", одновременных: {maxConcurrent}")}, " +
                 $"регистры: {(requestLedgerPath ?? tokenLedgerPath ?? "в памяти процесса")}");
    }

    public long? RemainingDailyTokens() => _tokenLedger?.Remaining;

    // FLOW: ключ/регистры на месте? -> фейл-фаст Busy (без HTTP); chat/completions под
    //        _serialGate; 429/402 = Busy (фолбэк цепи); локальный сервер не отвечает = Busy;
    //        прочие не-2xx = исключение наружу; usage -> регистры -> AiChatResult
    // STATE: единственные мутации — расход в _reqLedger/_tokenLedger (под их _gate)
    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        if (IsKeyMissing)
        {
            var msg = "ключ не задан (см. WARN при старте) — провайдер вне цепи до ввода ключа";
            _log.LogWarn($"skip: {Name} — {msg}; решение: бесплатный ключ на сайте провайдера, " +
                         "ввести в конфиг — провайдер встанет в цепь сам");
            throw new AiProviderBusyException(msg);
        }

        if (_reqLedger is { IsExhausted: true })
        {
            var msg = $"локальный регистр: дневная капа {_reqLedger.Cap:N0} запросов исчерпана " +
                      $"(сделано {_reqLedger.UsedToday:N0}); сброс лимита — по расписанию провайдера";
            _log.LogWarn($"skip: {Name} — {msg}; решение: фолбэк к следующему провайдеру цепи " +
                         "(у ModelScope: 2000 запр/сутки на аккаунт И ≤200 на модель — смените модель)");
            throw new AiProviderBusyException(msg);
        }

        if (_tokenLedger is { IsExhausted: true })
        {
            var msg = $"локальный регистр: дневная капа {_tokenLedger.Cap:N0} токенов исчерпана " +
                      $"(потрачено {_tokenLedger.UsedToday:N0})";
            _log.LogWarn($"skip: {Name} — {msg}; решение: фолбэк к следующему провайдеру цепи");
            throw new AiProviderBusyException(msg);
        }

        if (_serialGate is not null)
            await _serialGate.WaitAsync(ct); // ModelScope: «single-request concurrency» — держим контракт

        try
        {
            return await SendChatAsync(request, ct);
        }
        finally
        {
            _serialGate?.Release();
        }
    }

    private async Task<AiChatResult> SendChatAsync(AiChatRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _log.Log($"[{Name}] запрос chat/completions: model={_model}; " +
                 $"промпт: система {request.SystemPrompt.Length} симв + пользователь {request.UserPrompt.Length} симв" +
                 (_reqLedger is null ? "" : $"; запросов сегодня: {_reqLedger.UsedToday:N0}/{_reqLedger.Cap:N0}"));

        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            max_tokens = request.MaxTokens,
            temperature = request.Temperature
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        if (_apiKey.Length > 0)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            if (_isLocal)
            {
                // Локальный сервер не поднят/модель не загружена — это конфигурация, не сбой сети:
                // цепь честно фолбэкит, пользователь видит причину и решение (НИ-2).
                var msg = $"локальный сервер {_baseLabel} не отвечает: {ex.Message}";
                _log.LogWarn($"skip: {Name} — {msg}; решение: поднять сервер (Ollama/LM Studio), " +
                             "загрузить модель и включить галочку провайдера — до этого цепь идёт дальше");
                throw new AiProviderBusyException(msg);
            }
            // НИ-1: что делали / что поймали / что решили.
            _log.LogError($"[{Name}] сеть недоступна: {ex.Message}; решение: исключение наружу — цепь решает");
            throw;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogError($"[{Name}] таймаут 120с; решение: исключение наружу — цепь решает");
            throw new TimeoutException($"{Name}: таймаут запроса chat/completions");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var shortBody = body.Length > 200 ? body[..200] + "…" : body;

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
            {
                var ra = resp.Headers.RetryAfter?.Delta is { } d
                    ? $" (Retry-After: {(int)d.TotalSeconds}с)" : "";
                var perModel = Name == "modelscope"
                    ? "; у ModelScope капа ≤200 запр/сутки НА МОДЕЛЬ — смените модель (AIJOB_MODELSCOPE_MODEL)"
                    : "";
                var msg = $"HTTP {(int)resp.StatusCode}{ra} — лимит/бюджет исчерпан на стороне {Name}{perModel}";
                // 429/402 — не сбой, а сигнал фолбэка: осознанный skip с причиной (НИ-2).
                _log.LogWarn($"skip: {Name} отклонил запрос — {msg}; тело: {shortBody}; " +
                             "решение: фолбэк к следующему провайдеру цепи");
                throw new AiProviderBusyException(msg);
            }
            _log.LogError($"[{Name}] сбой: HTTP {(int)resp.StatusCode}: {shortBody}; решение: исключение наружу — цепь решает");
            throw new HttpRequestException($"{Name}: HTTP {(int)resp.StatusCode}: {shortBody}");
        }

        string content;
        long pTok, cTok;
        var estimated = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            if (root.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("prompt_tokens", out var pt) &&
                usage.TryGetProperty("completion_tokens", out var ct2))
            {
                pTok = pt.GetInt64();
                cTok = ct2.GetInt64();
            }
            else
            {
                // Сервер не отдал usage — оцениваем по символам (≈4 симв/токен), кричим об оценке.
                estimated = true;
                pTok = (request.SystemPrompt.Length + request.UserPrompt.Length) / 4;
                cTok = content.Length / 4;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogError($"[{Name}] неожиданный формат ответа: {ex.Message}; тело: {shortBody}; " +
                          "решение: исключение наружу — расход этого запроса регистрами не учтён");
            throw;
        }

        _reqLedger?.Add(1);      // учитываем факт запроса (ModelScope: 2000/сутки)
        _tokenLedger?.Add(pTok + cTok);
        sw.Stop();
        _log.LogSuccess($"[{Name}] OK: {pTok + cTok} ток (вх {pTok} / вых {cTok}{(estimated ? ", оценка" : "")}) " +
                        $"за {sw.Elapsed.TotalSeconds:F1}с" +
                        (_reqLedger is null ? "" : $"; запросов сегодня: {_reqLedger.UsedToday:N0}/{_reqLedger.Cap:N0}"));
        return new AiChatResult(content, _model, new AiChatUsage(pTok, cTok), Name, sw.Elapsed);
    }

    // FLOW: GET models -> каталог в протокол -> модель в каталоге?; падение пробы = WARN, не краш (НИ-3)
    public async Task<bool> ProbeModelsAsync(CancellationToken ct = default)
    {
        try
        {
            _log.Log($"[{Name}] проба каталога: GET models ({_baseLabel})");
            using var resp = await _http.GetAsync("models", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarn($"skip: проба каталога {Name} — HTTP {(int)resp.StatusCode}; " +
                             "решение: работаем с моделью по конфигу, сверить вручную на сайте провайдера");
                return false;
            }
            using var doc = JsonDocument.Parse(body);
            var ids = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                ids.Add(m.GetProperty("id").GetString() ?? "");
            var head = string.Join(", ", ids.Take(10));
            _log.LogSuccess($"[{Name}] каталог живой: {ids.Count} моделей; первые: {head}" +
                            (ids.Count > 10 ? $" (+{ids.Count - 10})" : ""));
            if (!ids.Contains(_model))
            {
                _log.LogError($"[{Name}] модели {_model} нет в каталоге — обновите конфиг " +
                              "(env *_MODEL); иначе первый же chat/completions вернёт ошибку");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: проба каталога {Name} не удалась — причина: {ex.Message}; " +
                         "решение: работаем с моделью по конфигу, лимиты ведут локальные регистры");
            return false;
        }
    }

    private static string DefaultModelFor(string name) => name switch
    {
        "modelscope" => ModelScopeDefaultModel,
        "siliconflow" => SiliconFlowDefaultModel,
        "ollama-local" => "qwen3:4b",   // под 4GB VRAM; точная — та, что скачал пользователь (README)
        "lm-studio" => "local-model",   // LM Studio подставляет загруженную модель
        _ => "local-model"
    };
}
