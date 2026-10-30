using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Services.Ai;
using AiJobSearchExUssr.Tests.Logging;
using AiJobSearchExUssr.ViewModels;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: MainViewModel — сайты из реестра, флаг HasVacancies, история,
// подсказка при пустом резюме, персистенция резюме при выходе.
// LIFECYCLE: каждый тест — свои temp-база/протокол; диспетчер не крутится —
// вызываются синхронные Apply*/команды напрямую.
public class MainViewModelWorkspaceTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir, echoToConsole: false);

    private ApplicationStore Store(LogService log) =>
        new(Path.Combine(_dir, "test.db"), log);

    [Fact]
    public void LoadSites_BuildsToggles_FromRegistry()
    {
        var log = Log();
        var registry = new Portals.PortalRegistry(new[]
        {
            new FakePortal("alpha", priority: 6, defaultEnabled: true),
            new FakePortal("beta", priority: 7, defaultEnabled: false),
        });
        var vm = new MainViewModel(log, providers: null, registry: registry);

        vm.LoadSites();

        Assert.Equal(2, vm.Sites.Count);
        Assert.Equal("alpha", vm.Sites[0].PortalId);
        Assert.True(vm.Sites[0].IsChecked);
        Assert.False(vm.Sites[1].IsChecked); // выключенный по умолчанию портал
    }

    [Fact]
    public async Task LoadSites_RestoresChecked_FromStore()
    {
        var log = Log();
        var store = Store(log);
        store.SetSetting("sites.checked", """["wwr"]""");
        var registry = new Portals.PortalRegistry(new[]
        {
            new FakePortal("remoteok", priority: 6, defaultEnabled: true),
            new FakePortal("wwr", priority: 7, defaultEnabled: false),
        });
        var vm = new MainViewModel(log, providers: null, registry: registry, store: store);

        await vm.InitializeAsync();

        // Легаси-формат (массив только отмеченных): выбор юзера сильнее
        // дефолта карточки — wwr отмечен, remoteok (из эпохи легаси) снят.
        Assert.False(vm.Sites[0].IsChecked);
        Assert.True(vm.Sites[1].IsChecked);
    }

    [Fact]
    public async Task LoadSites_LegacyFormat_NewPortalKeepsDefault()
    {
        // Портал, появившийся ПОСЛЕ легаси-записи (нет в списке эпохи
        // легаси), сохраняет дефолт карточки — его «нет в массиве»
        // не означает «выключено пользователем».
        var log = Log();
        var store = Store(log);
        store.SetSetting("sites.checked", """["remoteok"]""");
        var registry = new Portals.PortalRegistry(new[]
        {
            new FakePortal("remoteok", priority: 6, defaultEnabled: true),
            new FakePortal("hh-ru", priority: 5, defaultEnabled: true), // новое зеркало
            new FakePortal("wwr", priority: 7, defaultEnabled: false),
        });
        var vm = new MainViewModel(log, providers: null, registry: registry, store: store);

        await vm.InitializeAsync();

        Assert.True(vm.Sites.Single(s => s.PortalId == "remoteok").IsChecked);
        Assert.True(vm.Sites.Single(s => s.PortalId == "hh-ru").IsChecked); // дефолт
        Assert.False(vm.Sites.Single(s => s.PortalId == "wwr").IsChecked);
    }

    [Fact]
    public async Task LoadSites_V2Snapshot_AppliedExactly()
    {
        // Полный формат v2 {id: bool} — применяется ровно как сохранено.
        var log = Log();
        var store = Store(log);
        store.SetSetting("sites.checked.v2",
            """{"remoteok": false, "hh-ru": true, "wwr": true}""");
        var registry = new Portals.PortalRegistry(new[]
        {
            new FakePortal("remoteok", priority: 6, defaultEnabled: true),
            new FakePortal("hh-ru", priority: 5, defaultEnabled: true),
            new FakePortal("wwr", priority: 7, defaultEnabled: false),
        });
        var vm = new MainViewModel(log, providers: null, registry: registry, store: store);

        await vm.InitializeAsync();

        Assert.False(vm.Sites.Single(s => s.PortalId == "remoteok").IsChecked);
        Assert.True(vm.Sites.Single(s => s.PortalId == "hh-ru").IsChecked);
        Assert.True(vm.Sites.Single(s => s.PortalId == "wwr").IsChecked);
    }

    [Fact]
    public void ApplyVacancies_SetsHasVacanciesFlag()
    {
        var vm = new MainViewModel(Log(), providers: null);

        Assert.False(vm.HasVacancies);
        vm.ApplyVacancies([Posting()]);
        Assert.True(vm.HasVacancies);
        Assert.Single(vm.Vacancies);
        Assert.Equal("remoteok", vm.Vacancies[0].Site);
        Assert.Equal("Acme", vm.Vacancies[0].Meta);
    }

    [Fact]
    public void ApplyHistory_FillsHistoryRows()
    {
        var vm = new MainViewModel(Log(), providers: null, store: Store(Log()));
        var record = new ApplicationRecord
        {
            Id = Guid.NewGuid(),
            VacancyId = Guid.NewGuid(),
            Employer = "ООО Ромашка",
            VacancyTitle = "Senior Developer",
            VacancyUrl = "https://example.org/v/1",
            CvPath = @"C:\out\ООО Ромашка_20261022_141530.pdf",
            SentAt = new DateTimeOffset(2026, 10, 22, 14, 15, 30, TimeSpan.Zero),
            CreatedAt = DateTimeOffset.Now,
        };

        vm.ApplyHistory([record]);

        var row = Assert.Single(vm.History);
        Assert.Equal("ООО Ромашка", row.Employer);
        Assert.Equal("22.10.2026 14:15", row.SentDisplay);
        Assert.Equal("Senior Developer", row.Title);
    }

    [Fact]
    public void SelectVacancy_OnlySelects_NoAdaptation_NoNetwork()
    {
        // Клик по вакансии ничего делать НЕ надо —
        // просто подсветить: выбор запоминает строку и чистит окно
        // результата, но НЕ запускает адаптацию и НЕ трогает резюме.
        var log = Log();
        var adaptation = new ResumeAdaptationService(log,
            new TaskRoutingPolicy(log, Array.Empty<IAiChatProvider>()));
        var vm = new MainViewModel(log, providers: null, adaptation: adaptation);
        var posting = Posting();
        var row = new VacancyRow { Posting = posting };

        vm.SelectVacancyCommand.Execute(row);

        Assert.Same(posting, vm.SelectedVacancy);
        Assert.Equal(string.Empty, vm.AdaptedResumeText); // текст не писался
        Assert.False(vm.IsAdapting);
        Assert.Contains(log.Logs, line => line.Contains("[выбор]"));
        // Ни одна строка протокола не похожа на запуск адаптации.
        Assert.DoesNotContain(log.Logs, line => line.Contains("[adapt]"));
    }

    [Fact]
    public async Task AdaptVacancy_WithoutVacancy_Warns()
    {
        // кнопка без выбранной строки — не молчит, а подсказывает.
        var log = Log();
        var vm = new MainViewModel(log, providers: null);

        await vm.AdaptVacancyCommand.ExecuteAsync(null);

        Assert.Contains(log.Logs, line => line.Contains("вакансия не выбрана"));
    }

    [Fact]
    public async Task AdaptVacancy_WithoutResume_ShowsHint()
    {
        // адаптация не молчит — подсказка в окно и WARN в протокол.
        // адаптация — только по кнопке, клик строку не трогает.
        var log = Log();
        var adaptation = new ResumeAdaptationService(log,
            new TaskRoutingPolicy(log, Array.Empty<IAiChatProvider>()));
        var vm = new MainViewModel(log, providers: null, adaptation: adaptation);
        vm.SelectVacancyCommand.Execute(new VacancyRow { Posting = Posting() });

        await vm.AdaptVacancyCommand.ExecuteAsync(null);

        // подсказка ведёт в панель профилей.
        Assert.Contains("Сначала загрузи резюме", vm.AdaptedResumeText);
        // LogService рисует предупреждение символом ⚠ (не словом WARN — это SilentLog).
        Assert.Contains(log.Logs, line => line.Contains("⚠") && line.Contains("резюме пустое"));
        Assert.False(vm.IsAdapting);
    }

    [Fact]
    public async Task AdaptVacancy_RunsChain_SavesPdfAndHistory_InVacancyLanguage()
    {
        // кнопка «Адаптировать» — цепочка ИИ (адаптация), текст в окно,
        // PDF на диск, строка в истории; браузер НЕ открывается (скрепка).
        var log = Log();
        var provider = new FakeAdaptProvider();
        var adaptation = new ResumeAdaptationService(log,
            new TaskRoutingPolicy(log, new IAiChatProvider[] { provider }));
        var store = Store(log);
        store.SetSetting("outbox.path", _dir); // PDF — в temp-каталог теста
        var vm = new MainViewModel(log, providers: null, store: store,
            adaptation: adaptation, pdf: new FakePdfExporter(_dir));
        vm.ResumeText = "моё резюме: 5 лет C#";
        // Русская вакансия — адаптация должна пойти на русском.
        var posting = Posting() with
        {
            Title = "Системный администратор",
            Company = "ООО Ромашка",
            DescriptionMd = "Требования: Windows Server, сети.",
        };
        vm.SelectVacancyCommand.Execute(new VacancyRow { Posting = posting });

        await vm.AdaptVacancyCommand.ExecuteAsync(null);

        Assert.Equal("АДАПТИРОВАННОЕ РЕЗЮМЕ", vm.AdaptedResumeText);
        Assert.Contains("адаптировано (на русском)", vm.AdaptedMeta);
        Assert.False(vm.IsAdapting);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, provider.Requests);

        // PDF лёг на диск, строка истории — в базе. (vm.History мутируется через RunOnUi —
        // диспетчер в тестах не крутится, база — источник правды.)
        var record = Assert.Single(store.RecentApplications());
        Assert.Equal("adapted", record.Status);
        Assert.True(File.Exists(record.CvPath), $"PDF нет: {record.CvPath}");
        Assert.Contains(log.Logs, l => l.Contains("адаптация готова, PDF сохранён"));
    }

    [Fact]
    public async Task AdaptVacancy_EmptyContent_NoPdfNoHistory()
    {
        // Пустой ответ модели — НЕ результат: в окно честная причина,
        // PDF и запись истории не создаются (глух «адаптировано,
        // а резюме пустое» — воспроизведён 2026-10-29).
        var log = Log();
        var provider = new FakeAdaptProvider(content: "");
        var adaptation = new ResumeAdaptationService(log,
            new TaskRoutingPolicy(log, new IAiChatProvider[] { provider }));
        var store = Store(log);
        var vm = new MainViewModel(log, providers: null, store: store,
            adaptation: adaptation, pdf: new FakePdfExporter(_dir));
        vm.ResumeText = "моё резюме: 5 лет C#";
        vm.SelectVacancyCommand.Execute(new VacancyRow { Posting = Posting() });

        await vm.AdaptVacancyCommand.ExecuteAsync(null);

        Assert.Contains("пустой текст", vm.AdaptedResumeText);
        Assert.False(vm.IsAdapting);
        Assert.Empty(store.RecentApplications()); // запись истории НЕ создана
        Assert.Contains(log.Logs, l => l.Contains("[adapt] модель вернула пустой текст"));
    }

    [Fact]
    public void ApplyVacancies_Dismissed_CountsInHeader()
    {
        // Вопрос владельца: «если найдены уже удалённые — там будет инфа?» —
        // да: счётчик у окна «удалённых вами скрыто: N».
        var log = Log();
        var vm = new MainViewModel(log, providers: null);
        var posting = Posting();

        vm.ApplyVacancies([posting]);
        vm.DeleteVacancyCommand.Execute(vm.Vacancies[0]); // крестик
        Assert.Empty(vm.Vacancies);
        Assert.Equal("", vm.VacancyDismissedText); // до новой встречи счётчик молчит

        vm.ApplyVacancies([posting]); // тот же поиск нашёл её снова

        Assert.Empty(vm.Vacancies); // в окно не возвращается
        Assert.Equal(1, vm.DismissedHidden);
        Assert.Equal("удалённых вами скрыто: 1", vm.VacancyDismissedText);
        Assert.True(vm.HasDismissedHidden);
        Assert.Contains(log.Logs, l => l.Contains("с крестиком пропущено: 1"));
    }

    [Fact]
    public async Task ShutdownAsync_PersistsResume()
    {
        var log = Log();
        var store = Store(log);
        var vm = new MainViewModel(log, providers: null, store: store)
        {
            ResumeText = "моё сохранённое резюме",
        };

        await vm.ShutdownAsync();

        Assert.Equal("моё сохранённое резюме", store.GetSetting("resume.text"));
    }

    private static JobPosting Posting() => new()
    {
        PortalId = "remoteok",
        ExternalId = "1",
        CanonicalUrl = "https://example.org/vacancy/1",
        Title = "Senior Developer",
        Company = "Acme",
        DedupeHash = DedupeHasher.ComputeComposite("remoteok", "1", "https://example.org/vacancy/1"),
    };

    // Фейковый провайдер ИИ-цепи: возвращает готовый текст адаптации
    // (content: "" — случай «модель вернула пустоту»).
    private sealed class FakeAdaptProvider(string content = "АДАПТИРОВАННОЕ РЕЗЮМЕ") : IAiChatProvider
    {
        public string Name => "fake-adapt";
        public int QualityRank => 1;
        public long? RemainingDailyTokens() => null;
        public int Requests { get; private set; }

        public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
        {
            Requests++;
            return Task.FromResult(new AiChatResult(
                content, Name,
                new AiChatUsage(100, 50), Name, TimeSpan.FromMilliseconds(10)));
        }
    }

    // Фейковый экспортер PDF: пишет файл-заглушку в указанный каталог.
    private sealed class FakePdfExporter(string dir) : IPdfExporter
    {
        public Task<string> SaveResumePdfAsync(string employer, string? vacancyTitle,
            string resumeText, DateTimeOffset createdAt, string outDir)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, PdfExportService.BuildPdfFileName(employer, createdAt));
            File.WriteAllText(path, "pdf-заглушка");
            return Task.FromResult(path);
        }
    }
}
