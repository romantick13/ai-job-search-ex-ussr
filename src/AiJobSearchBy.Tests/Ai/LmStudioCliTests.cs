using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests.Ai;

// ============================================================================
// Тесты LmStudioCli: парсеры всех ответов `lms` на дословных выводах:
// `lms daemon up` (already running / started),
// `lms ps` (пусто / таблица IDENTIFIER), `lms ls` (12 моделей, секции LLM и
// EMBEDDING), `lms load <id> --yes` (успех 12.94s + идентификатор из кавычек).
// Плюс цепочка EnsureReadyAsync на тестовом шве runner (без реальных
// процессов): happy path, чужая модель без UI = LoadTarget, решение
// Skip = null, UseLoaded = работа с загруженной, lms отсутствует = null.
// GUI-сервер НЕ используется; HTTP-проба — фейковый HttpClient (200).
// ============================================================================

public sealed class LmStudioCliTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir);

    // / <summary>CLI с runner-швом: файл-заглушка lms обязательна — иначе
    // / LmsExists честно не найдёт CLI в PATH песочницы и ступень будет мимо.</summary>
    private LmStudioCli Cli(LogService log, Func<string, string?> runner,
        Func<HttpClient>? httpFactory = null)
    {
        var stub = Path.Combine(_dir, "fake-lms.exe");
        File.WriteAllText(stub, "");
        return new LmStudioCli(log, lmsPath: stub, runner: runner, httpClientFactory: httpFactory);
    }

    // --- Дословные ответы CLI ---

    private const string DaemonUpAlready = "The daemon is already running (PID: 6740).";
    private const string DaemonUpStarted = "Waking up LM Studio service...\nllmster started (PID: 11860).";
    private const string DaemonDownReply = "Shutting down llmster...\nDone.";
    private const string PsEmpty = "No models are currently loaded.\nTo load a model, run:\nlms load <model path>";
    private const string LoadOk = "Model loaded successfully in 12.94s.\n(5.76 GiB)\n" +
        "To use the model in the API/SDK, use the identifier \"qwen3-vl-8b-instruct\".";

    // дословный хвост `lms load` из лога «Проверить
    // всех» — «Model loaded successfully» в нём НЕТ, а загрузка была УСПЕШНОЙ.
    private const string LoadLlmTail = "(6.10 GiB) ⏎ To use the model in the API/SDK, " +
        "use the identifier \"qwen/qwen3.5-9b\".";

    private const string PsQwenTable = "IDENTIFIER\nqwen/qwen3.5-9b";

    private const string PsLoadedTable = """
        IDENTIFIER                       STATE        CONTEXT LENGTH
        qwen3-vl-8b-instruct             LOADED       4096
        llama                            LOADED       4096
        """;

