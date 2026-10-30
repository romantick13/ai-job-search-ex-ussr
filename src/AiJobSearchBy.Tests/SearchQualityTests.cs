using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;
using AiJobSearchBy.Tests.Logging;
using AiJobSearchBy.ViewModels;
using Xunit;

namespace AiJobSearchBy.Tests;

// ============================================================================
// Гейты качества поиска (по живым прогонам — три симптома, три гейта):
// 1) в выдачу просачивалось чужое гео (Казань, село Толмачёво), а на
// работа.by оставалась одна вакансия → поиск hh-группы идёт с area=16
// (Беларусь), как у сайта;
// 2) дубли «администратор по возвратам» шли пачкой одинаковых карточек
// → дедуп «название + работодатель до первой точки» (перепосты hh
// под разными id и филиалы «Компания. Отдел») — в обходе портала,
// в окне и при загрузке сохранённых;
// 3) счётчик «вакансий всегда 20?» → судьба пагинации ЯВНО в протоколе.
// Вкладок-деталей больше нет — обход идёт ТОЛЬКО по
// страницам выдачи (счётчик «Найдено N» + пагинатор data-qa=
// "pager-page", href ордер-агностичен), тесты переписаны на новую модель.
// Сети нет — FakeHttpHandler/SilentLog.
// ============================================================================
public class TitleKeyDedupTests
{
    [Fact]
    public void TitleKey_FoldsCasePunctuation_AndEmployerFamily()
    {
        // «Все — одинаковые»: филиал не делает вакансию новой:
        // «Логика молока. Логистика» и «Логика молока. Офис» — один работодатель.
        Assert.Equal(
            DedupeHasher.TitleKey("Администратор по возвратам", "Логика молока. Логистика"),
            DedupeHasher.TitleKey("Администратор по возвратам", "Логика молока. Офис"));

        // Другой работодатель — другая строка, даже с тем же названием.
        Assert.NotEqual(
            DedupeHasher.TitleKey("Администратор", "ООО Ромашка"),
            DedupeHasher.TitleKey("Администратор", "ООО Тюльпан"));

        // Нормализация: регистр, ё→е, кавычки/знаки/пробелы схлопываются.
        Assert.Equal(
            DedupeHasher.TitleKey("SAP Basis administrator", "ООО «Ромашка»"),
            DedupeHasher.TitleKey("sap basis administrator", "ООО Ромашка"));
        Assert.Equal(
            DedupeHasher.TitleKey("Администратор склада", "ООО Ёлка"),
            DedupeHasher.TitleKey("администратор склада", "ООО Елка"));

        // Пустое название — пустой ключ (дедупа нет).
        Assert.Equal(string.Empty, DedupeHasher.TitleKey(null, "ООО Ромашка"));
        Assert.Equal(string.Empty, DedupeHasher.TitleKey("  ", null));
    }
}

