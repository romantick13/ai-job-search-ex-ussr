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

        try
        {
            Services = CompositionRoot.Build(bootLog);
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

    // FLOW: единая точка конфигурации рантайма и XAML-дизайнера.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
