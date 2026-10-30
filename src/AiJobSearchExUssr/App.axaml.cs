using AiJobSearchExUssr.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchExUssr;

// LIFECYCLE: корень Avalonia-приложения; окно выдаётся DI-контейнером
// (Program.Services). Публичный ctor без параметров обязателен.
public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Второй экземпляр (Program ставит флаг, когда именованный Mutex
            // уже занят): маленькое окно «уже запущено» с ОК вместо рабочего
            // окна — DI-контейнер не поднимается вовсе (Program вернулся до
            // CompositionRoot.Build), окно собирается кодом. Закрытие окна
            // (ОК/крестик) гасит lifetime — процесс завершается.
            if (Program.ShowAlreadyRunningNotice)
            {
                desktop.MainWindow = BuildAlreadyRunningNoticeWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // FLOW: контейнер поднят в Program.Main; в дизайнере он null —
            // запасной путь строит окно с прямой моделью.
            desktop.MainWindow = Program.Services?.GetRequiredService<MainWindow>()
                                 ?? new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

        SetupTrayIcon();
    }

    // r20: окошко «приложение уже запущено» для второго экземпляра.
    // Кодом, без XAML: один разовый диалог, в AXAML не стоит отдельного окна.
    private static Window BuildAlreadyRunningNoticeWindow()
    {
        var okButton = new Button
        {
            Content = "ОК",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel { Margin = new Thickness(24, 20), Spacing = 14 };
        stack.Children.Add(new TextBlock
        {
            Text = "ai-job-search-ex-ussr уже запущено.",
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Второй экземпляр закрывается — работайте в уже открытом окне.",
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(okButton);

        var window = new Window
        {
            Title = "ai-job-search-ex-ussr",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Topmost = true,
            Content = stack,
        };
        okButton.Click += (_, _) => window.Close();
        return window;
    }

    // 1. Трей.
    // ставится кодом: attached-свойство TrayIcon.Icons в XAML окна не
    // компилируется (AVLN2000), а на Application в XAML — NRE при populate
    // на X11 до готовности трей-сервиса. Обертка try/catch — на случай среды
    // вовсе без сервиса трея (X11 без системного трея): пропуск со строкой
    // в лог; на Windows системный трей есть всегда.
    private void SetupTrayIcon()
    {
        try
        {
            // WindowIcon(string) открывает ПУТЬ НА ДИСКЕ (File.OpenRead) и
            // avares:// не понимает — грузим актив стримом (паттерн из
            // ControlCatalog): AssetLoader.Open(new Uri(...)) ->
            // WindowIcon(stream). Иконка живёт в DLL (AvaloniaResource),
            // зависимость от раскладки файлов рядом с exe не нужна.
            using var stream = AssetLoader.Open(new Uri("avares://AiJobSearchExUssr/Assets/app.ico"));
            var icon = new WindowIcon(stream);

            TrayIcon.SetIcons(this, new TrayIcons
            {
                new TrayIcon { Icon = icon, ToolTipText = "ai-job-search-ex-ussr — дашборд" }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[tray] иконка трея не поставлена — " +
                              $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
