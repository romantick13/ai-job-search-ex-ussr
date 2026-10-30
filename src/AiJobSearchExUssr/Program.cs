using AiJobSearchExUssr.Services;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchExUssr;

internal static class Program
{
    // STATE: единственный DI-контейнер приложения; поднимается до старта UI,
    // после старта не заменяется. Ссылка nullable: в дизайнере XAML контейнер
    // ещё не поднят, окно создаётся через прямой new MainViewModel.
    internal static ServiceProvider? Services { get; private set; }

    // === ОДИН ЭКЗЕМПЛЯР (r20, по образцу донора UniD) ===
    // Windows — именованный Mutex «Global\» (канон Windows, видно во всех
    // сессиях пользователей). НЕ-Windows — блокировочный файл app.lock с
    // FileShare.None: в ряде песочниц/ядер именованные мьютексы рантайма
    // НЕ шарятся между процессами (воспроизведено 2026-10-29: второй
    // процесс получал createdNew=true, а открытие чужого падало
    // IOException), а файловая блокировка честна всегда. Оба механизма
    // самоосвобождаются при краше (ОС закрывает хэндл) — следующий запуск
    // не заблокирован. Почему это обязательно: SQLite-база не рассчитана
    // на двух писателей (SQLITE_BUSY), двойной обход сайтов валит щиты
    // порталов, двойная автопроба каталогов — лишний трафик.
    private const string WindowsMutexName = @"Global\AiJobSearchExUssr_SingleInstance";

    public enum SingleInstanceGuardResult { Acquired, AlreadyRunning, Unavailable }

    // Взять «замок единственности». handle живёт весь lifetime первого
    // экземпляра (Dispose в finally Main). Unavailable — среда не дала
    // ни мьютекса, ни файла (экзотика): старт продолжается БЕЗ блокировки,
    // честная строка в протокол вместо краша.
    private static SingleInstanceGuardResult AcquireSingleInstanceGuard(
        out IDisposable? handle, out string? warning)
    {
        handle = null;
        warning = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                handle = new Mutex(true, WindowsMutexName, out var createdNew);
                return createdNew
                    ? SingleInstanceGuardResult.Acquired
                    : SingleInstanceGuardResult.AlreadyRunning;
            }

