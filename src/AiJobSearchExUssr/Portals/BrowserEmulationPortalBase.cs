using System.Net;
using System.Text.RegularExpressions;
using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Portals;

// =====================================================================
// FLOW: база браузерной эмуляции — имитация человека, листающего
// вакансии. Контракт обхода:
//   запрос -> выдача -> счётчик «Найдено N вакансий» + пагинация сайта ->
//   кэширование всех страниц -> разбор карточек из кэша с проверкой на
//   дубликаты. Парсим ТОЛЬКО страницы выдачи — вкладки-детали не
//   открываются (данных карточки из листинга достаточно; подбор похожих —
//   дело математики сайта).
// ТЕМП ОБХОДА:
// • страница 1 — обычный заход; каждая следующая — НОВАЯ ВКЛАДКА с
//   первой страницы (Referer = первая страница, цепочки нет) и рваной
//   паузой 5-25 с (Pacing.Human.TabOpen*, в тестах Silent = 0).
//   Мгновенная скачка по всем страницам подряд приводит к щиту сайта:
//   вместо списка приходит заглушка без карточек, и разбор падает
//   (воспроизведено на rabota.by: страницы 5-8 прогона оказались
//   заглушками, из-за одной страницы пропали четыре);
// • итог фазы открытия — отдельная строка протокола («все страницы
//   выдачи открыты вкладками и в кэше» либо честное «в кэше X из N»);
// • одна плохая страница кэша не убивает обход: пропуск с warn, парсим
//   остальные;
// • «Отмена» во время медленной фазы открытия — кэш ДОБИРАЕТСЯ и
//   парсится, наработанное не пропадает;
// • паузы чтения/перелистывания нет; ретрай на 403/429/503 и сбоях
//   сети остался (реакция на щит, не человеко-темп);
// • заголовки настоящего браузера (имитация браузера);
// • карточки дедупятся в прогоне: канонический URL + «название+
//   работодатель» (перепосты), финальный дедуп — в базе.
// ПЛАН СТРАНИЦ: счётчик «Найдено N вакансий» + пагинация сайта
// (у hh — нумерованный data-qa="pager-page", page=0-базный, старого
// pager-next больше нет — снято с живой выдачи 2026-10-28); парсим
// РОВНО столько страниц, сколько вернул поиск (кеп MaxPages — только
// страховка от бездонного «*»).
// robots.txt: правила для роботов-краулеров; адаптер работает руками
// пользователя (личный просмотр своих слов поиска, никакого каталога на
// сторону) — карточка портала в config/registry.json.
// THREAD: прогон строго последовательный — состояние без блокировок.
// =====================================================================
public abstract class BrowserEmulationPortalBase : ScrapePortalBase
{
    // TabOpen* — рабочие поля: рваная пауза 5-25 с перед открытием каждой
    // новой вкладки выдачи. Остальные поля рекорда — совместимость
    // сигнатур (паузы чтения сняты; ретраи щита живут в FetchCoreAsync).
    public sealed record Pacing(
        double ScreenMinSeconds,
        double ScreenSecondsPer100K,
        double ScreenMaxSeconds,
        double JitterFraction,
        double TabOpenMinSeconds,
        double TabOpenMaxSeconds,
        double NextPageMinSeconds,
        double NextPageMaxSeconds,
        int MaxVacanciesPerRun)
    {
        public static Pacing Human { get; } = new(5.0, 1.5, 25.0, 0.25, 5.0, 25.0, 5.0, 12.0, 120);

        public static Pacing Silent { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 120);
    }

    private readonly Random _rnd = new();
    private readonly Pacing _pacing;
    private string? _lastPageUrl;

    // перепосты «название+работодатель», отсечённые в ТЕКУЩЕМ
    // прогоне обхода, — для счётчика у окна «Вакансии» (найдено X,
    // дублей исключено Y). Обнуляется на старте каждого SearchAsync.
    public int LastRunDuplicatesExcluded { get; private set; }

    // Щит сайта держится в ТЕКУЩЕМ прогоне (заглушка пережила свежий
    // заход — ретраи прекращены). Оркестратор (MainViewModel) читает после
    // SearchAsync и поднимает модальное окно «повтори поиск через
    // 20-30 минут» — решение владельца r22c: предупреждение прямо на
    // морду, не только строкой в протоколе. Обнуляется на старте SearchAsync.
    public bool LastRunShieldHeld { get; private set; }

