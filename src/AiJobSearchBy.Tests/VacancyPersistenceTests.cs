using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests.Logging;
using AiJobSearchBy.ViewModels;
using Xunit;

namespace AiJobSearchBy.Tests;

// ============================================================================
// Гейты персистентности вакансий:
// 1) вакансии живут в базе и возвращаются в окно при старте;
// 2) крестик у строки: юзер решает; помеченное (таблица удалённых
// вакансий) при новых поисках не возвращается;
// 3) дедуп окна: повторные находки не дублируются в окне;
// 4) похожие со страниц деталей и блок «Вам подойдут
// эти вакансии» отменены — парсим только страницы выдачи;
// 5) смежные запросы (RelatedQueriesService).
// Сети нет — FakeHttpHandler/SilentLog; диспетчер не крутится — синхронные
// Apply*-методы напрямую.
// ============================================================================
public class VacancyPersistenceTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private ApplicationStore Store()
        => new(Path.Combine(_dir, "test.db"),
            new LogService(_dir, echoToConsole: false));

    private static JobPosting Posting(string url, string title, DateTimeOffset fetched, bool similar = false)
        => new()
        {
            PortalId = "hh-by",
            ExternalId = new string(url.Where(char.IsDigit).ToArray()),
            CanonicalUrl = url,
            Title = title,
            Company = "ООО Ромашка",
            Location = "Минск",
            SalaryText = "от 3 000 Br",
            DescriptionMd = "Опыт: 1–3 года",
            DedupeHash = $"h-{url}",
            FetchedAt = fetched,
            IsSimilar = similar,
        };

    [Fact]
    public void RecentVacancies_Roundtrip_KeepsFieldsAndIsSimilar()
    {
        var store = Store();
        var p1 = Posting("https://rabota.by/vacancy/111", "SAP Basis administrator",
            new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero), similar: true);
        var p2 = Posting("https://rabota.by/vacancy/222", "Системный администратор",
            new DateTimeOffset(2026, 10, 27, 11, 0, 0, TimeSpan.Zero));

        store.UpsertVacancies([p1, p2]);
        var loaded = store.RecentVacancies();

        Assert.Equal(2, loaded.Count);
        // Свежие первыми.
        Assert.Equal("https://rabota.by/vacancy/222", loaded[0].CanonicalUrl);
        var similar = loaded.Single(x => x.CanonicalUrl.EndsWith("/111"));
        Assert.True(similar.IsSimilar);
        Assert.False(loaded[0].IsSimilar);
        Assert.Equal("hh-by", similar.PortalId);
        Assert.Equal("ООО Ромашка", similar.Company);
        Assert.Equal("Минск", similar.Location);
        Assert.Equal("от 3 000 Br", similar.SalaryText);
        Assert.Equal(p1.DedupeHash, similar.DedupeHash);
        // Повторная вставка — дедуп по базе: новеньких 0.
        Assert.Equal(0, store.UpsertVacancies([p1, p2]));
    }

    [Fact]
    public void DeleteVacancy_Tombstones_UpsertSkipsDismissed()
    {
        // Крестик — юзер решает; удалённое
        // не должно возвращаться НИКОГДА.
        var store = Store();
        var p1 = Posting("https://rabota.by/vacancy/111", "SAP Basis administrator",
            DateTimeOffset.Now);
        var p2 = Posting("https://rabota.by/vacancy/222", "Системный администратор",
            DateTimeOffset.Now);
        store.UpsertVacancies([p1, p2]);

        Assert.True(store.DeleteVacancy(p1.DedupeHash));

        Assert.DoesNotContain(store.RecentVacancies(), x => x.DedupeHash == p1.DedupeHash);
        Assert.Single(store.RecentVacancies());
        Assert.Equal(p1.DedupeHash, Assert.Single(store.DismissedHashes()));

        // Новый поиск нашёл то же самое — в базу не вернётся.
        Assert.Equal(0, store.UpsertVacancies([p1]));
        Assert.Single(store.RecentVacancies());
        // Удаление дважды — идемпотентно (камень уже стоит).
        Assert.True(store.DeleteVacancy(p1.DedupeHash));
    }
}

public class VacancyPersistenceWindowTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir, echoToConsole: false);

    private static JobPosting Posting(string id, string title, string? location = "Минск",
        bool similar = false)
        => new()
        {
            PortalId = "hh-by",
            ExternalId = id,
            CanonicalUrl = $"https://rabota.by/vacancy/{id}",
            Title = title,
            Company = "ООО Ромашка",
            Location = location,
            DedupeHash = $"h-{id}",
            IsSimilar = similar,
        };

    [Fact]
    public void ApplyVacancies_SecondSearch_NoDuplicates()
    {
        // Повторные находки не дублируются в окне с каждым новым поиском.
        var log = Log();
        var vm = new MainViewModel(log, providers: null);
        var p = Posting("111", "SAP Basis administrator");

        var first = vm.ApplyVacancies([p], "SAP Basis");
        var second = vm.ApplyVacancies([p], "SAP Basis");

        Assert.Equal(1, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Single(vm.Vacancies);
        Assert.Contains(log.Logs, l => l.Contains("дублей в окне пропущено: 1"));
    }

    [Fact]
    public void DeleteVacancyCommand_RemovesRow_TombstonesInDb()
    {
        var log = Log();
        var store = new ApplicationStore(Path.Combine(_dir, "test.db"), log);
        var vm = new MainViewModel(log, providers: null, store: store);
        var p1 = Posting("111", "SAP Basis administrator");
        var p2 = Posting("222", "Системный администратор");
        vm.ApplyVacancies([p1, p2], null);

        var row = vm.Vacancies.First(v => v.Posting.DedupeHash == p1.DedupeHash);
        vm.DeleteVacancyCommand.Execute(row);

        // Из окна строка ушла, из базы удалена, камень поставлен.
        Assert.Single(vm.Vacancies);
        Assert.DoesNotContain(store.RecentVacancies(), x => x.DedupeHash == p1.DedupeHash);
        Assert.Contains(p1.DedupeHash, store.DismissedHashes());
        Assert.Contains(log.Logs, l => l.Contains("[крестик]") && l.Contains("SAP Basis administrator"));
        // Новый поиск то же самое находит — в окно/базу не возвращается.
        var again = vm.ApplyVacancies([p1], null);
        Assert.Equal(0, again.Added);
        Assert.Single(vm.Vacancies);
        Assert.Contains(log.Logs, l => l.Contains("с крестиком пропущено: 1"));
    }

    [Fact]
    public void IsSimilar_BypassesRelevance_GeoStillApplies()
    {
        // hh сам нашёл близкие специальности — они проходят мимо релевант-гейта:
        // «Специалист по ИТ» при запросе «SAP Basis» релевант-гайт спрятал бы,
        // но похожих hh назвал сам. Гео — общее: Ташкент прячется.
        var log = Log();
        var vm = new MainViewModel(log, providers: null);

        var regular = Posting("111", "Специалист по информационным технологиям");
        var similarMinsk = Posting("222", "Специалист по информационным технологиям", similar: true);
        var similarTashkent = Posting("333", "Специалист по информационным технологиям",
            location: "Ташкент", similar: true);

        var result = vm.ApplyVacancies([regular, similarMinsk, similarTashkent], "SAP Basis");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.GeoHidden);
        Assert.Equal(1, result.Irrelevant);
        var row = Assert.Single(vm.Vacancies);
        Assert.True(row.IsSimilar);
        Assert.Equal("222", row.Posting.ExternalId);
    }

    [Fact]
    public async Task InitializeAsync_RestoresVacanciesFromStore()
    {
        // Персистентность: после перезапуска найденные вакансии возвращаются из базы.
        var log = Log();
        var store = new ApplicationStore(Path.Combine(_dir, "test.db"), log);
        var minsk = Posting("111", "SAP Basis administrator");
        var tashkent = Posting("222", "SAP Expert", location: "Ташкент");
        store.UpsertVacancies([minsk, tashkent]);

        var vm = new MainViewModel(log, providers: null, store: store);
        await vm.InitializeAsync();

        var row = Assert.Single(vm.Vacancies);
        Assert.Equal("SAP Basis administrator", row.Title);
        Assert.True(vm.HasVacancies);
        Assert.Contains(log.Logs, l => l.Contains("[база]") && l.Contains("1 из 2"));
    }

    [Fact]
    public async Task InitializeAsync_DismissedFromDb_NotRestored()
    {
        var log = Log();
        var store = new ApplicationStore(Path.Combine(_dir, "test.db"), log);
        var p1 = Posting("111", "SAP Basis administrator");
        var p2 = Posting("222", "Системный администратор");
        store.UpsertVacancies([p1, p2]);
        store.DeleteVacancy(p1.DedupeHash); // в прошлой сессии юзер нажал крестик

        var vm = new MainViewModel(log, providers: null, store: store);
        await vm.InitializeAsync();

        var row = Assert.Single(vm.Vacancies);
        Assert.Equal("Системный администратор", row.Title);
        // И новые поиски его тоже не приведут.
        Assert.Equal(0, vm.ApplyVacancies([p1], null).Added);
    }
}



