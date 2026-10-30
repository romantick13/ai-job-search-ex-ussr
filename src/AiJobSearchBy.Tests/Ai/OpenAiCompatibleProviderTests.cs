using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests.Ai;

// ============================================================================
// Тесты OpenAiCompatibleProvider (ModelScope / SiliconFlow / локальные):
// разбор ответа, отсутствие ключа = Busy без HTTP (фейл-фаст), локальный
// профиль без ключа работает, 429 → Busy (фолбэк цепи), 500 кричит, сервер
// не поднят = Busy, исчерпание капы запросов — фейл-фаст, токен-регистр,
// сериализация одновременных запросов (ModelScope: 1 одновременный),
// enable_thinking=false уходит только у профилей с disableThinking (504-урок
// ModelScope), по умолчанию поле отсутствует вовсе.
// ============================================================================

// FLOW: тесты -> OpenAiCompatibleProvider поверх фейкового HttpMessageHandler (без сети)
// -> разбор/лимиты/регистры/сериализация
// LIFECYCLE: каждый тест — свой tmp-каталог протокола и свой StubHandler
// STATE: StubHandler фиксирует вызовы/заголовки/параллельность; эхо консоли выключено
// THREAD: однопоточные сценарии; один конкурентный тест — сериализация ModelScope
public sealed class OpenAiCompatibleProviderTests : IDisposable
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

    private static HttpClient Http(HttpMessageHandler h) => new(h)
    {
        BaseAddress = new Uri(OpenAiCompatibleProvider.ModelScopeBaseUrl)
    };

    // Облачный профиль «как ModelScope»: ключ обязателен, капа 2000/сутки, 1 одновременный.
    private static OpenAiCompatibleProvider ModelScopeLike(
        LogService log, HttpMessageHandler h, string? apiKey = "test-key",
        long? reqCap = OpenAiCompatibleProvider.ModelScopeDailyRequestCap,
        string? requestLedger = null, bool disableThinking = false)
        => new(log, "modelscope",
               OpenAiCompatibleProvider.ModelScopeBaseUrl,
               model: "Qwen/Qwen3-32B",
               apiKey: apiKey,
               qualityRank: 2,
               dailyRequestCap: reqCap,
               maxConcurrent: 1,
               requireApiKey: true,
               requestLedgerPath: requestLedger,
               disableThinking: disableThinking,
               signupHint: "modelscope.ai",
               httpClient: Http(h));

    [Fact]
    public async Task Success_Parses_Usage_And_Counts_Request()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = ModelScopeLike(log, h, requestLedger: null);

        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("готовое письмо", r.Content);
        Assert.Equal(120, r.Usage.PromptTokens);
        Assert.Equal(80, r.Usage.CompletionTokens);
        Assert.Equal("modelscope", r.ProviderName);
        Assert.Single(h.RequestsTo("chat/completions"));
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("[modelscope] OK"));
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("запросов сегодня: 1/")); // культур-независимый префикс
    }

    [Fact]
    public async Task Missing_Key_Fails_Fast_Without_Http()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = ModelScopeLike(log, h, apiKey: null);

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("ключ не задан", ex.Message);
        Assert.Equal(0, h.TotalCalls); // HTTP не вызывался — фейл-фаст
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u26A0") && x.Contains("modelscope.ai"));
    }

    [Fact]
    public async Task Local_Profile_Works_Without_Key()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        // Локальный профиль цепи — lm-studio (ollama-local убран из дефолта).
        var p = new OpenAiCompatibleProvider(log, "lm-studio",
            OpenAiCompatibleProvider.LmStudioBaseUrl,
            model: "local-model", apiKey: null, requireApiKey: false,
            httpClient: new HttpClient(h) { BaseAddress = new Uri(OpenAiCompatibleProvider.LmStudioBaseUrl) });

        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("готовое письмо", r.Content);
        Assert.Null(h.LastAuthorization); // локально — ключ не шлём вовсе
    }

    [Fact]
    public async Task TooManyRequests_Is_Busy_With_RetryAfter()
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
        var p = ModelScopeLike(log, h);

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("429", ex.Message);
        Assert.Contains("Retry-After: 30", ex.Message);
        Assert.Contains("200", ex.Message); // напоминание про ≤200 запр/сутки на модель
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
        var p = ModelScopeLike(log, h);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u274C") && x.Contains("HTTP 500"));
    }

    [Fact]
    public async Task Unauthorized_Explains_Token_Rejected_By_Provider()
    {
        // 401 = ключ ЕСТЬ (иначе Busy до запроса),
        // его отклонила сторона провайдера — в протоколе честная причина и
        // путь решения, исключение наружу — цепь теперь перебирает остальных.
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":{"message":"Authentication failed, please make sure that a valid ModelScope token is supplied."}}""")
        });
        var log = Log();
        var p = ModelScopeLike(log, h);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("401", ex.Message);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u274C")
            && x.Contains("HTTP 401")
            && x.Contains("ключ отклонён на стороне провайдера")
            && x.Contains("выпустить новый в кабинете провайдера")
            && x.Contains("перебираются сами"));
    }

    [Fact]
    public async Task Local_Server_Down_Is_Busy_Not_Crash()
    {
        var h = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var log = Log();
        // ollama-local убран из дефолтной цепи; локальная ступень
        // теперь lm-studio — поведение «сервер не отвечает = Busy» не менялось.
        var p = new OpenAiCompatibleProvider(log, "lm-studio",
            OpenAiCompatibleProvider.LmStudioBaseUrl,
            model: "local-model", apiKey: null, requireApiKey: false,
            httpClient: new HttpClient(h) { BaseAddress = new Uri(OpenAiCompatibleProvider.LmStudioBaseUrl) });

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("не отвечает", ex.Message);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u26A0") && x.Contains("поднять сервер"));
    }

    [Fact]
    public async Task Cloud_Connection_Error_Is_Loud_Exception()
    {
        var h = new StubHandler(_ => throw new HttpRequestException("dns fail"));
        var log = Log();
        var p = ModelScopeLike(log, h);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u274C") && x.Contains("сеть недоступна"));
    }

    [Fact]
    public async Task Request_Cap_Exhausted_Fails_Fast_Without_Http()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = ModelScopeLike(log, h, reqCap: 1);

        await p.CompleteAsync(new AiChatRequest("s", "u", 256)); // запрос №1 — ок
        var callsBefore = h.TotalCalls;

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));
        Assert.Contains("запросов исчерпана", ex.Message);
        Assert.Equal(callsBefore, h.TotalCalls); // HTTP не вызывался — фейл-фаст
        // Подсказка про per-model капу ModelScope ушла в WARN-протокол .
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u26A0") && x.Contains("200 на модель"));
    }

    [Fact]
    public async Task Request_Ledger_Persists_Across_Instances_Same_Day()
    {
        var file = Path.Combine(_dir, "state", "modelscope-requests.json");
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();

        var first = ModelScopeLike(log, h, requestLedger: file);
        await first.CompleteAsync(new AiChatRequest("s", "u", 256)); // +1 запрос

        var second = ModelScopeLike(log, h, requestLedger: file);
        await second.CompleteAsync(new AiChatRequest("s", "u", 256)); // +1 → 2 всего

        var json = File.ReadAllText(file);
        Assert.Contains("\"usedTokens\":2", json); // регистр один на файл — запросы копятся
    }

    [Fact]
    public async Task Token_Cap_Tracks_Remaining()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = new OpenAiCompatibleProvider(log, "siliconflow",
            OpenAiCompatibleProvider.SiliconFlowBaseUrl,
            model: "Qwen/Qwen3-8B", apiKey: "test-key",
            dailyTokenCap: 1000, tokenLedgerPath: null,
            httpClient: new HttpClient(h) { BaseAddress = new Uri(OpenAiCompatibleProvider.SiliconFlowBaseUrl) });

        Assert.Equal(1000, p.RemainingDailyTokens());
        await p.CompleteAsync(new AiChatRequest("s", "u", 256)); // usage 120+80=200
        Assert.Equal(800, p.RemainingDailyTokens());
    }

    [Fact]
    public async Task ModelScope_Serializes_Concurrent_Requests()
    {
        var h = new SerialStubHandler();
        var log = Log();
        var p = ModelScopeLike(log, h);

        var tasks = new Task[3];
        for (var i = 0; i < 3; i++)
            tasks[i] = p.CompleteAsync(new AiChatRequest("s", "u", 256));
        await Task.WhenAll(tasks);

        Assert.Equal(3, h.TotalCalls);
        Assert.Equal(1, h.MaxParallel); // «single-request concurrency» соблюдён
    }

    // 504-урок ModelScope: гибридным Qwen шлём enable_thinking=false,
    // иначе модель «думает» дольше таймаута шлюза.
    [Fact]
    public async Task DisableThinking_Sends_Enable_Thinking_False()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = ModelScopeLike(log, h, requestLedger: null, disableThinking: true);

        await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Contains("\"enable_thinking\":false", h.Bodies[0]);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("thinking: выключен"));
    }

    [Fact]
    public async Task Enable_Thinking_Absent_By_Default()
    {
        var h = new StubHandler(_ => Ok(ChatBody));
        var log = Log();
        var p = ModelScopeLike(log, h, requestLedger: null); // локальным и llm7-подобным лишнее поле не уходит

        await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.DoesNotContain("enable_thinking", h.Bodies[0]);
    }

    // Пустой content при ненулевом usage — сбой ступени, не результат:
    // reasoning-модель унесла ответ в reasoning_content. Provider обязан
    // кинуть BusyException (цепь переберёт следующих), а не отдать пустоту
    // наверх — иначе в историю ложится «адаптировано, а резюме пустое».
    [Fact]
    public async Task Empty_Content_With_Reasoning_Is_Busy()
    {
        const string body = """
            {"choices":[{"index":0,"message":{"role":"assistant","content":"","reasoning_content":"сначала подумаем: резюме надо…"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2300,"completion_tokens":379,"total_tokens":2679}}
            """;
        var h = new StubHandler(_ => Ok(body));
        var log = Log();
        var p = ModelScopeLike(log, h, requestLedger: null);

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("reasoning_content", ex.Message);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("пустой текст ответа") && x.Contains("reasoning_content"));
    }

    [Fact]
    public async Task Empty_Content_Without_Reasoning_Is_Busy()
    {
        const string body = """
            {"choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"length"}],"usage":{"prompt_tokens":100,"completion_tokens":0,"total_tokens":100}}
            """;
        var h = new StubHandler(_ => Ok(body));
        var log = Log();
        var p = ModelScopeLike(log, h, requestLedger: null);

        var ex = await Assert.ThrowsAsync<AiProviderBusyException>(
            () => p.CompleteAsync(new AiChatRequest("s", "u", 256)));

        Assert.Contains("пустой content", ex.Message);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("ответ шлюза без текста"));
    }

    // Часть шлюзов возвращают content массивом частей [{type:"text",…}] —
    // читаем оба формата, текст склеивается.
    [Fact]
    public async Task Content_As_Part_Array_Is_Joined()
    {
        const string body = """
            {"choices":[{"index":0,"message":{"role":"assistant","content":[{"type":"text","text":"часть 1 "},{"type":"text","text":"часть 2"}]},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
            """;
        var h = new StubHandler(_ => Ok(body));
        var log = Log();
        var p = ModelScopeLike(log, h, requestLedger: null);

        var r = await p.CompleteAsync(new AiChatRequest("s", "u", 256));

        Assert.Equal("часть 1 часть 2", r.Content);
    }

    // / <summary>Фейковый обработчик: без сети, фиксирует запросы и тела.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public int TotalCalls;
        public readonly List<string> RequestUris = new();
        public readonly List<string> Bodies = new();
        public string? LastAuthorization;

        public System.Collections.Generic.IEnumerable<string> RequestsTo(string path) =>
            RequestUris.Where(u => u.Contains(path));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref TotalCalls);
            RequestUris.Add(request.RequestUri?.ToString() ?? "");
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return _responder(request);
        }
    }

    // / <summary>Обработчик с задержкой: меряет фактическую параллельность вызовов.</summary>
    private sealed class SerialStubHandler : HttpMessageHandler
    {
        private int _current;
        private int _max;

        public int TotalCalls;
        public int MaxParallel => _max;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref TotalCalls);
            var now = Interlocked.Increment(ref _current);
            // Атомарный максимум одновременных — классический CAS-цикл.
            var snapshot = Volatile.Read(ref _max);
            while (now > snapshot && Interlocked.CompareExchange(ref _max, now, snapshot) != snapshot)
                snapshot = Volatile.Read(ref _max);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref _current);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
