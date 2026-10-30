using System.Text.Json.Nodes;
using AiJobSearchExUssr.Services;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// юнит-гейты ConfigService — создание из шаблона, сохранение
// ключей при merge, бэкап битого файла, лестница env -> local.json,
// ловец опечаток, Portals.* через шов ILocalSecrets. Каталог — временный
// на каждый тест (baseDirOverride); env AIJOB_* в tested-переменных гасится
// на время теста; окно AIJOB_LLM7_API_KEY — под гвардом TestEnvGuard (урок:
// параллельные классы ловили это окно конструктором Llm7Provider).
public class ConfigServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly SilentLog _log = new();

    public ConfigServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aijs-exu-config-tests", Guid.NewGuid().ToString("N"));
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
        // Решение: OpenRouter/OrcaRouter вернулись в цепь.
        Assert.NotNull(root["Ai"]!["OpenRouter"]!["ApiKey"]);
        Assert.NotNull(root["Ai"]!["OrcaRouter"]!["ApiKey"]);
        // r21: секция Ai.Embeddings вырезана вместе с векторизацией.
        // секция кастомных звеньев цепи.
        Assert.NotNull(root["Chain"]!["Custom"]);
        Assert.NotNull(root["Portals"]);
    }

    [Fact]
    public void Existing_Keys_Preserved_NewFields_Merged()
    {
        File.WriteAllText(ConfigPath,
            "{\"version\":1,\"Ai\":{\"Llm7\":{\"ApiKey\":\"tok-old-1234567890\"}},\"Portals\":{}}");

        var svc = ConfigService.LoadOrCreate(_log, _dir);

        Assert.False(svc.WasCreated);
        // Шаблон добавляет недостающие поля; Llm7 и Portals не тронуты.
        // (секция Ai.Jina выбыла. Долились секции ключей и Chain —
        // кастомные звенья цепи, целиком, без рекурсии внутрь: пустой массив
        // Custom клонируется как есть. Долились Ai.OpenRouter и
        // Ai.OrcaRouter — вернулись в дефолтную цепь. r21: Ai.Embeddings
        // вырезана вместе с векторизацией и больше не доливается.)
        Assert.Equal(new[] { "Ai.ModelScope", "Ai.SiliconFlow", "Ai.OpenRouter", "Ai.OrcaRouter", "Chain" }, svc.AddedFields);

        var text = File.ReadAllText(svc.FilePath);
        Assert.Contains("tok-old-1234567890", text); // ключ юзера сохранён
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
        // Урок: окно переменной — под гвардом: раньше finally гасил
        // её в null, а xUnit в это окно загонял параллельные классы (конструктор
        // Llm7Provider(apiKey: null) ловил чужой ключ). Гвард сериализует окно
        // и возвращает переменную машины как была.
        using (TestEnvGuard.Scope("AIJOB_LLM7_API_KEY"))
        {
            Environment.SetEnvironmentVariable("AIJOB_LLM7_API_KEY", "\"env-key-1234567890\"");

            var svc = ConfigService.LoadOrCreate(_log, _dir);
            var (value, source) = svc.ResolveAiKey("Llm7", "AIJOB_LLM7_API_KEY");

            Assert.Equal("\"env-key-1234567890\"", value); // env выиграл
            Assert.Equal("env", source);
            Assert.True(JoinLogs().Contains("вставки"),
                "WARN про мусор не найден; лог:\n" + JoinLogs());   // WARN про мусор
            Assert.Contains("env…890", JoinLogs());             // маска показывает кавычку
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

    // r21: ResolveEmbeddings_*/UpdateEmbeddings_* вырезаны вместе с векторизацией.
}