            // Файл в рабочем каталоге приложения — тот же, где база.
            var lockPath = Path.Combine(AppPaths.DataDir, "app.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            // FileShare.None: пока первый экземпляр держит поток, второй
            // открыть его не может — IOException = «уже запущено». На диске
            // остаётся пустой файл с PID — это норма, блокировка в нём,
            // не в содержимом.
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(
                $"{Environment.ProcessId} {DateTimeOffset.Now:O}\n"));
            stream.Flush();
            handle = stream;
            return SingleInstanceGuardResult.Acquired;
        }
        catch (IOException)
        {
            // Ресурс занят другим процессом = приложение уже запущено.
            handle = null;
            return SingleInstanceGuardResult.AlreadyRunning;
        }
        catch (Exception ex)
        {
            // Замок не взялся — старт продолжается без блокировки, причина
            // уйдёт в протокол сразу после его создания (guardWarning).
            warning = $"[один-экземпляр] замок не взялся ({ex.GetType().Name}: {ex.Message}) — " +
                      "защита от второго запуска выключена; решение: не запускать вручную второй экземпляр";
            handle = null;
            return SingleInstanceGuardResult.Unavailable;
        }
    }

    // Флаг для App.axaml.cs: вместо главного окна показать окошко
    // «приложение уже запущено» (путь второго экземпляра).
    internal static bool ShowAlreadyRunningNotice { get; private set; }

    // THREAD: STAThread обязателен — Avalonia требует STA UI-поток.
    [STAThread]
    public static void Main(string[] args)
    {
        // FLOW: замок единственности -> пути данных -> протокол ->
        // crash-хендлеры -> контейнер -> Avalonia -> MainWindow.
        // Порядок принципиален:
        // 1) замок ДО всего — второй экземпляр выходит раньше, чем что-либо
        //    трогает папку данных;
        // 2) пути ДО создания протокола — лог сразу пишется в финальный
        //    каталог.
        var guard = AcquireSingleInstanceGuard(out var guardHandle, out var guardWarning);
        if (guard == SingleInstanceGuardResult.AlreadyRunning)
        {
            var secondLog = new LogService();
            secondLog.LogWarn("второй экземпляр: приложение уже запущено — показываю уведомление " +
                              "и закрываюсь; решение: работать в уже открытом окне");
            ShowAlreadyRunningNotice = true;
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                secondLog.LogError($"FATAL Exception in Main: {ex}");
                throw;
            }

            return;
        }

        // Пути личных данных: %USERPROFILE%\ai-job-search-ex-ussr\.
        // Заметки (фолбэк путей) — в протокол первыми строками
        // (контракт «никаких тихих игноров»). Код переноса со старых
        // адресов снесён (r22c): переезд выполнен, владелец подтвердил.
        AppPaths.Initialize();
        var bootLog = new LogService();
        foreach (var note in AppPaths.Notes)
        {
            bootLog.Log(note);
        }

        if (guardWarning is not null)
        {
            bootLog.LogWarn(guardWarning);
        }

        // 1. Необработанные исключения в любом потоке — в протокол.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            bootLog.LogError($"FATAL UnhandledException: {e.ExceptionObject}");

        // 2. Необработанные исключения фоновых задач — в протокол; процесс
        // не роняем мгновенно (SetObserved), но событие записано.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            bootLog.LogError($"FATAL UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        bootLog.Log("=== ai-job-search-ex-ussr: старт ===");

        // Хотфикс: путь протокола — сразу в баннере (урок: лог было трудно
        // найти; теперь путь виден в окне с первой строки)
        bootLog.Log($"протокол: {bootLog.CurrentLogFilePath}");

        try
        {
            Services = CompositionRoot.Build(bootLog);
            // Требование: автопроба провайдеров автоматом
            // при запуске — НУЖНА, поиск — нет. Разбор: на старте НИКОГДА
            // не запускался и не запускается ПОИСК вакансий (он только по
            // кнопке) — единственная сетевая активность старта была и есть
            // проба каталогов моделей ИИ-провайдеров: она
            // красит лампы звеньев зелёным/красным ещё до нажатий. Пробу
            // возвращаем; поиск вакансий сам не стартует — проверено.
            ProbeCatalogsInBackground(bootLog);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            bootLog.LogSuccess("приложение завершено штатно");
        }
        catch (Exception ex)
        {
            // что делали / что поймали / что решили — решение: наружу,
            // процесс завершается аварийно, но причина уже в протоколе.
            bootLog.LogError($"FATAL Exception in Main: {ex}");
            throw;
        }
        finally
        {
            // Замок единственности держим весь lifetime первого экземпляра;
            // Dispose снимает его — следующий запуск стартует штатно.
            guardHandle?.Dispose();
        }
    }

    // Проба каталогов моделей на старте — сознательное решение:
    // автопроба провайдеров нужна (лампы), а поиск вакансий сам не стартует.
    // Кнопка «Проверить всех» (CheckAllAsync в VM) остаётся —
    // ручная перепроверка поверх автопробы.

    // FLOW: старт приложения -> фоновая проба каталога моделей каждого облачного
    // провайдера (LLM7 — всегда, ModelScope/SiliconFlow — только с введённым
    // ключом) -> исходы в протокол И В ЛАМПЫ КАРТОЧЕК:
    // лампочки загораются после успеха — зелёные,
    // после неудачи — красные; каждый исход уходит в StartupProbeHub
    // (событие + снимок для окна, открывшегося позже пробы). Локальные
    // (ollama/lm-studio) НЕ пробуем: сервер часто сознательно выключен,
    // их готовность громко кричит при первом же запросе (Busy с причиной).
    // Проба не блокирует UI и не роняет старт: ProbeModelsAsync ловит всё
    // внутри, наружный try/catch — страховка от регрессии.
    // ProbeModelsAsync — один раз при старте приложения.
    private static void ProbeCatalogsInBackground(ILogService log)
    {
        try
        {
            var probes = new List<(string Name, Func<Task<bool>> Run)>();
            foreach (var p in Services!.GetServices<Services.Ai.IAiChatProvider>())
            {
                switch (p)
                {
                    case Services.Ai.Llm7Provider l7:
                        probes.Add((l7.Name, () => l7.ProbeModelsAsync()));
                        break;
                    case Services.Ai.OpenAiCompatibleProvider oc when oc is { IsLocal: false, IsKeyMissing: false }:
                        probes.Add((oc.Name, () => oc.ProbeModelsAsync()));
                        break;
                }
            }
            if (probes.Count == 0) return; // без ключей облачных пробы нет — ctor уже кричал причины

            log.Log($"[probe] проба каталога моделей на старте: {string.Join(", ", probes.Select(x => x.Name))}");
            _ = Task.Run(async () =>
            {
                foreach (var (name, run) in probes)
                {
                    try
                    {
                        // ProbeModelsAsync ловит сбои внутри и возвращает
                        // bool — красная лампа ставится и по false, не только по исключению.
                        var ok = await run().ConfigureAwait(false);
                        StartupProbeHub.Publish(name, ok);
                    }
                    catch (Exception ex)
                    {
                        // проба не должна падать наружу даже при регрессии — кричим и живём.
                        log.LogError($"[probe] сбой пробы {name}: {ex.Message}; " +
                                     "решение: провайдер остаётся в цепи, сверить модель вручную (env *_MODEL)");
                        StartupProbeHub.Publish(name, ok: false);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            log.LogError($"[probe] настройка пробы не удалась — {ex.Message}; " +
                         "решение: старт продолжается без пробы каталога, сверить вручную");
        }
    }

    // FLOW: единая точка конфигурации рантайма и XAML-дизайнера.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
