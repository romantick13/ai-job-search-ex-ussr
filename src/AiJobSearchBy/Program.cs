using AiJobSearchBy.Services;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchBy;

internal static class Program
{
    // STATE: единственный DI-контейнер приложения; поднимается до старта UI,
    // после старта не заменяется. Ссылка nullable: в дизайнере XAML контейнер
    // ещё не поднят, окно создаётся через прямой new MainViewModel().
    internal static ServiceProvider? Services { get; private set; }

    // THREAD: STAThread обязателен — Avalonia требует STA UI-поток.
    [STAThread]
    public static void Main(string[] args)
    {
        // FLOW: протокол -> crash-хендлеры -> контейнер -> Avalonia -> MainWindow.
        // Контракт «никаких тихих игноров» (LOG-CONTRACT.md): лог — первый объект
        // приложения; тот же экземпляр уходит в DI (CompositionRoot.Build(bootLog)).
        // Схема аварийных хендлеров — вербатим донора Program.cs (MDC, Task 51):
        // UnhandledException + UnobservedTaskException с SetObserved, слово FATAL.
        var bootLog = new LogService();

        // 1. Необработанные исключения в любом потоке — в протокол.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            bootLog.LogError($"FATAL UnhandledException: {e.ExceptionObject}");

        // 2. Необработанные исключения фоновых задач — в протокол; процесс
        //    не роняем мгновенно (SetObserved), но событие записано.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            bootLog.LogError($"FATAL UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        bootLog.Log("=== ai-job-search-by: старт ===");

        // Хотфикс 2026-10-22: путь протокола — сразу в баннере (урок владельца
        // «еле нашёл лог»: теперь путь виден в окне, а не только в Finder-разведке)
        bootLog.Log($"протокол: {bootLog.CurrentLogFilePath}");

        try
        {
            Services = CompositionRoot.Build(bootLog);
            ProbeCatalogsInBackground(bootLog); // проба каталогов ИИ — фоновая, UI не ждёт
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            bootLog.LogSuccess("приложение завершено штатно");
        }
        catch (Exception ex)
        {
            // НИ-1: что делали / что поймали / что решили — решение: наружу,
            // процесс завершается аварийно, но причина уже в протоколе.
            bootLog.LogError($"FATAL Exception in Main: {ex}");
            throw;
        }
    }

    // FLOW: старт приложения -> фоновая проба каталога моделей каждого облачного
    //        провайдера (LLM7 — всегда, ModelScope/SiliconFlow — только с введённым
    //        ключом) -> исходы в протокол. Локальные (ollama/lm-studio) НЕ пробуем:
    //        сервер часто сознательно выключен, их готовность громко кричит при
    //        первом же запросе (Busy с причиной). Проба не блокирует UI и не роняет
    //        старт: ProbeModelsAsync ловит всё внутри (НИ-3), наружный try/catch —
    //        страховка от регрессии (НИ-1). Основание: LLM7-INTEGRATION.md §7 п.3
    //        («ProbeModelsAsync — один раз при старте приложения»).
    private static void ProbeCatalogsInBackground(ILogService log)
    {
        try
        {
            var probes = new List<(string Name, Func<Task> Run)>();
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
            if (probes.Count == 0) return; // без ключей облачных пробы нет — ctor уже кричал причины (НИ-2)

            log.Log($"[probe] проба каталога моделей на старте: {string.Join(", ", probes.Select(x => x.Name))}");
            _ = Task.Run(async () =>
            {
                foreach (var (name, run) in probes)
                {
                    try
                    {
                        await run();
                    }
                    catch (Exception ex)
                    {
                        // НИ-1: проба не должна падать наружу даже при регрессии — кричим и живём.
                        log.LogError($"[probe] сбой пробы {name}: {ex.Message}; " +
                                     "решение: провайдер остаётся в цепи, сверить модель вручную (env *_MODEL)");
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
