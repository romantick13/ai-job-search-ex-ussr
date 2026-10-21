using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;

namespace AiJobSearchBy.Services;

// ============================================================================
// [ДОНОР] Машина протокола: вербатим-порт MDC/Services/ILogService.cs —
//         ObservableCollection<string> Logs, кольцо 300 + RemoveAt(0)
//         («Защита от утечки памяти»), формат окна [HH:mm:ss], маркеры
//         ✅/❌, методы Log/LogSuccess/LogError/Clear.
// [НАШЕ-К1] Дисковый протокол: <профиль>/ai-job-search-by/logs/
//           ai-job-search-by-YYYYMMDD.log (ротация по дате), строка
//           "[HH:mm:ss.fff] [HH:mm:ss] текст". Clear() диск не трогает.
// [НАШЕ-К2] LogWarn — осознанный skip с обязательной причиной (маркер ⚠).
//           Правила НИ-1..5 (LOG-CONTRACT.md §2): каждый catch кричит,
//           тихих skip нет, протокол сам не молчит и не роняет,
//           два канала (окно+диск) всегда.
// [НАШЕ-К3] Сбой записи на диск не молчит: stderr на 1-й и каждый 10-й,
//           публичный счётчик DiskWriteFailures. В окно сбой не пишется
//           (иначе рекурсия AddLog→AppendDisk→AddLog).
// [НАШЕ-К4] EchoToConsole: stdout = «окно приложения» в CLI-режиме.
// [НАШЕ]    lock вместо Avalonia Dispatcher (CLI, UI-потока нет);
//           \u2705/\u274C/\u26A0 вместо литералов (независимость от
//           BOM/кодовой страницы); guard относительного пути профиля →
//           фолбэк AppContext.BaseDirectory (как донорский startup_log)
//           с анонсом первой строкой протокола.
// ============================================================================

public interface ILogService
{
    ObservableCollection<string> Logs { get; }
    void Log(string message);
    void LogSuccess(string message);
    void LogError(string message);
    void LogWarn(string message);   // НАШЕ-К2: осознанный skip с причиной
    void Clear();
}

public class LogService : ILogService
{
    // [ДОНОР] Кольцо окна: 300 записей, старшие вытесняются.
    public ObservableCollection<string> Logs { get; } = new();

    // [НАШЕ] Публичный счётчик сбоев диска (К3) — виден дашборду WP-U.
    private long _diskWriteFailures;
    public long DiskWriteFailures => Interlocked.Read(ref _diskWriteFailures);

    private readonly object _gate = new();      // НАШЕ: CLI, нет UI-потока — lock
    private readonly string _logDirectory;      // НАШЕ-К1
    private readonly bool _echoToConsole;       // НАШЕ-К4
    private long _stderrAnnouncements;          // НАШЕ-К3: 1-й и каждый 10-й

    public LogService(string? logDirectory = null, bool echoToConsole = true)
    {
        // [НАШЕ] Guard: путь профиля должен быть задан и абсолютен; иначе
        // фолбэк рядом с исполняемым файлом (как донорский startup_log),
        // с обязательным анонсом — тихий относительный путь запрещён (НИ-2).
        // Явный каталог (тесты/конфиг) никогда не анонсируется как фолбэк.
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
        var fellBack = logDirectory is null && !rooted;
        _logDirectory = logDirectory ?? Path.Combine(
            rooted ? profileDir : AppContext.BaseDirectory,
            "ai-job-search-by", "logs");
        _echoToConsole = echoToConsole;

        Directory.CreateDirectory(_logDirectory);
        if (fellBack)
        {
            AddLog($"НАШЕ-К1: каталог профиля не задан или не абсолютен — " +
                   $"протокол пишется в {_logDirectory} (фолбэк AppContext.BaseDirectory)");
        }
    }

    public void Log(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] {message}");            // [ДОНОР]
    public void LogSuccess(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u2705 {message}"); // [ДОНОР]
    public void LogError(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u274C {message}");   // [ДОНОР]
    public void LogWarn(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u26A0 {message}");    // [НАШЕ-К2]

    public void Clear()
    {
        // [ДОНОР] Кольцо опустошается. [НАШЕ-К1] Факт очистки — только в
        // дисковый протокол: файл не трогаем, история дней сохраняется.
        lock (_gate)
        {
            Logs.Clear();
        }
        AppendDisk($"[{DateTime.Now:HH:mm:ss}] Журнал окна очищен (файл протокола не тронут)");
    }

    private void AddLog(string message)
    {
        lock (_gate)
        {
            Logs.Add(message);                          // [ДОНОР]
            if (Logs.Count > 300) Logs.RemoveAt(0);     // [ДОНОР] Защита от утечки памяти
            AppendDisk(message);                        // НАШЕ-К1: два канала всегда (НИ-5)
            if (_echoToConsole) Console.WriteLine(message); // НАШЕ-К4: stdout = окно приложения
        }
    }

    private void AppendDisk(string message)
    {
        // НАШЕ-К1: один файл в сутки, строка "[HH:mm:ss.fff] {окно-строка}".
        try
        {
            var path = Path.Combine(_logDirectory, $"ai-job-search-by-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // НАШЕ-К3: не молчит (НИ-1/НИ-3), но и не роняет поток (НИ-3).
            // В окно событие НЕ дублируется: AddLog вызвал бы AppendDisk
            // снова — рекурсия. Каналы сигнала: stderr (1-й и каждый 10-й)
            // и счётчик DiskWriteFailures (дашборд WP-U).
            var n = Interlocked.Increment(ref _diskWriteFailures);
            if (Interlocked.Increment(ref _stderrAnnouncements) == 1 ||
                Interlocked.Read(ref _diskWriteFailures) % 10 == 0)
            {
                Console.Error.WriteLine(
                    $"ai-job-search-by: строка протокола не записана на диск (сбой #{n}): {ex.Message}");
            }
        }
    }
}
