using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;
using Xunit;

namespace AiJobSearchBy.Tests;

// Гейты устойчивости портального слоя (по живым прогонам):
// починка четырёх портальных поломок, найденных в живых прогонах.
// nodesk — RSS с HTML-сущностями (&nbsp;/&ldquo;) без DTD падал
// «undefined entity», хотя лента содержательно валидна;
// jobicy — jobType/jobIndustry приходят МАССИВАМИ, GetString ронял
// ВЕСЬ каталог (InvalidOperationException), один item без url
// обнулял остальные 49 вакансий;
// remotive — браузерные заголовки (UA/Accept) уже на общем HttpClient;
// тут фиксируем, что ленточный tolerant-путь не влияет.
// Контракт «никаких тихих игноров»: каждый пропуск/починка — событие протокола.
public class PortalRobustnessTests
{
    private static JobPosting Posting() => new()
    {
        PortalId = "test",
        CanonicalUrl = "https://test/vac/1",
        Title = "X",
        DedupeHash = "h",
    };

    // --------------------------------------------- nodesk: HTML-сущности ----

    [Fact]
    public void ParseFeed_HtmlEntities_SanitizedWithoutDtd()
    {
// Живой кейс nodesk: &nbsp;/&ldquo; в описаниях — строгий
// XDocument.Parse падает «undefined entity», tolerant-пречистка чинит.
        const string payload = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel><title>Remote Jobs</title>
              <item>
                <title>Backend Engineer at Acme</title>
                <link>https://nodesk.co/remote-jobs/backend-engineer/</link>
                <guid>https://nodesk.co/remote-jobs/backend-engineer/</guid>
                <description>Fully remote&nbsp;team &ldquo;friendly&rdquo; perks&hellip;</description>
              </item>
            </channel></rss>
            """;

        string? note = null;
        var items = FeedPortalBase.ParseFeed(payload, onNote: n => note = n);

        var item = Assert.Single(items);
        Assert.NotNull(item.Summary);
        Assert.Equal("Backend Engineer at Acme", item.Title);
        Assert.Contains('\u00A0', item.Summary); // &nbsp; -> неразрывный пробел
        Assert.Contains('“', item.Summary);      // &ldquo; -> кавычка
        Assert.Contains('…', item.Summary);      // &hellip; -> многоточие
        Assert.NotNull(note);                    // починка НЕ молчит
        Assert.Contains("пречистк", note);
    }

    [Fact]
    public void ParseFeed_BareAmpersand_Escaped()
    {
// «R&D team» — голый амперсанд без сущности: экранируется в &amp;,
// текст сохраняется как есть.
        const string payload = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel>
              <item>
                <title>Developer at R&D Studio</title>
                <link>https://nodesk.co/remote-jobs/dev/</link>
                <description>Join our R&D team, work from anywhere</description>
              </item>
            </channel></rss>
            """;

        var items = FeedPortalBase.ParseFeed(payload);

        var item = Assert.Single(items);
        Assert.NotNull(item.Summary);
        Assert.Equal("Developer at R&D Studio", item.Title);
        Assert.Contains("R&D team", item.Summary);
    }

    [Fact]
    public void ParseFeed_LegalEntitiesAndNumericRefs_Untouched()
    {
        const string payload = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel>
              <item>
                <title>Sales &amp; Marketing</title>
                <link>https://x/v/1</link>
                <description>Salary from 5&#160;000 € — numeric refs legal</description>
              </item>
            </channel></rss>
            """;

        var items = FeedPortalBase.ParseFeed(payload);

        var item = Assert.Single(items);
        Assert.Equal("Sales & Marketing", item.Title);
        Assert.Contains('\u00A0', item.Summary!); // числовая &#160; — легальна (CS8604: Summary nullable)
    }

    [Fact]
    public void ParseFeed_GenuinelyBroken_StillThrowsWithPreview()
    {
        // Пречистка не лечит настоящий мусор: «<rss><item>» — всё ещё
        // ParseFailedException с превью payload .
        var ex = Assert.Throws<ParseFailedException>(
            () => FeedPortalBase.ParseFeed("<rss><item>"));
        Assert.Contains("начало payload", ex.Message);
    }

    // ---------------------------------------------------- jobicy: массивы ----

    [Fact]
    public async Task Jobicy_ArrayFields_ParseTolerantly()
    {
// Живой кейс «SAP» (зонд): jobType/jobIndustry — массивы.
// GetString падал InvalidOperationException и ронял весь каталог.
        const string payload = """
            {"jobCount":1,
             "jobs":[{"id":150754,"url":"https://jobicy.com/jobs/150754",
               "jobTitle":"SAP Consultant","companyName":"Yopeso",
               "jobGeo":"Worldwide","jobLevel":"Senior",
               "jobType":["Full-Time"],"jobIndustry":["Marketing & Sales","IT"],
               "jobExcerpt":"SAP RE/FX consulting"}]}
            """;
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of(payload, "application/json"));
        var portal = new JobicyPortal(new HttpClient(handler), new SilentLog());

        var items = await portal.SearchAsync("SAP");

        var item = Assert.Single(items);
        Assert.Equal("Full-Time", item.SalaryText);           // массив -> «, »-join, один элемент без хвоста
        Assert.Equal("SAP Consultant", item.Title);
        Assert.Equal("Yopeso", item.Company);
        Assert.Equal("Worldwide", item.Location);
        Assert.Equal("SAP RE/FX consulting", item.DescriptionMd);
    }

    [Fact]
    public async Task Jobicy_ItemWithoutUrl_SkipsAndKeepsRest()
    {
// Один битый item больше не обнуляет каталог (был ParseFailedException
// на первом же кривом элементе): пропуск с событием, остальные — в базу.
        const string payload = """
            {"jobCount":2,
             "jobs":[{"id":1,"jobTitle":"Broken item"},
                     {"id":2,"url":"https://jobicy.com/jobs/2",
                      "jobTitle":"Good item","companyName":"Acme"}]}
            """;
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of(payload, "application/json"));
        var log = new SilentLog();
        var portal = new JobicyPortal(new HttpClient(handler), log);

        var items = await portal.SearchAsync("*");

        var item = Assert.Single(items);
        Assert.Equal("Good item", item.Title);
        Assert.Contains(log.Logs, l => l.Contains("item без url пропущен"));
    }

    [Fact]
    public async Task Jobicy_MissingJobsArray_StillSchemaChange()
    {
        // Смена схемы (нет jobs[]) — честный ParseFailedException, не тихая пустота.
        const string payload = """{"error":"unexpected"}""";
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of(payload, "application/json"));
        var portal = new JobicyPortal(new HttpClient(handler), new SilentLog());

        await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("x"));
    }

    // --------------------------------------------------- список стран ООН ----

    [Fact]
    public void CountryTable_AllDisplays_Unique()
    {
        // Список для модала «Страны…»: без дублей — иначе строка склеится.
        var displays = Services.Resume.GeoMatcher.AllDisplays();
        Assert.Equal(displays.Count, displays.Distinct().Count());
        Assert.Contains("Беларусь", displays);
        Assert.Contains("США", displays);
    }
}
