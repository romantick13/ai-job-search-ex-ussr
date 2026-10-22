using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiJobSearchBy.Tests;

// Хотфикс 2026-10-22 (урок живого прогона владельца): Build() без bootLog
// создавал LogService с реальным %APPDATA%\ai-job-search-by\logs — строки
// [config] от dotnet test падали в ЖИВОЙ протокол владельца (видно в его
// логе 2026-10-21 20:20/20:39). TestConfigEnv изолирует local.json, эта
// обвязка — протокол: каждый контейнерный тест обязан передавать явный
// bootLog с temp-каталогом. Пара к TestConfigEnv, лог-напарник.
internal static class TestBootLogs
{
    internal static Services.LogService Fresh()
        => new(Path.Combine(Path.GetTempPath(), "aijsb-di-logs", Guid.NewGuid().ToString("N")),
               echoToConsole: false);
}

// FLOW: каркасный гейт WP-0 — контейнер собирается, регистрации на месте.
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
    public void Build_RegistersPortalStack_Wpp0()
    {
        // WP-P0: реестр жив, keys-check на месте, ключи читаются через ILocalSecrets.
        // WP-U1: реестр 8 порталов, дефолт-цепь 6 Core (см. Wpp1-гейт ниже).
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var registry = services.GetRequiredService<Portals.PortalRegistry>();
        Assert.Equal(6, registry.ResolveChain().Count);

        var keysCheck = services.GetRequiredService<Services.IKeysCheckService>();
        Assert.IsAssignableFrom<Services.KeysCheckService>(keysCheck);

        // WP-CONFIG (RFC-004): шов ILocalSecrets встал на реализацию —
        // ConfigService (local.json в %APPDATA%). NullLocalSecrets остался
        // для фикстур (Fakes.cs).
        Assert.IsAssignableFrom<Services.ConfigService>(
            services.GetRequiredService<Services.ILocalSecrets>());
    }
}

// WP-P1 (PLAN §7): РБ-адаптеры в реестре, но вне цепи дефолта; HttpClient —
// общий синглтон с донорским таймаутом 60 с (Atlas GitHubHarvester, Task 51).
// WP-U1: + шесть Core-адаптеров коробки v1 — реестр 8 порталов, дефолт-цепь 6.
public class CompositionRootWpp1Tests
{
    [Fact]
    public void Build_RegistryHasEightPortals_CoreSixInDefaultChain()
    {
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var registry = services.GetRequiredService<Portals.PortalRegistry>();

        Assert.Equal(8, registry.Portals.Count);
        // РБ-порталы — Priority 5, вне цепи дефолта (карточки §2.11).
        Assert.Equal(new[] { "hh-by", "rabota-by" },
            registry.Portals.Take(2).Select(p => p.Descriptor.Id));
        // Дефолт-цепь: шесть Core, порядок = приоритет, при равенстве — Id ordinal.
        Assert.Equal(
            new[] { "himalayas", "nodesk", "jobicy", "wwr", "remoteok", "remotive" },
            registry.ResolveChain().Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void Build_HttpClientIsSingleton_WithDonorTimeout()
    {
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var first = services.GetRequiredService<System.Net.Http.HttpClient>();
        var second = services.GetRequiredService<System.Net.Http.HttpClient>();

        Assert.Same(first, second);
        Assert.Equal(TimeSpan.FromSeconds(60), first.Timeout);
    }
}

// Контракт «никаких тихих игноров» (LOG-CONTRACT.md): протокол — первый сервис
// графа; boot-экземпляр из Program.Main доезжает до DI без пересоздания.
public class CompositionRootLogContractTests
{
    [Fact]
    public void Build_RegistersLogService_AsSingleton()
    {
        // Явный bootLog: гейт изоляции — граф не создаёт LogService с живым
        // %APPDATA% (урок [config]-строк от тестов в протоколе владельца).
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
        // НИ-4: адаптеры обязаны получить протокол — иначе сборка графа падает
        // (ctor PortalBase требует ILogService) — это и есть CI-гейт контракта.
        using var services = CompositionRoot.Build(TestBootLogs.Fresh());

        var registry = services.GetRequiredService<Portals.PortalRegistry>();

        Assert.Equal(8, registry.Portals.Count);
    }

    // WP-U1 (решение владельца 2026-10-22): стек откликов в графе — база,
    // векторизация (WP-U2: локальный эмбеддер), PDF-экспорт, индексатор,
    // адаптация, модель настроек.
    public class CompositionRootWpu1Tests
    {
        [Fact]
        public void Build_RegistersApplicationStack()
        {
            using var services = CompositionRoot.Build(TestBootLogs.Fresh());

            var store = services.GetRequiredService<Services.IApplicationStore>();
            Assert.IsAssignableFrom<Services.ApplicationStore>(store);
            Assert.Contains(Path.Combine("ai-job-search-by", "data"), store.DbPath);

            // WP-U2: локальный эмбеддер — ключа не нужно, живость решает
            // самопробник (до него — честный оптимизм Enabled=true).
            var embeddings = services.GetRequiredService<Services.Ai.IEmbeddingService>();
            Assert.IsAssignableFrom<Services.Ai.LocalEmbeddingService>(embeddings);
            Assert.True(embeddings.Enabled);

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
