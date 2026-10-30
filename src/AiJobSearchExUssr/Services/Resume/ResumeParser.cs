using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using U = DocumentFormat.OpenXml.Wordprocessing;
using PdfDocument = UglyToad.PdfPig.PdfDocument;

namespace AiJobSearchExUssr.Services.Resume;

// ============================================================================
// одноразовый парс загруженного резюме
// в MD-канон. Формат: PDF / DOCX / TXT / MD; старый .doc (97-2003) НЕ парсим —
// «сохрани как DOCX... мы не няньки» (NotSupportedException
// с внятной подсказкой — VM покажет её в протоколе).

// MD-канон: детерминированная разметка. Заголовки секций узнаём по маркерам
// («Навыки», «Опыт», «Образование», «Языки», «Портфолио», «Контакты»… Ru/En)
// -> «## Имя»; текст до первой секции — шапка («# Заголовок»). Маркеров нет —
// весь текст идёт одной секцией «## Резюме»: экстрактор всё равно пройдёт.

// FLOW: ResumeProfilesViewModel.AddResumeAsync -> ParseAsync -> ResumeDb.
// STATE: без состояния (PdfPig/OpenXml открывают файлы локально).
// THREAD: вызов из Task.Run; файлы читаются одноразово.
// ============================================================================

public sealed record ResumeParseResult
{
    public required string CanonicalMd { get; init; }
    public required IReadOnlyDictionary<string, string> Sections { get; init; }
    public required string SourceHash { get; init; }

    // True — файл открылся, но текста в нём нет (скан без текстового слоя).
    public bool IsEmptyText { get; init; }
}

public interface IResumeParser
{
    ResumeParseResult Parse(string filePath);
    ResumeParseResult ParseMarkdown(string markdown, string sourceName);
}

public sealed class ResumeParser : IResumeParser
{
    private static readonly string[] SectionMarkers =
    {
        // canonical_name, маркеры (нормализованные)
        "Навыки|навык|skills|ключевые слова|компетенции|технологии|стек",
        "Опыт|опыт работы|experience|трудовая",
        "Образование|education|учёба|учеба",
        "Языки|languages|язык",
        "Портфолио|проекты|portfolio|projects|github",
        "Контакты|контакт|contacts",
        "О себе|обо мне|about|резюме|summary|профиль",
        "Курсы|сертификаты|certificates|courses",
        "Дополнительно|доп|additional|прочее",
    };

    public ResumeParseResult Parse(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("файл резюме не найден", filePath);
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var raw = ext switch
        {
            ".pdf" => ExtractPdf(filePath),
            ".docx" => ExtractDocx(filePath),
            ".txt" => File.ReadAllText(filePath, Encoding.UTF8),
            ".md" => File.ReadAllText(filePath, Encoding.UTF8),
            ".doc" => throw new NotSupportedException(
                "старый формат .doc (97-2003) не парсим — пересохрани как DOCX " +
                "(открыть в Word/LibreOffice -> «Сохранить как» -> DOCX) и загрузи снова"),
            _ => throw new NotSupportedException(
                $"формат «{ext}» не поддерживается — загрузи PDF, DOCX, TXT или MD"),
        };

        return BuildCanonical(raw, Path.GetFileNameWithoutExtension(filePath));
    }

    public ResumeParseResult ParseMarkdown(string markdown, string sourceName)
        => BuildCanonical(markdown, sourceName);

    // --- извлечение текста ------------------------------------------------
    private static string ExtractPdf(string path)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            var text = page.Text ?? "";
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ExtractDocx(string path)
    {
        // OpenXml: абзацы -> строки, пустые — разделители. Форматирование не
        // важно: MD-канон собирает структуру по маркерам секций.
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var paragraph in body.Descendants<U.Paragraph>())
        {
            var text = paragraph.InnerText?.Trim() ?? "";
            sb.AppendLine(text);
        }

        return sb.ToString();
    }

    // --- MD-канон ----------------------------------------------------------
    internal static ResumeParseResult BuildCanonical(string raw, string sourceName)
    {
        var lines = raw
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        // Пустой текст (скан без слоя): честно отдаём флаг, VM кричит.
        var totalChars = lines.Sum(l => l.Trim().Length);
        if (totalChars < 20)
        {
            return new ResumeParseResult
            {
                CanonicalMd = "",
                Sections = new Dictionary<string, string>(),
                SourceHash = Hash(raw),
                IsEmptyText = true,
            };
        }

        var sb = new StringBuilder();
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var current = "Шапка"; // до первого маркера — шапка (контакты, имя)
        var buffer = new List<string>();

        foreach (var line in lines)
        {
            var marker = DetectSectionMarker(line);
            if (marker is not null)
            {
                Flush(sections, current, buffer);
                current = marker;
                sb.AppendLine($"## {marker}");
                continue;
            }

            buffer.Add(line);
            sb.AppendLine(line);
        }

        Flush(sections, current, buffer);

        // Заголовок документа: если вход уже размечен «# ...» — не дублируем;
        // иначе шапкой становится первая содержательная строка (или имя файла).
        var headFirst = lines.FirstOrDefault(l => l.Trim().Length > 1);
        var title = string.IsNullOrWhiteSpace(headFirst) ? sourceName : headFirst.Trim();
        var canon = new StringBuilder();
        if (title.StartsWith("# ", StringComparison.Ordinal))
        {
            canon.AppendLine(title);
            canon.AppendLine();
        }
        else
        {
            canon.AppendLine($"# {title}");
            canon.AppendLine();
            canon.Append(sb);
            return new ResumeParseResult
            {
                CanonicalMd = canon.ToString(),
                Sections = sections,
                SourceHash = Hash(raw),
                IsEmptyText = false,
            };
        }

        canon.Append(sb);

        return new ResumeParseResult
        {
            CanonicalMd = canon.ToString(),
            Sections = sections,
            SourceHash = Hash(raw),
            IsEmptyText = false,
        };
    }

    private static void Flush(Dictionary<string, string> sections, string name, List<string> buffer)
    {
        var text = string.Join('\n', buffer).Trim();
        if (text.Length > 0 && !sections.ContainsKey(name))
        {
            sections[name] = text;
        }

        buffer.Clear();
    }

    private static string? DetectSectionMarker(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t.Length > 60)
        {
            return null;
        }

        // Маркер = строка-заголовок: уже MD-заголовок, «Навыки:» или просто «Навыки».
        var clean = t.TrimStart('#').TrimEnd(':', ' ').Trim();
        if (clean.Length == 0 || clean.Length > 40)
        {
            return null;
        }

        var norm = GeoMatcher.Normalize(clean);
        foreach (var group in SectionMarkers)
        {
            foreach (var variant in group.Split('|'))
            {
                if (norm == GeoMatcher.Normalize(variant))
                {
                    return group.Split('|')[0];
                }
            }
        }

        return null;
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..32];
    }
}
