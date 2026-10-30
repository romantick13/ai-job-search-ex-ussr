using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Services.Ai;
using AiJobSearchExUssr.Tests.Logging;
using Xunit;

namespace AiJobSearchExUssr.Tests.Ai;

// ============================================================================
// Тесты роутинга: match без ИИ (отказ политики
// кричит), порядок письма (llm7 первым), фолбэк цепи, локальный скоринг,
// формат строки [cost].
// ============================================================================

// FLOW: тесты -> TaskRoutingPolicy / LocalMatchScorer / ResponseCostCounter на StubProvider-цепи
// LIFECYCLE: каждый тест — свой tmp-каталог протокола
// THREAD: однопоточные тесты
public sealed class RoutingScorerCostTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir);

    // STATE: StubProvider(name, rank, busy, failWith) — сценарии фолбэка и порядков стадий;
    // failWith — сбои НЕ-«занятость» (HTTP 401/500, таймаут): — цепь обязана их перебирать
    private sealed class StubProvider : IAiChatProvider
    {
        public string Name { get; }
        public int QualityRank { get; }
        private readonly bool _busy;
        private readonly Exception? _failWith;

        public StubProvider(string name, int qualityRank, bool busy = false,
            Exception? failWith = null)
        {
            Name = name;
            QualityRank = qualityRank;
            _busy = busy;
            _failWith = failWith;
        }

        public long? RemainingDailyTokens() => null;

        public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
        {
            if (_failWith is not null) throw _failWith;
            if (_busy) throw new AiProviderBusyException("тест: занят");
            return Task.FromResult(new AiChatResult(
                "ответ", Name, new AiChatUsage(10, 5), Name, TimeSpan.FromMilliseconds(1)));
        }
    }

    // ------------------------------------------------------------------ роутинг

    [Fact]
    public void Match_Assessment_Is_Refused_With_Loud_Warn()
    {
        var log = Log();
        var policy = new TaskRoutingPolicy(log, new IAiChatProvider[]
        {
            new StubProvider("openrouter", 1), new StubProvider("llm7", 3)
        });

        var chain = policy.OrderFor(AiTaskKind.MatchAssessment);

        Assert.Empty(chain); // ИИ на match не тратится — политика
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("\u26A0") && x.Contains("LocalMatchScorer") && x.Contains("критика Мистраля"));
    }

    [Fact]
    public void Resume_Uses_Quality_Order_Letter_Uses_Cheap_Order()
    {
        var log = Log();
        var policy = new TaskRoutingPolicy(
            log,
            new IAiChatProvider[]
            {
                new StubProvider("openrouter", 1),
                new StubProvider("orcarouter", 2),
                new StubProvider("llm7", 3)
            },
            coverLetterOrder: new[] { "llm7" });

        var resume = policy.OrderFor(AiTaskKind.ResumeAdaptation).Select(p => p.Name).ToList();
        var letter = policy.OrderFor(AiTaskKind.CoverLetter).Select(p => p.Name).ToList();

        Assert.Equal(new[] { "openrouter", "orcarouter", "llm7" }, resume); // лучшая модель
        Assert.Equal("llm7", letter[0]);                                    // дешёвый — первым
        Assert.Equal(3, letter.Count);                                      // фолбэк сохранён
    }

    [Fact]
    public async Task Chain_Failover_Busy_To_Next()
    {
        var log = Log();
        var policy = new TaskRoutingPolicy(
            log,
            new IAiChatProvider[]
            {
                new StubProvider("first", 1, busy: true),
                new StubProvider("second", 2, busy: false)
            });

        var result = await policy.RunAsync(AiTaskKind.CoverLetter, new AiChatRequest("s", "u", 256));

        Assert.NotNull(result);
        Assert.Equal("second", result!.ProviderName); // победил следующий
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("\u26A0") && x.Contains("first недоступен") && x.Contains("следующий в цепи: second"));
    }

    [Fact]
    public async Task Chain_Exhaustion_Is_Loud_Error()
    {
        var log = Log();
        var policy = new TaskRoutingPolicy(
            log,
            new IAiChatProvider[]
            {
                new StubProvider("a", 1, busy: true),
                new StubProvider("b", 2, busy: true)
            });

        var result = await policy.RunAsync(AiTaskKind.ResumeAdaptation, new AiChatRequest("s", "u", 256));

        Assert.Null(result);
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("\u274C") && x.Contains("провайдеры исчерпаны") && x.Contains("a: тест: занят"));
    }

    // ------------------------------------------------------------- цепь перебирает ВСЕ сбои

    [Fact]
    public async Task Chain_Failover_Http401_To_Next()
    {
        // Сценарий: ModelScope 401 (ключ сервером отклонён)
        // — адаптация НЕ должна падать, цепь идёт к следующему.
        var log = Log();
        var policy = new TaskRoutingPolicy(
            log,
            new IAiChatProvider[]
            {
                new StubProvider("modelscope", 1, failWith:
                    new HttpRequestException("modelscope: HTTP 401: Authentication failed")),
                new StubProvider("orcarouter", 2)
            });

        var result = await policy.RunAsync(AiTaskKind.ResumeAdaptation, new AiChatRequest("s", "u", 256));

        Assert.NotNull(result);
        Assert.Equal("orcarouter", result!.ProviderName); // следующий победил
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("\u26A0") && x.Contains("modelscope сбой")
                && x.Contains("HttpRequestException")
                && x.Contains("следующий в цепи: orcarouter"));
    }

    [Fact]
    public async Task Chain_Failover_Timeout_To_Next()
    {
        var log = Log();
        var policy = new TaskRoutingPolicy(
            log,
            new IAiChatProvider[]
            {
                new StubProvider("modelscope", 1, failWith: new TimeoutException("modelscope: таймаут запроса")),
                new StubProvider("llm7", 2)
            });

        var result = await policy.RunAsync(AiTaskKind.CoverLetter, new AiChatRequest("s", "u", 256));

        Assert.NotNull(result);
        Assert.Equal("llm7", result!.ProviderName);
    }

    [Fact]
    public async Task Chain_All_Fail_Loud_Null_NoThrow()
    {
        // Все провайдеры сбойнулись — стадия честно не сделана (null, громкий
        // LogError), а НЕ исключение наверх, как было до .
        var log = Log();
        var policy = new TaskRoutingPolicy(
            log,
            new IAiChatProvider[]
            {
                new StubProvider("modelscope", 1, failWith:
                    new HttpRequestException("modelscope: HTTP 401: Authentication failed")),
                new StubProvider("orcarouter", 2, failWith:
                    new HttpRequestException("orcarouter: HTTP 500: boom"))
            });

        var result = await policy.RunAsync(AiTaskKind.ResumeAdaptation, new AiChatRequest("s", "u", 256));

        Assert.Null(result);
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("\u274C") && x.Contains("провайдеры исчерпаны")
                && x.Contains("modelscope: HTTP 401") && x.Contains("orcarouter: HTTP 500"));
    }

    [Fact]
    public async Task Chain_User_Cancel_Propagates_NoFailover()
    {
        // «Отмена» юзера — не сбой провайдера: наверх без перебора остальных.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var log = Log();
        var policy = new TaskRoutingPolicy(
            log,
            new IAiChatProvider[]
            {
                new StubProvider("modelscope", 1, failWith: new OperationCanceledException(cts.Token)),
                new StubProvider("orcarouter", 2)
            });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => policy.RunAsync(AiTaskKind.ResumeAdaptation, new AiChatRequest("s", "u", 256), cts.Token));
    }

    // ------------------------------------------------------------------ скоринг

    [Fact]
    public void Scorer_Scores_Keywords_Without_Ai()
    {
        var log = Log();
        var scorer = new LocalMatchScorer(log);

        var v = scorer.Score(
            "Резюме: C#, .NET, ASP.NET Core, SQL Server, Docker, Git. Пять лет опыта.",
            "Требования: .NET разработчик, C#, SQL, Docker, Kubernetes, React.");

        Assert.InRange(v.Score, 40, 69);
        Assert.Contains("net", v.Matched);
        Assert.Contains("kubernetes", v.Missing);
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("[scorer]") && x.Contains("0 ИИ-запросов, 0 токенов"));
    }

    [Fact]
    public void Scorer_Gives_Low_Score_For_Unrelated_Resume()
    {
        var log = Log();
        var scorer = new LocalMatchScorer(log);

        var v = scorer.Score(
            "Продавец-консультант: касса, мерчандайзинг, 1С торговля.",
            "Требования: .NET разработчик, C#, SQL, Docker, Kubernetes, React.");

        Assert.True(v.Score < 40);
        Assert.Equal("низкий", v.Level);
    }

    // ------------------------------------------------------------------ счётчик

    [Fact]
    public void Cost_Counter_Builds_Summary_Line()
    {
        var log = Log();
        var counter = new ResponseCostCounter(log);

        counter.BeginResponse(3);
        counter.Record("match", "local", 0, 0, 0, TimeSpan.FromMilliseconds(1));
        counter.Record("letter", "llm7", 500, 242, 1, TimeSpan.FromSeconds(1));
        var line = counter.EndResponse();

        Assert.Contains("отклик #3", line);
        Assert.Contains("match=local(0т/0з)", line);
        Assert.Contains("letter=llm7(742т/1з)", line);
        Assert.Contains("итого 742 ток / 1 запр", line);
        Assert.Contains("за процесс: 742 ток / 1 запр", line);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("\u2705") && x.Contains("[cost]"));
    }
}
