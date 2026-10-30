using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Services.Ai;
using AiJobSearchExUssr.Tests.Logging;
using Xunit;

namespace AiJobSearchExUssr.Tests.Ai;

// ============================================================================
// Тесты агрегаторного режима:
// список бесплатных -> наилучшая -> отказ = следующая -> «липучесть» в файл.
// StickyModelLedger: запись/чтение/сброс. Free-каталог в провайдере:
// сортировка по контексту, 429 = следующая модель, липучесть первой на
// следующем запросе, явная модель юзера отключает автоматику. OverrideModel:
// бутстрап lm-studio подменяет модель в payload. Декоратор: бутстрап один раз
// на процесс, повторные запросы идут напрямую.
// ============================================================================

public sealed class StickyFreeCatalogTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir);

    private const string ChatBody = """
        {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}
        """;

// Каталог: платные + free на 8k + free на 128k — наилучшая = больший контекст.
    private const string CatalogBody = """
        {"data":[
          {"id":"vendor/big-paid","pricing":{"prompt":"0.001"},"context_length":65536},
          {"id":"vendor/free-small:free","pricing":{"prompt":"0"},"context_length":8192},
          {"id":"vendor/free-big:free","pricing":{"prompt":"0"},"context_length":131072},
          {"id":"vendor/free-zero-priced","pricing":{"prompt":"0"},"context_length":16384}
        ]}
        """;

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TooMany() => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent("rate limited")
    };

    // / <summary>Хендлер: GET models -> каталог, POST chat -> по счётчику вызовов.</summary>
    private sealed class CatalogHandler : HttpMessageHandler
    {
        private int _chatCalls;
        public List<string> ChatModels = new();

        public Func<int, HttpStatusCode>? ChatStatus { get; set; } // null = всегда 200

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Method == HttpMethod.Get)
                return Ok(CatalogBody);

            Interlocked.Increment(ref _chatCalls);
            if (r.Content is not null)
            {
                var body = await r.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                ChatModels.Add(doc.RootElement.GetProperty("model").GetString() ?? "");
            }
            var code = ChatStatus?.Invoke(_chatCalls) ?? HttpStatusCode.OK;
            return code == HttpStatusCode.OK
                ? Ok(ChatBody)
                : new HttpResponseMessage(code) { Content = new StringContent("limited") };
        }
    }

    private OpenAiCompatibleProvider OpenRouterLike(LogService log, HttpMessageHandler h, string? sticky = null,
        string? model = null) =>
        new(log, "openrouter",
            OpenAiCompatibleProvider.OpenRouterBaseUrl,
            model: model,
            apiKey: "test-key",
            modelEnvVar: null,
            qualityRank: 5,
            requireApiKey: true,
            freeCatalog: true,
            stickyFilePath: sticky,
            httpClient: new HttpClient(h) { BaseAddress = new Uri(OpenAiCompatibleProvider.OpenRouterBaseUrl) });

    // --- StickyModelLedger ---

    [Fact]
    public void Sticky_Save_Load_RoundTrip()
    {
        var file = Path.Combine(_dir, "state", "openrouter-sticky.json");
        var log = Log();
        new StickyModelLedger(file, log).Save("vendor/free-big:free", "ok");

        var second = new StickyModelLedger(file, log);
        Assert.True(second.IsFresh);
        Assert.Equal("vendor/free-big:free", second.Model);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Sticky_Clear_Removes_Record()
    {
        var file = Path.Combine(_dir, "state", "openrouter-sticky.json");
        var log = Log();
        var ledger = new StickyModelLedger(file, log);
        ledger.Save("vendor/free-big:free", "ok");
        ledger.Clear("429 на vendor/free-big:free");

        Assert.False(ledger.IsFresh);
        Assert.False(File.Exists(file)); // файл удалён — перебор начнётся заново
    }

    // --- Free-каталог в провайдере ---

    [Fact]
    public async Task FreeCatalog_Picks_Best_By_Context_And_Saves_Sticky()
    {
        var stickyFile = Path.Combine(_dir, "state", "openrouter-sticky.json");
        var h = new CatalogHandler();
        var log = Log();
        var p = OpenRouterLike(log, h, sticky: stickyFile);

        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        // Наилучшая free = больший context_length (131072), платные мимо.
        Assert.Equal("vendor/free-big:free", r.Model);
        Assert.Equal(new[] { "vendor/free-big:free" }, h.ChatModels);
        // Липучесть записана.
        var ledger = new StickyModelLedger(stickyFile, log);
        Assert.Equal("vendor/free-big:free", ledger.Model);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("запрос списка бесплатных моделей"));
    }

    [Fact]
    public async Task FreeCatalog_429_Tries_Next_Free_Model()
    {
        var h = new CatalogHandler { ChatStatus = call => call == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK };
        var log = Log();
        var p = OpenRouterLike(log, h, sticky: Path.Combine(_dir, "state", "s1.json"));

        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        // Порядок по контексту: big(131k) -> zero-priced(16k) -> small(8k);
        // 429 на первой — вторая попытка берёт zero-priced.
        Assert.Equal("vendor/free-zero-priced", r.Model);
        Assert.Equal(new[] { "vendor/free-big:free", "vendor/free-zero-priced" }, h.ChatModels);
    }

    [Fact]
    public async Task FreeCatalog_Sticky_Goes_First_On_Next_Run()
    {
        var stickyFile = Path.Combine(_dir, "state", "openrouter-sticky.json");
        var log = Log();
        var h1 = new CatalogHandler();
        await OpenRouterLike(log, h1, sticky: stickyFile)
            .CompleteAsync(new AiChatRequest("s", "u", 256)); // липучесть -> free-big

        // Новый процесс-инстанс: липучесть из файла стартует первой.
        var h2 = new CatalogHandler();
        var r = await OpenRouterLike(log, h2, sticky: stickyFile)
            .CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("vendor/free-big:free", r.Model);
        Assert.Equal(new[] { "vendor/free-big:free" }, h2.ChatModels);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("липучесть"));
    }

    [Fact]
    public async Task FreeCatalog_Exhausted_Is_Busy()
    {
        var h = new CatalogHandler { ChatStatus = _ => HttpStatusCode.TooManyRequests };
        var log = Log();
        var p = OpenRouterLike(log, h, sticky: Path.Combine(_dir, "state", "s2.json"));

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("бесплатные модели исчерпаны", ex.Message);
        Assert.Equal(3, h.ChatModels.Count); // перебор ограничен тремя попытками
    }

    [Fact]
    public async Task Explicit_Model_Disables_Free_Catalog()
    {
        var h = new CatalogHandler();
        var log = Log();
        // Юзер зафиксировал модель — автоматика free-каталога не активна.
        var p = OpenRouterLike(log, h, sticky: Path.Combine(_dir, "state", "s3.json"),
            model: "vendor/my-fixed:free");

        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("vendor/my-fixed:free", r.Model);
        Assert.Single(h.ChatModels);
    }

    // --- OverrideModel (бутстрап lm-studio) ---

    [Fact]
    public async Task OverrideModel_Changes_Payload_And_Card_Model()
    {
        var h = new CatalogHandler();
        var log = Log();
        var p = new OpenAiCompatibleProvider(log, "lm-studio",
            OpenAiCompatibleProvider.LmStudioBaseUrl,
            model: "local-model", apiKey: null, requireApiKey: false,
            httpClient: new HttpClient(h) { BaseAddress = new Uri(OpenAiCompatibleProvider.LmStudioBaseUrl) });

        p.OverrideModel("qwen3-vl-8b-instruct");
        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("qwen3-vl-8b-instruct", r.Model); // в результате
        Assert.Equal("qwen3-vl-8b-instruct", h.ChatModels[0]); // и в payload
        Assert.Equal("qwen3-vl-8b-instruct", p.Model); // и на карточке дашборда
    }

    // --- Декоратор LmStudioBootstrappingProvider ---

    [Fact]
    public async Task Decorator_Bootstraps_Once_Then_Passes_Through()
    {
        const string PsTableOne = "IDENTIFIER\nllama";
        // Счётчик daemon up: бутстрап должен пройти ОДИН раз на процесс;
        // (ps зовётся дважды — проверка и перепроверка после load, это норма).
        var daemonUps = 0;
        var psCount = 0;
        string? Runner(string args)
        {
            if (args == "daemon up") { daemonUps++; return "The daemon is already running (PID: 6740)."; }
            if (args == "ps") return ++psCount == 1 ? "No models are currently loaded." : PsTableOne;
            if (args == "ls") return "LLM\nllama                     8B      Llama    5.73 GB    Local";
            if (args.StartsWith("load ", StringComparison.Ordinal))
                return "Model loaded successfully in 5.0s.\n" +
                       "To use the model in the API/SDK, use the identifier \"llama\".";
            return null;
        }

        var log = Log();
        var inner = new OpenAiCompatibleProvider(log, "lm-studio",
            OpenAiCompatibleProvider.LmStudioBaseUrl,
            model: "local-model", apiKey: null, requireApiKey: false,
            httpClient: new HttpClient(new CatalogHandler())
            {
                BaseAddress = new Uri(OpenAiCompatibleProvider.LmStudioBaseUrl)
            });
        var lmsStub = Path.Combine(_dir, "fake-lms.exe");
        File.WriteAllText(lmsStub, "");
        var cli = new LmStudioCli(log, lmsPath: lmsStub, runner: Runner, httpClientFactory: () =>
            new HttpClient(new CatalogHandler())
            {
                BaseAddress = new Uri(OpenAiCompatibleProvider.LmStudioBaseUrl)
            });
        var deco = new LmStudioBootstrappingProvider(inner, cli, log);

        var r1 = await deco.CompleteAsync(new AiChatRequest("s", "u", 256));
        var r2 = await deco.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("llama", r1.Model); // модель выбрана бутстрапом из ls
        Assert.Equal(1, daemonUps); // бутстрап один: второй запрос прошёл напрямую (ps был 2 раза — норма)
        Assert.Equal("lm-studio", deco.Name);
        Assert.Equal(deco.QualityRank, inner.QualityRank);
    }

    [Fact]
    public async Task Decorator_Bootstrap_Fail_Is_Busy_Fallback()
    {
        var log = Log();
        var inner = new OpenAiCompatibleProvider(log, "lm-studio",
            OpenAiCompatibleProvider.LmStudioBaseUrl,
            model: "local-model", apiKey: null, requireApiKey: false,
            httpClient: new HttpClient(new CatalogHandler())
            {
                BaseAddress = new Uri(OpenAiCompatibleProvider.LmStudioBaseUrl)
            });
        var cli = new LmStudioCli(log,
            lmsPath: Path.Combine(_dir, "no-such-lms.exe"),
            runner: _ => throw new InvalidOperationException("не должен вызываться"));
        var deco = new LmStudioBootstrappingProvider(inner, cli, log);

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => deco.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("lm-studio недоступна", ex.Message);
        // Карточка снялась с внутреннего провайдера (Model/BaseLabel живые).
        Assert.Equal("lm-studio", deco.Inner.Name);
    }
}
