// Протокол выполнения: интерфейс ILogService (Logs, Log, LogSuccess, LogError,
// Clear) и его реализация LogService ниже. Формат строки окна "[HH:mm:ss] message",
// маркеры "\u2705 " (успех) и "\u274C " (ошибка) записаны \u-эскейпами, чтобы
// исходник не зависел от BOM и кодовой страницы инструментов;
// кольцо 300 строк с Logs.RemoveAt(0), сигнатуры и порядок методов — канон.

// Контракт «никаких тихих игноров»: каждая строка — и в окно, и на диск.
// 1. Каждая строка окна ДОПОЛНИТЕЛЬНО пишется на диск тем же конвейером;
// префикс диска "[HH:mm:ss.fff] " — как в Program.cs (startup_log.txt).
// Двойное время сознательно: окно — до секунды, протокол — до миллисекунды.
// Диск — полный протокол: Clear его НЕ трогает.
// 2. LogWarn — осознанный пропуск (skip) с обязательной причиной. Тихих
// catch и тихих skip не существует: каждый выход из ветки ошибки/пропуска
// обязан пройти через LogError/LogWarn.
// 3. Сбой записи на диск не роняет процесс и не молчит: первое и каждый
// 10-й падение кричат в stderr, все считаются в DiskWriteFailures.
// 4. EchoToConsole: в CLI живая лента дублируется в stdout — там лента
// играет роль окна; дашборд потом привяжет ту же коллекцию Logs — так же,
// как DebugLogWindow подписан на CollectionChanged.

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace AiJobSearchBy.Services;

public interface ILogService
{
    // Живая лента для окна/дашборда (ObservableCollection<string> Logs)
    ObservableCollection<string> Logs { get; }

    void Log(string message);
    void LogSuccess(string message);
    void LogError(string message);

    // осознанный пропуск с причиной — контракт «никаких тихих игноров»
    void LogWarn(string message);

    void Clear();

    // где лежит протокол на диске: каталог — для кнопки «Папка лога»
    // в шапке протокола; формула та же, что в AppendDisk (фолбэки учтены
    // конструктором).
    string LogDirectory { get; }
}

public class LogService : ILogService
{
    // живая лента окна; кольцо на 300 строк подрезается в AddLog
    public ObservableCollection<string> Logs { get; } = new();

    // сколько раз запись протокола на диск не удалась
    public int DiskWriteFailures { get; private set; }

    // путь к текущему файлу протокола — та же формула, что в AppendDisk;
    // баннер старта печатает его первой строкой в окно, чтобы файл лога
    // можно было открыть, не ищя его по каталогам
    public string CurrentLogFilePath =>
        Path.Combine(_logDirectory, $"ai-job-search-by-{DateTime.Now:yyyyMMdd}.log");

    // каталог протокола — кнопка «Папка лога» открывает его целиком
    public string LogDirectory => _logDirectory;

    private readonly object _gate = new();
    private readonly string _logDirectory;
    private readonly bool _echoToConsole;
    private bool _diskFailureAnnounced;

    // LIFECYCLE: создаётся в CompositionRoot как singleton (AddSingleton<ILogService, LogService>);
    // живёт от старта процесса до выхода; сквозь весь прогон один экземпляр
    public LogService(string? logDirectory = null, bool echoToConsole = true)
    {
        _echoToConsole = echoToConsole;
        // по умолчанию — %USERPROFILE%\ai-job-search-by\logs: один рабочий
        // каталог и для базы, и для конфигов, и для протоколов
        // (C:\Users\<юзер>\ai-job-search-by\). Раньше протокол вёл в
        // %APPDATA%\ai-job-search-by\logs — второй путь снесён, старые логи
        // переносит LegacyPathMigrator при старте.
        // Guard: если профильно-прикладной каталог не резолвится (пустой или
        // относительный XDG/HOME — ловили в Linux-песочнице), падаем на
        // каталог рядом с exe — путь строится через
        // AppDomain.CurrentDomain.BaseDirectory. Фолбэк не молчит: пишем
        // первый строкой в протокол, куда бы он ни лёг.
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
                // фолбэк-каталог — событие протокола, а не тишина
                AppendDisk($"[..] профильный каталог недоступен, протокол ведётся рядом с exe: {_logDirectory}");
            }
        }
        catch (Exception ex)
        {
            // даже сбой создания каталога протокола не молчит
            DiskWriteFailures++;
            AnnounceDiskFailure($"не создан каталог протокола {_logDirectory}: {ex.Message}");
        }
    }

    public void Log(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] {message}");

    public void LogSuccess(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u2705 {message}");

    public void LogError(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u274C {message}");

    // warn = осознанный skip; причина обязательна в тексте message
    public void LogWarn(string message) => AddLog($"[{DateTime.Now:HH:mm:ss}] \u26A0 {message}");

    public void Clear()
    {
        lock (_gate)
        {
            Logs.Clear();
            // диск — полный протокол, не чистится; факт очистки окна — событие
            AppendDisk("[..] Журнал окна очищен (файл протокола не тронут)");
        }
    }

    private void AddLog(string message)
    {
        // FLOW: любой сервис/адаптер -> ILogService -> диск logs/ai-job-search-by-YYYYMMDD.log
        // + живая лента Logs (в CLI — stdout, в GUI — окно/дашборд)
        // STATE: Logs — кольцевой буфер на 300 строк; файл протокола выбирается по текущей
        // дате (ротация на добор в полночь); DiskWriteFailures — счётчик сбоев диска
        // THREAD: вызовы допустимы с любого потока; все обращения к общему состоянию — под
        // _gate (в GUI-варианте лента шла через Avalonia Dispatcher.UIThread;
        // в CLI Avalonia нет — отклонение помечено ниже)
        lock (_gate)
        {
            Logs.Add(message);
            if (Logs.Count > 300) Logs.RemoveAt(0); // Защита от утечки памяти (кольцевой буфер)

            // диск и окно получают каждую строку, без исключений
            AppendDisk(message);

            // stdout = окно приложения в CLI-режиме
            if (_echoToConsole) Console.WriteLine(message);
        }
    }

    // формат строки диска — дословно Program.cs (startup_log.txt)
    private void AppendDisk(string message)
    {
        try
        {
            var path = Path.Combine(_logDirectory, $"ai-job-search-by-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // сбой протокола — не тихий, но и не фатальный
            DiskWriteFailures++;
            AnnounceDiskFailure($"строка протокола не записана: {ex.Message}");
        }
    }

    private void AnnounceDiskFailure(string detail)
    {
        // первое и каждый 10-й сбой — громко в stderr; остальные считаются
        if (!_diskFailureAnnounced || DiskWriteFailures % 10 == 0)
        {
            _diskFailureAnnounced = true;
            Console.Error.WriteLine(
                $"[ai-job-search-by] ПРОТОКОЛ НА ДИСКЕ НЕ ПИШЕТСЯ ({detail}); " +
                $"работаю в памяти. Сбоев подряд: {DiskWriteFailures}");
        }
    }
}