    protected BrowserEmulationPortalBase(
        PortalDescriptor descriptor,
        HttpClient http,
        Services.ILogService log,
        Pacing? pacing = null,
        PolitenessOptions? politeness = null)
        : base(descriptor, http, log, politeness)
    {
        // null (DI) = человеческий темп вкладок; тесты — Silent.
        _pacing = pacing ?? Pacing.Human;
    }

    // Страховка от бездонной выдачи («*» = вся база группы): максимум
    // страниц за прогон. Обычный запрос («системный администратор» ~9 стр.)
    // укладывается с запасом — план страниц честнее кепа.
    protected virtual int MaxPages => 25;


    // План страниц выдачи («смотришь, сколько страниц выдало
    // в поиске»). Хуки адаптера; дефолты = одна страница (пагинации нет).


    // Сколько ВСЕГО вакансий нашёл сайт (счётчик «Найдено N вакансий»);
    // null — счётчика на странице нет/не распознался.
    protected virtual int? ExtractTotalFound(string firstPageHtml) => null;

    // Старший номер страницы в пагинации сайта (0-базный); null — нет.
    protected virtual int? MaxPagerPage(string firstPageHtml) => null;

    // URL страницы pageIndex (1..N-1) из URL первой страницы;
    // null — пагинации нет (план обрывается с честной строкой протокола).
    protected virtual string? BuildPageUrl(string firstPageUrl, int pageIndex) => null;

    // Скелетный обход ScrapePortalBase здесь НЕ используется (SearchAsync
    // переопределён целиком); пагинация — через план страниц выше.
    // Абстрактный член базы гасим null-ом: адаптеру браузерной эмуляции
    // «следующая страница» не нужна.
    protected override string? GetNextPageUrl(string pageHtml, string pageUrl) => null;


    // Обход выдачи : страница 1 -> план страниц -> кэш ВСЕХ
    // страниц -> парсинг карточек из кэша -> дедуп -> собранное.
    // Вкладок-деталей нет; отмена/кеп возвращают собранное.

    public override async Task<IReadOnlyList<JobPosting>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        var collected = new List<JobPosting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var titleKeys = new HashSet<string>(StringComparer.Ordinal);
        List<(string Url, string Html)> cache = [];
        IReadOnlyList<JobPosting> firstCards = [];
        var firstUrl = "";
        // Сколько страниц кэша уже разобрано (для добора после «Отмены»).
        var parsedPages = 0;
        // План страниц (назначается в try; 1 = ещё не дошёл план) — добор
        // после «Отмены» имеет смысл только для ДОЛГОЙ фазы открытия .
        var totalPages = 1;
        // Щит-заглушка подтверждена повторным заходом — свежие заходы в этом
        // прогоне прекращаем (не дёргаем сайт, страницы просто пропускаем).
        var stubRetryBlocked = false;

        // Один свежий заход вместо заглушки из кэша: щит часто отпускает
        // через мгновение (владелец r22a: страницы 5-8 — заглушки, потеряно
        // полвыдачи при рваных паузах 5-25 с — темп не помог). null — щит
        // держится (или страница снова пуста): ретраи в прогоне прекращаем.
        async Task<IReadOnlyList<JobPosting>?> RetryStubPageAsync(
            int pageIndex, string pageUrl, CancellationToken token)
        {
            if (stubRetryBlocked)
            {
                return null;
            }

            var paused = await TabOpenPauseAsync(token).ConfigureAwait(false);
            Log.Log($"портал {Descriptor.Id}: страница {pageIndex + 1} — пауза "
                + $"{paused:0.#} с и свежий заход (как человек: обновил вкладку)");
            var html = await FetchPageInNewTabAsync(pageUrl, firstUrl, token)
                .ConfigureAwait(false);
            if (html is null)
            {
                stubRetryBlocked = true;
                LastRunShieldHeld = true;
                return null;
            }

            try
            {
                return await ParsePageAsync(html, pageUrl, token).ConfigureAwait(false);
            }
            catch (ParseFailedException)
            {
                stubRetryBlocked = true;
                LastRunShieldHeld = true;
                Log.LogWarn($"портал {Descriptor.Id}: страница {pageIndex + 1} снова без "
                    + "карточек — щит держится, свежие заходы в этом прогоне прекращаю; "
                    + "решение: повтори поиск через 20-30 минут — сайт отпускает, "
                    + "дедуп в базе ничего не задвоит");
                return null;
            }
        }

