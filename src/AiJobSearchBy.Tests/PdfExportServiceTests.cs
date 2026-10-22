using AiJobSearchBy.Services;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: PdfExportService (WP-U1) — имя файла по требованию владельца
// «работодатель_дата-время», чистка мусора в имени, генерация PDF (QuestPDF,
// Community-лицензия идемпотентно).
// LIFECYCLE: каждый тест — своя temp-папка; сгенерированные файлы удаляются.
public class PdfExportServiceTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    [Fact]
    public void BuildPdfFileName_EmployerAndTimestamp()
    {
        var name = PdfExportService.BuildPdfFileName(
            "ООО Ромашка", new DateTimeOffset(2026, 10, 22, 14, 15, 30, TimeSpan.Zero));

        Assert.Equal("ООО Ромашка_20261022_141530.pdf", name);
    }

    [Theory]
    [InlineData("ООО <Ромашка> \"Лютик\"", "ООО Ромашка Лютик.pdf")]
    [InlineData("  Acme   Corp  ", "Acme Corp.pdf")]
    [InlineData("ЗАО*Василёк?", "ЗАО Василёк.pdf")]
    [InlineData("", "работодатель.pdf")]
    [InlineData("///", "работодатель.pdf")]
    [InlineData("A<B>C|D:E", "A B C D E.pdf")]
    public void SanitizeFileName_StripsInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, PdfExportService.SanitizeFileName(input) + ".pdf");
    }

    [Fact]
    public async Task SaveResumePdf_CreatesValidPdfFile()
    {
        var exporter = new PdfExportService(new LogService(_dir, echoToConsole: false));
        var outDir = Path.Combine(_dir, "out");

        var path = await exporter.SaveResumePdfAsync(
            "ООО Ромашка", "Senior Developer",
            "Опыт работы:\n— 5 лет разработки\n\nНавыки: C#, Avalonia",
            new DateTimeOffset(2026, 10, 22, 14, 15, 30, TimeSpan.Zero), outDir);

        Assert.True(File.Exists(path));
        // Имя по требованию владельца: работодатель + дата-время создания.
        Assert.EndsWith("ООО Ромашка_20261022_141530.pdf", path);

        // Валидный PDF: магический заголовок %PDF- и разумный размер.
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 500);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));

        // PdfPig (тот же пин, что у индексатора) открывает наш файл и читает текст.
        var extracted = PdfIndexerService.ExtractText(path);
        Assert.False(string.IsNullOrWhiteSpace(extracted));
        Assert.Contains("ООО Ромашка", extracted!);
    }
}
