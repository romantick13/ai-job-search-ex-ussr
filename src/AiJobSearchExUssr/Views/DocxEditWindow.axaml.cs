using System.IO;
using AiJobSearchExUssr.Controls;
using AiJobSearchExUssr.Services;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchExUssr.Views;

// LIFECYCLE: у каждого Window публичный ctor без параметров.
public partial class DocxEditWindow : Window
{
    private readonly IDocxTextEditor _editor;
    private readonly ILogService _log;
    private readonly IPdfExporter? _pdf;
    private readonly IApplicationStore? _store;
    private readonly ApplicationRecord? _record;
    private IReadOnlyList<string> _paragraphs = [];
    private string? _path;
    private string _savedText = "";

    public DocxEditWindow()
        : this(application: null)
    {
    }

    // FLOW: ApplicationDetailsWindow («Править резюме») -> этот диалог
    // поверх; по успешному сохранению возвращает обновлённый ApplicationRecord,
    // null — закрыто без сохранения (окно деталей свой снимок не меняет).
    public DocxEditWindow(ApplicationRecord? application)
        : this(application, editor: null, log: null)
    {
    }

    // FLOW: «Править DOCX» -> выбор файла с диска, точечная замена.
    // STATE: локальный снимок абзацев/текста для превью и подсчёта вхождений;
    // в режиме DOCX файл правится только по кнопке «Заменить и сохранить», в
    // режиме отклика база и PDF меняются только по «Сохранить и пересобрать PDF».
    public DocxEditWindow(ApplicationRecord? application, IDocxTextEditor? editor,
        ILogService? log, IPdfExporter? pdf = null, IApplicationStore? store = null)
    {
        InitializeComponent();
        ModalClose.Attach(this); // Esc закрывает модалку

        // Окно с памятью — размер и позиция окна правки
        // DOCX живут в layout.json.
        UiLayout.Attach(this, "DocxEditWindow");

        _record = application;
        _pdf = pdf ?? Program.Services?.GetRequiredService<IPdfExporter>();
        _store = store ?? Program.Services?.GetRequiredService<IApplicationStore>();

        // Дизайнер/тесты без контейнера — как у MainWindow: прямой new с дефолтами.
        _editor = editor
            ?? Program.Services?.GetRequiredService<IDocxTextEditor>()
            ?? new DocxTextEditService(new LogService(echoToConsole: false));
        _log = log
            ?? Program.Services?.GetRequiredService<ILogService>()
            ?? new LogService(echoToConsole: false);

        PickButton.Click += async (_, _) => await PickFileAsync();
        FindBox.TextChanged += (_, _) => RefreshMatches();
        ReplaceBox.TextChanged += (_, _) => RefreshMatches();
        MatchCaseBox.Click += (_, _) => RefreshMatches();
        ReplaceButton.Click += async (_, _) => await ApplyReplaceAsync();
        SaveResumeButton.Click += async (_, _) => await SaveResumeAsync();
        PreviewBox.TextChanged += (_, _) => RefreshSaveState();
        CloseButton.Click += (_, _) => Close();

        if (_record is null)
        {
            return; // режим правки DOCX — как был
        }

        // Режим правки отправленного резюме — файл не выбирается, текст
        // редактируется прямо в окне, сохранение пересобирает наш PDF.
        Title = "Правка резюме отклика — ai-job-search-ex-ussr";
        PickButton.IsVisible = false;
        DocxPathText.Text = $"отклик: {_record.Employer} · {_record.VacancyTitle ?? "вакансия"}";
        ReplaceButton.Content = "Заменить в тексте";
        PreviewTitle.Text = "Текст отправленного резюме";
        FootHint.Text = "Правь текст руками или заменой, затем «Сохранить и пересобрать PDF» — " +
                        "обновятся и текст в базе, и наш PDF-файл (тот же шаблон, то же имя). " +
                        "Закрытие без сохранения отбрасывает правки.";
        PreviewBox.IsReadOnly = false;
        _savedText = _record.ResumeText ?? "";
        PreviewBox.Text = _savedText;
        SaveResumeButton.IsVisible = true;
        StatusText.Text = "правь текст; сохранение — кнопкой внизу";
        _log.Log($"[resume] открыт текст отклика {_record.Id} ({_savedText.Length} зн.)");
    }

