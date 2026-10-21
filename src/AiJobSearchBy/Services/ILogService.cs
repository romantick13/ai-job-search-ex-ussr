// FLOW: любой сервис/адаптер -> ILogService -> диск logs/ai-job-search-by-YYYYMMDD.log
//        + живая лента Logs (сейчас stdout/окно, после WP-U — окна дашборда)
// LIFECYCLE: создаётся в CompositionRoot как singleton (AddSingleton<ILogService, LogService>);
//        живёт от старта процесса до выхода; сквозь весь прогон один экземпляр
// STATE: Logs — кольцевой буфер на 300 строк; файл протокола выбирается по текущей
//        дате (ротация на добор в полночь); DiskWriteFailures — счётчик сбоев диска
// THREAD: вызовы допустимы с любого потока; все обращения к общему состоянию — под
//        _gate (в доноре маршалинг был через Avalonia Dispatcher.UIThread.Invoke;
//        в CLI Avalonia нет — отклонение помечено ниже)

// ВЕРБАТИМ-ПОРТ из донора MDC (upload/MDC.zip -> MDC/MDC/Services/ILogService.cs,
// исходники получены от владельца; см. worklog, задача по распаковке доноров):
// сохранено дословно — интерфейс ILogService (Logs, Log, LogSuccess, LogError,
// Clear), формат строки окна "[HH:mm:ss] message", маркеры "\u2705 " (успех)
// и "\u274C " (ошибка) — в доноре литералы ✅/❌, здесь \u2705/\u274C, чтобы
// исходник не зависел от BOM и кодовой страницы инструментов владельца;
// кольцо 300 с Logs.RemoveAt(0) и комментарием
// донора «Защита от утечки памяти», сигнатуры и порядок методов.
//
// НАШЕ РАСШИРЕНИЕ (контракт владельца 2026-10-21 «никаких тихих игноров»: всё —
// на диск и параллельно в окно; у донора на диск пишется только startup_log.txt
// в Program.cs, а ряд catch-блоков молчит — у нас так запрещено):
//   К1. Каждая строка окна ДОПОЛНИТЕЛЬНО пишется на диск тем же конвейером;
//       префикс диска "[HH:mm:ss.fff] " взят дословно у донора (Program.cs,
//       startup_log.txt). Двойное время сознательно: окно — до секунды,
//       протокол — до миллисекунды. Диск — полный протокол: Clear() его НЕ трогает.
//   К2. LogWarn — осознанный пропуск (skip) с обязательной причиной. Тихих
//       catch и тихих skip не существует: каждый выход из ветки ошибки/пропуска
//       обязан пройти через LogError/LogWarn (см. LOG-CONTRACT.md, правила НИ).
//   К3. Сбой записи на диск не роняет процесс и не молчит: первое и каждый
//       10-й падение кричат в stderr, все считаются в DiskWriteFailures.
//   К4. EchoToConsole: в CLI живая лента дублируется в stdout («окно приложения»
//       до WP-U; дашборд WP-U потом привяжет ту же коллекцию Logs — так же,
//       как DebugLogWindow донора подписан на CollectionChanged).

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace AiJobSearchBy.Services;

public interface ILogService
{
    // Живая лента для окна/дашборда (донор: ObservableCollection<string> Logs)
    ObservableCollection<string> Logs { get; }

    void Log(string message);
    void LogSuccess(string message);
    void LogError(string message);

    // К2: осознанный пропуск с причиной — контракт «никаких тихих игноров»
    // (в доноре отсутствует; добавлено по контракту владельца 2026-10-21)
    void LogWarn(string message);

    void Clear();
}

public class LogService : ILogService
{
    // Донор: публичная коллекция, кольцо на 300
    public ObservableCollection<string> Logs { get; } = new();

    // К3: сколько раз запись протокола на диск не удалась
    public int DiskWriteFailures { get; private set; }

    private readonly object _gate = new();
    private readonly string _logDirectory;
    private readonly bool _echoToConsole;
    private bool _diskFailureAnnounced;

    public LogService(string? logDirectory = null, bool echoToConsole = true)
    {
        _echoToConsole = echoToConsole;
        // К1: по умолчанию — %APPDATA%\ai-job-search-by\logs (личные данные —
        // только вне репо; то же правило, что у local.json и data/).
        // Guard: если профильно-прикладной каталог не резолвится (пустой или
        // относительный XDG/HOME — ловили в Linux-песочнице), падаем на
        // каталог рядом с exe — ровно как донор MDC ведёт startup_log.txt
        // через AppDomain.CurrentDomain.BaseDirectory. Фолбэк не молчит: пишем
        // первый строкой в протокол, куда бы он ни лёг.
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
        var baseDir = rooted ? profileDir : AppContext.BaseDirectory;
        // Фолбэк считается только когда каталог не задан снаружи;
        // явный каталог (тесты/конфиг) никогда не анонсируется как фолбэк
        var fellBack = logDirectory is null && !rooted;
        _logDirectory = logDirectory ?? Path.Combine(baseDir, "ai-job-search-by", "logs");

        try
        {
            Directory.CreateDirectory(_logDirectory);
            if (fellBack)
            {
                // НАШЕ: фолбэк-каталог — событие протокола, а не тишина
                AppendDisk($"[..] профильный каталог недоступен, протокол ведётся рядом с exe: {_logDirectory}");
            }
        }
        catch (Exception ex)
        {
            // К3: даже сбой создания каталога протокола не молчит
            DiskWriteFailures++;
            AnnounceDiskFailure($"не создан каталог протокола {_logDirectory}: {ex.Message}");
        }
    }

    public void Log(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] {message}");

    public void LogSuccess(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u2705 {message}");

    public void LogError(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u274C {message}");

    // К2: warn = осознанный skip; причина обязательна в тексте message
    public void LogWarn(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u26A0 {message}");

    public void Clear()
    {
        lock (_gate)
        {
            Logs.Clear();
            // К1: диск — полный протокол, не чистится; факт очистки окна — событие
            AppendDisk("[..] Журнал окна очищен (файл протокола не тронут)");
        }
    }

    private void AddLog(string message)
    {
        lock (_gate)
        {
            Logs.Add(message);
            if (Logs.Count > 300) Logs.RemoveAt(0); // Защита от утечки памяти (донор)

            // К1: диск и окно получают каждую строку, без исключений
            AppendDisk(message);

            // К4: stdout = окно приложения в CLI-режиме
            if (_echoToConsole) Console.WriteLine(message);
        }
    }

    // К1: формат строки диска — дословно донорский Program.cs (startup_log.txt)
    private void AppendDisk(string message)
    {
        try
        {
            var path = Path.Combine(_logDirectory, $"ai-job-search-by-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // К3: сбой протокола — не тихий, но и не фатальный
            DiskWriteFailures++;
            AnnounceDiskFailure($"строка протокола не записана: {ex.Message}");
        }
    }

    private void AnnounceDiskFailure(string detail)
    {
        // К3: первое и каждый 10-й сбой — громко в stderr; остальные считаются
        if (!_diskFailureAnnounced || DiskWriteFailures % 10 == 0)
        {
            _diskFailureAnnounced = true;
            Console.Error.WriteLine(
                $"[ai-job-search-by] ПРОТОКОЛ НА ДИСКЕ НЕ ПИШЕТСЯ ({detail}); " +
                $"работаю в памяти. Сбоев подряд: {DiskWriteFailures}");
        }
    }
}