// Класс similar-parsing-тестов (блок «Вам подойдут эти вакансии» со страниц
// деталей, DrainSimilarVacancies) удалён вместе с фичей —
// разбором «похожих» занимается сам сайт. Здесь живут гейты листинговой модели: ноль вкладок.

public class ListingOnlySearchTests
{
    private const string SerpOneCard = """
    <html><body>
    <div id="138846557" class="vacancy-card--n77Dj8TY8VIUF0yM"><div class="vacancy-info--x">
    <a class="magritte-link___x" data-qa="serp-item__title" href="https://hh.ru/vacancy/138846557?query=sap" target="_blank">
      <span data-qa="serp-item__title-text">SAP Basis administrator</span></a>
    <a data-qa="vacancy-serp__vacancy-employer" href="/employer/9587240"><span data-qa="vacancy-serp__vacancy-employer-text">СпроАйТи</span></a>
    <address><span data-qa="vacancy-serp__vacancy-address">Минск</span></address>
    </div></div>
    </body></html>
    """;

    private static HhGroupPortal Create(FakeHttpHandler handler, out FakeHttpHandler h)
    {
        h = handler;
        return new HhGroupPortal(BuiltInPortals.HhBy, new HttpClient(handler), new SilentLog(),
            BrowserEmulationPortalBase.Pacing.Silent);
    }

    private static FakeHttpHandler HandlerOf()
        => new(req => FakeHttpHandler.Of(SerpOneCard, "text/html"));

    [Fact]
    public async Task SearchAsync_ListingOnly_NoDetailRequests()
    {
        // вкладки-детали отменены — запрос только на страницу выдачи,
        // ни одного обращения к /vacancy/.
        var portal = Create(HandlerOf(), out var handler);

        var postings = await portal.SearchAsync("SAP Basis");

        var card = Assert.Single(postings);
        Assert.Equal("SAP Basis administrator", card.Title);
        Assert.Equal("https://hh.ru/vacancy/138846557", card.CanonicalUrl);
        Assert.False(card.IsSimilar); // похожих больше не производится
        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain(handler.Requests,
            r => r.RequestUri!.ToString().Contains("/vacancy/138"));
    }

    [Fact]
    public async Task SearchPreview_FirstPageOnly_NoDetailTabs()
    {
        // смежный запрос — лёгкий проход: одна страница выдачи,
        // без вкладок (в это и есть вся модель).
        var portal = Create(HandlerOf(), out var handler);

        var preview = await portal.SearchPreviewAsync("системный администратор");

        var card = Assert.Single(preview);
        Assert.Equal("SAP Basis administrator", card.Title);
        Assert.Equal("https://hh.ru/vacancy/138846557", card.CanonicalUrl);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(card.IsSimilar);
    }
}

