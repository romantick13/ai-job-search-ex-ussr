using AiJobSearchExUssr.Services.Resume;
using Xunit;

namespace AiJobSearchExUssr.Tests.Resume;

// парс резюме в MD-канон + пересохранение в DOCX (приложение само,
// без ручной доработки за юзера). Круговой тест: MdToDocx -> ResumeParser.Parse(docx).
public class ResumeParserTests : IDisposable
{
    private readonly string _dir;

    public ResumeParserTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "resumeparse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void Parse_Markdown_KeepsStructure()
    {
        var md = """
            # Иван Иванов
            ## Навыки
            C#, Docker
            ## Опыт
            EPAM — 4 года
            """;
        var parser = new ResumeParser();
        var result = parser.ParseMarkdown(md, "resume");

        Assert.False(result.IsEmptyText);
        Assert.StartsWith("# Иван Иванов", result.CanonicalMd);
        Assert.True(result.Sections.ContainsKey("Навыки"));
        Assert.True(result.Sections.ContainsKey("Опыт"));
        Assert.False(string.IsNullOrEmpty(result.SourceHash));
    }

    [Fact]
    public void Parse_TxtWithoutMarkers_SingleSection()
    {
        var path = Path.Combine(_dir, "plain.txt");
        File.WriteAllText(path, "Иван Иванов\nC# разработчик\nОпыт: 5 лет, .NET, SQL");

        var parser = new ResumeParser();
        var result = parser.Parse(path);

        Assert.False(result.IsEmptyText);
        Assert.Contains("C# разработчик", result.CanonicalMd);
    }

    [Fact]
    public void Parse_Docx_RoundtripThroughMdToDocx()
    {
        // Приложение само пересохраняет MD -> DOCX; парсер обязан прочесть
        // свой же DOCX обратно (замкнутый круг канона).
        var md = "# Пётр Сидоров\n\n## Навыки\nPowerShell, ESXi, Zabbix\n\n## Опыт\nПарк 300+ машин";
        var docxPath = Path.Combine(_dir, "resume.docx");
        new MdToDocx().Convert(md, docxPath);

        var parser = new ResumeParser();
        var result = parser.Parse(docxPath);

        Assert.False(result.IsEmptyText);
        Assert.Contains("Пётр Сидоров", result.CanonicalMd);
        Assert.Contains("ESXi", result.CanonicalMd);
    }

    [Fact]
    public void Parse_OldDoc_IsRejectedWithNannyFreeHint()
    {
        // Старый .doc не парсим — юзер пересохранил бы сам,
        // но отказ обязаны объяснить внятно.
        var path = Path.Combine(_dir, "old.doc");
        File.WriteAllText(path, "fake binary doc");

        var parser = new ResumeParser();
        var ex = Assert.Throws<NotSupportedException>(() => parser.Parse(path));
        Assert.Contains("DOCX", ex.Message);
    }

    [Fact]
    public void Parse_EmptyText_ScanDetected()
    {
        var path = Path.Combine(_dir, "scan.txt");
        File.WriteAllText(path, "   \n  . \n");

        var parser = new ResumeParser();
        var result = parser.Parse(path);
        Assert.True(result.IsEmptyText);
    }
}
