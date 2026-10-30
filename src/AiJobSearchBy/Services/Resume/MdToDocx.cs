using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using U = DocumentFormat.OpenXml.Wordprocessing;

namespace AiJobSearchBy.Services.Resume;

// ============================================================================
// «Пересохранить в DOCX» — приложение
// само гонит MD-канон профиля в DOCX-копию (OpenXml 3.2, уже в проекте).
// Формула «пусть пересохраняет сам, мы не няньки» — юзер ничего
// не конвертирует руками (кроме старого .doc, который мы не парсим).

// Простое честное отображение: "# " -> Heading1, "## " -> Heading2,
// "### " -> Heading3, "- "/"* " -> маркер «• », **жирный** -> Bold,
// пустые строки -> разделитель. Этого достаточно для резюме: DOCX-копия
// нужна как вложение на сайтах, не как шедевр вёрстки.

// FLOW: ResumeProfilesViewModel.ResaveDocxAsync -> Convert -> файл рядом
// с исходником (resume\files\<профиль>\resume.docx).
// STATE: без состояния.
// THREAD: вызов из Task.Run.
// ============================================================================

public interface IMdToDocx
{
    void Convert(string markdown, string outPath);
}

public sealed class MdToDocx : IMdToDocx
{
    public void Convert(string markdown, string outPath)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Атомарно: пишем в tmp, потом подменяем (краш на полпути не рвёт файл).
        var tmp = outPath + ".tmp";
        try
        {
            using (var doc = WordprocessingDocument.Create(
                       tmp, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new U.Document(new U.Body());
                var body = main.Document.Body!;

                foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
                {
                    AppendLine(body, line);
                }

                main.Document.Save();
            }

            if (File.Exists(outPath))
            {
                File.Replace(tmp, outPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, outPath);
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private static void AppendLine(U.Body body, string line)
    {
        var t = line.TrimEnd();

        if (t.Trim().Length == 0)
        {
            body.Append(new U.Paragraph(new U.ParagraphProperties(
                new U.SpacingBetweenLines { After = "60" })));
            return;
        }

        int level = 0;
        if (t.StartsWith("### ", StringComparison.Ordinal))
        {
            level = 3;
        }
        else if (t.StartsWith("## ", StringComparison.Ordinal))
        {
            level = 2;
        }
        else if (t.StartsWith("# ", StringComparison.Ordinal))
        {
            level = 1;
        }

        var paragraph = new U.Paragraph();
        if (level > 0)
        {
            paragraph.Append(new U.ParagraphProperties(
                new U.ParagraphStyleId { Val = $"Heading{level}" }));
            t = t[(level + 1)..].Trim();
        }
        else if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
        {
            t = "• " + t[2..].Trim();
        }

        // **жирный** -> Bold-раны; остальное — обычным текстом.
        var parts = SplitBold(t);
        foreach (var (text, bold) in parts)
        {
            var run = new U.Run(new U.Text(text) { Space = SpaceProcessingModeValues.Preserve });
            if (bold)
            {
                run.RunProperties = new U.RunProperties(new U.Bold());
            }

            paragraph.Append(run);
        }

        body.Append(paragraph);
    }

    private static List<(string Text, bool Bold)> SplitBold(string text)
    {
        var result = new List<(string, bool)>();
        var rx = new Regex(@"\*\*(.+?)\*\*");
        var pos = 0;
        foreach (Match m in rx.Matches(text))
        {
            if (m.Index > pos)
            {
                result.Add((text[pos..m.Index], false));
            }

            result.Add((m.Groups[1].Value, true));
            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
        {
            result.Add((text[pos..], false));
        }

        if (result.Count == 0)
        {
            result.Add((text, false));
        }

        return result;
    }
}
