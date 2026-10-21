using CommunityToolkit.Mvvm.ComponentModel;

namespace AiJobSearchBy.ViewModels;

// FLOW: MainWindow -> MainViewModel (DataContext) -> будущие команды прогонов
// LIFECYCLE: синглтон DI (CompositionRoot); живёт от старта окна до выхода
// STATE: корневая модель окна каркаса WP-0. MVVM CommunityToolkit
// (RFC-001 §2): [ObservableProperty]/[RelayCommand], без ReactiveUI/Fody.
// THREAD: все свойства — только с UI-потока (Avalonia).
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _greeting =
        "ai-job-search-by — каркас WP-0. Поиск, отклики и трекер появятся " +
        "в следующих фазах (PLAN.md §7).";
}
