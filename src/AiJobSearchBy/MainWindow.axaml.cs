using AiJobSearchBy.ViewModels;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchBy;

// LIFECYCLE: у каждого Window публичный ctor без параметров (RFC-001 §2).
public partial class MainWindow : Window
{
    public MainWindow()
        : this(Program.Services?.GetRequiredService<MainViewModel>()
               ?? new MainViewModel())
    {
    }

    // FLOW: DI-ctor — единая точка привязки контекста; дизайнера и тесты
    // обслуживает параметр по умолчанию через прямую модель.
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