public class VacancyWindowTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir, echoToConsole: false);

    private static JobPosting Posting(string id, string title, string company, string location)
        => new()
        {
            PortalId = "hh-by",
            ExternalId = id,
            CanonicalUrl = $"https://rabota.by/vacancy/{id}",
            Title = title,
            Company = company,
            Location = location,
            DedupeHash = $"h-{id}",
        };

    [Fact]
    public void ApplyVacancies_RepostsCollapseToOneRow()
    {
        // Живой CSV «Администратор по возвратам» ×N у «Логика
        // молока.*» в разных городах (Казань/Екатеринбург/Троице-Сельцо) —
        // для юзера одна вакансия. Одна строка, отсечки — в протоколе.
        var log = Log();
        var vm = new MainViewModel(log, providers: null);

        var applied = vm.ApplyVacancies(
        [
            Posting("138508043", "Администратор по возвратам", "Логика молока. Логистика", "Казань, р-н Приволжский"),
            Posting("138457305", "Администратор по возвратам", "Логика молока. Офис", "Екатеринбург, р-н Железнодорожный"),
            Posting("137161056", "Администратор по возвратам", "Логика молока. Логистика", "деревня Троице-Сельцо"),
        ], null);

        Assert.Equal(1, applied.Added);
        var row = Assert.Single(vm.Vacancies);
        Assert.Equal("138508043", row.Posting.ExternalId); // выживает первый
        Assert.Contains(log.Logs, l => l.Contains("дублей в окне пропущено: 2"));
        // Тихих игноров нет: каждая отсечка — с URL и причиной.
        Assert.Contains(log.Logs, l => l.Contains("[search] дубль «Администратор по возвратам»")
            && l.Contains("138457305"));
        Assert.Contains(log.Logs, l => l.Contains("[search] дубль «Администратор по возвратам»")
            && l.Contains("137161056"));
    }

    [Fact]
    public void ApplyVacancies_SameTitleDifferentEmployer_BothKept()
    {
        // Дедуп не душит разные компании с одинаковым названием должности.
        var log = Log();
        var vm = new MainViewModel(log, providers: null);

        var applied = vm.ApplyVacancies(
        [
            Posting("111", "Администратор", "ООО Ромашка", "Минск"),
            Posting("222", "Администратор", "ООО Тюльпан", "Гомель"),
        ], null);

        Assert.Equal(2, applied.Added);
        Assert.Equal(2, vm.Vacancies.Count);
        Assert.DoesNotContain(log.Logs, l => l.Contains("дублей в окне"));
    }

    [Fact]
    public void ApplyVacancies_SecondSearch_DoesNotReAddRepostOfExistingRow()
    {
        // Существующая строка защищена и от
        // перепостов — новый поиск не доливает «Администратор по возвратам»
        // той же сети, уже болтающейся в окне.
        var log = Log();
        var vm = new MainViewModel(log, providers: null);
        vm.ApplyVacancies(
        [
            Posting("111", "Администратор по возвратам", "Логика молока. Логистика", "Минск"),
        ], null);

        var second = vm.ApplyVacancies(
        [
            Posting("222", "Администратор по возвратам", "Логика молока. Офис", "Минск"),
        ], null);

        Assert.Equal(0, second.Added);
        Assert.Single(vm.Vacancies);
    }

    [Fact]
    public void ApplySavedVacancies_StartupCollapsesReposts()
    {
        // После перезапуска найденные вакансии не должны пропадать
        // (плюс CSV с перепостами). При загрузке из базы
        // перепосты тоже схлопываются: свежие первыми, выживает самый новый.
        var log = Log();
        var vm = new MainViewModel(log, providers: null);

        var (shown, _) = vm.ApplySavedVacancies(
        [
            Posting("135835918", "Администратор по возвратам", "Логика молока. Логистика", "Минск"),
            Posting("135835945", "Администратор по возвратам", "Логика молока. Логистика", "Минск"),
            Posting("137161056", "Администратор по возвратам", "Логика молока. Офис", "Минск"),
            Posting("138900001", "SAP Basis administrator", "СпроАйТи", "Минск"),
        ]);

        Assert.Equal(2, shown);
        Assert.Equal(2, vm.Vacancies.Count);
        Assert.Contains(log.Logs, l => l.Contains("[база]") && l.Contains("дублей название+работодатель 2"));
    }
}

public class HhSerpSearchTests
{
// ---- Фикстуры: перепосты в выдаче + пагинация с href ПЕРЕД data-qa ----

    private const string SerpReposts = """
    <html><body>
    <div id="138508043" class="vacancy-card--aA1"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://hh.ru/vacancy/138508043" target="_blank">
      <span data-qa="serp-item__title-text">Администратор по возвратам</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/1"><span>Логика молока. Логистика</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">Казань, р-н Приволжский</span></address>
    </div></div>
    <div id="138457305" class="vacancy-card--bB2"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://hh.ru/vacancy/138457305" target="_blank">
      <span data-qa="serp-item__title-text">Администратор по возвратам</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/1"><span>Логика молока. Офис</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">Екатеринбург</span></address>
    </div></div>
    <div id="137161056" class="vacancy-card--cC3"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://hh.ru/vacancy/137161056" target="_blank">
      <span data-qa="serp-item__title-text">Администратор по возвратам</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/1"><span>Логика молока. Логистика</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">деревня Троице-Сельцо</span></address>
    </div></div>
    </body></html>
    """;

    private const string SerpTwoCardsPagerPage = """
    <html><body>
    <div id="138846557" class="vacancy-card--n77"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://hh.ru/vacancy/138846557" target="_blank">
      <span data-qa="serp-item__title-text">Head of Development (C#, MS SQL)</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/1"><span data-qa="vacancy-serp__vacancy-employer-text">ООО Ромашка</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">Минск</span></address>
    </div></div>
    <div id="138731587" class="vacancy-card--zZ9"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://rabota.by/vacancies/138731587" target="_blank">
      <span data-qa="serp-item__title-text">SAP Basis консультант</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/2"><span data-qa="vacancy-serp__vacancy-employer-text">ООО Тюльпан</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">Гомель</span></address>
    </div></div>
    <!-- нумерованный пагинатор (живая разметка), href
         ПЕРЕД data-qa — ордер-агностичность обязана выживать -->
    <a aria-current="false" class="pager"
      href="/search/vacancy?text=c%23&amp;page=1&amp;search_session_id=abc" data-qa="pager-page"></a>
    </body></html>
    """;

