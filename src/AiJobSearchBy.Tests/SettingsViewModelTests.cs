using AiJobSearchBy.Services;
using AiJobSearchBy.Tests.Logging;
using AiJobSearchBy.ViewModels;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: SettingsViewModel (WP-U1) — модальное окно настроек: ключи из
// PeekAiKey, запись через UpdateAiKey (local.json), папка откликов в
// SettingsKV, CloseRequested для окна.
// LIFECYCLE: temp-каталог конфига и temp-база на каждый тест (RFC-004 §2.3).
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
    public void Load_FillsThreeProviderRows_EmbeddingsDefaults_AndDefaultOutbox()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = Make(log, out _, out _);

        Assert.Equal(
            new[] { "Llm7", "ModelScope", "SiliconFlow" },
            vm.Rows.Select(r => r.ProviderId));
        // WP-U2: эмбеддер — из шаблона долились дефолты Ollama.
        Assert.Equal(Services.Ai.LocalEmbeddingService.DefaultBaseUrl, vm.EmbeddingsBaseUrl);
        Assert.Equal(Services.Ai.LocalEmbeddingService.DefaultModel, vm.EmbeddingsModel);
        Assert.Equal(ApplicationStore.DefaultOutboxDir, vm.OutboxPath);
    }

    [Fact]
    public void Load_ShowsExistingKeys_FromConfig()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = Make(log, out var config, out _);
        config.UpdateAiKey("ModelScope", "ms_key_loaded");
        config.UpdateEmbeddings("http://127.0.0.1:8080/v1", "emb-model-loaded");

        var vm2 = new SettingsViewModel(config, new ApplicationStore(
            Path.Combine(_dir, "test.db"), log), log);

        Assert.Equal("ms_key_loaded", vm2.Rows.Single(r => r.ProviderId == "ModelScope").ApiKey);
        Assert.Equal("http://127.0.0.1:8080/v1", vm2.EmbeddingsBaseUrl);
        Assert.Equal("emb-model-loaded", vm2.EmbeddingsModel);
    }

    [Fact]
    public void Save_WritesKeysEmbeddingsAndOutbox_FiresCloseRequested()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var vm = Make(log, out var config, out var store);
        vm.Rows.Single(r => r.ProviderId == "ModelScope").ApiKey = "ms_key_saved_0001";
        vm.EmbeddingsBaseUrl = "http://127.0.0.1:1234/v1";
        vm.EmbeddingsModel = "emb-model-saved";
        vm.OutboxPath = @"C:\Отклики";

        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        Assert.Equal("ms_key_saved_0001", reloaded.PeekAiKey("ModelScope"));
        var (baseUrl, model) = reloaded.PeekEmbeddings();
        Assert.Equal("http://127.0.0.1:1234/v1", baseUrl);
        Assert.Equal("emb-model-saved", model);
        Assert.Equal(@"C:\Отклики", store.GetSetting("outbox.path"));
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
