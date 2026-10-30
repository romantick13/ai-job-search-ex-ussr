using System.IO;
using AiJobSearchBy.Controls;
using AiJobSearchBy.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchBy.Views;

// LIFECYCLE: у каждого Window публичный ctor без параметров.
public partial class PdfOverlayWindow : Window
{
    private readonly IPdfOverlay _overlay;
    private readonly ILogService _log;
    private string? _path;
    private PdfOverlayScan? _scan;

    public PdfOverlayWindow()
        : this(overlay: null, log: null)
    {
    }

    // FLOW: MainWindow («Заплатка PDF») -> выбор чужого PDF -> поиск
    // слов (PdfPig) -> «Залатать» (плашка + штамп, PdfSharpCore) -> индекс
    // PdfDocuments обновляется сервисом, вектор — кнопкой «⟳ PDF».
    public PdfOverlayWindow(IPdfOverlay? overlay, ILogService? log)
    {
        InitializeComponent();
        ModalClose.Attach(this); // Esc закрывает модалку

        // Окно с памятью — размер и позиция окна
        // заплатки PDF живут в layout.json.
        UiLayout.Attach(this, "PdfOverlayWindow");

        // Дизайнер/тесты без контейнера — как у MainWindow: прямой new с дефолтами.
        _overlay = overlay
            ?? Program.Services?.GetRequiredService<IPdfOverlay>()
            ?? new PdfOverlayService(store: null, new LogService(echoToConsole: false));
        _log = log
            ?? Program.Services?.GetRequiredService<ILogService>()
            ?? new LogService(echoToConsole: false);

        PickButton.Click += async (_, _) => await PickFileAsync();
        SearchButton.Click += async (_, _) => await SearchAsync();
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = SearchAsync();
            }
        };
        OverlayButton.Click += async (_, _) => await ApplyAsync();
        CloseButton.Click += (_, _) => Close();

        _log.Log("[pdf-overlay] открыто окно заплатки PDF");
    }

    // Диалог выбора PDF стартует из текущего каталога адаптированных
    // резюме (resume\ рядом с exe или
    // кастом из настроек) — там лежат готовые к индексации файлы. Без подсказки Windows
    // помнил последний использованный каталог.
    private async Task<IStorageFolder?> SuggestOutboxFolderAsync()
    {
        try
        {
            var store = Program.Services?.GetService<Services.ApplicationStore>();
            var dir = Services.ApplicationStore.ResolveOutbox(store?.GetSetting("outbox.path"));
            var candidates = new[]
            {
                Directory.Exists(dir) ? dir : null,
                Directory.Exists(AppContext.BaseDirectory) ? AppContext.BaseDirectory : null,
            };
            foreach (var path in candidates)
            {
                if (path is null)
                {
                    continue;
                }

                var folder = await StorageProvider.TryGetFolderFromPathAsync(path);
                if (folder is not null)
                {
                    return folder;
                }
            }
        }
        catch
        {
        // дизайнер/тесты без контейнера — диалог стартует как раньше
        }

        return null;
    }

    private async Task PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбери PDF для заплатки",
            AllowMultiple = false,
            SuggestedStartLocation = await SuggestOutboxFolderAsync(),
            FileTypeFilter =
            [
                new FilePickerFileType("Файл PDF")
                {
                    Patterns = ["*.pdf"],
                    MimeTypes = ["application/pdf"],
                },
            ],
        });
        if (files.Count == 0)
        {
            return; // отмена выбора — не событие протокола
        }

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            _log.LogWarn("skip: [pdf-overlay] файл не открыт — причина: у выбранного файла нет " +
                         "локального пути (виртуальное хранилище); решение: выбрать файл с диска");
            return;
        }

        _path = path;
        _scan = null;
        PdfPathText.Text = path;
        SearchButton.IsEnabled = true;
        OverlayButton.IsEnabled = false;
        OverlayButton.Content = "Залатать";
        HitsList.ItemsSource = null;
        MatchesText.Text = string.Empty;
        StatusText.Text = $"файл открыт: {Path.GetFileName(path)} — введи слово и нажми «Найти»";
        _log.Log($"[pdf-overlay] выбран файл: {path}");
    }

    // Поиск вхождений: слова со страниц чужого PDF (PdfPig внутри сервиса).
    private async Task SearchAsync()
    {
        if (_path is null)
        {
            return;
        }
        var find = FindBox.Text ?? string.Empty;
        if (find.Length == 0)
        {
            StatusText.Text = "введи слово или его часть — искать пустоту нечего";
            return;
        }

        SearchButton.IsEnabled = false;
        StatusText.Text = "ищу…";
        try
        {
            var matchCase = MatchCaseBox.IsChecked == true;
            var scan = await Task.Run(() => _overlay.FindAsync(_path!, find, matchCase));
            _scan = scan.Hits > 0 ? scan : null;
            HitsList.ItemsSource = scan.Items
                .Select(h => $"стр. {h.PageNumber} · «{h.Word}»")
                .ToList();
            MatchesText.Text = $"вхождений: {scan.Hits} · страниц: {scan.PageCount}";
            OverlayButton.Content = $"Залатать ({scan.Hits})";
            OverlayButton.IsEnabled = scan.Hits > 0;
            StatusText.Text = scan.Hits > 0
                ? $"найдено {scan.Hits} — проверь список и жми «Залатать»"
                : "вхождений нет — проверь регистр (флажок) или попробуй часть слова";
            if (scan.Hits == 0)
            {
                _log.LogWarn($"skip: [pdf-overlay] «{find}» в {Path.GetFileName(_path!)} не найдено — " +
                             "решение: поиск идёт по словам-токенам, попробуй часть слова без пунктуации");
            }
        }
        catch (Exception ex)
        {
            _scan = null;
            OverlayButton.IsEnabled = false;
            StatusText.Text = "файл не читается — причина в протоколе";
            _log.LogError($"[pdf-overlay] поиск не прошёл в {_path} — {ex.GetType().Name}: {ex.Message}; " +
                          "решение: файл повреждён или защищён паролем — проверить в просмотрщике");
        }
        SearchButton.IsEnabled = true;
    }

    // Заплатка: плашка + штамп по всем найденным вхождениям, атомарная
    // подмена файла, обновление индекса (внутри сервиса).
    private async Task ApplyAsync()
    {
        if (_path is null || _scan is null || _scan.Hits == 0)
        {
            return;
        }

        OverlayButton.IsEnabled = false;
        StatusText.Text = "заплатываю…";
        var find = FindBox.Text ?? string.Empty;
        try
        {
            var replace = ReplaceBox.Text; // пусто/null = закрасить
            var result = await Task.Run(() =>
                _overlay.ApplyAsync(_path!, _scan!.Items, replace));
            StatusText.Text = result.Applied > 0
                ? $"готово: {result.Applied} × «{find}» — индекс обновлён; вектор перестроит «⟳ PDF»"
                : "нечего платить — сначала «Найти»";
            _log.LogSuccess($"[pdf-overlay] окно: заплачено {result.Applied} вх. в {Path.GetFileName(_path!)}");

            // Свежий скан: список вхождений после заплатки.
            await SearchAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "заплатка не прошла — причина в протоколе";
            _log.LogError($"[pdf-overlay] заплатка не прошла в {_path} — {ex.GetType().Name}: {ex.Message}; " +
                          "решение: закрыть PDF в просмотрщике (файл занят) и повторить");
        }
        OverlayButton.IsEnabled = _scan is not null && _scan.Hits > 0;
    }
}
