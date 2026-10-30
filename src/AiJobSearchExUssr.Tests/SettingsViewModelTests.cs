using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Tests.Logging;
using AiJobSearchExUssr.ViewModels;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: SettingsViewModel — модальное окно настроек: ключи из
// PeekAiKey, запись через UpdateAiKey (local.json), папка откликов в
// SettingsKV, CloseRequested для окна.
// LIFECYCLE: temp-каталог конфига и temp-база на каждый тест.
public class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private SettingsViewModel Make(LogService log, out ConfigService config, out ApplicationStore store)
    {
        config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        store = new ApplicationStore(Path.Combine(_dir, "test.db"), log);
        store.Initialize();
        return new SettingsViewModel(config, store, log);
    }

    [Fact]
    public void Load_FillsProviderRows_AndDefaultOutbox()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = Make(log, out _, out _);

        // OpenRouter/OrcaRouter вернулись в цепь — пять встроенных строк.
        Assert.Equal(
            new[] { "Llm7", "ModelScope", "SiliconFlow", "OpenRouter", "OrcaRouter" },
            vm.Rows.Select(r => r.ProviderId));
        // r21: emb-строки окна вырезаны вместе с векторизацией.
        Assert.Equal(ApplicationStore.DefaultOutboxDir, vm.OutboxPath);
    }

    [Fact]
    public void Load_ShowsExistingKeys_FromConfig()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = Make(log, out var config, out _);
        config.UpdateAiKey("ModelScope", "ms_key_loaded");

        var vm2 = new SettingsViewModel(config, new ApplicationStore(
            Path.Combine(_dir, "test.db"), log), log);

        Assert.Equal("ms_key_loaded", vm2.Rows.Single(r => r.ProviderId == "ModelScope").ApiKey);
    }

    [Fact]
    public void Save_WritesKeysAndOutbox_FiresCloseRequested()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = Make(log, out var config, out var store);
        vm.Rows.Single(r => r.ProviderId == "ModelScope").ApiKey = "ms_key_saved_0001";
        vm.OutboxPath = @"C:\Отклики";

        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        Assert.Equal("ms_key_saved_0001", reloaded.PeekAiKey("ModelScope"));
        Assert.Equal(@"C:\Отклики", store.GetSetting("outbox.path"));
        // r21: emb-поля окна настроек вырезаны вместе с векторизацией.
    }

    [Fact]
    public void Save_EmptyOutboxPath_FallsBackToDefault()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = Make(log, out _, out var store);
        vm.OutboxPath = "   ";

        vm.SaveCommand.Execute(null);

        Assert.Equal(ApplicationStore.DefaultOutboxDir, store.GetSetting("outbox.path"));
    }

    [Fact]
    public void Done_FiresCloseRequested_WithoutWriting()
    {
        // Урок: ResolveAiKey читает AIJOB_LLM7_API_KEY — чужое окно
        // ConfigServiceTests (параллельный класс) давало env-значение. Гвард
        // гасит переменную на время теста.
        using (TestEnvGuard.Scope("AIJOB_LLM7_API_KEY"))
        {
            var log = new LogService(_dir, echoToConsole: false);
            var vm = Make(log, out var config, out _);
            vm.Rows.Single(r => r.ProviderId == "Llm7").ApiKey = "не_сохранять";

            var closed = false;
            vm.CloseRequested += () => closed = true;
            vm.DoneCommand.Execute(null);

            Assert.True(closed);
            // «Закрыть без сохранения»: ключ не записан (в шаблоне он пуст — "") и
            // в файле не появился; лестница env -> local.json не находит значения.
            Assert.Equal("", config.PeekAiKey("Llm7"));
            Assert.Null(config.ResolveAiKey("Llm7", "AIJOB_LLM7_API_KEY").Value);
        }
    }
}
