using System.Text.RegularExpressions;
using AiJobSearchBy.Services;
using Xunit;

namespace AiJobSearchBy.Tests;

// WP-CONFIG (RFC-004 §2.5): security-гард. RFC планирует CI-джобу на каждый
// PR; CI в проекте ещё нет — пока гард живёт xUnit-тестом: сканирует файлы
// репо на ключеобразные паттерны, находка = красный прогон до коммита.
// Маска находки — только края совпадения (секреты не в логи, PRIVACY.md).
public class RepoSecretsGuardTests
{
    private static readonly (string Label, Regex Pattern)[] Patterns =
    {
        ("ModelScope-ключ (ms-…)", new Regex(@"ms-[A-Za-z0-9]{16,}")),
        ("SiliconFlow/OpenRouter-ключ (sk-…)", new Regex(@"sk-[A-Za-z0-9]{20,}")),
        ("Bearer-токен", new Regex(@"Bearer [A-Za-z0-9_\-.=]{20,}")),
        ("захардкоженный AIJOB-ключ", new Regex(@"AIJOB_[A-Z]+_API_KEY\s*[=:]\s*[""']?[A-Za-z0-9][A-Za-z0-9_\-]{15,}")),
    };

    [Fact]
    public void Repo_Contains_No_Key_Shape_Strings()
    {
        var root = FindRepoRoot();
        var hits = new List<string>();

        foreach (var file in EnumerateScannableFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (label, pattern) in Patterns)
                {
                    var match = pattern.Match(lines[i]);
                    if (match.Success)
                    {
                        hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1} — " +
                                 $"{label}: {ConfigService.Mask(match.Value)}");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0,
            "ключеобразные строки в репо (RFC-004 §2.5) — убери в local.json/env:\n" +
            string.Join("\n", hits));
    }

    // Корень репо: от бинарей тестов вверх до каталога с DECISIONS.md + src/.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DECISIONS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException(
            $"корень репо не найден вверх от {AppContext.BaseDirectory} (нет DECISIONS.md + src)");
    }

    private static IEnumerable<string> EnumerateScannableFiles(string root)
    {
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git" };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".md", ".json", ".py", ".ps1", ".cmd", ".sh", ".axaml", ".csproj", ".sln", ".txt",
        };

        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(root, file);
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Any(parts2 => skipDirs.Contains(parts2))
                    && extensions.Contains(Path.GetExtension(file));
            });
    }
}
