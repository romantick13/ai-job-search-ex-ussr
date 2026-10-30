using AiJobSearchBy.Services;
using AiJobSearchBy.Tests.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: PdfOverlayService — точечная заплатка чужого
// PDF: белый прямоугольник поверх слова + штамп замены, атомарная подмена,
// обновление строки PdfDocuments. LIFECYCLE: каждый тест — своя temp-папка
// (база + файлы), состояния между тестами нет. THREAD: однопоточно.
public class PdfOverlayServiceTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();
    private readonly ApplicationStore _store;
    private readonly PdfOverlayService _service;

    public PdfOverlayServiceTests()
    {
        _store = new ApplicationStore(
            Path.Combine(_dir, "test.db"), new LogService(_dir, echoToConsole: false));
        _store.Initialize();
        _service = new PdfOverlayService(_store, new LogService(_dir, echoToConsole: false));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    // Тестовый «чужой» PDF: две строки кириллицы (шрифт резолвера — Segoe UI
    // на Windows, DejaVu в песочнице; ставится конструктором сервиса).
    private string CreatePdf(string name, string line1, string line2)
    {
        var path = Path.Combine(_dir, name);
        var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Segoe UI", 11, XFontStyleEx.Regular,
                new XPdfFontOptions(PdfFontEncoding.Unicode));
            gfx.DrawString(line1, font, XBrushes.Black, 50, 100, XStringFormats.BaseLineLeft);
            gfx.DrawString(line2, font, XBrushes.Black, 50, 130, XStringFormats.BaseLineLeft);
        }
        doc.Save(path);
        return path;
    }

    private static string ExtractAllText(string path)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join(" ", pig.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
    }

    private static string HashOf(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private string IndexedPdf(string name, string line1, string line2)
    {
        var path = CreatePdf(name, line1, line2);
        _store.SavePdfIndex(new PdfDocumentRecord
        {
            Id = Guid.NewGuid(),
            FilePath = path,
            FileHash = HashOf(path),
            ExtractedText = $"{line1} {line2}",
            VectorJson = null,
            VectorModel = null,
            IndexedAt = DateTimeOffset.Now,
        });
        return path;
    }

    [Fact]
    public async Task Find_FindsWord_WithPageNumber()
    {
        var path = CreatePdf("find.pdf", "ЗАО Ромашка, инженер", "вторая строка");

        var scan = await _service.FindAsync(path, "Ромашка", matchCase: false);

        Assert.Equal(1, scan.PageCount);
        Assert.Equal(1, scan.Hits);
        var hit = Assert.Single(scan.Items);
        Assert.Equal(1, hit.PageNumber);
        Assert.Contains("Ромашка", hit.Word, StringComparison.Ordinal);
        Assert.True(hit.Width > 0 && hit.Height > 0);
    }

    [Fact]
    public async Task Overlay_ReplacesWord_AndRefreshesIndex()
    {
        var path = IndexedPdf("ovl-apply.pdf", "ЗАО Ромашка, инженер", "строка для контекста");
        var oldHash = HashOf(path);
        var scan = await _service.FindAsync(path, "Ромашка", matchCase: false);

        var result = await _service.ApplyAsync(path, scan.Items, "Ромашки");

        Assert.Equal(1, result.Applied);
        Assert.Equal(path, result.Path);
        Assert.True(File.Exists(path));
        Assert.Contains("Ромашки", ExtractAllText(path), StringComparison.Ordinal);
        var row = Assert.Single(_store.ListIndexedPdfs(), p =>
            string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(oldHash, row.FileHash);
        Assert.Contains("Ромашки", row.ExtractedText, StringComparison.Ordinal);
        Assert.Null(row.VectorJson);
        Assert.Null(row.VectorModel);
    }

    [Fact]
    public async Task Overlay_WithNoHits_FileByteIdentical()
    {
        var path = CreatePdf("ovl-none.pdf", "обычный текст", "без совпадений");
        var before = File.ReadAllBytes(path);
        var scan = await _service.FindAsync(path, "Люмпен", matchCase: false);
        Assert.Equal(0, scan.Hits);

        var result = await _service.ApplyAsync(path, scan.Items, "замена");

        Assert.Equal(0, result.Applied);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Overlay_EmptyReplace_WhitesOut()
    {
        var path = IndexedPdf("ovl-white.pdf", "ЗАО Ромашка, инженер", "контекст");
        var scan = await _service.FindAsync(path, "Ромашка", matchCase: false);

        var result = await _service.ApplyAsync(path, scan.Items, replace: null);

        Assert.Equal(1, result.Applied);
        // Осознанная граница: плашка закрывает слово визуально, текстовый слой
        // сохраняется (поиск по базе найдёт и старое, и новое слово).
        Assert.Contains("Ромашка", ExtractAllText(path), StringComparison.Ordinal);
        var row = Assert.Single(_store.ListIndexedPdfs());
        Assert.Null(row.VectorModel);
    }

    [Fact]
    public async Task Find_GarbageFile_Throws()
    {
        var path = Path.Combine(_dir, "garbage.pdf");
        File.WriteAllText(path, "это не pdf, просто текст");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _service.FindAsync(path, "Ромашка", matchCase: false));
    }

    [Fact]
    public async Task Overlay_NotIndexedFile_StillApplies()
    {
        var path = CreatePdf("ovl-outside.pdf", "привет Ромашка", "вне индекса");

        var scan = await _service.FindAsync(path, "Ромашка", matchCase: false);
        var result = await _service.ApplyAsync(path, scan.Items, "Продукты");

        Assert.Equal(1, result.Applied);
        Assert.DoesNotContain(_store.ListIndexedPdfs(), p =>
            string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Overlay_LongReplacement_ShrinksFont()
    {
        var path = CreatePdf("ovl-long.pdf", "ЗАО Ромашка, инженер", "контекст");
        var scan = await _service.FindAsync(path, "Ромашка", matchCase: false);

        var longText = new string('Я', 60);
        var result = await _service.ApplyAsync(path, scan.Items, longText);

        Assert.Equal(1, result.Applied);
        Assert.True(File.Exists(path));
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        Assert.Equal(1, pig.NumberOfPages);
    }
}
