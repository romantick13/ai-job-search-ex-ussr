using AiJobSearchBy.ViewModels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchBy;

// LIFECYCLE: корень Avalonia-приложения; окно выдаётся DI-контейнером
// (Program.Services). Публичный ctor без параметров обязателен (RFC-001 §2).
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
            // FLOW: контейнер поднят в Program.Main; в дизайнере он null —
            // запасной путь строит окно с прямой моделью.
            desktop.MainWindow = Program.Services?.GetRequiredService<MainWindow>()
                                 ?? new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
