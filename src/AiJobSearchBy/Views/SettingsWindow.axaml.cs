using AiJobSearchBy.Controls;
using AiJobSearchBy.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;

namespace AiJobSearchBy.Views;

// LIFECYCLE: у каждого Window публичный ctor без параметров.
public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(new SettingsViewModel())
    {
    }

    // FLOW: DI-ctor — модель из CompositionRoot (ConfigService + store + log).
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        ModalClose.Attach(this); // Esc закрывает модалку
        DataContext = viewModel;

        viewModel.CloseRequested += Close;

        // -UI: изменяемые окна с памятью — ширина и позиция окна
        // настроек живут в layout.json (высота — SizeToContent).
        UiLayout.Attach(this, "SettingsWindow");

        // Выбор папки откликов — системный диалог (StorageProvider).
        PickFolderButton.Click += async (_, _) =>
        {
            var storage = StorageProvider;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Папка откликов (PDF)",
                AllowMultiple = false,
            });
            if (folders.Count > 0)
            {
                viewModel.OutboxPath = folders[0].TryGetLocalPath() ?? viewModel.OutboxPath;
            }
        };
    }
}
