using System.Collections.Specialized;
using System.Linq;
using AiJobSearchBy.Controls;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services;
using AiJobSearchBy.ViewModels;
using AiJobSearchBy.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchBy;

// LIFECYCLE: у каждого Window публичный ctor без параметров.
public partial class MainWindow : Window
{
    // ScrollViewer списка протокола (находится после привязки к дереву).
    private ScrollViewer? _protocolScroll;

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

        // автоскролл протокола до хвоста — каждая новая строка
        // всегда видна; кто хочет — покрутит историю вверх. LogFeed мутирует только на UI-потоке
        // (маршалинг в MainViewModel), поэтому достаточно Post. Ведём
        // Offset на дно напрямую: ScrollIntoView на виртуализованном
        // списке может встать мимо хвоста из-за оценки смещения, а
        // double.MaxValue ScrollViewer сам прижимает к допустимому дну.
        ProtocolList.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                _protocolScroll = ProtocolList.GetVisualDescendants()
                    .OfType<ScrollViewer>().FirstOrDefault();
            });

        viewModel.LogFeed.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            Dispatcher.UIThread.Post(() =>
            {
                var scroll = _protocolScroll;
                if (scroll is null) return;
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, double.MaxValue);
            });
        };

        if (viewModel.IsDesignerMode)
        {
            return;
        }

        // Секция правок прогона занимает
        // звёздную строку только пока права на сцене (появились/приняты) — иначе
        // строка Auto и словарю достаётся вся высота панели. Без этого фикси-
        // рованные Auto-строки съедали короткую панель: словарь схлопывался до
        // полстроки, а перелив срезал низ списка правок даже на макс. скролле.
        // (строка 3 панели ключевых слов; x:Name на RowDefinition поля не даёт)
        SuggestionSection.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                KeywordsPanel.RowDefinitions[3].Height = SuggestionSection.IsVisible
                    ? new GridLength(1, GridUnitType.Star)
                    : GridLength.Auto;
            }
        };

        // Окна с памятью — размер/позиция окна и
        // раскладка RootGrid (рабочая зона / протокол), WorkGrid (зона /
        // история) и ZoneGrid (вакансии / резюме) живут в layout.json в
        // папке конфига; нет файла — копируется дефолт (Controls/UiLayout).
        UiLayout.Attach(this, "MainWindow", RootGrid, WorkGrid, ZoneGrid);

        // стартовая загрузка (сайты, резюме, история, переиндексация PDF)
        // не тормозит открытие окна; любые падения — в протокол.
        _ = viewModel.InitializeAsync();

        // шестерёнка -> модальное окно настроек.
        viewModel.SettingsRequested += OpenSettings;

        // Клик по строке вакансии ничего НЕ открывает — только
        // подсветка, подложка остаётся темнее; скрепка —
        // браузер; "адаптировать" — цепочка ИИ. Клик по строке ТОЛЬКО
        // выбирает её. SelectedIndex больше НЕ сбрасываем — выделение
        // остаётся на строке (видно, с какой работаешь); «Адаптировать»
        // и скрепка действуют на выбранную строку.
        VacancyList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems is [VacancyRow row])
            {
                viewModel.SelectVacancyCommand.Execute(row);
            }
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

        // Заплатка чужого PDF — модальное окно поверх главного.
        OverlayPdfButton.Click += (_, _) => _ = new PdfOverlayWindow().ShowDialog(this);

        // «＋ Добавить» — диалог файла (PDF/DOCX/TXT/MD)
        // -> конвейер профиля (парс -> база -> ключевые).
        AddResumeButton.Click += async (_, _) => await PickResumeFileAsync(viewModel);

        // «MD-канон» — модалка правки канона активного профиля.
        EditCanonButton.Click += (_, _) => ShowCanonDialog(viewModel);

        // Конфига в %USERPROFILE% не было —
        // создан дефолт. Честное модальное окно первого запуска: пусть заново
        // настраивает ключи (шестерёнка -> настройки).
        if (viewModel.ConfigCreatedFromTemplate)
        {
            Opened += async (_, _) => await ShowFirstRunDialogAsync(viewModel);
        }

        // Ступень lm-studio: чужая модель в VRAM — модальное окно выбора
        // («загружена X, а надо Y»): загрузить нужную / работать с X / пропустить.
        // Выгрузка чужих НЕ делается — выбор всегда за юзером.
        // В дизайнере/тестах хендлер остаётся null — дефолт LoadTarget (в протоколе).
        if (!viewModel.IsDesignerMode)
        {
            var lmsCli = Program.Services?.GetService<Services.Ai.LmStudioCli>();
            if (lmsCli is not null)
            {
                lmsCli.MismatchHandler = info => ShowLmStudioMismatchDialogAsync(info);
                // чекбокс «авто-LMS» уже прочитан VM-ctor из SettingsKV —
                // стартовое состояние CLI синхронизируем здесь (дальше держит
                // OnLmsAutoLoadChanged при каждом переключении).
                lmsCli.AutoLoad = viewModel.LmsAutoLoad;
            }
        }

        // Выход: резюме сохраняем лучшими усилиями (Loss недопустим).
        Closing += async (_, _) => await viewModel.ShutdownAsync();
    }

    // Модалка «в LM Studio загружена другая модель»: показывает загруженную и
    // требуемую, возвращает . Вызов из фонового потока
    // бутстрапа — модалка раскрывается через Dispatcher, ожидание через TCS.
    private async Task<Services.Ai.LmStudioMismatchDecision> ShowLmStudioMismatchDialogAsync(
        Services.Ai.LmStudioMismatchInfo info)
    {
        var tcs = new TaskCompletionSource<Services.Ai.LmStudioMismatchDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var dialog = new Window
                {
                    Title = "LM Studio — занятая модель",
                    Width = 560,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#0A1A14")),
                };
                Controls.ModalClose.Attach(dialog); // Esc закрывает модалку

                var loaded = string.Join(", ", info.LoadedIdentifiers);
                var text = new TextBlock
                {
                    Margin = new Avalonia.Thickness(24, 24, 24, 8),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = Avalonia.Media.Brushes.White,
                    Text = "В LM Studio сейчас загружена модель:\n\n"
                           + loaded
                           + "\n\nА для работы цепи нужна:\n\n"
                           + info.TargetModel
                           + "\n\nЧто сделать? Чужую модель не выгружаем — "
                           + "загрузка нужной добавится рядом (в VRAM поместятся обе, если влезают).",
                };

                var buttons = new WrapPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    // Перенос кнопок: три длинные подписи не влезают в 560px одной строкой.
                    ItemWidth = double.NaN,
                    Margin = new Avalonia.Thickness(24, 8, 24, 20),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                };

                Button MakeButton(string caption, Services.Ai.LmStudioMismatchDecision decision,
                    bool isDefault = false)
                {
                    var b = new Button
                    {
                        Content = caption,
                        Padding = new Avalonia.Thickness(14, 7),
                        Margin = new Avalonia.Thickness(0, 0, 10, 8), // зазор в WrapPanel (Spacing нет)
                        FontWeight = isDefault
                            ? Avalonia.Media.FontWeight.SemiBold
                            : Avalonia.Media.FontWeight.Normal,
                    };
                    if (isDefault) b.Classes.Add("accent");
                    b.Click += (_, _) =>
                    {
                        tcs.TrySetResult(decision);
                        dialog.Close();
                    };
                    return b;
                }

                buttons.Children.Add(MakeButton($"Загрузить «{info.TargetModel}»",
                    Services.Ai.LmStudioMismatchDecision.LoadTarget, isDefault: true));
                if (info.LoadedIdentifiers.Count > 0)
                    buttons.Children.Add(MakeButton($"Работать с «{info.LoadedIdentifiers[0]}»",
                        Services.Ai.LmStudioMismatchDecision.UseLoaded));
                buttons.Children.Add(MakeButton("Пропустить lm-studio",
                    Services.Ai.LmStudioMismatchDecision.Skip));

                dialog.Closed += (_, _) => tcs.TrySetResult(
                    // Окно закрыли крестиком — решение по умолчанию: загрузить требуемую.
                    tcs.Task.IsCompleted
                        ? tcs.Task.Result
                        : Services.Ai.LmStudioMismatchDecision.LoadTarget);

                var panel = new StackPanel { Spacing = 4 };
                panel.Children.Add(text);
                panel.Children.Add(buttons);
                dialog.Content = panel;
                await dialog.ShowDialog(this);
            }
            catch (Exception)
            {
                // Окно недоступно (закрывается/свёрнуто в момент диалога) — дефолт.
                tcs.TrySetResult(Services.Ai.LmStudioMismatchDecision.LoadTarget);
            }
        });

        return await tcs.Task;
    }

    // Модалка первого запуска: конфиг создан из шаблона — ключи надо ввести.
    private async Task ShowFirstRunDialogAsync(MainViewModel viewModel)
    {
        var dialog = new Window
        {
            Title = "Первый запуск — настройка ключей",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#0A1A14")),
        };
        Controls.ModalClose.Attach(dialog); // Esc закрывает модалку
        var text = new TextBlock
        {
            Margin = new Avalonia.Thickness(24, 24, 24, 8),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Avalonia.Media.Brushes.White,
            Text = "Файл конфигурации в профиле не найден — создан со значениями по умолчанию:\n\n"
                   + viewModel.ConfigPathDisplay
                   + "\n\nСтарые ключи API туда не перенеслись (или их ещё нет). "
                   + "Откройте «Настройки» (шестерёнка) и введите ключи провайдеров — "
                   + "без них ИИ-цепь не соберётся; поиск и экспорт работают и так. "
                   + "Позже файл можно править и руками — он вне репо и при перекомпиляции не затирается.",
        };
        var ok = new Button
        {
            Content = "Понятно — настрою",
            Padding = new Avalonia.Thickness(18, 7),
            Margin = new Avalonia.Thickness(24, 8, 24, 20),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        // Изумрудный акцент и здесь: единственная кнопка модалки — главное
        // действие окна (та же логика, что у «Загрузить» в диалоге ниже).
        ok.Classes.Add("accent");
        ok.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(text);
        panel.Children.Add(ok);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private async void OpenSettings()
    {
        var vm = Program.Services?.GetRequiredService<SettingsViewModel>()
                 ?? new SettingsViewModel();
        await new SettingsWindow(vm).ShowDialog(this);

        // настройки могли добавить/убрать кастомные звенья — карточки
        // на дашборде обновляются сразу; встроенные ключи — после перезапуска
        // (WARN ConfigService.UpdateAiKey).
        (DataContext as MainViewModel)?.ReloadCustomLinks();
    }

    // (кнопка «Страны…» у гео-полей): модал текстового списка
    // разрешённых стран удалёнки. Чтение/запись — через MainViewModel
    // (SettingsKV geo.allowed); список действует сразу после закрытия.
    private void OnOpenCountries(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        new Views.GeoCountriesWindow(viewModel).ShowDialog(this);
    }

    // диалог выбора файла резюме (PDF/DOCX/TXT/MD; старый .doc тоже
    // можно выбрать — парсер честно откажет с подсказкой «сохрани как DOCX»).
    private async Task PickResumeFileAsync(MainViewModel viewModel)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выбери резюме — парсим сами (PDF · DOCX · TXT · MD)",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Резюме (PDF, DOCX, TXT, MD)")
                    {
                        Patterns = new[] { "*.pdf", "*.docx", "*.txt", "*.md" },
                    },
                    FilePickerFileTypes.All,
                },
            });

            if (files is { Count: > 0 })
            {
                var path = files[0].Path.LocalPath;
                viewModel.Resume.AddResumeCommand.Execute(path);
            }
        }
        catch (Exception ex)
        {
            // диалог не открылся — не роняем окно.
            viewModel.Resume.AddResumeCommand.Execute(""); // безопасный no-op
            Console.Error.WriteLine($"[resume] диалог файла не открылся: {ex.Message}");
        }
    }

    // модалка правки MD-канона активного профиля (панель плотная —
    // редактор живёт отдельным окном, сохранение — SaveCanonCommand).
    private void ShowCanonDialog(MainViewModel viewModel)
    {
        var profile = viewModel.Resume.ActiveProfile;
        if (profile is null)
        {
            return;
        }

        var dialog = new Window
        {
            Title = $"MD-канон — {profile.Name}",
            Width = 760,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#0A1A14")),
        };
        Controls.ModalClose.Attach(dialog); // Esc закрывает модалку
        var box = new TextBox
        {
            Text = profile.CanonMd,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Avalonia.Thickness(14, 14, 14, 6),
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#0F231B")),
            Foreground = Avalonia.Media.Brushes.White,
        };
        var save = new Button
        {
            Content = "Сохранить канон",
            Padding = new Avalonia.Thickness(16, 7),
            Margin = new Avalonia.Thickness(0, 6, 14, 14),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        save.Classes.Add("accent");
        save.Click += (_, _) =>
        {
            viewModel.Resume.SaveCanonCommand.Execute(box.Text);
            dialog.Close();
        };
        var panel = new DockPanel();
        DockPanel.SetDock(save, Dock.Bottom);
        panel.Children.Add(box);
        panel.Children.Add(save);
        dialog.Content = panel;
        _ = dialog.ShowDialog(this);
    }
}
