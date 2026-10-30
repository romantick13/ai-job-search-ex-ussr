using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Portals;
using AiJobSearchExUssr.Services;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: гейты адаптера группы hh: разбор ЖИВОЙ разметки выдачи (curl
// rabota.by 2026-10-28: «Найдено 162 вакансии», 20 карточек, нумерованный
// пагинатор data-qa="pager-page", зарплата/опыт/выплаты в карточке), план
// страниц по счётчику и пагинатору, кэш всех страниц, ноль вкладок-деталей,
// честный ParseFailed при смене вёрстки, «Отмена» возвращает собранное,
// зеркала строят URL своего хоста и своего региона.
// Сети нет — FakeHttpHandler; паузы в тестах отключены (Pacing.Silent).
public class HhGroupPortalTests
{
    // ---- Golden-фикстуры: живая разметка rabota.by 2026-10-28 ----
    // Карточка 1: зарплата (span typography-label-1-regular с <data value=…>),
    // опыт (data-qa="…-work-experience-…"), выплаты (data-qa="…-compensation-
    // frequency-…") — порядок атрибутов гуляет (class РАНЬШЕ data-qa).
    private const string SerpTwoCards = """
    <html><body>
    <div id="138718332" class="vacancy-card--n77Dj8TY8VIUF0yM font-inter"><div class="vacancy-info--ieHKDTkezpEj0Gsx">
    <span class="vacancy-name-wrapper--PSD41i3dJDUNb5Tr"><a class="magritte-link___b4rEM_7-2-7_xhh" data-qa="serp-item__title" tabindex="0" href="https://rabota.by/vacancy/138718332?query=c%23" target="_blank">
      <span data-qa="serp-item__title-text">Ведущий администратор сетей</span></a></span>
    <a class="magritte-link___b4rEM_7-2-7_xhh" data-qa="vacancy-serp__vacancy-employer" tabindex="0" href="/employer/9587240?hhtmFrom=vacancy_search_list">
      <span data-qa="vacancy-serp__vacancy-employer-text">ОАО АЛЕВКУРП</span></a>
    <span class="magritte-text___pbpft_5-3-17_xhh magritte-text_typography-label-1-regular___pi3R-_5-3-17_xhh"><data value="3594">3 594</data> – <data value="3990">3 990 </data><data value="BYN">Br</data> <!-- -->за месяц, <!-- -->до вычета налогов</span>
    <div class="magritte-tag__label___YHV-o_5-4-4_xhh"><data value="between3And6" data-qa="vacancy-serp__vacancy-work-experience-between3And6">Опыт 3-6 лет</data></div>
    <div class="magritte-tag__label___YHV-o_5-4-4_xhh"><span data-qa="vacancy-serp__vacancy-compensation-frequency-TWICE_PER_MONTH">Выплаты: Два раза в месяц</span></div>
    <address><span data-qa="vacancy-serp__vacancy-address">деревня Королёв Стан, Московская улица, 1А</span></address>
    </div></div>
    <div id="138731587" class="vacancy-card--zZ9KlPqQ3rBs2vNm"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://rabota.by/vacancy/138731587?query=c%23" target="_blank">
      <span data-qa="serp-item__title-text">Сетевой администратор</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/5349502"><span data-qa="vacancy-serp__vacancy-employer-text">Unitsky String Technologies</span></a>
    <div class="magritte-tag__label___x"><data value="between3And6" data-qa="vacancy-serp__vacancy-work-experience-between3And6">Опыт 3-6 лет</data></div>
    <address><span data-qa="vacancy-serp__vacancy-address">Минск, Михалово</span></address>
    <div class="t"><span data-qa="vacancy-label-work-schedule-remote">Можно удалённо</span></div>
    </div></div>
    </body></html>
    """;

    // Страница со счётчиком и нумерованной пагинацией (живая форма:
    // h1 «Найдено N вакансии», data-qa="pager-page", page=0-базный).
    private const string SerpCounterPager = """
    <html><body>
    <h1 data-qa="title" class="magritte-text___x"><span>Найдено 3 вакансии</span> «системный администратор»</h1>
    <div id="138718332" class="vacancy-card--n77Dj8TY8VIUF0yM"><div class="vacancy-info--x">
    <a class="magritte-link___x" data-qa="serp-item__title" href="https://rabota.by/vacancy/138718332" target="_blank">
      <span data-qa="serp-item__title-text">Ведущий администратор сетей</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/9587240"><span data-qa="vacancy-serp__vacancy-employer-text">ОАО АЛЕВКУРП</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">деревня Королёв Стан</span></address>
    </div></div>
    <div id="138731587" class="vacancy-card--zZ9KlPqQ3rBs2vNm"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://rabota.by/vacancy/138731587" target="_blank">
      <span data-qa="serp-item__title-text">Сетевой администратор</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/5349502"><span data-qa="vacancy-serp__vacancy-employer-text">Unitsky String Technologies</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">Минск, Михалово</span></address>
    </div></div>
    <nav data-qa="pager-block" aria-label="Пагинация"><ul>
    <li><a data-qa="pager-page" aria-current="true" class="x" href="/search/vacancy?text=c%23&amp;area=16&amp;page=0&amp;search_session_id=abc">1</a></li>
    <li><a data-qa="pager-page" aria-current="false" class="x" href="/search/vacancy?text=c%23&amp;area=16&amp;page=1&amp;search_session_id=abc">2</a></li>
    </ul></nav>
    </body></html>
    """;

    // Страница 2 выдачи (page=1): одна карточка, пагинации больше нет.
    private const string SerpPage2 = """
    <html><body>
    <div id="138900001" class="vacancy-card--pP2xYwW1oOvB3uDd"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://hh.ru/vacancy/138900001" target="_blank">
      <span data-qa="serp-item__title-text">DevOps инженер</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/111"><span data-qa="vacancy-serp__vacancy-employer-text">ООО Ландыш</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">Минск</span></address>
    </div></div>
    </body></html>
    """;

    private static (HhGroupPortal Portal, SilentLog Log, FakeHttpHandler Handler) Create(
        FakeHttpHandler handler)
    {
        var log = new SilentLog();
        var portal = new HhGroupPortal(BuiltInPortals.HhBy, new HttpClient(handler), log,
            BrowserEmulationPortalBase.Pacing.Silent);
        return (portal, log, handler);
    }

    // Страница page=1 (0-базный пагинатор) -> SerpPage2, остальное — firstSerp.
    private static FakeHttpHandler HandlerOf(string firstSerp)
        => new(req =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("page=")
                ? FakeHttpHandler.Of(SerpPage2, "text/html")
                : FakeHttpHandler.Of(firstSerp, "text/html");
        });

    [Fact]
    public async Task Descriptor_MatchesBrowserCard()
    {
        var (portal, _, _) = Create(HandlerOf(SerpTwoCards));

        var d = portal.Descriptor;
        Assert.Equal("hh-by", d.Id);
        // Имя карточки честно показывает оба зеркала одной базы (hh.by
        // 302 -> rabota.by) и страну поиска.
        Assert.Equal("HH.by + rabota.by (Беларусь)", d.DisplayName);
        Assert.Same(BuiltInPortals.HhBy.Card, d);
        Assert.Equal(PortalSourceClass.Commercial, d.SourceClass);
        Assert.Equal(PortalAccessKind.Scrape, d.Access);
        Assert.Equal(PortalAuthKind.None, d.Auth);
        Assert.Equal(5, d.DefaultPriority);
        // r21 (решение владельца): выключен по умолчанию — все чекбоксы выключены.
        Assert.False(d.DefaultEnabled);
        Assert.NotNull(d.ApplyWarning);
        Assert.Contains("регистрация не нужна", d.ApplyWarning);
    }

    [Fact]
    public async Task Search_UrlTargets_MirrorHostAndArea()
    {
        // Каждое зеркало ищет на СВОЁМ хосте в СВОЕМ регионе:
        // Беларусь 16, Казахстан 40, Россия 113, Узбекистан 97.
        var mirrors = new[]
        {
            (BuiltInPortals.HhBy, "https://rabota.by/search/vacancy", "area=16"),
            (BuiltInPortals.HhKz, "https://hh.kz/search/vacancy", "area=40"),
            (BuiltInPortals.HhRu, "https://hh.ru/search/vacancy", "area=113"),
            (BuiltInPortals.HhUz, "https://hh.uz/search/vacancy", "area=97"),
        };
        foreach (var (mirror, urlPrefix, area) in mirrors)
        {
            var handler = new FakeHttpHandler(_ =>
                FakeHttpHandler.Of(SerpTwoCards, "text/html"));
            var portal = new HhGroupPortal(mirror, new HttpClient(handler), new SilentLog(),
                BrowserEmulationPortalBase.Pacing.Silent);

            await portal.SearchAsync("системный администратор");

            var url = handler.Requests[0].RequestUri!.ToString();
            Assert.StartsWith(urlPrefix, url);
            Assert.Contains(area, url);
        }
    }

    [Fact]
    public async Task Search_ParsesListingCards_NoDetailTabs()
    {
        // Данными карточки из листинга достаточно — вкладок НЕТ.
        var handler = HandlerOf(SerpTwoCards);
        var (portal, _, _) = Create(handler);

        var postings = await portal.SearchAsync("системный администратор");

        Assert.Equal(2, postings.Count);
        Assert.Equal(1, handler.RequestCount); // только страница выдачи

        var first = postings[0];
        Assert.Equal("hh-by", first.PortalId);
        Assert.Equal("138718332", first.ExternalId);
        // Канонический URL без мусорных ?query=… параметров выдачи.
        Assert.Equal("https://rabota.by/vacancy/138718332", first.CanonicalUrl);
        Assert.Equal("Ведущий администратор сетей", first.Title);
        Assert.Equal("ОАО АЛЕВКУРП", first.Company);
        Assert.Equal("деревня Королёв Стан, Московская улица, 1А", first.Location);
        // Зарплата собрана со span без data-qa (живая разметка 2026-10-28),
        // HTML-комментарии вычищены.
        Assert.Equal("3 594 – 3 990 Br за месяц, до вычета налогов", first.SalaryText);
        // Опыт и выплаты — с карточки листинга.
        Assert.Contains("Опыт 3-6 лет", first.DescriptionMd);
        Assert.Contains("Выплаты: Два раза в месяц", first.DescriptionMd);
        Assert.DoesNotContain("Режим", first.DescriptionMd);

        // Дедуп обязателен и детерминирован (композитный хеш).
        Assert.Equal(
            DedupeHasher.ComputeComposite("hh-by", "138718332", "https://rabota.by/vacancy/138718332"),
            first.DedupeHash);

        // Браузерные заголовки: настоящий User-Agent (щит видит браузер).
        Assert.Contains("Chrome", handler.Requests[0].Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Search_SecondCard_RemoteAndExperience_NoSalary()
    {
        var (portal, _, _) = Create(HandlerOf(SerpTwoCards));

        var postings = await portal.SearchAsync("системный администратор");
        var second = postings[1];

        Assert.Equal("Сетевой администратор", second.Title);
        Assert.Equal("Unitsky String Technologies", second.Company);
        Assert.Equal("Минск, Михалово", second.Location);
        Assert.Null(second.SalaryText); // карточка без зарплаты — как на живой выдаче
        Assert.Contains("Режим: можно удалённо", second.DescriptionMd);
        Assert.Contains("Опыт 3-6 лет", second.DescriptionMd);
        Assert.DoesNotContain("Выплаты", second.DescriptionMd);
    }

    [Fact]
    public async Task Search_CounterAndPager_PlansAllPages_NoDetailTabs()
    {
        // «Смотришь, сколько страниц выдало в поиске, запоминаешь,
        // кэшируешь» — счётчик «Найдено 3 вакансии» при 2 карточках на
        // странице = 2 страницы; парсим ОБЕ, вкладок по-прежнему ноль.
        var handler = HandlerOf(SerpCounterPager);
        var (portal, log, _) = Create(handler);

        var postings = await portal.SearchAsync("системный администратор");

        Assert.Equal(3, postings.Count);
        Assert.Equal(2, handler.RequestCount); // 2 страницы выдачи, 0 вкладок
        // URL второй страницы собран из первой подменой page=0 -> page=1.
        Assert.Contains("page=1", handler.Requests[1].RequestUri!.ToString());
        // План страниц — строкой в протоколе.
        Assert.Contains(log.Logs, l => l.Contains("сайт нашёл 3 вакансий"));
        Assert.Equal("DevOps инженер", postings[2].Title);
    }

    [Fact]
    public async Task Search_TitleKeyDedup_SkipsReposts()
    {
        // Перепост (другой id, то же название+работодатель) — одна строка,
        // отсечка в протоколе. Меняем и название, и работодателя — иначе
        // это не перепост, а разные вакансии двух компаний с одинаковой
        // должностью (их дедуп НЕ режет).
        var duplicate = SerpTwoCards.Replace(
            "Сетевой администратор", "Ведущий администратор сетей", StringComparison.Ordinal)
            .Replace("Unitsky String Technologies", "ОАО АЛЕВКУРП", StringComparison.Ordinal)
            .Replace("138731587", "138999999", StringComparison.Ordinal);
        var (portal, log, _) = Create(HandlerOf(duplicate));

        var postings = await portal.SearchAsync("системный администратор");

        Assert.Single(postings);
        Assert.Equal("138718332", postings[0].ExternalId); // первый пришёл — первым остался
        Assert.Contains(log.Logs, l => l.Contains("дубль названия и работодателя"));
        // Отсечённые перепосты считаются для счётчика у окна «Вакансии».
        Assert.Equal(1, portal.LastRunDuplicatesExcluded);
    }

    [Fact]
    public async Task Search_SchemaChanged_ThrowsParseFailed()
    {
        // Ни одной карточки div.vacancy-card-- = щит-заглушка или смена
        // вёрстки (r22b: честный текст вместо «вёрстка выдачи сменилась»).
        var handler = HandlerOf("<html><body><div class='aaa'>пусто</div></body></html>");
        var (portal, _, _) = Create(handler);

        var ex = await Assert.ThrowsAsync<ParseFailedException>(
            () => portal.SearchAsync("c#"));
        Assert.Contains("карточек нет", ex.Message);
        Assert.Contains("щит-заглушка", ex.Message);
        Assert.Contains("вёрстки", ex.Message);
    }

    [Fact]
    public async Task Search_Cancelled_ReturnsCollected_InsteadOfThrowing()
    {
        // «Отмена» — собранное не пропадает.
        var handler = HandlerOf(SerpTwoCards);
        var (portal, _, _) = Create(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var postings = await portal.SearchAsync("c#", cts.Token);

        Assert.Empty(postings);
    }

    [Fact]
    public async Task Search_CardWithoutLink_SkippedWithWarn_OthersKept()
    {
        var broken = """
        <html><body>
        <div id="111" class="vacancy-card--aA1bB2cC3"><div>битая карточка без ссылки</div></div>
        <div id="138846557" class="vacancy-card--n77Dj8TY8VIUF0yM"><div class="vacancy-info--x">
        <a data-qa="serp-item__title" href="https://rabota.by/vacancy/138846557" target="_blank">
          <span data-qa="serp-item__title-text">Целая карточка</span></a>
        </div></div>
        </body></html>
        """;
        var handler = HandlerOf(broken);
        var (portal, log, _) = Create(handler);

        var postings = await portal.SearchAsync("c#");

        // Битую карточку пропустили с warn (тихих игноров нет), целую сохранили.
        var posting = Assert.Single(postings);
        Assert.Equal("Целая карточка", posting.Title);
        Assert.Contains(log.Logs, line => line.Contains("без ссылки"));
    }

    [Fact]
    public async Task HealthCheck_200_ReportsOk()
    {
        var (portal, _, _) = Create(HandlerOf(SerpTwoCards));

        var report = await portal.HealthCheckAsync();

        Assert.Equal(PortalHealthStatus.Ok, report.Status);
        Assert.Contains("без регистрации", report.Detail);
    }

    [Fact]
    public async Task HealthCheck_403_AfterRetry_ReportsDegraded()
    {
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of("", "text/html", status: 403));
        var (portal, _, _) = Create(handler);

        var report = await portal.HealthCheckAsync();

        Assert.Equal(PortalHealthStatus.Degraded, report.Status);
        Assert.Contains("403", report.Detail);
    }

    [Fact]
    public async Task HealthCheck_NetworkFailure_ReportsDegradedNotThrow()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("dns dead"));
        var (portal, _, _) = Create(handler);

        var report = await portal.HealthCheckAsync();

        Assert.Equal(PortalHealthStatus.Degraded, report.Status);
        Assert.Contains("dns dead", report.Detail);
    }
}
