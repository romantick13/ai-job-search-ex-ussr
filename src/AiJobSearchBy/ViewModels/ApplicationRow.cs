using System.IO;

namespace AiJobSearchBy.ViewModels;

// WP-U1: строка окна истории откликов — «работодатель, дата-время отправки»
// (требование владельца); клик открывает модальное окно с вариантом резюме.
// STATE: immutable проекция ApplicationRecord.
public sealed class ApplicationRow
{
    public required Services.ApplicationRecord Record { get; init; }

    public string Employer => string.IsNullOrWhiteSpace(Record.Employer)
        ? "(работодатель не указан)"
        : Record.Employer;

    public string SentDisplay => Record.SentAt.ToString("dd.MM.yyyy HH:mm");

    public string Title => Record.VacancyTitle ?? "(без названия)";

    public string PdfName => Path.GetFileName(Record.CvPath ?? "");

    public bool PdfExists => !string.IsNullOrWhiteSpace(Record.CvPath) && File.Exists(Record.CvPath);

    public bool VectorPresent => !string.IsNullOrWhiteSpace(Record.VectorJson);
}
