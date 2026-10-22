using AiJobSearchBy.Services;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: WP-U1 — окно настроек пишет ключи обратно в local.json
// (ConfigService.UpdateAiKey): атомарная запись, сохранение остальных полей,
// маска в протоколе, честный WARN «перезапусти приложение».
// LIFECYCLE: каждый тест — свой temp-каталог конфига (RFC-004 §2.3).
public class ConfigUpdateKeyTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    [Fact]
    public void UpdateAiKey_WritesFile_ReloadSeesValue()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);

        config.UpdateAiKey("ModelScope", "ms_secret_token");

        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        Assert.Equal("ms_secret_token", reloaded.PeekAiKey("ModelScope"));
        // Событие протокола с маской, не сырым ключом (RFC-004).
        Assert.Contains(log.Logs, line => line.Contains("Ai.ModelScope.ApiKey сохранён") && line.Contains("ms_s…oken"));
    }

    [Fact]
    public void UpdateAiKey_PreservesOtherFields()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        config.UpdateAiKey("Llm7", "llm7_key_0001");

        config.UpdateAiKey("ModelScope", "ms_key_0002");

        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        Assert.Equal("llm7_key_0001", reloaded.PeekAiKey("Llm7"));
        Assert.Equal("ms_key_0002", reloaded.PeekAiKey("ModelScope"));
        // Portals-секция не задета.
        Assert.Null(reloaded.GetPortalKey("hh-by"));
    }

    [Fact]
    public void UpdateAiKey_EmptyValue_ClearsKey()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        config.UpdateAiKey("SiliconFlow", "sf_key_0099");

        config.UpdateAiKey("SiliconFlow", "  ");

        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        // Пустая строка = ключа нет (лестница env -> local.json -> анонимно).
        Assert.Equal("", reloaded.PeekAiKey("SiliconFlow"));
        Assert.Null(reloaded.ResolveAiKey("SiliconFlow", "AIJOB_SILICONFLOW_API_KEY").Value);
    }

    [Fact]
    public void UpdateAiKey_TrimsWhitespace()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);

        // Урок 401-саги SiliconFlow: мусор по краям значения срезается при записи.
        config.UpdateAiKey("Llm7", "  llm7_key_with_spaces  ");

        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        Assert.Equal("llm7_key_with_spaces", reloaded.PeekAiKey("Llm7"));
    }

    [Fact]
    public void UpdateEmbeddings_WritesFile_ReloadSeesValue()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);

        // WP-U2: эмбеддер живёт в Ai.Embeddings (BaseUrl/Model, без ключа).
        config.UpdateEmbeddings("http://127.0.0.1:1234/v1", "  emb-model-01  ");

        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        var (baseUrl, model) = reloaded.PeekEmbeddings();
        Assert.Equal("http://127.0.0.1:1234/v1", baseUrl);
        Assert.Equal("emb-model-01", model); // мусор по краям срезан
        Assert.Contains(log.Logs, line => line.Contains("эмбеддер сохранён из окна настроек"));
    }

    [Fact]
    public void UpdateEmbeddings_PreservesKeys_AndViceVersa()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);

        config.UpdateAiKey("Llm7", "llm7_key_keep_0001");
        config.UpdateEmbeddings("http://127.0.0.1:1234/v1", "emb-model-02");
        config.UpdateAiKey("ModelScope", "ms_key_keep_0002");

        var reloaded = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        Assert.Equal("llm7_key_keep_0001", reloaded.PeekAiKey("Llm7"));
        Assert.Equal("ms_key_keep_0002", reloaded.PeekAiKey("ModelScope"));
        var (baseUrl, model) = reloaded.PeekEmbeddings();
        Assert.Equal("http://127.0.0.1:1234/v1", baseUrl);
        Assert.Equal("emb-model-02", model);
        // Portals-секция не задета.
        Assert.Null(reloaded.GetPortalKey("hh-by"));
    }
}