    private const string SerpPage2 = """
    <html><body>
    <div id="138900001" class="vacancy-card--pP2"><div class="vacancy-info--x">
    <a data-qa="serp-item__title" href="https://hh.ru/vacancy/138900001" target="_blank">
      <span data-qa="serp-item__title-text">DevOps инженер</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/3"><span data-qa="vacancy-serp__vacancy-employer-text">ООО Ландыш</span></a>
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

    private static FakeHttpHandler HandlerOf(
        string firstSerp, Func<HttpRequestMessage, string>? page2 = null)
        => new(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/search/vacancy")
                && req.RequestUri!.Query.Contains("page=")
                && page2 is not null)
            {
                return FakeHttpHandler.Of(page2(req), "text/html");
            }
            if (url.Contains("/search/vacancy"))
            {
                return FakeHttpHandler.Of(firstSerp, "text/html");
            }
            return FakeHttpHandler.Of(SerpPage2, "text/html");
        });

    [Fact]
    public async Task Search_RepostCards_NoTabOpened()
    {
        // «Аж 7 штук! и все — одинаковые»: перепосты схлопываются ещё в обходе.
        // вкладок нет вообще — запрос ТОЛЬКО на страницу выдачи.
        var handler = HandlerOf(SerpReposts);
        var (portal, log, _) = Create(handler);

        var postings = await portal.SearchAsync("администратор");

        var first = Assert.Single(postings);
        Assert.Equal("138508043", first.ExternalId);
        Assert.Equal(1, handler.RequestCount); // только страница выдачи
        Assert.Contains(log.Logs, l => l.Contains("дубль названия и работодателя"));
        Assert.Contains(log.Logs, l => l.Contains("138457305"));
    }

    [Fact]
    public async Task Search_FirstPageUrl_ContainsBelarusArea()
    {
        // «откуда казань?!» — поиск всегда с area=16 (Беларусь),
        // как выдача rabota.by руками; без area hh льёт всю базу группы.
        var handler = HandlerOf(SerpReposts);
        var (portal, _, _) = Create(handler);

        _ = await portal.SearchAsync("администратор");

        Assert.Contains(handler.Requests,
            r => r.RequestUri!.Query.Contains("area=16"));
    }

    [Fact]
    public async Task Search_PagerPage_HrefBeforeDataQa_StillFollows()
    {
        // Вёрстка magritte меняет порядок атрибутов — href до data-qa.
        // Раньше такой pager терялся («всегда 20»), теперь тег ищется целиком.
        // пагинатор нумерованный (pager-page, page=0-базный).
        var handler = HandlerOf(SerpTwoCardsPagerPage, _ => SerpPage2);
        var (portal, _, _) = Create(handler);

        var postings = await portal.SearchAsync("c#");

        Assert.Equal(3, postings.Count);
        Assert.Equal(2, handler.RequestCount); // две страницы выдачи, 0 вкладок
        Assert.Contains("page=1", handler.Requests[1].RequestUri!.ToString());
        Assert.DoesNotContain(handler.Requests,
            r => r.RequestUri!.ToString().Contains("search_session_id"));
    }

    [Fact]
    public async Task Search_NoPager_LogsExhausted()
    {
        // «Вакансий всегда 20? а больше?» — протокол отвечает ЯВНО:
        // ни счётчика, ни пагинатора = выдача кончилась (а не молчаливый стоп).
        var handler = HandlerOf(SerpReposts);
        var (portal, log, _) = Create(handler);

        _ = await portal.SearchAsync("администратор");

        Assert.Contains(log.Logs, l => l.Contains("выдача кончилась"));
        Assert.DoesNotContain(log.Logs, l => l.Contains("потолок"));
    }

    [Fact]
    public async Task Search_PagerPresent_PlansTwoPages()
    {
        // план страниц — по пагинатору; «страниц выдачи: 2» в протоколе.
        var handler = HandlerOf(SerpTwoCardsPagerPage, _ => SerpPage2);
        var (portal, log, _) = Create(handler);

        _ = await portal.SearchAsync("c#");

        Assert.Contains(log.Logs, l => l.Contains("страниц выдачи: 2"));
    }
}
