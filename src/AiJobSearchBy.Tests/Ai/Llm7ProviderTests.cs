using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests.Ai;

// ============================================================================
// [НАШЕ] Тесты Llm7Provider: разбор ответа, анонимный режим, 429 → Busy
// (фолбэк цепи), прочие HTTP-сбои кричат, локальный бюджетный регистр
// (исчерпание до запроса + персистентность между процессами).
// ============================================================================

// FLOW: тесты -> Llm7Provider поверх фейкового HttpMessageHandler (без сети) -> разбор/лимиты/регистр
// LIFECYCLE: каждый тест — свой tmp-каталог протокола и свой StubHandler
// STATE: StubHandler фиксирует вызовы/заголовки; эхо консоли выключено
// THREAD: однопоточные тесты; один конкурентный сценарий — фейл-фаст регистра
public sealed class Llm7ProviderTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir);

    private const string ChatBody = """
        {"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"готовое письмо"},"finish_reason":"stop"}],"usage":{"prompt_tokens":120,"completion_tokens":80,"total_tokens":200}}
        """;

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpClient Http(StubHandler h) => new(h)
    {
        BaseAddress = new Uri(Llm7Provider.DefaultBaseUrl)
    };

    [Fact]
    public async Task Success_Parses_Usage_And_Updates_Ledger()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = new Llm7Provider(log, Http(h), apiKey: "test-key", budgetFilePath: null);

        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("готовое письмо", r.Content);
        Assert.Equal(120, r.Usage.PromptTokens);
        Assert.Equal(80, r.Usage.CompletionTokens);
        Assert.Equal(Llm7Provider.DefaultDailyTokenCap - 200, p.RemainingDailyTokens()); // регистр +200
        Assert.Contains("[llm7] OK", log.Logs.Cast<string>().Last(x => x.Contains("[llm7]")));
        Assert.Single(h.RequestsTo("chat/completions"));
    }

    [Fact]
    public void DefaultModel_Is_Live_Turbo_Model()
    {
        // Гейт ротации дефолта (урок 2026-10-21: DeepSeek-V4-Flash-0731 протух
        // между снятием каталога и живым прогоном; владелец обходил через
        // AIJOB_LLM7_MODEL=gpt-oss:20b). Если модель снова ротанёт — тест
        // падает громко, дефолт правим осознанно, не молча.
        Assert.Equal("gpt-oss:20b", Llm7Provider.DefaultModel);
    }

    [Fact]
    public async Task Anonymous_Mode_Uses_Placeholder_And_Warns()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = new Llm7Provider(log, Http(h), apiKey: null, budgetFilePath: null);

        Assert.True(p.IsAnonymous);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u26A0") && x.Contains("AIJOB_LLM7_API_KEY"));

        await p.CompleteAsync(new AiChatRequest("s", "u", 256));
        Assert.Equal("Bearer unused", h.LastAuthorization); // заглушка анонимного доступа
    }

    [Fact]
    public async Task TooManyRequests_Means_Busy_With_RetryAfter()
    {
        var h = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited")
            };
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return resp;
        });
        var log = Log();
        var p = new Llm7Provider(log, Http(h), apiKey: "test-key", budgetFilePath: null);

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("429", ex.Message);
        Assert.Contains("Retry-After: 30", ex.Message);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u26A0") && x.Contains("фолбэк"));
    }

    [Fact]
    public async Task Server_Error_Is_Loud_Not_Silent()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        var log = Log();
        var p = new Llm7Provider(log, Http(h), apiKey: "test-key", budgetFilePath: null);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u274C") && x.Contains("HTTP 500"));
    }

    [Fact]
    public async Task Exhausted_Local_Ledger_Fails_Fast_Without_Http()
    {
        // Капа 100, один успешный вызов списывает 200 → следующий не ходит в сеть.
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = new Llm7Provider(log, Http(h), apiKey: "test-key",
                                 dailyTokenCap: 100, budgetFilePath: null);

        await p.CompleteAsync(new AiChatRequest("s", "u", 256)); // 200 ток → исчерпан
        var callsBefore = h.TotalCalls;

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));
        Assert.Contains("бюджет", ex.Message);
        Assert.Equal(callsBefore, h.TotalCalls); // HTTP не вызывался — фейл-фаст
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u26A0") && x.Contains("регистр"));
    }

    [Fact]
    public async Task Ledger_Persists_Across_Instances_Same_Day()
    {
        var file = Path.Combine(_dir, "state", "llm7-budget.json");
        var log = Log();
        var h = new StubHandler(_ => Ok(ChatBody));

        var first = new Llm7Provider(log, Http(h), apiKey: "test-key", budgetFilePath: file);
        await first.CompleteAsync(new AiChatRequest("s", "u", 256)); // +200 ток

        var second = new Llm7Provider(log, Http(h), apiKey: "test-key", budgetFilePath: file);
        Assert.Equal(Llm7Provider.DefaultDailyTokenCap - 200, second.RemainingDailyTokens());
    }

    [Fact]
    public async Task Probe_Models_Reports_Catalog_And_Missing_Model()
    {
        // Фикстура = снимок живого каталога 2026-10-21; gpt-oss:20b обязан быть:
        // это дефолт (гейт DefaultModel_Is_Live_Turbo_Model), проба ищет его первой
        var body = """
            {"object":"list","data":[{"id":"DeepSeek-V4-Flash-0731","tier":"turbo"},{"id":"glm-5.3-flash","tier":"turbo"},{"id":"gpt-oss:20b","tier":"turbo"},{"id":"gpt-5.4","tier":"pro"}]}
            """;
        var h = new StubHandler(_ => Ok(body));
        var log = Log();
        var p = new Llm7Provider(log, Http(h), apiKey: "test-key", budgetFilePath: null);

        var found = await p.ProbeModelsAsync();

        Assert.True(found);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("turbo-тир (free)") && x.Contains("DeepSeek-V4-Flash-0731"));

        var p2 = new Llm7Provider(log, Http(h), apiKey: "test-key", model: "no-such-model", budgetFilePath: null);
        Assert.False(await p2.ProbeModelsAsync());
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u274C") && x.Contains("нет в каталоге"));
    }

    /// <summary>Фейковый обработчик: без сети, фиксирует запросы.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public int TotalCalls;
        public readonly List<string> RequestUris = new();
        public string? LastAuthorization;

        public System.Collections.Generic.IEnumerable<string> RequestsTo(string path) =>
            RequestUris.Where(u => u.Contains(path));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref TotalCalls);
            RequestUris.Add(request.RequestUri?.ToString() ?? "");
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(_responder(request));
        }
    }
}
