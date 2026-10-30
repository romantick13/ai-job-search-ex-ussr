using System.Globalization;
using AiJobSearchBy.Models;

namespace AiJobSearchBy.Portals;

// FLOW: polite-crawl: паузы между запросами + рандомизация
// задержек, уважение Retry-After/X-RateLimit-Remaining, инкрементальный since,
// лимит страниц за прогон, per-portal breaker после N подряд падений парсинга.
// Цикл и лимиты — честный polite-crawl; семантика квоты
// X-RateLimit-Remaining < 20 →
// «останавливаюсь до следующего раза» (стоп прогона без ошибки),
// порог 20 — константа.
// РАСШИРЕНИЯ против базовой схемы:
// джиттер ±30%, Retry-After в формате HTTP-даты, breaker после N падений.
// РЕТРАИ 403/429 — НЕ здесь:
// ретраи/backoff/кулдауны — в ScrapeOrchestrator.
// robots.txt решает карточка портала (forbidden = адаптера не существует).
// THREAD: прогон строго последовательный (architecture.md §6) — состояние без
// блокировок; конкурентный вызов одного адаптера не предполагается.
public abstract class ScrapePortalBase : PortalBase
{
    // Скелетные дефолты вежливости; адаптер уточняет их по карточке портала
    // (лимиты почти везде неизвестны — консервативная база).
    public sealed record PolitenessOptions(
        TimeSpan MinDelay,
        double JitterFraction,
        int MaxPagesPerRun,
        int BreakerThreshold,
        int QuotaStopThreshold = 20)
    {
        public static PolitenessOptions Default { get; } = new(
            MinDelay: TimeSpan.FromSeconds(2),
            JitterFraction: 0.30,
            MaxPagesPerRun: 5,
            BreakerThreshold: 3);
    }

    private readonly PolitenessOptions _options;
    private readonly Random _jitter = new();

    // STATE: breaker и вежливость — единственное мутируемое состояние адаптера.
    private int _consecutiveParseFailures;
    private bool _breakerOpen;
    private DateTimeOffset _respectUntil;
    private DateTimeOffset _lastFetchAt;

    protected ScrapePortalBase(
        PortalDescriptor descriptor,
        HttpClient http,
        Services.ILogService log,
        PolitenessOptions? options = null)
        : base(descriptor, http, log)
    {
        _options = options ?? PolitenessOptions.Default;
    }

    // Breaker открыт → портал вне цепи до ручного подъёма (kill-switch в UI).
    public bool IsBreakerOpen => _breakerOpen;

    // Мостики для юнит-тестов (InternalsVisibleTo AiJobSearchBy.Tests).
    internal DateTimeOffset RespectUntil => _respectUntil;

    internal int ConsecutiveParseFailures => _consecutiveParseFailures;

    // Инкрементальный since: «выдай изменившееся с последнего прогона».
    // Адаптер читает в Build*/Parse* и сдвигает после успешного прогона.
    protected DateTimeOffset Since { get; set; }

    protected abstract string BuildFirstPageUrl(string query);

    // null = пагинация кончилась; URL следующей страницы адаптер выцепляет из HTML.
    protected abstract string? GetNextPageUrl(string pageHtml, string pageUrl);

    // Разбор страницы → вакансии; сменившаяся вёрстка = ParseFailedException
    // (golden-фикстура разметки в тестах делает это красным тестом).
    protected abstract Task<IReadOnlyList<JobPosting>> ParsePageAsync(
        string pageHtml, string pageUrl, CancellationToken ct);

    // Скачивание страницы с вежливой паузой; адаптер может переопределить
    // (браузерные заголовки, сессии) — база отдаёт общий цикл.
    // null = семантика «останавливаюсь до следующего раза»
    // (квота близка к исчерпанию) — собранное возвращается без ошибки.
    protected virtual async Task<string?> FetchPageAsync(string url, CancellationToken ct)
    {
        await PoliteWaitAsync(ct).ConfigureAwait(false);
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);

        // Заголовки читаем ДО проверки статуса: 429 тоже несёт Retry-After.
        var quotaLow = ObserveRateLimitHeaders(response);
        response.EnsureSuccessStatusCode();
        if (quotaLow)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (_breakerOpen)
        {
            // осознанный пропуск с причиной — до исключения, чтобы отказ
            // был виден в протоколе независимо от того, кто поймает исключение.
            Log.LogWarn($"skip: портал {Descriptor.Id} — обход «{query}» не начат: "
                + $"breaker открыт после {_options.BreakerThreshold} подряд падений парсинга");
            throw new BreakerOpenException(Descriptor.Id, _options.BreakerThreshold);
        }

