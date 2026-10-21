using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AiJobSearchBy.Services;
using Xunit;

namespace AiJobSearchBy.Tests.Logging;

// ============================================================================
// [НАШЕ] Тесты машины протокола (восстановлены по контракту LOG-CONTRACT.md):
// формат окна/диска, маркеры, кольцо 300, полнота диска, Clear, сбой диска,
// гонки. Каждый тест — свой временный каталог протокола.
// ============================================================================

public sealed class LogServiceTests : IDisposable
{
    private readonly string _dir = TempLogDir();

    private LogService Log(bool echo = false) => new(_dir, echo);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    internal static string TempLogDir() =>
        Path.Combine(Path.GetTempPath(), "aijob-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Window_Format_And_Markers()
    {
        var log = Log();
        log.Log("текст");
        log.LogSuccess("ок");
        log.LogError("плохо");
        log.LogWarn("осторожно");

        Assert.Matches(new Regex(@"^\[\d{2}:\d{2}:\d{2}\] текст$"), log.Logs[0]);
        Assert.Contains("\u2705 ок", log.Logs[1]);
        Assert.Contains("\u274C плохо", log.Logs[2]);
        Assert.Contains("\u26A0 осторожно", log.Logs[3]);
    }

    [Fact]
    public void Disk_Line_Format_And_File_Name()
    {
        var log = Log();
        log.Log("строка протокола");

        var file = Directory.GetFiles(_dir, "*.log").Single();
        Assert.Matches(new Regex(@"^ai-job-search-by-\d{8}\.log$"), Path.GetFileName(file));
        var line = File.ReadAllLines(file).Single();
        Assert.Matches(new Regex(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[\d{2}:\d{2}:\d{2}\] строка протокола$"), line);
    }

    [Fact]
    public void Ring_Keeps_Last_300()
    {
        var log = Log();
        for (var i = 1; i <= 305; i++) log.Log($"e{i}");

        Assert.Equal(300, log.Logs.Count);
        Assert.Contains("e6", log.Logs[0]);    // первые 5 вытеснены
        Assert.Contains("e305", log.Logs[^1]); // хвост цел
    }

    [Fact]
    public void All_Four_Methods_Appear_On_Disk()
    {
        var log = Log();
        log.Log("a");
        log.LogSuccess("b");
        log.LogError("c");
        log.LogWarn("d");

        var lines = File.ReadAllLines(Directory.GetFiles(_dir, "*.log").Single());
        Assert.Equal(4, lines.Length);
        Assert.Contains(" a", lines[0]);
        Assert.Contains("\u2705 b", lines[1]);
        Assert.Contains("\u274C c", lines[2]);
        Assert.Contains("\u26A0 d", lines[3]);
    }

    [Fact]
    public void Clear_Empties_Ring_And_Announces_To_Disk_Only()
    {
        var log = Log();
        log.Log("до очистки");
        log.Clear();

        Assert.Empty(log.Logs); // [ДОНОР] кольцо опустошено
        var lines = File.ReadAllLines(Directory.GetFiles(_dir, "*.log").Single());
        Assert.Equal(2, lines.Length); // [НАШЕ-К1] факт очистки — только в диск
        Assert.Contains("Журнал окна очищен (файл протокола не тронут)", lines[1]);
    }

    [Fact]
    public void Disk_Failure_Is_Counted_And_Window_Survives()
    {
        var log = Log();
        // Блокер: каталог с именем дневного файла → AppendAllText падает каждый раз.
        Directory.CreateDirectory(Path.Combine(_dir, $"ai-job-search-by-{DateTime.Now:yyyyMMdd}.log"));

        log.Log("1");
        log.LogSuccess("2");
        log.LogError("3");
        log.LogWarn("4");

        Assert.Equal(4, log.Logs.Count);           // окно живёт (НИ-3)
        Assert.True(log.DiskWriteFailures >= 4);   // сбой не молчит (НИ-1/К3)
    }

    [Fact]
    public async Task Races_Lose_Nothing_On_Disk()
    {
        var log = Log();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) log.Log($"r{t}-{i}");
        })));

        var lines = File.ReadAllLines(Directory.GetFiles(_dir, "*.log").Single());
        Assert.Equal(400, lines.Length);                       // ничего не потеряно
        Assert.Equal(400, lines.Distinct().Count());           // и не задублировано
        Assert.True(log.Logs.Count <= 300);                    // кольцо держит
    }
}
