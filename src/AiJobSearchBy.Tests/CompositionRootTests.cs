using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiJobSearchBy.Tests;

// Хотфикс (урок живого прогона): Build без bootLog
// создавал LogService с реальным %APPDATA%\ai-job-search-by\logs — строки
// [config] от dotnet test падали в ЖИВОЙ протокол. TestConfigEnv изолирует local.json, эта
// обвязка — протокол: каждый контейнерный тест обязан передавать явный
// bootLog с temp-каталогом. Пара к TestConfigEnv, лог-напарник.
internal static class TestBootLogs
{
    internal static Services.LogService Fresh()
        => new(Path.Combine(Path.GetTempPath(), "aijsb-di-logs", Guid.NewGuid().ToString("N")),
               echoToConsole: false);
}

// FLOW: каркасный гейт — контейнер собирается, регистрации на месте.
// Окна не инстанцируем (нужен Avalonia-рантайм), проверяем дескрипторы.
public class CompositionRootTests
{
    [Fact]
    public void Build_ResolvesMainViewModel_AsSingleton()
    {
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var first = services.GetRequiredService<ViewModels.MainViewModel>();
        var second = services.GetRequiredService<ViewModels.MainViewModel>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Build_RegistersMainWindow_AsSingleton()
    {
        var registrations = CompositionRoot.AddRegistrations(new ServiceCollection());

        var descriptor = Assert.Single(registrations,
            d => d.ServiceType == typeof(MainWindow));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void Build_RegistersPortalStack()
    {
        // реестр жив, keys-check на месте, ключи читаются через ILocalSecrets.
        // r21 (решение владельца): все чекбоксы по умолчанию выключены —
        // дефолт-цепь ПУСТА (юзер собирает её галочками).
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var registry = services.GetRequiredService<Portals.PortalRegistry>();
        Assert.Empty(registry.ResolveChain());

        var keysCheck = services.GetRequiredService<Services.IKeysCheckService>();
        Assert.IsAssignableFrom<Services.KeysCheckService>(keysCheck);

        // Шов ILocalSecrets встал на реализацию —
        // ConfigService (local.json в %APPDATA%). NullLocalSecrets остался
        // для фикстур (Fakes.cs).
        Assert.IsAssignableFrom<Services.ConfigService>(
            services.GetRequiredService<Services.ILocalSecrets>());
    }
}

// Гейты реестра порталов: РБ-адаптеры в реестре; HttpClient —
// общий синглтон с таймаутом 60 с.
// Шесть Core-адаптеров + четыре зеркала hh (Deferred) —
// реестр повторяет каталог BuiltInPortals.
public class CompositionRootPortalRegistryTests
{
    [Fact]
    public void Build_RegistryMirrorsCatalog_HhMirrorsInDefaultChain()
    {
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var registry = services.GetRequiredService<Portals.PortalRegistry>();

        // 6 Core + 4 зеркала hh; стуба rabota-by больше нет. praca.by вырезан
        // в r21 (подключался решением владельца — решение отменено в тот же
        // день: сайт низкого качества для IT-поиска).
        Assert.Equal(Portals.BuiltInPortals.Core.Count + Portals.BuiltInPortals.Deferred.Count,
                     registry.Portals.Count);
        // r21 (решение владельца): дефолт-цепь ПУСТА — все чекбоксы выключены,
        // юзер собирает цепочку галочками (выбор запоминается в базе).
        Assert.Empty(registry.ResolveChain());
        // Порядок списка чекбоксов: hh-зеркала (5) → Core (6-8).
        Assert.Equal(
            new[] { "hh-by", "hh-kz", "hh-ru", "hh-uz",
                    "himalayas", "nodesk", "jobicy", "wwr", "remoteok", "remotive" },
            registry.Portals.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void Build_HttpClientIsSingleton_WithSharedTimeout()
    {
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var first = services.GetRequiredService<System.Net.Http.HttpClient>();
        var second = services.GetRequiredService<System.Net.Http.HttpClient>();

        Assert.Same(first, second);
        Assert.Equal(TimeSpan.FromSeconds(60), first.Timeout);
    }
}

// Контракт «никаких тихих игноров»: протокол — первый сервис
// графа; boot-экземпляр из Program.Main доезжает до DI без пересоздания.
public class CompositionRootLogContractTests
{
    [Fact]
    public void Build_RegistersLogService_AsSingleton()
    {
        // Явный bootLog: гейт изоляции — граф не создаёт LogService с живым
        // %APPDATA% (урок: [config]-строки от тестов в живом протоколе).
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var first = services.GetRequiredService<Services.ILogService>();
        var second = services.GetRequiredService<Services.ILogService>();

        Assert.Same(first, second);
        Assert.IsAssignableFrom<Services.LogService>(first);
    }

    [Fact]
    public void Build_WithBootLog_KeepsSameInstance_InGraph()
    {
        var bootLog = new Services.LogService(
            Path.Combine(Path.GetTempPath(), "aijsb-di-tests", Guid.NewGuid().ToString("N")),
            echoToConsole: false);

        using var services = CompositionRoot.Build(bootLog);

        Assert.Same(bootLog, services.GetRequiredService<Services.ILogService>());
    }

    [Fact]
    public void Build_AdaptersReceiveLogService_PortalRegistryResolvable()
    {
        // адаптеры обязаны получить протокол — иначе сборка графа падает
        // (ctor PortalBase требует ILogService) — это и есть CI-гейт контракта.
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var registry = services.GetRequiredService<Portals.PortalRegistry>();

        Assert.Equal(Portals.BuiltInPortals.Core.Count + Portals.BuiltInPortals.Deferred.Count,
                     registry.Portals.Count);
    }

    // Стек откликов в графе — база,
    // PDF-экспорт, индексатор (r21: векторизация вырезана),
    // адаптация, модель настроек.
    public class CompositionRootWorkspaceTests
    {
        [Fact]
        public void Build_RegistersApplicationStack()
        {
            using var services = CompositionRoot.Build(TestBootLogs.Fresh());

            var store = services.GetRequiredService<Services.IApplicationStore>();
            Assert.IsAssignableFrom<Services.ApplicationStore>(store);
            Assert.Contains(Path.Combine("ai-job-search-by", "data"), store.DbPath);

            // r21: векторизация вырезана — IEmbeddingService из графа исчез;
            // индексатор PDF живёт без эмбеддера.

            Assert.IsAssignableFrom<Services.PdfExportService>(
                services.GetRequiredService<Services.IPdfExporter>());
            Assert.IsAssignableFrom<Services.ResumeAdaptationService>(
                services.GetRequiredService<Services.ResumeAdaptationService>());
            Assert.IsAssignableFrom<ViewModels.SettingsViewModel>(
                services.GetRequiredService<ViewModels.SettingsViewModel>());
        }

        [Fact]
        public void Build_MainViewModel_ReceivesRegistry()
        {
            using var services = CompositionRoot.Build(TestBootLogs.Fresh());

            var vm = services.GetRequiredService<ViewModels.MainViewModel>();
            Assert.False(vm.IsDesignerMode);
        }
    }
}
