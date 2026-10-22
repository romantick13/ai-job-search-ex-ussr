using AiJobSearchBy.Models;
using AiJobSearchBy.Services;
using AiJobSearchBy.ViewModels;
using AiJobSearchBy.Views;
using Avalonia.Controls;
using Avalonia.Threading;
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

        // WP-U0: протокол прилипает к низу — каждая новая строка всегда видна.
        // LogFeed мутирует только на UI-потоке (маршалинг в MainViewModel),
        // поэтому здесь достаточно Post после добавления.
        viewModel.LogFeed.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                var items = ProtocolList.Items;
                if (items is null || items.Count == 0) return;
                var last = items[items.Count - 1];
                if (last is not null) ProtocolList.ScrollIntoView(last);
            });

        if (viewModel.IsDesignerMode)
        {
            return;
        }

        // WP-U1: стартовая загрузка (сайты, резюме, история, переиндексация PDF)
        // не тормозит открытие окна; любые падения — в протокол (НИ-1).
        _ = viewModel.InitializeAsync();

        // WP-U1: шестерёнка -> модальное окно настроек (решение владельца).
        viewModel.SettingsRequested += OpenSettings;

        // Клик по вакансии -> адаптация; выбор сбрасываем, чтобы повторный
        // клик по той же строке тоже срабатывал.
        VacancyList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems is [VacancyRow row])
            {
                viewModel.SelectVacancyCommand.Execute(row);
            }
            VacancyList.SelectedIndex = -1;
        };

        // Клик по строке истории -> модальное окно отправленного резюме.
        HistoryList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems is [ApplicationRow application])
            {
                _ = new ApplicationDetailsWindow(application).ShowDialog(this);
            }
            HistoryList.SelectedIndex = -1;
        };

        // Выход: резюме сохраняем лучшими усилиями (Loss недопустим).
        Closing += async (_, _) => await viewModel.ShutdownAsync();
    }

    private void OpenSettings()
    {
        var vm = Program.Services?.GetRequiredService<SettingsViewModel>()
                 ?? new SettingsViewModel();
        _ = new SettingsWindow(vm).ShowDialog(this);
    }
}
