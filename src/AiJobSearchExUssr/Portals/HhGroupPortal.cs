using System.Text.RegularExpressions;
using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Portals;

// =====================================================================
// FLOW: адаптер сайтов группы hh (hh.ru, rabota.by, hh.kz, hh.uz) —
// браузерная эмуляция, обход ТОЛЬКО страниц выдачи (без вкладок-деталей:
// все поля карточки снимаются с листинга). Группа использует общий движок
// и общую разметку выдачи, поэтому один адаптер параметризуется зеркалом
// (хост + код региона страны из справочника api.hh.ru/areas/countries).
// Живые пробы разметки (curl, 2026-10-28/29, rabota.by и hh.ru, поиск
// «системный администратор»: 159-162 вакансии, 20 карточек на страницу):
// карточка: <div id="{id}" class="vacancy-card--…">, внутри
// <a … data-qa="serp-item__title" href=…> (порядок атрибутов ГУЛЯЕТ —
// href достаётся из атрибутов найденного тега);
// data-qa="vacancy-serp__vacancy-employer-text" (название компании);
// зарплата БЕЗ data-qa: <span class="magritte-text___… typography-
// label-1-regular___…"><data value="3594">3 594</data> – <data
// value="3990">3 990 </data><data value="BYN">Br</data> за месяц,
// до вычета налогов</span> — якорь: span с классом typography-
// label-1-regular, в теле которого есть <data value= (у опыта
// <data> живёт в div.magritte-tag__label — не спутаются);
// опыт: data-qa="vacancy-serp__vacancy-work-experience-{диапазон}"
// → «Опыт 3-6 лет» (текст уже с префиксом);
// выплаты: data-qa="vacancy-serp__vacancy-compensation-frequency-
// {режим}" → «Выплаты: Два раза в месяц»;
// data-qa="vacancy-serp__vacancy-address"; «Можно удалённо» —
// vacancy-label-work-schedule-remote.
// счётчик: h1 «Найдено 162 вакансии» (без data-qa — берём текстом;
// Найдено/Найдена/Найден — грамматика числа);
// пагинация: нумерованные data-qa="pager-page" href="…page=N…" —
// 0-базная; старого data-qa="pager-next" в разметке больше нет.
// ТЕМП ОБХОДА: каждая следующая страница выдачи открывается новой
// вкладкой с рваной паузой 5-25 с (BrowserEmulationPortalBase).
// Мгновенная скачка по всем страницам подряд приводит к щиту сайта:
// вместо списка приходит заглушка без карточек (воспроизведено
// на rabota.by: страницы 5-8 прогона оказались заглушками). Параметр
// items_on_page в URL увеличивает число страниц в расчёте пагинатора,
// но НЕ число карточек в HTML (остальные дорисовывает JavaScript в
// браузере) — план страниц считается по счётчику «Найдено N» и
// фактическим 20 карточкам на страницу.
// Смена вёрстки = ParseFailedException (брейкер оркестратора).
// Регистрация не нужна; отклик — руками на сайте (ApplyWarning).
// ГЕОГРАФИЯ: каждое зеркало ищет по своей стране (area); поисковая сессия
// зеркала может приносить и чужестранные вакансии группы (ссылка ведёт на
// домен-источник) — их сортирует гео-фильтр приложения. Канонический
// URL вакансии — без мусорных параметров выдачи (?query=…).
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны.
// =====================================================================
public sealed class HhGroupPortal : BrowserEmulationPortalBase
{
    private readonly string _host;
    private readonly string _areaId;

    public HhGroupPortal(
        PortalDescriptor card,
        string host,
        string areaId,
        HttpClient http,
        ILogService log,
        Pacing? pacing = null)
        : base(card, http, log, pacing)
    {
        _host = host.TrimEnd('/');
        _areaId = areaId;
    }

    // Удобная сборка из записи зеркала (каталог BuiltInPortals, тесты, DI).
    public HhGroupPortal(BuiltInPortals.HhMirror mirror, HttpClient http, ILogService log,
        Pacing? pacing = null)
        : this(mirror.Card, mirror.Host, mirror.AreaId, http, log, pacing)
    {
    }

    // Пустой запрос «*» = вся выдача страны без text-фильтра;
    // otherwise — текстовый поиск внутри своего региона (area).
    protected override string BuildFirstPageUrl(string query)
        => query == "*"
            ? $"{_host}/search/vacancy?area={_areaId}"
            : $"{_host}/search/vacancy?text={Uri.EscapeDataString(query)}&area={_areaId}";


