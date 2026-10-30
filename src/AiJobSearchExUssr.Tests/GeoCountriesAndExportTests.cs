using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Tests.Logging;
using AiJobSearchExUssr.ViewModels;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// ============================================================================
// Гейты гео-фильтра и экспорта:
// 1) дефолт списка разрешённых стран «Беларусь + Россия» (ApplyAllowed-
// Countries: нет настройки/пусто -> дефолт; полный список -> весь мир);
// 2) кнопка «Экспорт CSV» в окне вакансий (VacancyCsvExport + команда VM).
// Сети и диспетчера нет — SilentLog/LogService с temp-каталогом.
// ============================================================================
public class GeoCountriesTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private MainViewModel Vm()
        => new(new LogService(_dir, echoToConsole: false), providers: null);

    [Fact]
    public void ApplyAllowedCountries_NoSetting_DefaultsToBelarusAndRussia()
    {
        var vm = Vm();

        vm.ApplyAllowedCountries(null);

        // Напрямую поле не видно — проверяем через поведение окна «Страны…»:
        // текст = дефолтные Беларусь + Россия.
        var text = vm.LoadAllowedCountriesText();
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("Беларусь", lines);
        Assert.Contains("Россия", lines);
    }

    [Fact]
    public void ApplyAllowedCountries_EmptyJson_DefaultsToo()
    {
        var vm = Vm();

        vm.ApplyAllowedCountries("");

        Assert.Equal(2, vm.LoadAllowedCountriesText()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ApplyVacancies_WithoutRestore_ForeignCountryHiddenByDefault()
    {
        // Страховка ApplyVacancies: даже без RestorePersistedStateAsync
        // (тесты/дизайнер) дефолт «Беларусь + Россия» действует — Ташкент
        // из лога больше не проходит.
        var vm = Vm();
        var p = new JobPosting
        {
            PortalId = "hh-by",
            CanonicalUrl = "https://rabota.by/vacancy/1",
            Title = "SAP Expert (remote)",
            Location = "Ташкент",
            DedupeHash = "h1",
        };

        var (shown, geoHidden, _) = vm.ApplyVacancies([p], null);

        Assert.Equal(0, shown);
        Assert.Equal(1, geoHidden);
        Assert.DoesNotContain(vm.Vacancies, r => r.Posting == p);
    }

    [Fact]
    public void SaveAllowedCountriesText_FullList_WorldAgain()
    {
        // Кнопка «Вернуть все страны» = весь мир: чужие страны НЕ прячутся
        // (и в этой же сессии: осознанный null дефолтом не перетирается).
        var vm = Vm();
        var full = string.Join(Environment.NewLine,
            Services.Resume.GeoMatcher.AllDisplays());

        vm.SaveAllowedCountriesText(full);

        var uz = new JobPosting
        {
            PortalId = "hh-by",
            CanonicalUrl = "https://rabota.by/vacancy/2",
            Title = "X (remote)",
            Location = "Ташкент",
            DedupeHash = "h2",
        };
        var (shown, geoHidden, _) = vm.ApplyVacancies([uz], null);
        Assert.Equal(1, shown);
        Assert.Equal(0, geoHidden);
    }
}

public class VacancyCsvExportTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();
    private readonly string _exportDir =
        Path.Combine(Path.GetTempPath(), "aijs-exu-exports", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка */ }
        try { Directory.Delete(_exportDir, recursive: true); } catch { /* уборка */ }
    }

    private static JobPosting Posting(string title, string? company = null, string? location = null)
        => new()
        {
            PortalId = "hh-by",
            CanonicalUrl = $"https://rabota.by/vacancy/{Uri.EscapeDataString(title)}",
            Title = title,
            Company = company,
            Location = location,
            DedupeHash = $"h-{title}",
        };

    [Fact]
    public async Task ExportCommand_WritesCsv_OfVisibleVacancies()
    {
        // Лог держим напрямую: LogService.Logs пишется синхронно, а LogFeed
        // наполняется через UI-диспетчер — в тестах его никто не качает.
        var log = new LogService(_dir, echoToConsole: false);
        var vm = new MainViewModel(log, providers: null)
        {
            ExportDirOverride = _exportDir,
        };
        vm.ApplyVacancies(
        [
            Posting("SAP Basis administrator (удалённо)", "ООО Ромашка", "Минск"),
            Posting("Администратор по возвратам", "ПАО Сливо", "Минск"),
        ], null);

        await vm.ExportVacanciesCommand.ExecuteAsync(null);

        var file = Assert.Single(Directory.GetFiles(_exportDir, "vacancies_*.csv"));
        var text = File.ReadAllText(file);
        Assert.StartsWith("Название;Работодатель;Локация;Зарплата;Режим;Гео;Совпадение, %;Ссылка;Сайт", text);
        Assert.Contains("SAP Basis administrator (удалённо);ООО Ромашка;Минск;", text);
        Assert.Contains("удалёнка", text); // колонка «Режим» словами
        // Локация «Минск» без маркеров удалёнки — режим «офис» (гео указано:
        // «?» больше не висит на строках с явным городом).
        Assert.Contains("офис", text);
        Assert.Contains("https://rabota.by/vacancy/", text);
        // Путь файла честно в протоколе (никаких тихих файлов).
        Assert.Contains(log.Logs, l => l.Contains("[экспорт]") && l.Contains("2 вакансий"));
    }

    [Fact]
    public async Task ExportCommand_EmptyList_WarnsAndWritesNothing()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = new MainViewModel(log, providers: null) { ExportDirOverride = _exportDir };

        await vm.ExportVacanciesCommand.ExecuteAsync(null);

        Assert.Empty(Directory.Exists(_exportDir) ? Directory.GetFiles(_exportDir) : []);
        Assert.Contains(log.Logs, l => l.Contains("экспорт не выполнен") && l.Contains("список вакансий пуст"));
    }

    [Fact]
    public void Build_EscapesSeparatorsAndQuotes()
    {
        var vm = new MainViewModel(null, null); // без зависимостей: коллекция живая
        vm.Vacancies.Add(new VacancyRow
        {
            // Компания с настоящей точкой-с-запятой и кавычкой — проверка
            // RFC 4180-эскейпа; запятая сама по себе кавычек не требует.
            Posting = Posting("C#; \"SQL\" разработчик", "ООО «Точка;запятая» \"Плюс\"", "Минск"),
        });

        var csv = VacancyCsvExport.Build(vm.Vacancies);

        Assert.Contains("\"C#; \"\"SQL\"\" разработчик\"", csv);
        Assert.Contains("\"ООО «Точка;запятая» \"\"Плюс\"\"\"", csv);
    }

    [Fact]
    public void FileName_SortsByDate()
    {
        var name = VacancyCsvExport.FileName(new DateTimeOffset(2026, 10, 27, 14, 32, 0, TimeSpan.Zero));
        Assert.Equal("vacancies_2026-10-27_1432.csv", name);
    }
}