        Log.Log($"портал {Descriptor.Id}: обход «{query}» — до {_options.MaxPagesPerRun} стр.");
        var results = new List<JobPosting>();
        var url = BuildFirstPageUrl(query);
        for (var page = 1; page <= _options.MaxPagesPerRun && url is not null; page++)
        {
            ct.ThrowIfCancellationRequested();
            Log.Log($"портал {Descriptor.Id}: страница {page}/{_options.MaxPagesPerRun}: GET {url}");
            var html = await FetchPageAsync(url, ct).ConfigureAwait(false);
            if (html is null)
            {
                // Семантика квоты: «осталось N запросов из лимита —
                // останавливаюсь до следующего раза».
                // остановка — осознанная, причина в протоколе.
                Log.LogWarn($"skip: портал {Descriptor.Id} — квота близка к исчерпанию "
                    + $"(X-RateLimit-Remaining < {_options.QuotaStopThreshold}), "
                    + "останавливаюсь до следующего раза, собранное отдаю без ошибки)");
                break;
            }

            IReadOnlyList<JobPosting> items;
            try
            {
                items = await ParsePageAsync(html, url, ct).ConfigureAwait(false);
            }
            catch (ParseFailedException ex)
            {
                // что делали / что поймали / что решили (re-throw —
                // оркестратор решает судьбу прогона, причина уже в протоколе).
                Log.LogError($"портал {Descriptor.Id}: разбор страницы {page} упал — "
                    + $"{ex.Message}; решение: наверх (оркестратору), встречено подряд: "
                    + $"{_consecutiveParseFailures + 1}/{_options.BreakerThreshold}");
                RegisterParseFailure();
                throw;
            }

            RegisterParseSuccess();
            results.AddRange(items);
            Log.Log($"портал {Descriptor.Id}: страница {page} разобрана: +{items.Count} вакансий (всего {results.Count})");
            url = GetNextPageUrl(html, url);
        }

        Log.LogSuccess($"портал {Descriptor.Id}: обход завершён — вакансий: {results.Count}");
        return results;
    }

    private void RegisterParseSuccess() => _consecutiveParseFailures = 0;

    private void RegisterParseFailure()
    {
        _consecutiveParseFailures++;
        if (_consecutiveParseFailures >= _options.BreakerThreshold)
        {
            // Авто-выключение после N подряд падений; подъём — вручную
            // (per-portal kill-switch в UI). Событие — в протокол.
            _breakerOpen = true;
            Log.LogError($"портал {Descriptor.Id}: BREAKER ОТКРЫТ после "
                + $"{_options.BreakerThreshold} подряд падений парсинга — портал вне цепи "
                + "до ручного подъёма (kill-switch UI)");
        }
    }

    // Пауза = MinDelay с джиттером ±JitterFraction (рандомизация задержек),
    // затем добор до окна Retry-After/X-RateLimit. В тестах MinDelay = 0.
    protected async Task PoliteWaitAsync(CancellationToken ct)
    {
        var sinceLast = DateTimeOffset.UtcNow - _lastFetchAt;
        var jitter = 1.0 + (_options.JitterFraction * (2.0 * _jitter.NextDouble() - 1.0));
        var target = TimeSpan.FromMilliseconds(_options.MinDelay.TotalMilliseconds * jitter);
        var remaining = target - sinceLast;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct).ConfigureAwait(false);
        }

        var respect = _respectUntil - DateTimeOffset.UtcNow;
        if (respect > TimeSpan.Zero)
        {
            await Task.Delay(respect, ct).ConfigureAwait(false);
        }

        _lastFetchAt = DateTimeOffset.UtcNow;
    }

    // Уважение заголовков: Retry-After (секунды или HTTP-дата) задаёт
    // окно ожидания; X-RateLimit-Remaining < QuotaStopThreshold —
    // сигнал «остановить прогон». Возвращает true,
    // если прогон надо остановить. HTTP-дата Retry-After и пауза при квоте == 0 —
    // наши расширения; базовая схема знает только секунды и стоп < 20.
    protected bool ObserveRateLimitHeaders(HttpResponseMessage response)
    {
        var stopRun = false;

        if (response.Headers.TryGetValues("Retry-After", out var retryAfter))
        {
            var raw = retryAfter.FirstOrDefault();
            if (int.TryParse(raw, out var seconds) && seconds > 0)
            {
                _respectUntil = Max(_respectUntil, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds));
                Log.LogWarn($"skip: портал {Descriptor.Id} — Retry-After: {seconds} с, "
                    + "откладываю запросы до конца окна");
            }
            else if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var until))
            {
                _respectUntil = Max(_respectUntil, until);
                Log.LogWarn($"skip: портал {Descriptor.Id} — Retry-After (HTTP-дата): {until:u}, "
                    + "откладываю запросы до конца окна");
            }
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingHeader)
            && int.TryParse(remainingHeader.FirstOrDefault(), out var left))
        {
            // Близко к исчерпанию квоты → не жадничаем,
            // останавливаемся до следующего раза.
            if (left < _options.QuotaStopThreshold)
            {
                Log.LogWarn($"skip: портал {Descriptor.Id} — X-RateLimit-Remaining: {left} "
                    + $"(< {_options.QuotaStopThreshold}), сигнал стоп-прогона");
                stopRun = true;
            }

            // Наше расширение: квота == 0 дополнительно открывает окно ожидания.
            if (left <= 0)
            {
                _respectUntil = Max(_respectUntil, DateTimeOffset.UtcNow + _options.MinDelay);
                Log.LogWarn($"skip: портал {Descriptor.Id} — квота == 0, окно ожидания "
                    + $"{_options.MinDelay.TotalSeconds:0} с");
            }
        }

        return stopRun;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
