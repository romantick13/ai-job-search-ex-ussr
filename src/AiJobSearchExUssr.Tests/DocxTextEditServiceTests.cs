using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Tests.Logging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// DocxTextEditService —
// точечная замена текста в DOCX с сохранением форматирования, «рваные» run-ы
// («при»+«вет»), таблицы, атомарная подмена файла.
// LIFECYCLE: каждый тест — своя temp-папка; документы создаются на месте и удаляются.
public class DocxTextEditServiceTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private DocxTextEditService CreateService()
        => new(new LogService(_dir, echoToConsole: false));

    private string CreateDocx(string name, params OpenXmlElement[] bodyChildren)
    {
        var path = Path.Combine(_dir, name);
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(bodyChildren));
            main.Document.Save();
        }
        return path;
    }

    private static List<string> ReadParagraphs(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart!.Document.Body!
            .Descendants<Paragraph>()
            .Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text ?? string.Empty)))
            .ToList();
    }

    private static byte[] ReadBytes(string path) => File.ReadAllBytes(path);

    [Fact]
    public async Task ReadText_ParagraphsIncludeTables()
    {
        var path = CreateDocx(
            "read.docx",
            new Paragraph(new Run(new Text("Опыт: пять лет"))),
            new Paragraph(new Run(new Text("Город: Минск"))),
            new Table(
                new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }),
                new TableGrid(new GridColumn()),
                new TableRow(new TableCell(new Paragraph(new Run(new Text("C# разработчик")))))));

        var text = await CreateService().ReadTextAsync(path);

        Assert.Equal(3, text.Paragraphs.Count);
        Assert.Contains("C# разработчик", text.Paragraphs);
    }

    [Fact]
    public async Task Replace_SingleRun_KeepsBoldFormatting()
    {
        var path = CreateDocx(
            "bold.docx",
            new Paragraph(
                new Run(new RunProperties(new Bold()), new Text("Опыт: пять лет"))),
            new Paragraph(new Run(new Text("Город: Минск"))));

        var result = await CreateService().ReplaceAsync(path, "пять", "шесть", matchCase: false);

        Assert.Equal(1, result.Replacements);
        Assert.Contains("Опыт: шесть лет",
            (await CreateService().ReadTextAsync(path)).Paragraphs);
        // Форматирование не тронуто: текст остался в исходном жирном run-е.
        using var doc = WordprocessingDocument.Open(path, false);
        var run = doc.MainDocumentPart!.Document.Descendants<Run>()
            .Single(r => r.Descendants<Text>().Any(t => (t.Text ?? string.Empty).Contains("шесть")));
        Assert.NotNull(run.GetFirstChild<RunProperties>()?.Bold);
    }

    [Fact]
    public async Task Replace_AcrossSplitRuns_JoinsPieces()
    {
        // Word режет текст на произвольные run-ы: «привет» лежит как «при»+«вет».
        var path = CreateDocx(
            "split.docx",
            new Paragraph(new Run(new Text("при")), new Run(new Text("вет, мир"))));

        var result = await CreateService().ReplaceAsync(path, "привет", "Привет", matchCase: false);

        Assert.Equal(1, result.Replacements);
        var paragraphs = (await CreateService().ReadTextAsync(path)).Paragraphs;
        Assert.Equal("Привет, мир", paragraphs[0]);
    }

    [Fact]
    public async Task Replace_InTableCell()
    {
        var path = CreateDocx(
            "table.docx",
            new Table(
                new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }),
                new TableGrid(new GridColumn()),
                new TableRow(new TableCell(new Paragraph(new Run(new Text("C# разработчик")))))));

        var result = await CreateService().ReplaceAsync(path, "C#", "Python", matchCase: true);

        Assert.Equal(1, result.Replacements);
        Assert.Contains("Python разработчик",
            (await CreateService().ReadTextAsync(path)).Paragraphs);
    }

    [Fact]
    public async Task Replace_MultipleOccurrences_AllReplaced()
    {
        var path = CreateDocx(
            "multi.docx",
            new Paragraph(new Run(new Text("а, а, а"))));

        var result = await CreateService().ReplaceAsync(path, "а", "б", matchCase: false);

        Assert.Equal(3, result.Replacements);
        Assert.Equal("б, б, б", (await CreateService().ReadTextAsync(path)).Paragraphs[0]);
    }

    [Fact]
    public async Task Replace_LeadingAndTrailingSpaces_Preserved()
    {
        var path = CreateDocx(
            "spaces.docx",
            new Paragraph(new Run(new Text("Опыт: пять лет"))));

        await CreateService().ReplaceAsync(path, "пять", " шесть с половиной", matchCase: false);

        Assert.Equal(
            "Опыт:  шесть с половиной лет",
            (await CreateService().ReadTextAsync(path)).Paragraphs[0]);
    }

    [Fact]
    public async Task Replace_NoMatch_FileBytesUntouched()
    {
        var path = CreateDocx(
            "nomatch.docx",
            new Paragraph(new Run(new Text("Опыт: пять лет"))));
        var before = ReadBytes(path);

        var result = await CreateService().ReplaceAsync(path, "Лондон", "Париж", matchCase: false);

        Assert.Equal(0, result.Replacements);
        Assert.Equal(before, ReadBytes(path));
    }

    [Fact]
    public async Task Replace_EmptyFind_SkipsWithoutChanges()
    {
        var path = CreateDocx(
            "emptyfind.docx",
            new Paragraph(new Run(new Text("Опыт: пять лет"))));
        var before = ReadBytes(path);

        var result = await CreateService().ReplaceAsync(path, string.Empty, "что-то", matchCase: false);

        Assert.Equal(0, result.Replacements);
        Assert.Equal(before, ReadBytes(path));
    }

    [Fact]
    public async Task Replace_MatchCaseOn_DoesNotTouchOtherCase()
    {
        var path = CreateDocx(
            "case.docx",
            new Paragraph(new Run(new Text("Минск минск"))));

        var result = await CreateService().ReplaceAsync(path, "Минск", "Гродно", matchCase: true);

        Assert.Equal(1, result.Replacements);
        Assert.Equal("Гродно минск", (await CreateService().ReadTextAsync(path)).Paragraphs[0]);
    }
}