public class RelatedQueriesTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    // Управляемый провайдер цепи: отвечает заготовкой, считает запросы.
    private sealed class FakeAiProvider : IAiChatProvider
    {
        public string Name => "fake";
        public int QualityRank => 5;
        public long? RemainingDailyTokens() => null;
        public int Calls { get; private set; }
        public string Response { get; set; } = "[]";

        public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new AiChatResult(Response, "fake-model",
                new AiChatUsage(10, 20), Name, TimeSpan.Zero));
        }
    }

    private RelatedQueriesService Service(FakeAiProvider provider, out ApplicationStore store)
    {
        store = new ApplicationStore(Path.Combine(_dir, "test.db"), new SilentLog());
        return new RelatedQueriesService(new SilentLog(),
            new TaskRoutingPolicy(new SilentLog(), [provider]), store);
    }

    [Fact]
    public void ParseRelated_HandlesFencesQuotesAndNoise()
    {
        var service = new RelatedQueriesService(new SilentLog(),
            new TaskRoutingPolicy(new SilentLog(), []));

        Assert.Equal(["системный администратор", "администратор серверов"],
            service.ParseRelated(
                "```json\n[\"системный администратор\", \"администратор серверов\"]\n```", "SAP"));
        Assert.Equal(["системный администратор"],
            service.ParseRelated("Вот что я думаю: [\"системный администратор\"] — годится?", "SAP"));
        // Оригинал и куски оригинала отсеиваются: «SAP»/«админ» при запросе
        // «SAP администратор» расширяют поиск, а не уточняют смежное.
        Assert.Empty(service.ParseRelated("""["SAP", "админ", "SAP администратор"]""",
            "SAP администратор"));
        Assert.Empty(service.ParseRelated("ни массива, ни смысла", "SAP"));
    }

    [Fact]
    public async Task ExpandAsync_AsksOnce_ThenUsesCache()
    {
        var provider = new FakeAiProvider
        {
            Response = """["системный администратор", "администратор серверов"]""",
        };
        var service = Service(provider, out var store);

        var first = await service.ExpandAsync("SAP администратор");
        var second = await service.ExpandAsync("SAP администратор");

        Assert.Equal(2, first.Count);
        Assert.Contains("системный администратор", first);
        // Второй поиск того же слова — ИИ не платится (кэш в SettingsKV).
        Assert.Equal(1, provider.Calls);
        Assert.Equal(2, second.Count);
        Assert.Equal(first, second);

        // Другое слово — новый запрос к ИИ.
        provider.Response = """["инженер по поддержке"]""";
        var third = await service.ExpandAsync("гардеробщик");
        Assert.Equal(2, provider.Calls);
        Assert.Single(third);
    }

    [Fact]
    public async Task ExpandAsync_ListQueryAndStar_SkipProvider()
    {
        var provider = new FakeAiProvider();
        var service = Service(provider, out _);

        Assert.Empty(await service.ExpandAsync("c#, sql, англ")); // список — юзер сам сказал
        Assert.Empty(await service.ExpandAsync("*"));             // каталог целиком
        Assert.Empty(await service.ExpandAsync("  "));            // пусто
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ExpandAsync_ChainExhausted_ReturnsEmptyWithoutThrow()
    {
        // Цепь ИИ недоступна — поиск продолжается без смежных.
        var log = new SilentLog();
        var service = new RelatedQueriesService(log,
            new TaskRoutingPolicy(log, Array.Empty<IAiChatProvider>()));

        var related = await service.ExpandAsync("SAP администратор");

        Assert.Empty(related);
        Assert.Contains(log.Logs, l => l.Contains("смежные запросы не получены") && l.Contains("ИИ недоступна"));
    }
}
