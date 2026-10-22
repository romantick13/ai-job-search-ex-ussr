using System.Text.Json.Nodes;
using AiJobSearchBy.Services;
using Xunit;

namespace AiJobSearchBy.Tests;

// WP-CONFIG: юнит-гейты ConfigService — создание из шаблона, сохранение
// ключей при merge, бэкап битого файла, лестница env -> local.json,
// ловец опечаток, Portals.* через шов ILocalSecrets. Каталог — временный
// на каждый тест (baseDirOverride); env AIJOB_* в tested-переменных
// принудительно гасится в finally — тест не зависит от машины.
public class ConfigServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly SilentLog _log = new();

    public ConfigServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aijsb-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // Temp-каталог теста чистим; гонки антивируса не роняем — лог канона
        // «никаких тихих игноров» здесь не нужен (это уборка, не бизнес-ветка).
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_dir, "local.json");

    private string JoinLogs() => string.Join("\n", _log.Logs);

    [Fact]
    public void Missing_File_Created_FromTemplate()
    {
        var svc = ConfigService.LoadOrCreate(_log, _dir);

        Assert.True(svc.WasCreated);
        Assert.True(File.Exists(svc.FilePath));
        Assert.Contains("создан из шаблона", JoinLogs());

        var root = JsonNode.Parse(File.ReadAllText(svc.FilePath))!.AsObject();
        Assert.Equal(1, root["version"]!.GetValue<int>());
        Assert.NotNull(root["Ai"]!["Llm7"]!["ApiKey"]);
        Assert.NotNull(root["Ai"]!["ModelScope"]!["ApiKey"]);
        Assert.NotNull(root["Ai"]!["SiliconFlow"]!["ApiKey"]);
        // WP-U2: секция локального эмбеддера (BaseUrl/Model вместо ключа).
        Assert.NotNull(root["Ai"]!["Embeddings"]!["BaseUrl"]);
        Assert.NotNull(root["Ai"]!["Embeddings"]!["Model"]);
        Assert.NotNull(root["Portals"]);
    }

    [Fact]
    public void Existing_Keys_Preserved_NewFields_Merged()
    {
        File.WriteAllText(ConfigPath,
            "{\"version\":1,\"Ai\":{\"Llm7\":{\"ApiKey\":\"tok-old-1234567890\"}},\"Portals\":{}}");

        var svc = ConfigService.LoadOrCreate(_log, _dir);

        Assert.False(svc.WasCreated);
        // Шаблон добавляет ровно три недостающих поля; Llm7 и Portals не тронуты.
        // (WP-U2: секция Ai.Jina выбыла — вместо неё Ai.Embeddings.)
        Assert.Equal(new[] { "Ai.ModelScope", "Ai.SiliconFlow", "Ai.Embeddings" }, svc.AddedFields);

        var text = File.ReadAllText(svc.FilePath);
        Assert.Contains("tok-old-1234567890", text); // ключ юзера сохранён
        Assert.Contains("\"Embeddings\"", text);    // новое поле долилось
        Assert.Contains("схема дополнена", JoinLogs());
    }

    [Fact]
    public void Corrupt_File_Backup_And_Rebuild()
    {
        File.WriteAllText(ConfigPath, "{ это не json ");

        var svc = ConfigService.LoadOrCreate(_log, _dir);

        var backups = Directory.GetFiles(_dir, "local.corrupt-*.json");
        Assert.Single(backups);
        Assert.Contains("{ это не json ", File.ReadAllText(backups[0])); // юзерский файл спасён в бэкап
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(svc.FilePath))!["version"]!.GetValue<int>());
        Assert.Contains("не читается", JoinLogs()); // LogError с путём бэкапа
        Assert.Contains("бэкап", JoinLogs());
    }

    [Fact]
    public void ResolveAiKey_Env_Wins_Over_File_And_Dirty_Warns()
    {
        File.WriteAllText(ConfigPath,
            "{\"version\":1,\"Ai\":{\"Llm7\":{\"ApiKey\":\"file-key-1234567890\"}},\"Portals\":{}}");
        // Кривая вставка из cmd: set VAR="..." кладёт кавычки в значение.
        Environment.SetEnvironmentVariable("AIJOB_LLM7_API_KEY", "\"env-key-1234567890\"");
        try
        {
            var svc = ConfigService.LoadOrCreate(_log, _dir);
            var (value, source) = svc.ResolveAiKey("Llm7", "AIJOB_LLM7_API_KEY");

            Assert.Equal("\"env-key-1234567890\"", value); // env выиграл
            Assert.Equal("env", source);
            Assert.True(JoinLogs().Contains("вставки"),
                "WARN про мусор не найден; лог:\n" + JoinLogs());   // WARN про мусор
            Assert.Contains("env…890", JoinLogs());             // маска показывает кавычку
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIJOB_LLM7_API_KEY", null);
        }
    }

    [Fact]
    public void ResolveAiKey_Falls_To_File_With_Mask()
    {
        // Фикстура короче 16 знаков после ms- — RepoSecretsGuardTests не
        // примет её за настоящий ключ (гвард сканирует и тестовые файлы).
        File.WriteAllText(ConfigPath,
            "{\"version\":1,\"Ai\":{\"ModelScope\":{\"ApiKey\":\"ms-testkey123\"}},\"Portals\":{}}");
        Environment.SetEnvironmentVariable("AIJOB_MODELSCOPE_API_KEY", null);
        try
        {
            var svc = ConfigService.LoadOrCreate(_log, _dir);
            var (value, source) = svc.ResolveAiKey("ModelScope", "AIJOB_MODELSCOPE_API_KEY");

            Assert.Equal("ms-testkey123", value);
            Assert.Equal("local.json", source);
            Assert.True(JoinLogs().Contains("ms-t…y123"),
                "маска файла не найдена; лог:\n" + JoinLogs());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIJOB_MODELSCOPE_API_KEY", null);
        }
    }

    [Fact]
    public void Unknown_Typo_Field_Warns()
    {
        // «Modelscope» vs «ModelScope» — классическая опечатка, JSON регистр
        // различает; ловец обязан крикнуть.
        File.WriteAllText(ConfigPath,
            "{\"version\":1,\"Ai\":{\"Modelscope\":{\"ApiKey\":\"x-1234567890123\"},\"ModelScope\":{\"ApiKey\":\"y-1234567890123\"}},\"Portals\":{}}");

        var svc = ConfigService.LoadOrCreate(_log, _dir);

        Assert.Contains("Ai.Modelscope", JoinLogs());
        // Легитимное поле не числится неизвестным.
        Assert.DoesNotContain("Ai.ModelScope", string.Join("\n",
            _log.Logs.Where(l => l.Contains("неизвестные поля"))));
    }

    [Fact]
    public void GetPortalKey_FromFile_Via_ILocalSecrets_Seam()
    {
        File.WriteAllText(ConfigPath,
            "{\"version\":1,\"Ai\":{},\"Portals\":{\"hirify\":{\"ApiKey\":\"pk-1234567890123\"}}}");

        ILocalSecrets svc = ConfigService.LoadOrCreate(_log, _dir);

        Assert.Equal("pk-1234567890123", svc.GetPortalKey("hirify"));
        Assert.Null(svc.GetPortalKey("nope")); // отсутствие — легитимный no_key
        Assert.Contains("Portals.hirify", JoinLogs());
    }

    [Fact]
    public void ResolveEmbeddings_FallsToDefaults_WithLog()
    {
        // WP-U2: ничего не задано — дефолт Ollama + qwen3-embedding:0.6b.
        Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_BASE_URL", null);
        Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_MODEL", null);

        var svc = ConfigService.LoadOrCreate(_log, _dir);
        var (baseUrl, model) = svc.ResolveEmbeddings(
            "AIJOB_EMBEDDINGS_BASE_URL", "AIJOB_EMBEDDINGS_MODEL");

        Assert.Equal(Services.Ai.LocalEmbeddingService.DefaultBaseUrl, baseUrl);
        Assert.Equal(Services.Ai.LocalEmbeddingService.DefaultModel, model);
        Assert.Contains("[config] эмбеддер:", JoinLogs());
    }

    [Fact]
    public void ResolveEmbeddings_FileThenEnvWins()
    {
        Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_BASE_URL", null);
        Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_MODEL", null);
        try
        {
            // 1) Значения из окна настроек (UpdateEmbeddings) читаются при перезагрузке.
            var writer = ConfigService.LoadOrCreate(_log, _dir);
            writer.UpdateEmbeddings("http://127.0.0.1:1234/v1", "qwen3-embedding:0.6b");
            var svc = ConfigService.LoadOrCreate(_log, _dir);
            var (baseUrl, model) = svc.ResolveEmbeddings(
                "AIJOB_EMBEDDINGS_BASE_URL", "AIJOB_EMBEDDINGS_MODEL");
            Assert.Equal("http://127.0.0.1:1234/v1", baseUrl);
            Assert.Equal("qwen3-embedding:0.6b", model);

            // 2) env — override поверх файла (RFC-004 §3).
            Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_BASE_URL", "http://127.0.0.1:8080/v1");
            var (envUrl, _) = svc.ResolveEmbeddings(
                "AIJOB_EMBEDDINGS_BASE_URL", "AIJOB_EMBEDDINGS_MODEL");
            Assert.Equal("http://127.0.0.1:8080/v1", envUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_BASE_URL", null);
            Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_MODEL", null);
        }
    }

    [Fact]
    public void UpdateEmbeddings_EmptyValue_FallsBackToDefaults()
    {
        Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_BASE_URL", null);
        Environment.SetEnvironmentVariable("AIJOB_EMBEDDINGS_MODEL", null);
        var writer = ConfigService.LoadOrCreate(_log, _dir);
        writer.UpdateEmbeddings("http://127.0.0.1:9999/v1", "старая-модель");

        // Очистка полей в окне настроек = «вернуть дефолт».
        writer.UpdateEmbeddings("  ", "");
        var reloaded = ConfigService.LoadOrCreate(_log, _dir);
        var (baseUrl, model) = reloaded.ResolveEmbeddings(
            "AIJOB_EMBEDDINGS_BASE_URL", "AIJOB_EMBEDDINGS_MODEL");

        Assert.Equal(Services.Ai.LocalEmbeddingService.DefaultBaseUrl, baseUrl);
        Assert.Equal(Services.Ai.LocalEmbeddingService.DefaultModel, model);
    }
}
