using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Services.Ai;
using AiJobSearchExUssr.Tests.Logging;
using Xunit;

namespace AiJobSearchExUssr.Tests.Ai;

// ============================================================================
// r25: честная цепь — ChainRuntime + TaskRoutingPolicy.
// • чекбоксы карточек глушат звенья для РЕАЛЬНЫХ запросов (не только для
//   «Проверить всех») — какие остались, в такие и стучимся;
// • отключённое звено кричит WARN в протокол, пустая цепь — LogError;
// • Rebuild() применяет правки настроек на лету, без перезапуска;
// • выбор чекбоксов персистится в SettingsKV и восстанавливается при старте.
// FLOW: тесты -> ChainRuntime(StubProvider) -> TaskRoutingPolicy -> RunAsync
// LIFECYCLE: каждый тест — свой tmp-каталог (протокол/база)
// THREAD: однопоточные тесты
// ============================================================================
public sealed class ChainRuntimeTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    // STATE: StubProvider — считает вызовы CompleteAsync (имя = LinkId встроенных).
    private sealed class StubProvider : IAiChatProvider
    {
        public string Name { get; }
        public int QualityRank { get; }
        public int Calls;

        public StubProvider(string name, int qualityRank = 1)
        {
            Name = name;
            QualityRank = qualityRank;
        }

        public long? RemainingDailyTokens() => null;

        public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new AiChatResult(
                "ответ", Name, new AiChatUsage(1, 1), Name, TimeSpan.FromMilliseconds(1)));
        }
    }

    private static ChainRuntime MakeChain(ILogService log, params IAiChatProvider[] providers)
        => new(log, () => providers
            .Select(p => new ChainRuntime.ChainEntry(p.Name, p)).ToList());

    private static readonly AiChatRequest Request =
        new("system", "user", MaxTokens: 16, Temperature: 0.1);

    // ---------------------------------------------------------------- чекбоксы

    [Fact]
    public async Task RunAsync_Skips_Disabled_Link_And_Knocks_On_The_Rest()
    {
        var log = new SilentLog();
        var llm7 = new StubProvider("llm7", 1);
        var lms = new StubProvider("lm-studio", 2);
        var runtime = MakeChain(log, llm7, lms);
        var policy = new TaskRoutingPolicy(log, runtime);

        // Сняли галочку с llm7 — запрос идёт в оставшееся звено.
        runtime.SetDisabled(new[] { "llm7" });
        var result = await policy.RunAsync(AiTaskKind.ResumeAdaptation, Request);

        Assert.Equal(0, llm7.Calls);                       // в отключённое не стучимся
        Assert.Equal(1, lms.Calls);                        // оставшееся работает
        Assert.NotNull(result);
        Assert.Equal("lm-studio", result!.ProviderName);
        // Честный WARN в протоколе: кто отключён и кто остался.
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("отключены чекбоксами") && x.Contains("llm7"));
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("в цепи остались") && x.Contains("lm-studio"));
    }

    [Fact]
    public async Task RunAsync_All_Disabled_Returns_Null_And_Logs_Loud()
    {
        var log = new SilentLog();
        var a = new StubProvider("llm7", 1);
        var b = new StubProvider("lm-studio", 2);
        var runtime = MakeChain(log, a, b);
        var policy = new TaskRoutingPolicy(log, runtime);

        runtime.SetDisabled(new[] { "llm7", "lm-studio" });
        var result = await policy.RunAsync(AiTaskKind.ResumeAdaptation, Request);

        Assert.Null(result);                                // стадия честно не выполнена
        Assert.Equal(0, a.Calls);
        Assert.Equal(0, b.Calls);
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("включённых звеньев нет") && x.Contains("чекбоксом"));
    }

    [Fact]
    public void CoverLetter_Skips_Disabled_Link_Too()
    {
        var log = new SilentLog();
        var llm7 = new StubProvider("llm7", 3);
        var modelscope = new StubProvider("modelscope", 1);
        var runtime = MakeChain(log, llm7, modelscope);
        var policy = new TaskRoutingPolicy(log, runtime,
            coverLetterOrder: new[] { "llm7", "modelscope" });

        runtime.SetDisabled(new[] { "llm7" });
        var order = policy.OrderFor(AiTaskKind.CoverLetter).Select(p => p.Name).ToList();

        Assert.Equal(new[] { "modelscope" }, order);        // отключённый дешёвый — мимо
    }

    // ---------------------------------------------------------------- на лету

    [Fact]
    public async Task Rebuild_Picks_Up_New_Link_Without_Restart()
    {
        var log = new SilentLog();
        var a = new StubProvider("llm7", 1);
        var b = new StubProvider("my-aggregator", 2);

        // Источник читает текущий состав — как CompositionRoot (звенья из DI + local.json).
        var current = new List<IAiChatProvider> { a };
        var runtime = new ChainRuntime(log,
            () => current.Select(p => new ChainRuntime.ChainEntry(p.Name, p)).ToList());
        var policy = new TaskRoutingPolicy(log, runtime);

        var first = await policy.RunAsync(AiTaskKind.ResumeAdaptation, Request);
        Assert.Equal("llm7", first!.ProviderName);          // нового звена ещё нет

        // «Добавили агрегатора в настройках» → SettingsViewModel.Save зовёт Rebuild.
        current.Add(b);
        runtime.Rebuild();

        runtime.SetDisabled(new[] { "llm7" });              // наши отключили — осталось своё
        var second = await policy.RunAsync(AiTaskKind.ResumeAdaptation, Request);

        Assert.Equal("my-aggregator", second!.ProviderName); // в цепи без перезапуска
        Assert.Contains(log.Logs.Cast<string>(),
            x => x.Contains("пересобрана на лету"));
    }

    // ------------------------------------------------------- персистентность

    [Fact]
    public void SetDisabled_Persists_To_SettingsKV()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var store = new ApplicationStore(System.IO.Path.Combine(_dir, "test.db"), log);
        store.Initialize();
        var runtime = new ChainRuntime(log,
            () => new[] { new StubProvider("llm7"), new StubProvider("lm-studio") }
                .Select(p => new ChainRuntime.ChainEntry(p.Name, p)).ToList(),
            store);

        runtime.SetDisabled(new[] { "llm7" });

        var raw = store.GetSetting("chain.disabled");
        Assert.NotNull(raw);
        Assert.Contains("llm7", raw);
    }

    [Fact]
    public void Constructor_Restores_Disabled_From_SettingsKV()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var store = new ApplicationStore(System.IO.Path.Combine(_dir, "test.db"), log);
        store.Initialize();
        store.SetSetting("chain.disabled", System.Text.Json.JsonSerializer.Serialize(new[] { "llm7" }));

        var llm7 = new StubProvider("llm7", 1);
        var lms = new StubProvider("lm-studio", 2);
        var runtime = new ChainRuntime(log,
            () => new[] { llm7, lms }
                .Select(p => new ChainRuntime.ChainEntry(p.Name, p)).ToList(),
            store);

        var (enabled, disabledNames, total) = runtime.Snapshot();
        Assert.Equal(2, total);
        Assert.Equal(new[] { "lm-studio" }, enabled.Select(p => p.Name).ToList());
        Assert.Equal(new[] { "llm7" }, disabledNames);
    }

    // -------------------------------------------------- кастомные звенья (Id)

    [Fact]
    public void Custom_Links_Carry_Ids_For_Honest_Toggles()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var config = ConfigService.LoadOrCreate(log, baseDirOverride: _dir);
        config.UpdateChain(new[]
        {
            new ChainLinkData("link-1", "Мой агрегатор", "https://example.com/v1/", "model-x", "key-1"),
        });

        var built = config.BuildCustomProviders(log);

        var link = Assert.Single(built);
        Assert.Equal("link-1", link.Id);                    // LinkId чекбокса = Id записи
        Assert.Equal("Мой агрегатор", link.Provider.Name);  // имя провайдера = имя звена
    }

    [Fact]
    public async Task Custom_Link_Toggle_Gates_By_Id_Not_Name()
    {
        var log = new SilentLog();
        var config = ConfigService.LoadOrCreate(new LogService(_dir, echoToConsole: false),
            baseDirOverride: _dir);
        config.UpdateChain(new[]
        {
            new ChainLinkData("link-1", "Мой агрегатор", "https://example.com/v1/", "model-x", "key-1"),
        });

        var custom = config.BuildCustomProviders(new SilentLog()).Single();
        var customCalls = 0;
        // Двойной контроль: провайдер кастомного звена считает вызовы отдельно.
        var tracked = new TrackedProvider(custom.Provider, () => Interlocked.Increment(ref customCalls));

        var builtIn = new StubProvider("llm7", 1);
        var runtime = new ChainRuntime(log, () => new[]
        {
            new ChainRuntime.ChainEntry(builtIn.Name, builtIn),
            new ChainRuntime.ChainEntry(custom.Id, tracked),
        }.ToList());

        // Карточка кастомного звена передаёт Id (как MainViewModel), а не имя.
        runtime.SetDisabled(new[] { "link-1" });
        var policy = new TaskRoutingPolicy(log, runtime);
        var result = await policy.RunAsync(AiTaskKind.ResumeAdaptation, Request);

        Assert.Equal(0, customCalls);                       // своё звено глушится по Id
        Assert.Equal("llm7", result!.ProviderName);         // запрос ушёл во встроенное
    }

    // Обёртка-счётчик поверх провайдера кастомного звена.
    private sealed class TrackedProvider : IAiChatProvider
    {
        private readonly IAiChatProvider _inner;
        private readonly Action _onCall;

        public TrackedProvider(IAiChatProvider inner, Action onCall)
        {
            _inner = inner;
            _onCall = onCall;
        }

        public string Name => _inner.Name;
        public int QualityRank => _inner.QualityRank;
        public long? RemainingDailyTokens() => _inner.RemainingDailyTokens();

        public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
        {
            _onCall();
            return _inner.CompleteAsync(request, ct);
        }
    }
}