        // Разбор кэша со страницы from (0-базной) — общий для нормального
        // хода и для добора после «Отмены» (наработанное не пропадает).
        async Task ParseCachedFromAsync(int from, CancellationToken token)
        {
            for (var i = from; i < cache.Count; i++)
            {
                var (pageUrl, html) = cache[i];
                IReadOnlyList<JobPosting> cards;
                if (i == 0)
                {
                    cards = firstCards;
                }
                else
                {
                    try
                    {
                        cards = await ParsePageAsync(html, pageUrl, token).ConfigureAwait(false);
                    }
                    catch (ParseFailedException ex)
                    {
                        // одна плохая страница кэша (щит-заглушка/смена
                        // вёрстки) больше не убивает портал: сначала ОДИН
                        // свежий заход — щит часто отпускает через мгновение
                        // (владелец r22a: страницы 5-8 — заглушки, потеряно
                        // полвыдачи); не помогло — пропуск, остальные парсим.
                        Log.LogWarn($"портал {Descriptor.Id}: страница {i + 1} из кэша не "
                            + $"распарсилась — {ex.Message}; решение: один свежий "
                            + "заход; не помогло — пропускаю, парсю остальные страницы кэша");
                        var retried = await RetryStubPageAsync(i, pageUrl, token)
                            .ConfigureAwait(false);
                        if (retried is null)
                        {
                            continue;
                        }

                        cards = retried;
                    }
                }

                var fresh = 0;
                foreach (var card in cards)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(card.CanonicalUrl)) continue;

                    // перепост одной вакансии (hh лежит под разными
                    // id) — одна строка, каждая отсечка в протоколе. Тот же
                    // URL повторно не доходит сюда (отсечён выше), так что
                    // добор после «Отмены» дублей не плодит и не пересчитывает.
                    var titleKey = DedupeHasher.TitleKey(card.Title, card.Company);
                    if (titleKey.Length > 0 && !titleKeys.Add(titleKey))
                    {
                        LastRunDuplicatesExcluded++;
                        Log.Log($"портал {Descriptor.Id}: «{card.Title}» "
                            + $"({card.Company ?? "работодатель не указан"}) — дубль названия "
                            + $"и работодателя ({card.CanonicalUrl}), пропущен");
                        continue;
                    }

                    collected.Add(card);
                    fresh++;
                }

