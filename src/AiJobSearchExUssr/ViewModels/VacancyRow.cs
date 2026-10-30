using System.IO;
using System.Text.RegularExpressions;
using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services.Resume;

namespace AiJobSearchExUssr.ViewModels;

// строка списка вакансий («бегут строки перебора») — проекция JobPosting
// для compiled bindings (x:DataType). STATE: immutable — Posting не меняется.
// Дополнения: бейдж режима «удалёнка/офис/?»
// (GeoMatcher), пояснение гео-вердикта и % совпадения ключевых профиля.
public sealed class VacancyRow
{
    public required JobPosting Posting { get; init; }

    // гео/режим и совпадение ключевых (заполняет MainViewModel).
    public RemoteKind Remote { get; init; } = RemoteKind.Unknown;
    public bool PassGeo { get; init; } = true;
    public bool UnknownGeo { get; init; }
    public string GeoNote { get; init; } = "";
    public int? MatchPercent { get; init; }

    public string Title => Posting.Title ?? "(без названия)";
    public string Site => Posting.PortalId;

    public string Meta
        => string.Join(" · ", new[] { Posting.Company, Posting.Location }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Salary => Posting.SalaryText ?? "";

    // Бейдж режима: короткий, слева-направо читается даже в плотной строке.
    public string RemoteBadge => Remote switch
    {
        RemoteKind.RemoteAllowed => "🏠 удалёнка",
        RemoteKind.OfficeOnly => "🏢 офис",
        _ => "? гео не указано",
    };

    // Классы бейджа (Avalonia: Classes.xxx="{Binding ...}").
    public bool IsRemote => Remote == RemoteKind.RemoteAllowed;
    public bool IsOffice => Remote == RemoteKind.OfficeOnly;
    public bool IsUnknown => Remote == RemoteKind.Unknown;

    // % совпадения для мини-полосы (int? -> double/bool для compiled bindings).
    public bool HasMatch => MatchPercent is not null;
    public double MatchFraction => MatchPercent ?? 0;

    // похожая вакансия — hh сам подобрал её на странице другой
    // (блок «Вам подойдут эти вакансии»); бейдж «✨ похожая» в строке.
    public bool IsSimilar => Posting.IsSimilar;

    // Нижняя строка строки-вакансии: сайт + зарплата + гео-пояснение + %.
    public string Footer
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Site))
            {
                parts.Add(Site);
            }

            if (!string.IsNullOrEmpty(Salary))
            {
                parts.Add(Salary);
            }

            if (GeoNote.Length > 0)
            {
                parts.Add(GeoNote);
            }

            if (MatchPercent is int pct)
            {
                parts.Add($"{pct}% ключевых");
            }

            return string.Join("   ", parts);
        }
    }

    // Пояснение совпадения ключевых для строки-шапки (протокол/тост не нужен).
    public string MatchDisplay => MatchPercent is int pct ? $"совпадение {pct}% ключевых профиля" : "";


    // первые строки описания без маркдаун-разметки. Держим коротко —
    // две строки в карточке (MaxLines в шаблоне), остальное по клику на сайте.
    public string Snippet
    {
        get
        {
            var raw = Posting.DescriptionMd ?? string.Empty;
            // remotive/wwr кладут HTML в описание — вырезаем теги,
            // автолинки и url-мусор, декодируем базовые entities.
            raw = Regex.Replace(raw, "<https?://[^>]+>", " ");          // md-автолинки <url>
            raw = Regex.Replace(raw, @"\[([^\]]*)\]\(([^)]*)\)", "$1"); // md-ссылки -> текст
            raw = Regex.Replace(raw, "<[^>]+>", " ");                   // html-теги
            raw = raw.Replace("\r", " ").Replace("\n", " ");
            raw = Regex.Replace(raw, "[#>*`_|]", "");
            raw = raw.Replace("&nbsp;", " ").Replace("&amp;", "&")
                     .Replace("&lt;", "<").Replace("&gt;", ">")
                     .Replace("&quot;", "\"").Replace("&#39;", "'");
            raw = Regex.Replace(raw, @"\s+", " ").Trim();
            return raw.Length <= 160 ? raw : raw[..157].TrimEnd() + "…";
        }
    }

    public bool HasSnippet => Snippet.Length > 0;

    // Кнопка-ссылка «🔗» в карточке — CanonicalUrl в браузере (точная вакансия).
    public string Url => Posting.CanonicalUrl;
    public bool HasUrl => !string.IsNullOrWhiteSpace(Posting.CanonicalUrl);
}