    // План страниц выдачи: «смотришь, сколько страниц выдало в поиске».
    // Счётчик «Найдено 162 вакансии»; пагинация — старший
    // data-qa="pager-page"; URL страницы — тот же первый URL с подменённым
    // page=N (page 0-базный, первая страница уже скачана).
    // search_session_id не нужен — сайт отвечает и без него (канонический
    // URL поиска).

    protected override int? ExtractTotalFound(string firstPageHtml)
    {
        var m = TotalFound.Match(firstPageHtml);
        if (!m.Success)
        {
            return null;
        }

        var digits = new string(m.Groups["num"].Value
            .Where(char.IsDigit)
            .ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var n) && n > 0
            ? n
            : null;
    }

    protected override int? MaxPagerPage(string firstPageHtml)
    {
        var max = -1;
        foreach (Match anchor in PagerPageAnchor.Matches(firstPageHtml))
        {
            // href из атрибутов найденного тега — порядок атрибутов гуляет.
            var href = TagHref.Match(anchor.Groups["attrs"].Value)
                .Groups["href"].Value.Replace("&amp;", "&");
            var page = PageParam.Match(href);
            if (page.Success && int.TryParse(page.Groups["n"].Value, out var n) && n > max)
            {
                max = n;
            }
        }

        return max < 0 ? null : max;
    }

    protected override string? BuildPageUrl(string firstPageUrl, int pageIndex)
    {
        // page=0 — уже скачанная первая страница; на страницы 1..N-1 — подмена.
        if (PageParam.IsMatch(firstPageUrl))
        {
            // Разделитель (?/&) сохраняем: матч начинается с него.
            return PageParam.Replace(
                firstPageUrl, m => $"{m.Value[0]}page={pageIndex}", 1);
        }

        return firstPageUrl + (firstPageUrl.Contains('?') ? "&" : "?") + $"page={pageIndex}";
    }

    // Разбор страницы выдачи: карточки без деталей — все поля снимаются
    // с карточки листинга. Ни одной карточки = смена вёрстки =
    // ParseFailedException.
    protected override Task<IReadOnlyList<JobPosting>> ParsePageAsync(
        string pageHtml, string pageUrl, CancellationToken ct)
    {
        var result = new List<JobPosting>();
        var matches = CardStart.Matches(pageHtml);
        for (var i = 0; i < matches.Count; i++)
        {
            var block = pageHtml[matches[i].Index
                ..(i + 1 < matches.Count ? matches[i + 1].Index : pageHtml.Length)];

            var posting = CardFromBlock(block, matches[i].Groups["id"].Value);
            if (posting is null)
            {
                // Ссылка вакансии — NOT NULL канона данных: без неё карточка
                // не читается — пропуск с записью в протокол.
                Log.LogWarn($"портал {Descriptor.Id}: карточка без ссылки — пропуск "
                    + "(тихих игноров нет, решение: warn и дальше)");
                continue;
            }

            result.Add(posting);
        }

        if (result.Count == 0)
        {
            // Ноль карточек на странице выдачи почти всегда означает не смену
            // вёрстки, а щит: сайт отдал заглушку (воспроизведено на rabota.by
            // 2026-10-28/29 и у владельца r22a: страницы 5-8 — заглушки).
            throw new ParseFailedException(
                "карточек нет (div.vacancy-card-- не найдено) — щит-заглушка "
                + "или смена вёрстки");
        }

        return Task.FromResult<IReadOnlyList<JobPosting>>(result);
    }

