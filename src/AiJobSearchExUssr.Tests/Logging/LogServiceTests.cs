using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;
using Xunit;

namespace AiJobSearchExUssr.Tests.Logging;

// FLOW: тесты -> LogService (в память-кольцо + диск во временный каталог)
// LIFECYCLE: каждый тест создаёт свой LogService со своим tmp-каталогом протокола
// STATE: tmp-каталоги одноразовые; echoToConsole: false — чтобы не шуметь в выводе тестов
public class LogServiceTests
{
    // internal: переиспользуется Ai-тестами (-LLM7, ) — единый tmp-каталог
    internal static string TempLogDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aijs-exu-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly Regex WindowLine =
        new(@"^\[\d{2}:\d{2}:\d{2}\] .+$", RegexOptions.Compiled);

    // Решение: дата в первых скобках, время без долей секунды
    private static readonly Regex DiskLine =
        new(@"^\[\d{4}-\d{2}-\d{2}\] \[\d{2}:\d{2}:\d{2}\] .+$", RegexOptions.Compiled);

    [Fact]
    public void Log_Adds_Timestamped_Line_To_LiveFeed()
    {
        var log = new LogService(TempLogDir(), echoToConsole: false);
        log.Log("проверка строки");

        var entry = Assert.Single(log.Logs);
        Assert.Matches(WindowLine, entry);
        Assert.EndsWith("проверка строки", entry);
    }

    [Fact]
    public void CurrentLogFilePath_Points_To_Today_File_On_Disk()
    {
        // Хотфикс: баннер старта печатает путь протокола — путь обязан
        // быть живым: та же формула, что в AppendDisk, файл существует после
        // первой же строки (пользователь идёт по напечатанному пути — и находит лог).
        var dir = TempLogDir();
        var log = new LogService(dir, echoToConsole: false);

        log.Log("строка протокола");

        Assert.StartsWith(dir, log.CurrentLogFilePath);
        Assert.EndsWith($"ai-job-search-ex-ussr-{DateTime.Now:yyyyMMdd}.log", log.CurrentLogFilePath);
        Assert.True(File.Exists(log.CurrentLogFilePath),
            "файл протокола должен существовать после первой строки");
    }

    [Fact]
    public void LogDirectory_Exposes_Injected_Folder_For_Button()
    {
        // кнопка «Папка лога» открывает каталог протокола — свойство
        // обязано отдавать ровно тот каталог, куда AppendDisk пишет строки.
        var dir = TempLogDir();
        var log = new LogService(dir, echoToConsole: false);

        log.Log("строка протокола");

        Assert.Equal(dir, log.LogDirectory);
        Assert.True(Directory.GetFiles(log.LogDirectory, "ai-job-search-ex-ussr-*.log").Length >= 1,
            "в каталоге протокола должен лежать файл лога после первой строки");
    }

    [Fact]
    public void Success_Error_Warn_Get_Explicit_Markers()
    {
        var log = new LogService(TempLogDir(), echoToConsole: false);

        log.LogSuccess("готово");
        log.LogError("упало");
        log.LogWarn("skip: причина указана");

        Assert.Matches(@"^\[[\d:]+\] \u2705 готово$", log.Logs[0]);
        Assert.Matches(@"^\[[\d:]+\] \u274C упало$", log.Logs[1]);
        Assert.Matches(@"^\[[\d:]+\] \u26A0 skip: причина указана$", log.Logs[2]);
    }

    [Fact]
    public void Ring_Caps_At_300_Like_Donor()
    {
        // Донор: кольцо 300 с RemoveAt(0) — «Защита от утечки памяти»
        var log = new LogService(TempLogDir(), echoToConsole: false);

        for (var i = 1; i <= 305; i++) log.Log($"e{i}");

        Assert.Equal(300, log.Logs.Count);
        Assert.EndsWith("e6", log.Logs[0]);      // первые пять вытеснены
        Assert.EndsWith("e305", log.Logs[^1]);   // хвост живой
    }

    [Fact]
    public void Every_Entry_Goes_To_Disk_Protocol()
    {
        // окно и диск получают каждую строку, без исключений
        var dir = TempLogDir();
        var log = new LogService(dir, echoToConsole: false);

        log.Log("раз");
        log.LogSuccess("два");
        log.LogError("три");
        log.LogWarn("skip: четыре");

        var file = Path.Combine(dir, $"ai-job-search-ex-ussr-{DateTime.Now:yyyyMMdd}.log");
        Assert.True(File.Exists(file), $"протокол не создан: {file}");

        var lines = File.ReadAllLines(file);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.Matches(DiskLine, l));
        Assert.Contains("три", lines[2]);
        Assert.Contains("\u2705", lines[1]);
    }

    [Fact]
    public void Clear_Empties_Window_Keeps_Disk_And_Records_Event()
    {
        // диск — полный протокол, Clear его не трогает
        var dir = TempLogDir();
        var log = new LogService(dir, echoToConsole: false);

        log.Log("до очистки");
        var before = File.ReadAllLines(Path.Combine(dir, $"ai-job-search-ex-ussr-{DateTime.Now:yyyyMMdd}.log")).Length;

        log.Clear();

        // Донор MDC: Clear опустошает кольцо; наше пишет факт очистки в протокол
        Assert.Empty(log.Logs);
        var after = File.ReadAllLines(Path.Combine(dir, $"ai-job-search-ex-ussr-{DateTime.Now:yyyyMMdd}.log"));
        Assert.Equal(before + 1, after.Length); // событие очистки попало в протокол
        Assert.Contains("очищен", after[^1]);
    }

    [Fact]
    public void Disk_Failure_Is_Not_Silent_But_Not_Fatal()
    {
        // каталог протокола невозможен (на его пути — обычный файл);
        // сервис не бросает, лента живёт, сбои считаются публично
        var dir = TempLogDir();
        var blocker = Path.Combine(dir, "это-файл");
        File.WriteAllText(blocker, "занимает место каталога");

        var log = new LogService(blocker, echoToConsole: false); // ctor уже словит сбой

        log.Log("а");
        log.LogError("б");
        log.Log("в");

        Assert.Equal(3, log.Logs.Count);          // окно работает
        Assert.True(log.DiskWriteFailures >= 4);  // ctor + 3 записи = минимум 4 сбоя
    }

    [Fact]
    public async Task Concurrent_Logs_All_Reach_Disk()
    {
        // THREAD: 8 потоков x 50 записей — каждая обязана дойти до диска
        // (xUnit1031: await Task.WhenAll вместо блокирующего Task.WaitAll)
        var dir = TempLogDir();
        var log = new LogService(dir, echoToConsole: false);

        var tasks = new Task[8];
        for (var t = 0; t < 8; t++)
        {
            var id = t;
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < 50; i++) log.Log($"t{id}-i{i}");
            });
        }
        await Task.WhenAll(tasks);

        var lines = File.ReadAllLines(Path.Combine(dir, $"ai-job-search-ex-ussr-{DateTime.Now:yyyyMMdd}.log"));
        Assert.Equal(400, lines.Length);
        Assert.Equal(400, lines.Distinct().Count()); // дублей из-за гонок нет
        Assert.True(log.Logs.Count <= 300);          // кольцо не распухло
    }
}
