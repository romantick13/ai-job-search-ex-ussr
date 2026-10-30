using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: RSS/Atom: BuildFeedUrl → GET → ParseFeed → MapItem.
// Разбор — System.Xml.Linq с матчингом по LocalName: ленты вольно обращаются
// с namespace (RSS без ns, Atom с «http://www.w3.org/2005/Atom»).
// Markdig пока не подключён — описания остаются сырыми,
// конвертация HTML→Markdown придёт вместе с ним.
// Контракт «никаких тихих игноров»: пропуски битых элементов
// НЕ молчат — onSkip в протокол; каждый шаг — событие.
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
    // DedupeHasher.ComputeComposite — ДО нормализации текста.
    protected abstract JobPosting MapItem(FeedItem item);

    public override async Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = BuildFeedUrl(query);
        Log.Log($"портал {Descriptor.Id}: лента «{query}» → GET {url}");

        try
        {
            // общий GET с одним ретраем (403/429/503/сеть — пауза 4 с);
            // человекочитаемая причина не-2xx теперь и у лент.
            var xml = await GetPayloadAsync(url, $"лента «{query}»", ct).ConfigureAwait(false);
            var items = ParseFeed(xml, onSkip: reason =>
                Log.LogWarn($"skip: {Descriptor.Id} лента — {reason}"),
                onNote: note =>
                    Log.LogWarn($"портал {Descriptor.Id}: {note}"));
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
            // /отмена пользователем — осознанный пропуск, не ошибка.
            Log.LogWarn($"skip: портал {Descriptor.Id} — лента «{query}» отменена пользователем (ct)");
            throw;
        }
        catch (Exception ex)
        {
            // битая XML (ParseFailedException), сеть, таймаут — причина
            // в протоколе, решение за оркестратором.
            Log.LogError($"портал {Descriptor.Id}: лента «{query}» упала — "
                + $"{ex.GetType().Name}: {ex.Message}; решение: наверх (оркестратору)");
            throw;
        }
    }

    // RSS/Atom не несёт отдельной карточки вакансии — деталь берётся из item'а
    // ленты. Адаптеры-исключения переопределяют явно.
    public override bool SupportsDetail => false;

    public override Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{Descriptor.Id}: RSS/Atom не несёт отдельной карточки вакансии; используйте SearchAsync");

    // LIFECYCLE: чистая функция — golden-фикстуры лент тестируют её без сети.
    // Незнакомые/пустые элементы (без link/href) пропускаются НЕ молча: каждый
    // пропуск уходит в onSkip — лента частично битая не
    // роняет весь прогон (отказ ≠ крах), но каждый пропуск — событие.
    // Живой кейс (nodesk падал
    // ParseFailedException при валидном на вид payload): лента nodesk несёт
    // HTML-сущности (&nbsp;, &ldquo; …) без DTD — строгий XDocument.Parse падает
    // «undefined entity», хотя лента содержательно валидна. Рецепт: одна
    // tolerant-пречистка — известные HTML-сущности → символы, оставшиеся голые
    // & → &amp;, управляющие символы → выброшены; после чистки парсим заново.
    // Чистка НЕ молчит: событие уходит в onNote (SearchAsync пишет WARN).
    public static IReadOnlyList<FeedItem> ParseFeed(string xml, Action<string>? onSkip = null,
        Action<string>? onNote = null)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException original)
        {
            var cleaned = SanitizeXml(xml);
            try
            {
                document = XDocument.Parse(cleaned);
                onNote?.Invoke("лента поправлена tolerant-пречисткой (HTML-сущности/голые «&») "
                    + $"— источник отдаёт не чистый XML; причина первой неудачи: {original.Message}");
            }
            catch (XmlException e)
            {
                // Трансформация исключения с сохранением причины — re-throw ниже
                // логируется вызывающим (SearchAsync, ). в сообщение
                // идёт начало payload — под HTML-щитом (nodesk) в
                // протоколе сразу видно, ЧТО пришло вместо XML.
                throw new ParseFailedException(
                    $"лента не разбирается как XML даже после пречистки (начало payload: {Preview(cleaned)})", e);
            }
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
                // пропуск — осознанный и видимый в протоколе.
                onSkip?.Invoke($"элемент без link пропущен (title: {item.Title ?? "—"}, "
                    + $"id: {item.ExternalId ?? "—"})");
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    // первые 120 знаков payload в одну строку — диагноз «лента не
    // разбирается» без повторного прогона (<!DOCTYPE html>… — щит, не лента).
    private static string Preview(string xml)
    {
        var flat = Regex.Replace(xml ?? string.Empty, "\\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    // tolerant-пречистка payload. Шаг 1 — управляющие символы вне
    // \t\r\n (XML их запрещает, ленты вставляют из-за кривых описаний);
    // шаг 2 — сущности: числовые (&#123;/&#x1F600;) и пять XML-шных (&amp;…)
    // легальны и не трогаются; известные HTML-сущности (&nbsp;/&ldquo;/…)
    // заменяются на символы; любой остальной & (голый амперсанд, «R&D»)
    // экранируется в &amp; — иначе XDocument.Parse отвечает «undefined entity».
    public static string SanitizeXml(string xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return xml;
        }

        var sb = new System.Text.StringBuilder(xml.Length);
        foreach (var ch in xml)
        {
            if (ch >= 0x20 || ch == '\t' || ch == '\n' || ch == '\r')
            {
                sb.Append(ch);
            }
        }

        return BareAmpRx.Replace(
            EntityRx.Replace(sb.ToString(), match =>
        {
            var name = match.Groups[1].Value;
            if (name.StartsWith('#') || XmlLegalEntities.Contains(name))
            {
                return match.Value; // числовые и пять XML-сущностей легальны
            }

            return HtmlEntities.TryGetValue(name, out var replacement)
                ? replacement
                : "&amp;" + name + ";"; // голый амперсанд — экранируем, имя сохраняем
        }), "&amp;");
    }

    // &(#...; | #x...; | буквы+цифры;) — кандидат на сущность.
    private static readonly Regex EntityRx = new(
        "&(#x[0-9a-fA-F]+|#[0-9]+|[a-zA-Z][a-zA-Z0-9]*);", RegexOptions.Compiled);

    // Второй проход: амперсанды БЕЗ точки с запятой («R&D», «AT&T», «& <слово»):
    // первый проход их не трогает (нет сущности) — а XDocument.Parse падает и на
    // них. Легальные сущности (после первого прохода они в правильном виде)
    // защищены lookahead'ом.
    private static readonly Regex BareAmpRx = new(
        "&(?![a-zA-Z][a-zA-Z0-9]*;|#\\d+;|#x[0-9a-fA-F]+;)", RegexOptions.Compiled);

    // Легальные для XML без DTD: числовые ловит префикс «#», тут — именные.
    private static readonly HashSet<string> XmlLegalEntities = new()
    { "amp", "lt", "gt", "quot", "apos" };

    // Частые HTML-сущности RSS-описаний (без DTD парсеру они незнакомы).
    private static readonly IReadOnlyDictionary<string, string> HtmlEntities =
        new Dictionary<string, string>
        {
            ["nbsp"] = "\u00A0", ["iexcl"] = "¡", ["cent"] = "¢", ["pound"] = "£",
            ["curren"] = "¤", ["yen"] = "¥", ["sect"] = "§", ["uml"] = "¨",
            ["copy"] = "©", ["reg"] = "®", ["deg"] = "°", ["plusmn"] = "±",
            ["acute"] = "´", ["micro"] = "µ", ["para"] = "¶", ["middot"] = "·",
            ["frac14"] = "¼", ["frac12"] = "½", ["frac34"] = "¾",
            ["iquest"] = "¿", ["times"] = "×", ["divide"] = "÷",
            ["oelig"] = "œ", ["scaron"] = "š", ["yuml"] = "ÿ",
            ["fnof"] = "ƒ", ["circ"] = "ˆ", ["tilde"] = "˜",
            ["ndash"] = "–", ["mdash"] = "—", ["lsquo"] = "‘", ["rsquo"] = "’",
            ["sbquo"] = "‚", ["ldquo"] = "“", ["rdquo"] = "”", ["bdquo"] = "„",
            ["dagger"] = "†", ["Dagger"] = "‡", ["bull"] = "•", ["hellip"] = "…",
            ["permil"] = "‰", ["prime"] = "′", ["Prime"] = "″",
            ["lsaquo"] = "‹", ["rsaquo"] = "›", ["oline"] = "‾", ["frasl"] = "⁄",
            ["euro"] = "€", ["trade"] = "™", ["quot"] = "\"",
            ["laquo"] = "«", ["raquo"] = "»", ["shy"] = "\u00AD",
        };

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

    // Ленты несут и ISO-8601 (Atom), и (RSS). Парсер .NET 10 не берёт
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