// `lms ls` — дословно (12 моделей, LLM + EMBEDDING секции).
    private const string LsCatalogue = """
        You have 12 models, taking up 48.93 GB of disk space.

        LLM                                   PARAMS    ARCH          SIZE         DEVICE
        glm-ocr                               891M      glm4          1.43 GB      Local
        google/gemma-4-12b-qat (1 variant)    12B       gemma4        7.15 GB      Local
        llama                                 8B        Llama         5.73 GB      Local
        mineru2.5-pro-2605-1.2b               494M      qwen2vl       1.86 GB      Local
        nvidia-nemotron-nano-12b-v2-vl        12B       nemotron_h    7.72 GB      Local
        qwen                                  7B        Qwen2         4.68 GB      Local
        qwen/qwen3.5-9b (1 variant)           9B        qwen35        6.55 GB      Local
        qwen2.5-vl-7b-instruct                7B        qwen2vl       6.04 GB      Local
        qwen3-vl-8b-instruct                  8B        qwen3vl       6.19 GB      Local
        whisper-large-v3-turbo                          whisper       864.66 MB    Local

        EMBEDDING                               PARAMS    ARCH          SIZE         DEVICE
        text-embedding-bge-m3                   567M      BERT          634.55 MB    Local
        text-embedding-nomic-embed-text-v1.5              Nomic BERT    84.11 MB     Local
        """;

    // --- daemon up ---

    [Fact]
    public void DaemonUp_AlreadyRunning_Parses_Pid()
    {
        var (status, pid) = LmStudioCli.ParseDaemonUp(DaemonUpAlready);
        Assert.Equal(LmStudioDaemonStatus.AlreadyRunning, status);
        Assert.Equal(6740, pid);
    }

    [Fact]
    public void DaemonUp_Started_Parses_Pid()
    {
        var (status, pid) = LmStudioCli.ParseDaemonUp(DaemonUpStarted);
        Assert.Equal(LmStudioDaemonStatus.Started, status);
        Assert.Equal(11860, pid);
    }

    [Fact]
    public void DaemonUp_DownReply_Is_Unknown()
    {
        // Ответ `lms daemon down` не является успехом подъёма — честный Unknown.
        var (status, pid) = LmStudioCli.ParseDaemonUp(DaemonDownReply);
        Assert.Equal(LmStudioDaemonStatus.Unknown, status);
        Assert.Null(pid);
    }

    // --- lms ps ---

    [Fact]
    public void Ps_Empty_Message_Produces_No_Phantoms()
    {
        // Ловушка: «No models are currently loaded. To load a model, run:
        // lms load <model path>» не должен дать моделей No/To/lms.
        var ids = LmStudioCli.ParseLoadedIdentifiers(PsEmpty);
        Assert.Empty(ids);
    }

    [Fact]
    public void Ps_Table_Parses_Identifiers()
    {
        var ids = LmStudioCli.ParseLoadedIdentifiers(PsLoadedTable);
        Assert.Equal(new[] { "qwen3-vl-8b-instruct", "llama" }, ids);
    }

    [Fact]
    public void Ps_Ansi_Noise_Is_Cleaned_First()
    {
        var noisy = "\x1b[2J\x1b[1;1H" + PsLoadedTable.Replace("\n", "\r\n");
        var ids = LmStudioCli.ParseLoadedIdentifiers(LmStudioCli.CleanCli(noisy));
        Assert.Equal(new[] { "qwen3-vl-8b-instruct", "llama" }, ids);
    }

    // --- lms ls ---

    [Fact]
    public void Ls_Catalogue_Parses_Llm_Only()
    {
        var ids = LmStudioCli.ParseLlmCandidates(LsCatalogue);
        // Суффиксы «(1 variant)» отпали, EMBEDDING-секция не попала, порядок каталога.
        Assert.Equal(new[]
        {
            "google/gemma-4-12b-qat",
            "llama",
            "nvidia-nemotron-nano-12b-v2-vl",
            "qwen",
            "qwen/qwen3.5-9b",
            "qwen2.5-vl-7b-instruct",
            "qwen3-vl-8b-instruct",
        }, ids);
    }

    [Fact]
    public void Ls_Whisper_Ocr_Minerm_Embedding_Filtered()
    {
        var ids = LmStudioCli.ParseLlmCandidates(LsCatalogue);
        Assert.DoesNotContain(ids, id => id.Contains("whisper"));
        Assert.DoesNotContain(ids, id => id.Contains("glm-ocr"));
        Assert.DoesNotContain(ids, id => id.Contains("mineru"));
        Assert.DoesNotContain(ids, id => id.StartsWith("text-embedding"));
    }

    [Fact]
    public void ResolveCandidate_From_Catalogue_Prefers_Qwen35()
    {
        var log = Log();
        var cli = new LmStudioCli(log, runner: args =>
            args == "ls" ? LsCatalogue : null);
        // Плейсхолдер-дефолт -> кандидат по приоритету качества (qwen3.5 первый).
        Assert.Equal("qwen/qwen3.5-9b", cli.ResolveCandidate("local-model", ""));
    }

    [Fact]
    public void ResolveCandidate_ExplicitTarget_Wins_Over_Catalogue()
    {
        var log = Log();
        var cli = new LmStudioCli(log, runner: args => args == "ls" ? LsCatalogue : null);
        Assert.Equal("llama", cli.ResolveCandidate("llama", ""));
    }

    // --- lms load ---

    [Fact]
    public void Load_Success_Parses_Identifier()
    {
        var (ok, id) = LmStudioCli.ParseLoad(LoadOk);
        Assert.True(ok);
        Assert.Equal("qwen3-vl-8b-instruct", id);
    }

    [Fact]
    public void Load_Failure_Parses_False()
    {
        var (ok, id) = LmStudioCli.ParseLoad("error: model not found");
        Assert.False(ok);
        Assert.Null(id);
    }

    [Fact]
    public void Load_Tui_Tail_Is_Success_With_Identifier()
    {
        // Регресс хвост без «Model loaded successfully» — НЕ провал,
        // это вердикт LM Studio об успешной загрузке (репродукс лога ).
        var (ok, id) = LmStudioCli.ParseLoad(LoadLlmTail);
        Assert.True(ok);
        Assert.Equal("qwen/qwen3.5-9b", id);
    }

    // --- Matches: точное совпадение, без ловушки Contains ---

    [Fact]
    public void Matches_Is_Exact_Not_Contains()
    {
        Assert.True(LmStudioCli.Matches("qwen2.5-vl-7b-instruct", "qwen2.5-vl-7b-instruct"));
        Assert.True(LmStudioCli.Matches("org/qwen3.5-9b", "qwen3.5-9b")); // сегмент после /
        Assert.False(LmStudioCli.Matches("qwen", "qwen2.5-vl")); // ловушка Contains
        Assert.False(LmStudioCli.Matches("llama", "qwen"));
    }

    // --- EnsureReadyAsync на шве runner (без реальных процессов) ---

    // / <summary>Стейт-машина CLI: daemon up -> ps (до/после load) -> ls -> load.
    // / PsScript — поэтапные ответы ps для свапа эмбеддера (пусто —
    // / 1-й ps = LoadedBefore, далее LoadedAfter); unload — считается.
    // / r21: свап вырезан — остаётся жила LoadedBefore/LoadedAfter.</summary>
    private sealed class FakeLms
    {
        private int _psCalls;
        public string LoadedBefore = PsEmpty;
        public string LoadedAfter = PsLoadedTable;
        public string LoadReply = LoadOk;
        public int LoadCalls;
        public int UnloadCalls;
        public List<string> Calls = new();

        public string? Run(string args)
        {
            Calls.Add(args);
            if (args == "daemon up") return DaemonUpAlready;
            if (args == "ps")
            {
                _psCalls++;
                return _psCalls == 1 ? LoadedBefore : LoadedAfter;
            }
            if (args == "ls") return LsCatalogue;
            if (args.StartsWith("load ", StringComparison.Ordinal)) { LoadCalls++; return LoadReply; }
            if (args.StartsWith("unload ", StringComparison.Ordinal)) { UnloadCalls++; return "Done."; }
            return null;
        }
    }

    private static HttpClient FakeHttp() => new(new FakeHttpHandler())
    {
        BaseAddress = new Uri(OpenAiCompatibleProvider.LmStudioBaseUrl)
    };

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task EnsureReady_HappyPath_Resolves_Candidate_And_Probes_Port()
    {
        var lms = new FakeLms(); // ps пусто -> ls -> load -> ps перепроверка
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);

        var model = await cli.EnsureReadyAsync("local-model"); // плейсхолдер-дефолт

        Assert.Equal("qwen3-vl-8b-instruct", model); // идентификатор из кавычек ответа load
        Assert.Equal(1, lms.LoadCalls);
        Assert.Contains(lms.Calls, c => c == "daemon up");
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("PID: 6740"));
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("модель 'qwen3-vl-8b-instruct' загружена"));
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("отклик модели"));
    }

    [Fact]
    public async Task EnsureReady_TuiTail_PsConfirms_LoadSucceeds()
    {
        // Точный репродукс load напечатал его хвост (без «Model
        // loaded successfully»), ps подтвердил, отклик есть — ступень готова.
        // В r8 этот сценарий получал вердикт «НЕУДАЧА (16,9 с)» и фолбэк.
        var lms = new FakeLms { LoadReply = LoadLlmTail, LoadedAfter = PsQwenTable };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);

        var model = await cli.EnsureReadyAsync("local-model");

        Assert.Equal("qwen/qwen3.5-9b", model);
        Assert.Equal(1, lms.LoadCalls);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("подтверждено lms ps"));
    }

    [Fact]
    public async Task EnsureReady_Identifier_But_PsMisses_Probe_Decides()
    {
        // ps молчит (лад/другой формат), но lms load отчитался идентификатором —
        // вердикт НЕ по тексту: решает отклик модели (у фейка он ОК).
        var lms = new FakeLms { LoadReply = LoadLlmTail, LoadedAfter = PsEmpty };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);

        var model = await cli.EnsureReadyAsync("local-model");

        Assert.Equal("qwen/qwen3.5-9b", model);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("верим только отклику"));
    }

    [Fact]
    public async Task EnsureReady_SilentLoad_PsEmpty_Fails_Fast()
    {
        // Ни ps, ни идентификатора — вот теперь честный провал и фолбэк.
        var lms = new FakeLms { LoadReply = "", LoadedAfter = PsEmpty };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);

        var model = await cli.EnsureReadyAsync("local-model");

        Assert.Null(model);
        Assert.Equal(1, lms.LoadCalls);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("загрузка 'qwen/qwen3.5-9b' не удалась"));
    }

    [Fact]
    public async Task EnsureReady_TargetLoaded_Skips_Load_Directly()
    {
        var lms = new FakeLms { LoadedBefore = PsLoadedTable };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);

        var model = await cli.EnsureReadyAsync("llama"); // уже загружена (сегмент после /)

        Assert.Equal("llama", model);
        Assert.Equal(0, lms.LoadCalls);
    }

    [Fact]
    public async Task EnsureReady_Mismatch_Without_Ui_Defaults_To_LoadTarget()
    {
        // Загружена чужая llama, цель — qwen3.5: хендлера нет (тесты/фоновый
        // режим) -> осознанный дефолт: загрузить требуемую (исход кричит).
        var lms = new FakeLms
        {
            LoadedBefore = PsLoadedTable,
            LoadReply = LoadOk.Replace("qwen3-vl-8b-instruct", "qwen/qwen3.5-9b"),
            LoadedAfter = "IDENTIFIER\nqwen/qwen3.5-9b",
        };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);

        var model = await cli.EnsureReadyAsync("qwen/qwen3.5-9b");

        Assert.Equal("qwen/qwen3.5-9b", model);
        Assert.Equal(1, lms.LoadCalls);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("UI-модалки нет"));
    }

    [Fact]
    public async Task EnsureReady_Mismatch_Skip_Decision_Returns_Null()
    {
        var lms = new FakeLms { LoadedBefore = PsLoadedTable };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);
        cli.MismatchHandler = _ => Task.FromResult(LmStudioMismatchDecision.Skip);

        var model = await cli.EnsureReadyAsync("qwen/qwen3.5-9b");

        Assert.Null(model); // ступень мимо — фолбэк цепи
        Assert.Equal(0, lms.LoadCalls); // чужую не трогали, нужную не грузили
    }

    [Fact]
    public async Task EnsureReady_Mismatch_UseLoaded_Works_With_Foreign()
    {
        var lms = new FakeLms { LoadedBefore = PsLoadedTable };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);
        cli.MismatchHandler = _ => Task.FromResult(LmStudioMismatchDecision.UseLoaded);

        var model = await cli.EnsureReadyAsync("qwen/qwen3.5-9b");

        Assert.Equal("qwen3-vl-8b-instruct", model); // первая из lms ps
        Assert.Equal(0, lms.LoadCalls);
    }

    [Fact]
    public async Task EnsureReady_Lms_Missing_Returns_Null_With_Loud_Skip()
    {
        var log = Log();
        var cli = new LmStudioCli(log,
            lmsPath: Path.Combine(_dir, "no-such-lms.exe"), // абсолютный и не существует
            runner: _ => throw new InvalidOperationException("не должен вызываться"));

        var model = await cli.EnsureReadyAsync("local-model");

        Assert.Null(model);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("lms не найден"));
    }

    // --- свап под эмбеддер (формула) ---

        // r21: emb-тесты (EnsureEmbedder_*/Restore_*) вырезаны вместе с векторизацией.

    [Fact]
    public async Task AutoLoad_True_Bypasses_Mismatch_Modal()
    {
        // Чекбокс «авто-LMS» установлен: чужая модель в VRAM — грузим молча,
        // хендлер-модалка (если бы кто-то подсунул) НЕ вызывается.
        var lms = new FakeLms
        {
            LoadedBefore = PsLoadedTable,
            LoadReply = LoadOk.Replace("qwen3-vl-8b-instruct", "qwen/qwen3.5-9b"),
            LoadedAfter = "IDENTIFIER\nqwen/qwen3.5-9b",
        };
        var log = Log();
        var cli = Cli(log, lms.Run, FakeHttp);
        cli.AutoLoad = true;
        cli.MismatchHandler = _ => throw new InvalidOperationException(
            "модалка при авто-загрузке не должна зваться");

        var model = await cli.EnsureReadyAsync("qwen/qwen3.5-9b");

        Assert.Equal("qwen/qwen3.5-9b", model);
        Assert.Equal(1, lms.LoadCalls);
        Assert.Contains(log.Logs.Cast<string>(), x => x.Contains("авто-загрузка включена"));
    }
}