    private async Task PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбери DOCX для правки",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Документ Word (DOCX)")
                {
                    Patterns = ["*.docx"],
                    MimeTypes =
                        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
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
            _log.LogWarn("skip: [docx] файл не открыт — причина: у выбранного файла нет " +
                         "локального пути (виртуальное хранилище); решение: выбрать файл с диска");
            return;
        }

        await LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            var text = await Task.Run(() => _editor.ReadTextAsync(path));
            _path = path;
            _paragraphs = text.Paragraphs;
            DocxPathText.Text = path;
            PreviewBox.Text = string.Join(Environment.NewLine, text.Paragraphs);
            StatusText.Text = $"файл открыт: {Path.GetFileName(path)}, " +
                              $"абзацев: {text.Paragraphs.Count}";
            _log.Log($"[docx] открыт: {path} ({text.Paragraphs.Count} абзацев)");
        }
        catch (Exception ex)
        {
            _path = null;
            _paragraphs = [];
            StatusText.Text = "файл не открылся — причина в протоколе";
            _log.LogError($"[docx] не открылся {path} — {ex.GetType().Name}: {ex.Message}; " +
                          "решение: убедиться, что это .docx (не .doc/.rtf) и файл не занят " +
                          "другой программой");
        }
        RefreshMatches();
    }

    private void RefreshMatches()
    {
        var find = FindBox.Text ?? string.Empty;
        var hasTarget = _record is not null || _path is not null;
        var matches = _record is not null
            ? CountMatchesInText(PreviewBox.Text ?? string.Empty, find, MatchCaseBox.IsChecked == true)
            : CountMatches(_paragraphs, find, MatchCaseBox.IsChecked == true);
        MatchesText.Text = !hasTarget
            ? string.Empty
            : find.Length == 0
                ? "введи текст, который нужно найти"
                : $"вхождений: {matches}";
        // Подсчёт здесь — оценка по превью; точный счёт правки вернёт сервис.
        ReplaceButton.IsEnabled = hasTarget && find.Length > 0 && matches > 0;
    }

    // Кнопка сохранения живёт только в режиме отклика и активируется,
    // когда текст в редакторе разошёлся с сохранённым.
    private void RefreshSaveState()
    {
        if (_record is null)
        {
            return;
        }
        SaveResumeButton.IsEnabled =
            !string.Equals(PreviewBox.Text, _savedText, StringComparison.Ordinal);
    }

    private async Task ApplyReplaceAsync()
    {
        var find = FindBox.Text ?? string.Empty;
        if (find.Length == 0)
        {
            return;
        }
        var replace = ReplaceBox.Text; // null/пусто = удалить найденное
        var matchCase = MatchCaseBox.IsChecked == true;

        if (_record is not null)
        {
            // Замена только в тексте редактора; база и PDF меняются
            // отдельной кнопкой — случайная замена не уедет в файл молча.
            var text = PreviewBox.Text ?? string.Empty;
            var matches = CountMatchesInText(text, find, matchCase);
            if (matches == 0)
            {
                StatusText.Text = "вхождений не найдено — текст не менялся";
                _log.LogWarn($"skip: [resume] замена не выполнена — причина: «{find}» в " +
                             "тексте не найдено; решение: проверить регистр (флажок) или текст");
                return;
            }
            var comparison = matchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            PreviewBox.Text = text.Replace(find, replace, comparison);
            StatusText.Text = $"готово: заменено {matches} × «{find}» в редакторе — " +
                              "сохрани кнопкой «Сохранить и пересобрать PDF»";
            return;
        }

        if (_path is null)
        {
            return;
        }

        ReplaceButton.IsEnabled = false;
        StatusText.Text = "сохраняю…";
        try
        {
            var result = await Task.Run(() =>
                _editor.ReplaceAsync(_path!, find, replace, matchCase));
            var text = await Task.Run(() => _editor.ReadTextAsync(_path!));
            _paragraphs = text.Paragraphs;
            PreviewBox.Text = string.Join(Environment.NewLine, text.Paragraphs);
            StatusText.Text = result.Replacements == 0
                ? "вхождений не найдено — файл не менялся"
                : $"готово: заменено {result.Replacements} × «{find}»";
        }
        catch (Exception ex)
        {
            StatusText.Text = "замена не прошла — причина в протоколе";
            _log.LogError($"[docx] замена не прошла в {_path} — {ex.GetType().Name}: {ex.Message}; " +
                          "решение: закрыть файл в Word/LibreOffice (файл занят) и повторить; " +
                          "проверить, что это .docx");
        }
        RefreshMatches();
    }

    // Шаг 2: пересборка нашего PDF тем же шаблоном (IPdfExporter) и точечное
    // обновление отклика в базе. Порядок: PDF -> база -> закрытие с результатом;
    // сбой PDF базу не трогает, сбой базы оставляет окно открытым с ошибкой.
    private async Task SaveResumeAsync()
    {
        if (_record is null || _pdf is null || _store is null)
        {
            return;
        }
        var text = PreviewBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "текст пуст — сохранение отменено";
            _log.LogWarn("skip: [resume] сохранение не выполнено — причина: текст резюме пуст; " +
                         "решение: вернуть текст (правка отменяется закрытием без сохранения)");
            return;
        }

        SaveResumeButton.IsEnabled = false;
        StatusText.Text = "пересобираю PDF…";
        try
        {
            // Имя PDF детерминировано (работодатель + CreatedAt): пересборка
            // ложится в тот же файл. Прежний путь пропал/переименован — файл
            // соберётся в папку откликов, а путь в базе обновится.
            var cvDir = Path.GetDirectoryName(_record.CvPath);
            var targetDir = !string.IsNullOrWhiteSpace(cvDir) && Directory.Exists(cvDir)
                ? cvDir!
                : ApplicationStore.DefaultOutboxDir; // <exe>\resume
            var pdfPath = await Task.Run(() => _pdf!.SaveResumePdfAsync(
                _record.Employer, _record.VacancyTitle, text, _record.CreatedAt, targetDir));

            if (!string.Equals(pdfPath, _record.CvPath, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarn($"[resume] прежний PDF недоступен ({_record.CvPath ?? "—"}) — " +
                             $"файл собран в новый путь: {pdfPath}; путь в базе обновлён");
            }

            var saved = await Task.Run(() =>
                _store!.UpdateApplicationResume(_record.Id, text, pdfPath));
            if (!saved)
            {
                StatusText.Text = "отклик не найден в базе — текст не сохранён";
                _log.LogError($"[resume] отклик {_record.Id} не найден в базе — обновление " +
                              "не выполнено; решение: открыть отклик заново из истории");
                RefreshSaveState();
                return;
            }

            _savedText = text;
            _log.LogSuccess($"[resume] текст отклика обновлён ({text.Length} зн.), PDF: {pdfPath}");
            _log.Log("[resume] вектор отклика оставлен прежним: точечная правка текста не " +
                     "перестраивает векторную ссылку (новый вектор — при новой отправке)");
            StatusText.Text = "готово: текст в базе обновлён, PDF пересобран";
            // Результат — окну деталей: оно перечитает текст и путь PDF.
            Close(_record with { ResumeText = text, CvPath = pdfPath });
        }
        catch (Exception ex)
        {
            StatusText.Text = "сохранение не прошло — причина в протоколе";
            _log.LogError($"[resume] пересборка PDF не прошла ({_record.Employer}) — " +
                          $"{ex.GetType().Name}: {ex.Message}; решение: закрыть PDF в " +
                          "просмотрщике (файл занят) и повторить сохранение");
            RefreshSaveState();
        }
    }

    // Оценка по превью (тот же перебор непересекающихся вхождений, что и в сервисе).
    private static int CountMatches(IReadOnlyList<string> paragraphs, string find, bool matchCase)
    {
        if (find.Length == 0)
        {
            return 0;
        }
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var count = 0;
        foreach (var paragraph in paragraphs)
        {
            var at = paragraph.IndexOf(find, comparison);
            while (at >= 0)
            {
                count++;
                at = paragraph.IndexOf(find, at + find.Length, comparison);
            }
        }
        return count;
    }

    // Тот же подсчёт по одному тексту (резюме отклика).
    private static int CountMatchesInText(string text, string find, bool matchCase)
    {
        if (find.Length == 0)
        {
            return 0;
        }
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var count = 0;
        var at = text.IndexOf(find, comparison);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(find, at + find.Length, comparison);
        }
        return count;
    }
}
