using System.Diagnostics;
using AiJobSearchBy.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiJobSearchBy.Views;

// LIFECYCLE: у каждого Window публичный ctor без параметров (RFC-001 §2).
public partial class ApplicationDetailsWindow : Window
{
    public ApplicationDetailsWindow()
    {
        InitializeComponent();
    }

    // FLOW: MainWindow.HistoryList SelectionChanged -> ShowDialog(владелец).
    // STATE: рендерит снимок ApplicationRecord; мутаций нет — окно чтения.
    public ApplicationDetailsWindow(ApplicationRow application)
    {
        InitializeComponent();
        Title = $"Отправленный отклик — {application.Employer}";
        EmployerText.Text = application.Employer;
        SentText.Text = application.SentDisplay;
        TitleText.Text = $"Вакансия: {application.Title}";
        UrlText.Text = application.Record.VacancyUrl;
        ResumeTextBox.Text = application.Record.ResumeText ?? "(текст не сохранён)";
        PdfPathText.Text = $"PDF: {application.Record.CvPath ?? "—"}";

        var vector = Services.Ai.VectorJson.From(application.Record.VectorJson);
        VectorText.Text = vector is null
            ? "вектор: не построен (локальный эмбеддер выключен или сбой — причина в протоколе)"
            : $"вектор: {vector.Length} измерений ({application.Record.VectorModel})";

        OpenPdfButton.Click += (_, _) => OpenPath(application.Record.CvPath);
        OpenPdfButton.IsEnabled = application.PdfExists;
        OpenVacancyButton.Click += (_, _) => OpenUrl(application.Record.VacancyUrl);
        CloseButton.Click += (_, _) => Close();
    }

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