                Log.Log($"портал {Descriptor.Id}: страница {i + 1} — карточек {cards.Count}, "
                    + $"новых {fresh}, всего собрано {collected.Count}");
                parsedPages = i + 1;
            }
        }

        try
        {
            firstUrl = BuildFirstPageUrl(query);
            LastRunDuplicatesExcluded = 0;
            LastRunShieldHeld = false;
            Log.Log($"портал {Descriptor.Id}: обход выдачи «{query}» — до {MaxPages} стр. "
                + "(только страницы выдачи; каждая следующая — новой вкладкой "
                + "с рваной паузой 5-25 с)");

            var firstHtml = await FetchPageAsync(firstUrl, ct).ConfigureAwait(false);
            if (firstHtml is null)
            {
                Log.LogWarn($"skip: портал {Descriptor.Id} — первая страница не отдалась "
                    + "(щит/лимит), закрываю обход, собранное отдаю");
                return collected;
            }

            // Сменившаяся вёрстка = ParseFailedException — наверх, оркестратору.
            firstCards = await ParsePageAsync(firstHtml, firstUrl, ct).ConfigureAwait(false);

            // План страниц: счётчик сайта + пагинация, кеп — страховка.
            var perPage = Math.Max(firstCards.Count, 1);
            var totalFound = ExtractTotalFound(firstHtml);
            var pagesByCounter = totalFound is null
                ? 1
                : (int)Math.Ceiling(totalFound.Value / (double)perPage);
            var pagesByPager = (MaxPagerPage(firstHtml) ?? -1) + 1;
            var planned = Math.Max(pagesByCounter, pagesByPager);
            totalPages = Math.Clamp(planned, 1, MaxPages);
            Log.Log($"портал {Descriptor.Id}: сайт нашёл {totalFound?.ToString() ?? "?"} вакансий, "
                + $"карточек на странице {perPage} — страниц выдачи: {totalPages}"
                + (planned > MaxPages
                    ? $" (план {planned} упёрся в кеп {MaxPages} — остальное на следующих прогонах, дедуп в базе)"
                    : ""));
            if (planned <= 1)
            {
                // Судьба пагинации — ЯВНО в протоколе. Ни счётчика,
                // ни пагинатора — поиск вернул ровно одну страницу,
                // выдача кончилась.
                Log.Log($"портал {Descriptor.Id}: пагинации и счётчика на странице нет — "
                    + "выдача кончилась (поиск вернул одну страницу)");
            }

            // Фаза A — КЭШ выдачи: страница 1 уже в кэше; каждую
            // следующую открываем НОВОЙ ВКЛАДКОЙ с первой страницы и рваной
            // паузой 5-25 с — мгновенная скачка приводит к щиту сайта.
            cache.Add((firstUrl, firstHtml));
            for (var i = 1; i < totalPages; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pageUrl = BuildPageUrl(firstUrl, i);
                if (pageUrl is null)
                {
                    Log.LogWarn($"портал {Descriptor.Id}: пагинации нет (страница {i + 1} "
                        + "не строится) — кэширую только скачанное");
                    break;
                }

                var paused = await TabOpenPauseAsync(ct).ConfigureAwait(false);
                Log.Log($"портал {Descriptor.Id}: вкладка {i + 1}/{totalPages} — пауза "
                    + $"{paused:0.#} с, открываю {pageUrl}");
                var html = await FetchPageInNewTabAsync(pageUrl, firstUrl, ct)
                    .ConfigureAwait(false);
                if (html is null)
                {
                    Log.LogWarn($"skip: портал {Descriptor.Id} — страница {i + 1} не отдалась, "
                        + "кэширую только скачанное");
                    break;
                }

                cache.Add((pageUrl, html));
            }

            // ИТОГОВАЯ строка фазы открытия — судьба ВСЕХ страниц плана.
            if (totalPages > 1)
            {
                if (cache.Count == totalPages)
                {
                    Log.LogSuccess($"портал {Descriptor.Id}: все страницы выдачи открыты "
                        + $"вкладками и в кэше — {totalPages} шт., беру по очереди и парсю");
                }
                else
                {
                    Log.LogWarn($"портал {Descriptor.Id}: в кэше {cache.Count} из "
                        + $"{totalPages} страниц — беру по очереди и парсю что есть "
                        + "(остаток доберёт следующий прогон, дедуп в базе)");
                }
            }

            Log.Log($"портал {Descriptor.Id}: кэш выдачи — {cache.Count} стр., "
                + "парсю карточки из кэша (дедуп: URL + название+работодатель)");

            // Фаза B — парсинг из кэша: карточка из листинга = финальная
            // (опыт/выплаты/зарплата сняты с той же страницы, ).
            // С НУЛЯ: страница 0 кладёт в собранное свои firstCards здесь.
            await ParseCachedFromAsync(0, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Пользователь нажал «Отмена» (фаза открытия теперь долгая — до
            // пары минут): наработанное не пропадает — добираем остаток
            // кэша БЕЗ отмены и отдаём собранное . Добор — только
            // для мног страничного плана: одностраничная отмена сохраняет
            // прежнюю семантику (отдаём собранное без перепарсинга).
            if (totalPages > 1 && cache.Count > parsedPages)
            {
                Log.LogWarn($"портал {Descriptor.Id}: обход остановлен пользователем — "
                    + $"добираю страницы {parsedPages + 1}-{cache.Count} из кэша");
                try
                {
                    await ParseCachedFromAsync(parsedPages, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.LogError($"портал {Descriptor.Id}: добор кэша после отмены упал — "
                        + $"{ex.GetType().Name}: {ex.Message}; решение: отдаю собранное");
                }
            }

            Log.LogWarn($"портал {Descriptor.Id}: обход остановлен пользователем — "
                + $"отдаю собранное: {collected.Count} вакансий (наработанное не пропадает)");
            return collected;
        }

        Log.LogSuccess($"портал {Descriptor.Id}: обход выдачи завершён — вакансий: {collected.Count}");
        return collected;
    }


    // лёгкий проход смежного запроса (RelatedQueriesService):
    // первая страница выдачи, карточки как есть (и так без вкладок).

    public async Task<IReadOnlyList<JobPosting>> SearchPreviewAsync(
        string query, CancellationToken ct = default)
    {
        var url = BuildFirstPageUrl(query);
        var html = await FetchPageAsync(url, ct).ConfigureAwait(false);
        if (html is null)
        {
            Log.LogWarn($"портал {Descriptor.Id}: смежный запрос «{query}» — выдача не отдалась; "
                        + "решение: пропускаю смежный проход, основной результат не тронут");
            return Array.Empty<JobPosting>();
        }

        return await ParsePageAsync(html, url, ct).ConfigureAwait(false);
    }


    // Браузерный GET: настоящие заголовки браузера, Referer предыдущей
    // страницы. паузы-человек сняты; осталась ретрай-пауза
    // на 403/429/503 и сетевые сбои (щит отпускает — рецепт)
    // и — — рваная пауза перед новой вкладкой (TabOpenPauseAsync).

    protected override Task<string?> FetchPageAsync(string url, CancellationToken ct)
        => FetchCoreAsync(url, ct, refererOverride: null, rememberAsLast: true);

    // «открывать каждую в новой вкладке» — вкладка открывается ИЗ
    // первой страницы выдачи (Referer = первая страница), цепочки
    // «страница -> страница» нет; _lastPageUrl не двигается — следующая
    // вкладка открывается так же, с первой страницы.
    protected Task<string?> FetchPageInNewTabAsync(string url, string referer, CancellationToken ct)
        => FetchCoreAsync(url, ct, refererOverride: referer, rememberAsLast: false);

    // Рваная пауза 5-25 с перед новой вкладкой ; в тестах
    // Pacing.Silent -> 0 с (мгновенно).
    private async Task<double> TabOpenPauseAsync(CancellationToken ct)
    {
        var seconds = _pacing.TabOpenMinSeconds
            + _rnd.NextDouble() * Math.Max(0, _pacing.TabOpenMaxSeconds - _pacing.TabOpenMinSeconds);
        if (seconds > 0.01)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }

        return seconds;
    }

    private async Task<string?> FetchCoreAsync(
        string url,
        CancellationToken ct,
        string? refererOverride,
        bool rememberAsLast)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                request.Headers.TryAddWithoutValidation("Accept-Language",
                    "ru-RU,ru;q=0.9,be;q=0.8,en;q=0.7");
                var referer = refererOverride ?? _lastPageUrl;
                if (referer is not null && referer != url)
                {
                    request.Headers.TryAddWithoutValidation("Referer", referer);
                }

                response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Отмена — не сбой
            }
            catch (Exception ex) when (attempt == 1
                && ex is HttpRequestException or TaskCanceledException)
            {
                Log.LogWarn($"портал {Descriptor.Id}: сеть притормозила ({ex.GetType().Name}) — "
                    + "пауза и повтор, как человек: подождал и обновил страницу");
                await RaggedAsync(5, 10, ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (attempt == 1 && !response.IsSuccessStatusCode && response.StatusCode is
                    HttpStatusCode.Forbidden
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable)
                {
                    Log.LogWarn($"портал {Descriptor.Id}: HTTP {(int)response.StatusCode} "
                        + "(щит/лимит) — пауза и повтор, как человек: отвлёкся и открыл заново");
                    await RaggedAsync(9, 16, ct).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (rememberAsLast)
                {
                    _lastPageUrl = url;
                }

                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
    }

    // Ретрай-пауза из диапазона (щит/сеть). Единственные оставшиеся паузы:
    // нормальный ход обхода их не вызывает.
    private async Task RaggedAsync(double minSeconds, double maxSeconds, CancellationToken ct)
    {
        var seconds = minSeconds + _rnd.NextDouble() * Math.Max(0, maxSeconds - minSeconds);
        if (seconds <= 0.01) return;
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
    }

    // Текст из HTML-фрагмента: срез тегов + декод сущностей + сжатие пробелов.
    protected static string Tagless(string fragment)
    {
        var text = Regex.Replace(Regex.Replace(fragment, "<[^>]+>", " "), @"\s+", " ");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}
