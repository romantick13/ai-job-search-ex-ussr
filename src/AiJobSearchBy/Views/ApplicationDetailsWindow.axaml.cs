using System.Diagnostics;
using AiJobSearchBy.Controls;
using AiJobSearchBy.Services;
using AiJobSearchBy.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform; // ClipboardExtensions.SetTextAsync (Avalonia 12)
using Avalonia.Interactivity;

namespace AiJobSearchBy.Views;

// LIFECYCLE: у каждого Window публичный ctor без параметров.
public partial class ApplicationDetailsWindow : Window
{
    // Текст резюме отклика (не подделка-плашка «(текст не сохранён)»):
    // источник для «Скопировать в буфер»; null — копировать нечего.
    private string? _resumeText;

    public ApplicationDetailsWindow()
    {
        InitializeComponent();
        ModalClose.Attach(this); // Esc закрывает модалку
    }

    // FLOW: MainWindow.HistoryList SelectionChanged -> ShowDialog.
    // STATE: рендерит снимок ApplicationRecord; мутаций нет — окно чтения.
    public ApplicationDetailsWindow(ApplicationRow application)
    {
        InitializeComponent();
        ModalClose.Attach(this); // Esc закрывает модалку

        // Окно с памятью — размер и позиция окна
        // просмотра отклика живут в layout.json.
        UiLayout.Attach(this, "ApplicationDetailsWindow");
        Title = $"Адаптированный отклик — {application.Employer}";
        EmployerText.Text = application.Employer;
        SentText.Text = application.SentDisplay;
        TitleText.Text = $"Вакансия: {application.Title}";
        UrlText.Text = application.Record.VacancyUrl;
        // Пустой текст старых записей (глух 2026-10-29) показываем честной
        // плашкой — не пустой белизной, которую легко принять за сбой окна.
        _resumeText = string.IsNullOrWhiteSpace(application.Record.ResumeText)
            ? null
            : application.Record.ResumeText;
        ResumeTextBox.Text = _resumeText ?? "(текст не сохранён)";
        // Честная семантика истории (решение владельца 2026-10-29): запись
        // появляется в момент успешной адаптации — отправка на сайте за человеком.
        HonestHintText.Text = "Честно: запись здесь появляется в момент успешной адаптации, " +
                              "а не фактической отправки. Отклик отправляешь сам: «Открыть вакансию» → " +
                              "на сайте кнопка «Откликнуться» → вставь скопированный текст.";
        PdfPathText.Text = $"PDF: {application.Record.CvPath ?? "—"}";
        RefreshCopyState();

        OpenPdfButton.Click += (_, _) => OpenPath(application.Record.CvPath);
        OpenPdfButton.IsEnabled = application.PdfExists;

        // Точечная правка DOCX нативно — модальное окно редактора поверх деталей отклика.
        EditDocxButton.Click += (_, _) => _ = new DocxEditWindow().ShowDialog(this);

        // Правка текста
        // отправленного резюме + пересборка PDF в той же модалке. Модалка
        // возвращает обновлённый отклик — поля деталей перечитываются, чтобы
        // окно не показывало устаревший снимок.
        EditResumeButton.Click += async (_, _) =>
        {
            var updated = await new DocxEditWindow(application.Record)
                .ShowDialog<ApplicationRecord?>(this);
            if (updated is null)
            {
                return; // закрыто без сохранения — снимок деталей не меняется
            }
            _resumeText = string.IsNullOrWhiteSpace(updated.ResumeText)
                ? null
                : updated.ResumeText;
            ResumeTextBox.Text = _resumeText ?? "(текст не сохранён)";
            PdfPathText.Text = $"PDF: {updated.CvPath ?? "—"}";
            OpenPdfButton.IsEnabled = System.IO.File.Exists(updated.CvPath);
            RefreshCopyState();
        };
        // «Скопировать в буфер» (решение владельца 2026-10-29): путь отклика —
        // прочитал → поправил → скопировал → на сайте «Откликнуться» → вставил.
        // Фидбек на кнопке: «✓ в буфере» на 2 секунды — без тихого успеха.
        CopyButton.Click += async (_, _) =>
        {
            if (_resumeText is null || Clipboard is null)
            {
                return; // копировать нечего / буфер недоступен — кнопка и так выключена
            }
            await Clipboard.SetTextAsync(_resumeText);
            CopyButton.Content = "✓ в буфере";
            CopyButton.IsEnabled = false;
            await Task.Delay(2000);
            CopyButton.Content = "Скопировать в буфер";
            RefreshCopyState();
        };
        OpenVacancyButton.Click += (_, _) => OpenUrl(application.Record.VacancyUrl);
        CloseButton.Click += (_, _) => Close();
    }

    // Кнопка копирования активна, только когда есть что копировать:
    // глухие записи 2026-10-29 (пустой текст) честно её выключают.
    private void RefreshCopyState()
        => CopyButton.IsEnabled = _resumeText is not null;

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
        // Просмотрщик не назначен — окно остаётся с текстом резюме;
        // путь виден в нижней плашке, файл можно открыть вручную.
        }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        // Браузер не открылся — ссылка видна в окне, копируется руками.
        }
    }
}