    // Сборщик карточки листинга: название/работодатель/зарплата/адрес +
    // опыт и выплаты (сняты с той же карточки). null — ссылки нет (битая
    // карточка).
    private JobPosting? CardFromBlock(string block, string? externalId)
    {
        var linkMatch = TitleLink.Match(block);
        if (!linkMatch.Success)
        {
            return null;
        }

        // href из атрибутов найденного тега — порядок атрибутов гуляет.
        var href = TagHref.Match(linkMatch.Groups["attrs"].Value)
            .Groups["href"].Value.Replace("&amp;", "&");
        if (href.Length == 0)
        {
            return null;
        }

        // Канонический URL: без ?query=… (мусорные параметры выдачи).
        var canonical = href.Split('?')[0];
        if (!canonical.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            canonical = $"{_host}{canonical}";
        }

        var title = Tagless(linkMatch.Groups["title"].Value);
        var company =
            (EmployerText.Match(block) is { Success: true } t ? Tagless(t.Groups[1].Value) : null)
            ?? (Employer.Match(block) is { Success: true } e ? Tagless(e.Groups[1].Value) : null);
        var salary = SalarySpan.Match(block) is { Success: true } s
                     && s.Groups["body"].Value.Contains("<data value=", StringComparison.Ordinal)
            ? Tagless(s.Groups["body"].Value)
            : (Compensation.Match(block) is { Success: true } c ? Tagless(c.Groups[1].Value) : null);
        var address = Address.Match(block) is { Success: true } a ? Tagless(a.Groups[1].Value) : null;
        var remote = block.Contains("vacancy-label-work-schedule-remote", StringComparison.Ordinal);
        externalId ??= Regex.Match(canonical, @"(\d+)").Value;

        // Опыт/выплаты: текст уже с префиксом («Опыт 3-6 лет», «Выплаты:
        // Два раза в месяц») — кладём как есть, одна строка на поле.
        var head = new List<string>();
        if (remote)
        {
            head.Add("Режим: можно удалённо");
        }

        if (Experience.Match(block) is { Success: true } x
            && Tagless(x.Groups[1].Value) is { Length: > 0 } exp)
        {
            head.Add(exp);
        }

        if (Payments.Match(block) is { Success: true } p
            && Tagless(p.Groups[1].Value) is { Length: > 0 } pay)
        {
            head.Add(pay);
        }

        return new JobPosting
        {
            PortalId = Descriptor.Id,
            ExternalId = externalId,
            CanonicalUrl = canonical,
            Title = title,
            Company = company,
            Location = address,
            SalaryText = salary,
            SourceClass = Descriptor.SourceClass,
            AccessKind = Descriptor.Access,
            DedupeHash = DedupeHasher.ComputeComposite(Descriptor.Id, externalId, canonical),
            DescriptionMd = head.Count > 0 ? string.Join("\n", head) : null,
        };
    }

    // Вкладка вакансии (DetailAsync — интерфейс IPortalService; поиск её
    // не открывает): «показать вакансию целиком по ссылке».
    public override async Task<JobPosting> DetailAsync(
        string idOrUrl, CancellationToken ct = default)
    {
        var url = idOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? idOrUrl
            : $"{_host}/vacancy/{idOrUrl}";
        var html = await FetchPageAsync(url, ct).ConfigureAwait(false)
            ?? throw new ParseFailedException("вкладка вакансии не отдалась");
        return EnrichFromDetail(new JobPosting
        {
            PortalId = Descriptor.Id,
            ExternalId = Regex.Match(url, @"(\d+)").Value,
            CanonicalUrl = url.Split('?')[0],
            SourceClass = Descriptor.SourceClass,
            AccessKind = Descriptor.Access,
            DedupeHash = DedupeHasher.ComputeComposite(
                Descriptor.Id, Regex.Match(url, @"(\d+)").Value, url.Split('?')[0]),
        }, html);
    }

    // Из HTML вкладки — опыт, навыки, зарплата, описание (до 6000 знаков).
    private JobPosting EnrichFromDetail(JobPosting card, string? detailHtml)
    {
        if (detailHtml is null)
        {
            return card;
        }

        var salary = SalaryOnPage.Match(detailHtml) is { Success: true } s
            ? Tagless(s.Groups[1].Value)
            : null;
        var experience = ExperienceOnPage.Match(detailHtml) is { Success: true } e
            ? Tagless(e.Groups[1].Value)
            : null;
        var skills = Skill.Matches(detailHtml)
            .Select(m => Tagless(m.Groups[1].Value))
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
        var body = Description.Match(detailHtml) is { Success: true } d
            ? Tagless(d.Groups[1].Value)
            : null;
        // r20: кеп поднят 1500 → 6000 — текст со страницы вакансии идёт
        // в промпт адаптации (лимит ResumeAdaptationService.MaxVacancyDescriptionChars).
        // Прежние 1500 знаков резали обязанности/требования на середине.
        if (body is { Length: > 6000 })
        {
            body = body[..6000].TrimEnd() + "…";
        }

        var head = new List<string>();
        if (card.DescriptionMd is not null)
        {
            head.Add(card.DescriptionMd); // «Опыт/Выплаты/удалёнка» из выдачи
        }
        if (!string.IsNullOrEmpty(experience))
        {
            head.Add($"Опыт: {experience}");
        }
        if (!string.IsNullOrEmpty(card.Location))
        {
            head.Add($"Адрес: {card.Location}");
        }
        if (!string.IsNullOrEmpty(salary))
        {
            head.Add($"Зарплата: {salary}");
        }
        if (skills.Count > 0)
        {
            head.Add("Навыки: " + string.Join(", ", skills));
        }

        var description = string.Join("\n", head);
        if (!string.IsNullOrEmpty(body))
        {
            description += "\n\n" + body;
        }

        return card with
        {
            SalaryText = card.SalaryText ?? salary,
            DescriptionMd = description.Length > 0 ? description : null,
        };
    }

