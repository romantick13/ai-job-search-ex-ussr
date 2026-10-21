using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: RSS/Atom (RFC-003 §2.1): BuildFeedUrl → GET → ParseFeed → MapItem.
// Разбор — System.Xml.Linq с матчингом по LocalName: ленты вольно обращаются
// с namespace (RSS без ns, Atom с «http://www.w3.org/2005/Atom»).
// Markdig подключается на WP-E (RFC-001 §4) — описания остаются сырыми,
// конвертация HTML→Markdown придёт вместе с ним.
// Контракт «никаких тихих игноров» (LOG-CONTRACT.md): пропуски битых элементов
// НЕ молчат — onSkip в протокол (НИ-2); каждый шаг — событие (НИ-4).
// THREAD: состояние нет — потокобезопасен.
public abstract class FeedPortalBase : PortalBase
{
    protected FeedPortalBase(
        PortalDescriptor descriptor,
        HttpClient http,
        ILogService log)
        : base(descriptor, http, log)
    {
    }

    // Плоский элемент ленты после разбора (RSS item / Atom entry).
    public sealed record FeedItem(
        string? Title,
        string Link,
        string? Summary,
        DateTimeOffset? Published,
        string? ExternalId);

    // URL ленты по строке запроса (адаптер знает свои категории/параметры).
    protected abstract string BuildFeedUrl(string query);

    // Маппинг элемента ленты → вакансия; дедуп-хэш адаптер считает через
    // DedupeHasher.ComputeComposite (§2.8 — ДО нормализации текста).
    protected abstract JobPosting MapItem(FeedItem item);

    public override async Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = BuildFeedUrl(query);
        Log.Log($"портал {Descriptor.Id}: лента «{query}» → GET {url}");

        try
        {
            var xml = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            var items = ParseFeed(xml, onSkip: reason =>
                Log.LogWarn($"skip: {Descriptor.Id} лента — {reason}"));
            var result = new List<JobPosting>(items.Count);
            foreach (var item in items)
            {
                result.Add(MapItem(item));
            }

            Log.LogSuccess($"портал {Descriptor.Id}: лента отработала — вакансий: {result.Count} "
                + $"(элементов в ленте: {items.Count})");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // НИ-1/НИ-2: отмена пользователем — осознанный пропуск, не ошибка.
            Log.LogWarn($"skip: портал {Descriptor.Id} — лента «{query}» отменена пользователем (ct)");
            throw;
        }
        catch (Exception ex)
        {
            // НИ-1: битая XML (ParseFailedException), сеть, таймаут — причина
            // в протоколе, решение за оркестратором (§7).
            Log.LogError($"портал {Descriptor.Id}: лента «{query}» упала — "
                + $"{ex.GetType().Name}: {ex.Message}; решение: наверх (оркестратору)");
            throw;
        }
    }

    // RSS/Atom не несёт отдельной карточки вакансии — деталь берётся из item'а
    // ленты. Адаптеры-исключения переопределяют явно.
    public override Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{Descriptor.Id}: RSS/Atom не несёт отдельной карточки вакансии; используйте SearchAsync");

    // LIFECYCLE: чистая функция — golden-фикстуры лент тестируют её без сети.
    // Незнакомые/пустые элементы (без link/href) пропускаются НЕ молча: каждый
    // пропуск уходит в onSkip (LOG-CONTRACT.md НИ-2) — лента частично битая не
    // роняет весь прогон (§7: отказ ≠ крах), но каждый пропуск — событие.
    public static IReadOnlyList<FeedItem> ParseFeed(string xml, Action<string>? onSkip = null)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException e)
        {
            // Трансформация исключения с сохранением причины — re-throw ниже
            // логируется вызывающим (SearchAsync, НИ-1).
            throw new ParseFailedException("лента не разбирается как XML", e);
        }

        var items = document.Descendants()
            .Where(e => e.Name.LocalName == "item")
            .Select(ParseRssItem)
            .Concat(document.Descendants()
                .Where(e => e.Name.LocalName == "entry")
                .Select(ParseAtomEntry));

        var result = new List<FeedItem>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Link))
            {
                // НИ-2: пропуск — осознанный и видимый в протоколе.
                onSkip?.Invoke($"элемент без link пропущен (title: {item.Title ?? "—"}, "
                    + $"id: {item.ExternalId ?? "—"})");
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    private static FeedItem ParseRssItem(XElement item) => new(
        Title: Text(item, "title"),
        Link: Text(item, "link") ?? string.Empty,
        Summary: Text(item, "description"),
        Published: Date(item, "pubDate"),
        ExternalId: Text(item, "guid"));

    private static FeedItem ParseAtomEntry(XElement entry) => new(
        Title: Text(entry, "title"),
        Link: AtomLink(entry) ?? string.Empty,
        Summary: Text(entry, "summary") ?? Text(entry, "content"),
        Published: Date(entry, "updated") ?? Date(entry, "published"),
        ExternalId: Text(entry, "id"));

    // Atom: <link rel="alternate" href="..."/> — alternate (или без rel — это
    // и есть alternate по RFC 4287), иначе первый непустой href (enclosure и пр.).
    private static string? AtomLink(XElement entry)
        => entry.Elements()
            .Where(e => e.Name.LocalName == "link")
            .Select(e => new LinkCandidate((string?)e.Attribute("href"), (string?)e.Attribute("rel")))
            .Where(l => !string.IsNullOrWhiteSpace(l.Href))
            .OrderBy(l => l.Rel is null || string.Equals(l.Rel, "alternate", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(l => l.Href)
            .FirstOrDefault();

    private sealed record LinkCandidate(string? Href, string? Rel);

    private static string? Text(XElement parent, string localName)
        => parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName == localName)
            ?.Value
            .Trim() is { Length: > 0 } value ? value : null;

    private static DateTimeOffset? Date(XElement parent, string localName)
        => Text(parent, localName) is { } raw ? ParseDate(raw) : null;

    // Ленты несут и ISO-8601 (Atom), и RFC-822 (RSS). Парсер .NET 10 не берёт
    // RFC1123 с днём недели («Tue, 21 Oct 2026 ... GMT» — проверено эмпирически),
    // поэтому fallback: отрезаем «ddd, » и добираем числовые зоны ±hhmm → ±hh:mm.
    private static DateTimeOffset? ParseDate(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed;
        }

        var comma = raw.IndexOf(',');
        var noDay = comma > 0 ? raw[(comma + 1)..].Trim() : raw;
        if (DateTimeOffset.TryParse(noDay, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            return parsed;
        }

        var fixedZone = Regex.Replace(noDay, "([+-]\\d{2})(\\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(fixedZone, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)
            ? parsed
            : null;
    }
}
