using System.IO;
using AiJobSearchBy.Services;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AiJobSearchBy.Services;

// ============================================================================
// генерация PDF отклика.
// QuestPDF 2026.8.0 — пин THIRD-PARTY-NOTICES.md §2; Community-лицензия
// выставляется идемпотентно (architecture.md §3.5), строка о «Built with
// QuestPDF» — в README и NOTICE (пин канона).

// Имя файла — требование «имя работодателя и дата (время создания
// в имени)»: {Работодатель}_{yyyyMMdd_HHmmss}.pdf; мусор в имени работодателя
// вычищается SanitizeFileName (пустое имя → «работодатель», файл никогда
// не безымянный).

// FLOW: MainViewModel.SendCommand -> SaveResumePdfAsync -> путь файла ->
// Applications.CvPath + PdfIndexerService (векторная ссылка в базе).
// STATE: без состояния; папка откликов передаётся на вызов (SettingsKV,
// дефолт Документы\ai-job-search-by).
// THREAD: QuestPDF.GeneratePdf потокобезопасен для разных документов; вызовы
// UI-кнопки последовательно.
// ============================================================================
public interface IPdfExporter
{
    // Создаёт папку при отсутствии, пишет PDF, возвращает полный путь.
    Task<string> SaveResumePdfAsync(string employer, string? vacancyTitle,
        string resumeText, DateTimeOffset createdAt, string outDir);
}

public sealed class PdfExportService : IPdfExporter
{
    private readonly ILogService _log;

    static PdfExportService()
    {
        // Идемпотентная установка лицензии (architecture.md §3.5): Community — бесплатно
        // для организаций с годовым доходом < $1M; строка лицензии — в README/NOTICE.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public PdfExportService(ILogService log)
    {
        _log = log;
    }

    // Требование работодатель + дата-время создания в имени.
    public static string BuildPdfFileName(string employer, DateTimeOffset at)
        => $"{SanitizeFileName(employer)}_{at:yyyyMMdd_HHmmss}.pdf";

    // Мусор в имени работодателя. НАБОР СИМВОЛОВ ПЛАТФОРМОНЕЗАВИСИМЫЙ: на
    // Linux Path.GetInvalidFileNameChars не знает про < > | : ? * — а файл
    // едет на Windows-сайт юзера. Чистим union Windows-запрещённых,
    // управляющие символы и переводы строк; хвостовые точки/пробелы (Windows);
    // длина > 80. Пробелы схлопываются, пустое имя → «работодатель».
    private static readonly char[] ForbiddenFileNameChars =
        ['"', '*', '/', ':', '<', '>', '?', '\\', '|', '\0'];

    public static string SanitizeFileName(string name)
    {
        var cleaned = new string(name
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Select(c => char.IsControl(c) || ForbiddenFileNameChars.Contains(c) ? ' ' : c)
            .ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80].TrimEnd();
        }
        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "работодатель" : cleaned;
    }

    public Task<string> SaveResumePdfAsync(string employer, string? vacancyTitle,
        string resumeText, DateTimeOffset createdAt, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, BuildPdfFileName(employer, createdAt));

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontFamily("Segoe UI").FontSize(10.5f).LineHeight(1.35f));

                page.Header().Column(col =>
                {
                    col.Item().PaddingBottom(2).Text(text =>
                    {
                        text.Span("Отклик на вакансию").FontSize(9).FontColor(Colors.Grey.Darken1);
                    });
                    col.Item().Text(text =>
                    {
                        text.Span(vacancyTitle ?? "вакансия").FontSize(14).SemiBold();
                    });
                    col.Item().PaddingBottom(6).Text(text =>
                    {
                        text.Span(employer).FontSize(11);
                        text.Span($"   ·   создан {createdAt:dd.MM.yyyy HH:mm}").FontSize(9).FontColor(Colors.Grey.Darken1);
                    });
                });

                page.Content().Column(col =>
                {
                    foreach (var line in resumeText.Replace("\r\n", "\n").Split('\n'))
                    {
                        var trimmed = line.TrimEnd();
                        if (trimmed.Length == 0)
                        {
                            col.Item().PaddingTop(4);
                            continue;
                        }
                        col.Item().PaddingBottom(2).Text(trimmed);
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Сформировано ai-job-search-by · Built with QuestPDF")
                        .FontSize(8).FontColor(Colors.Grey.Lighten1);
                });
            });
        }).GeneratePdf(path);

        _log.Log($"[pdf] файл отклика создан: {path}");
        return Task.FromResult(path);
    }
}