    // Канарейка: выдача отвечает без регистрации — Ok; щит/сбой — Degraded.
    public override async Task<PortalHealthReport> HealthCheckAsync(
        CancellationToken ct = default)
    {
        try
        {
            var html = await FetchPageAsync($"{_host}/search/vacancy", ct).ConfigureAwait(false);
            return html is null
                ? new PortalHealthReport(PortalHealthStatus.Degraded,
                    "выдача не отдалась (щит/лимит) — повторить позже")
                : new PortalHealthReport(PortalHealthStatus.Ok,
                    "HTML-выдача отвечает (браузерная эмуляция, без регистрации)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException)
        {
            // Причина — в протокол, вердикт — в отчёт.
            Log.LogWarn($"skip: {Descriptor.Id} канарейка недоступна — {ex.GetType().Name}: {ex.Message}; "
                + "решение: Degraded-отчёт");
            return new PortalHealthReport(PortalHealthStatus.Degraded,
                $"канарейка недоступна: {ex.Message}");
        }
    }

    // ------- разметка (снята с живой выдачи, golden в тестах) -------

    private static readonly Regex CardStart = new(
        @"<div id=""(?<id>\d+)"" class=""vacancy-card--",
        RegexOptions.Compiled);

    // Название: <a> с data-qa="serp-item__title" БЕЗ привязки к порядку
    // атрибутов (в живой разметке class стоит раньше data-qa); href —
    // из атрибутов найденного тега.
    private static readonly Regex TitleLink = new(
        @"<a\b(?<attrs>[^>]*data-qa=""serp-item__title""[^>]*)>"
        + @"(?<title>(?:(?!</a>).)*)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TagHref = new(
        @"href=""(?<href>[^""]+)""",
        RegexOptions.Compiled);

    // Работодатель: сначала точный -text (живая разметка), потом общий <a>
    // (старые страницы/другие зеркала).
    private static readonly Regex EmployerText = new(
        @"data-qa=""vacancy-serp__vacancy-employer-text""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Employer = new(
        @"data-qa=""vacancy-serp__vacancy-employer""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Зарплата: span с классом typography-label-1-regular, в теле —
    // <data value="3594">3 594</data>…<data value="BYN">Br</data> за месяц,
    // до вычета налогов. data-qa на элементе в живой разметке нет.
    private static readonly Regex SalarySpan = new(
        @"<span[^>]*class=""[^""]*typography-label-1-regular[^""]*""[^>]*>"
        + @"(?<body>(?:(?!</span>).)*)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Запасной якорь прежней разметки (если hh вернёт data-qa).
    private static readonly Regex Compensation = new(
        @"data-qa=""vacancy-serp__vacancy-compensation""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Опыт карточки: data-qa="vacancy-serp__vacancy-work-experience-{диапазон}"
    // («Опыт 3-6 лет» — суффикс варьируется, текст уже с префиксом).
    private static readonly Regex Experience = new(
        @"data-qa=""vacancy-serp__vacancy-work-experience-[^""]*""[^>]*>(.*?)</data>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Выплаты: data-qa="vacancy-serp__vacancy-compensation-frequency-{режим}"
    // («Выплаты: Два раза в месяц»).
    private static readonly Regex Payments = new(
        @"data-qa=""vacancy-serp__vacancy-compensation-frequency-[^""]*""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Address = new(
        @"data-qa=""vacancy-serp__vacancy-address""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Счётчик найденного: «Найдено 162 вакансии» (h1 data-qa="title", без
    // data-qa на самом счётчике — берём текстом; Найдено/Найдена/Найден).
    private static readonly Regex TotalFound = new(
        @"Найден\w{0,2}\s+(?<num>[\d\s\u00a0]+)\s*ваканс",
        RegexOptions.Compiled);

    // Пагинация: нумерованные ссылки data-qa="pager-page" (href="…page=N…",
    // нумерация с 0); старого data-qa="pager-next" в разметке нет.
    private static readonly Regex PagerPageAnchor = new(
        @"<a\b(?<attrs>[^>]*data-qa=""pager-page""[^>]*)>",
        RegexOptions.Compiled);

    private static readonly Regex PageParam = new(
        @"[?&]page=(?<n>\d+)",
        RegexOptions.Compiled);

    // ------- вкладка вакансии (DetailAsync; поиск её не открывает) -------

    private static readonly Regex SalaryOnPage = new(
        @"data-qa=""vacancy-salary""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ExperienceOnPage = new(
        @"data-qa=""vacancy-experience""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Skill = new(
        @"data-qa=""skills-element""[^>]*>(.*?)</li>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Description = new(
        @"data-qa=""vacancy-description""[^>]*>(.*)",
        RegexOptions.Compiled | RegexOptions.Singleline);
}
