using AiJobSearchBy.Models;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests.Logging;
using AiJobSearchBy.ViewModels;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: MainViewModel WP-U1 — сайты из реестра, флаг HasVacancies, история,
// подсказка при пустом резюме, персистенция резюме при выходе.
// LIFECYCLE: каждый тест — свои temp-база/протокол; диспетчер не крутится —
// вызываются синхронные Apply*/команды напрямую (паттерн WP-U0).
public class MainViewModelWpu1Tests : IDisposable
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
        store.SetSetting("sites.checked", """["beta"]""");
        var registry = new Portals.PortalRegistry(new[]
        {
            new FakePortal("alpha", priority: 6, defaultEnabled: true),
            new FakePortal("beta", priority: 7, defaultEnabled: false),
        });
        var vm = new MainViewModel(log, providers: null, registry: registry, store: store);

        await vm.InitializeAsync();

        // Выбор юзера сильнее дефолта карточки: beta отмечен, alpha снят.
        Assert.False(vm.Sites[0].IsChecked);
        Assert.True(vm.Sites[1].IsChecked);
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
    public async Task SelectVacancy_WithoutResume_LogsHint()
    {
        // НИ-2: адаптация не молчит — подсказка в окно и WARN в протокол.
        var log = Log();
        var adaptation = new ResumeAdaptationService(log,
            new TaskRoutingPolicy(log, Array.Empty<IAiChatProvider>()));
        var vm = new MainViewModel(log, providers: null, adaptation: adaptation);

        await vm.SelectVacancyCommand.ExecuteAsync(new VacancyRow { Posting = Posting() });

        Assert.Contains("Сначала заполни", vm.AdaptedResumeText);
        // LogService рисует предупреждение символом ⚠ (не словом WARN — это SilentLog).
        Assert.Contains(log.Logs, line => line.Contains("⚠") && line.Contains("резюме пустое"));
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
}
