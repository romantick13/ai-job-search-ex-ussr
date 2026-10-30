using System.Text;
using System.Text.RegularExpressions;

namespace AiJobSearchBy.Services.Resume;

// ============================================================================
// извлечение ключевых слов из MD-канона
// резюме — «прогнать и посмотреть на варианты правок» (формула ).

// ДВА ИСТОЧНИКА:
// 1) словарь: вхождения позиций keywords.json в текст резюме (case/ё/
// нормализация) -> категория уже известна;
// 2) эвристика: строки секции «Навыки/Skills/Ключевые слова» бьём по
// разделителям (запятая, точка с запятой, точка-буллит, ·, /) — то,
// чего нет в словаре, уходит в «новые» (кандидат «+ в словарь»).
// Так сисадмин увидит свои Zabbix/ESXi даже если словарь не пополнял,
// а бухгалтер — свои «1С:Бухгалтерия» из дефолтного каталога.

// FLOW: ResumeProfilesViewModel.RunExtractionAsync -> Extract -> правки-галочки.
// STATE: без состояния.
// THREAD: потокобезопасен (только чтение).
// ============================================================================

// Одна найденная правка: слово + источник + есть ли оно в словаре.
public sealed record ExtractedKeyword
{
    public required string Value { get; init; }
    public string Category { get; init; } = "";
    public required bool InDictionary { get; init; }
    public string Section { get; init; } = "Навыки";
}

public static class KeywordExtractor
{
    private static readonly string[] SkillSectionNames =
    {
        "Навыки", "Компетенции", "Технологии", "Стек",
        "Skills", "Ключевые слова", "О себе", "Профиль",
    };

    private static readonly char[] Splitters = { ',', ';', '·', '•', '|', '/', '\n' };

    // Порог длины кандидата: короче 2 — мусор, длиннее 40 — не «слово».
    private const int MinLen = 2;
    private const int MaxLen = 40;

    public static IReadOnlyList<ExtractedKeyword> Extract(
        string canonMd, IReadOnlyList<(string Value, string Category)> dictionary)
    {
        var result = new List<ExtractedKeyword>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // --- 1) вхождения словаря в текст ---------------------------------
        var text = GeoMatcher.Normalize(canonMd);
        foreach (var (value, category) in dictionary)
        {
            var norm = GeoMatcher.Normalize(value);
            if (norm.Length < MinLen || norm.Length > MaxLen)
            {
                continue;
            }

            if (text.Contains(norm, StringComparison.Ordinal) && seen.Add(norm))
            {
                result.Add(new ExtractedKeyword
                {
                    Value = value,
                    Category = category,
                    InDictionary = true,
                });
            }
        }

        // --- 2) эвристика секций навыков: кандидаты вне словаря -----------
        var dictNorms = new HashSet<string>(
            dictionary.Select(d => GeoMatcher.Normalize(d.Value)), StringComparer.Ordinal);

        foreach (var sectionName in SkillSectionNames)
        {
            var section = FindSection(canonMd, sectionName);
            if (section is null)
            {
                continue;
            }

            foreach (var candidate in SplitCandidates(section))
            {
                var norm = GeoMatcher.Normalize(candidate);
                if (norm.Length is < MinLen or > MaxLen || !seen.Add(norm))
                {
                    continue;
                }

                result.Add(new ExtractedKeyword
                {
                    Value = candidate,
                    Category = "Словарь пользователя",
                    InDictionary = dictNorms.Contains(norm),
                });
            }
        }

        return result;
    }

    // Секция «## Имя» из канона (до следующего «## »).
    internal static string? FindSection(string canonMd, string name)
    {
        var lines = canonMd.Replace("\r\n", "\n").Split('\n');
        var target = GeoMatcher.Normalize(name);
        var inside = false;
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = GeoMatcher.Normalize(t[3..]) == target;
                continue;
            }

            if (inside)
            {
                sb.AppendLine(line);
            }
        }

        var text = sb.ToString().Trim();
        return text.Length > 0 ? text : null;
    }

    private static IEnumerable<string> SplitCandidates(string section)
    {
        // Убираем MD-буллеты и эмфазу, бьём по разделителям.
        var cleaned = section.Replace("**", "").Replace("•", ",");
        foreach (var part in cleaned.Split(Splitters, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Trim()
                .Trim('.', '—', '-', '(', ')')
                .Trim();
            // Склейки через слэш уже разбились; склейки «C# (ASP.NET)» — берём целиком.
            if (value.Length is >= MinLen and <= MaxLen
                && Regex.IsMatch(value, @"[\p{L}\p{Nd}#]"))
            {
                yield return value;
            }
        }
    }
}
