using System.Net;
using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Portals;
using AiJobSearchExUssr.Services;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: гейты базовых классов адаптеров: API-цикл,
// разбор RSS/Atom, breaker polite-crawl, уважение rate-limit заголовков.
public class PortalBasesTests
{
    private static PortalDescriptor FakeDescriptor(string id, PortalAccessKind access)
        => new()
        {
            Id = id,
            DisplayName = id,
            SourceClass = PortalSourceClass.Commercial,
            Access = access,
            Auth = PortalAuthKind.None,
            DefaultPriority = 8,
            ApplySupported = true,
        };

    private static JobPosting MakePosting(PortalDescriptor descriptor, string? externalId, string url, string? title)
        => new()
        {
            PortalId = descriptor.Id,
            ExternalId = externalId,
            CanonicalUrl = url,
            Title = title,
            SourceClass = descriptor.SourceClass,
            AccessKind = descriptor.Access,
            DedupeHash = DedupeHasher.ComputeComposite(descriptor.Id, externalId, url),
            FetchedAt = DateTimeOffset.UtcNow,
        };

    // ---------------------------------------------------------------- API

    // Фейковый API-портал: JSON {"items":[{"external_id","url","title"}]}.
    private sealed class FakeApiPortal(FakeHttpHandler handler, PortalDescriptor? descriptor = null)
        : ApiPortalBase(descriptor ?? FakeDescriptor("remoteok", PortalAccessKind.OfficialApi), new HttpClient(handler), new SilentLog())
    {
        public FakeHttpHandler Handler { get; } = handler;

        public override Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default)
            => throw new NotSupportedException("fake");

        protected override string BuildSearchUrl(string query)
            => $"https://api.fake.test/search?q={Uri.EscapeDataString(query)}";

        protected override Task<IReadOnlyList<JobPosting>> ParseSearchAsync(string payload, CancellationToken ct)
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            if (!json.RootElement.TryGetProperty("items", out var items))
            {
                throw new ParseFailedException("схема payload сменилась: нет items");
            }

            var result = new List<JobPosting>();
            foreach (var item in items.EnumerateArray())
            {
                var externalId = item.GetProperty("external_id").GetString();
                var url = item.GetProperty("url").GetString()!;
                result.Add(MakePosting(
                    Descriptor,
                    externalId,
                    url,
                    item.GetProperty("title").GetString()));
            }

            return Task.FromResult<IReadOnlyList<JobPosting>>(result);
        }
    }

    [Fact]
    public async Task ApiPortalBase_SearchAsync_MapsPostings()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of(
            """{"items":[{"external_id":"42","url":"https://fake.test/v/42","title":"C# Dev"}]}""",
            "application/json"));
        var portal = new FakeApiPortal(handler);

        var postings = await portal.SearchAsync("c#");

        var posting = Assert.Single(postings);
        Assert.Equal("remoteok", posting.PortalId);
        Assert.Equal("42", posting.ExternalId);
        Assert.Equal("C# Dev", posting.Title);
        Assert.Equal(PortalAccessKind.OfficialApi, posting.AccessKind);
        Assert.Equal(64, posting.DedupeHash.Length);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ApiPortalBase_HttpError_PropagatesToOrchestrator()
    {
        // Одна попытка: не-2xx не ретраится базой — оркестратор решает.
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of("gone", "text/plain", 410));
        var portal = new FakeApiPortal(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => portal.SearchAsync("c#"));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ApiPortalBase_BrokenPayload_ThrowsParseFailed()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of("""{"nope":true}""", "application/json"));
        var portal = new FakeApiPortal(handler);

        await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("c#"));
    }

// --------------------------------------------------------------- Feed

    private const string RssFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel>
          <title>Remote jobs</title>
          <item>
            <title>Senior C# Dev</title>
            <link>https://wwr.test/postings/1</link>
            <pubDate>Tue, 21 Oct 2026 10:00:00 GMT</pubDate>
            <guid>wwr-1</guid>
            <description>Build things.</description>
          </item>
          <item>
            <title>Go Dev</title>
            <link>https://wwr.test/postings/2</link>
            <guid>wwr-2</guid>
          </item>
        </channel></rss>
        """;

    private const string AtomFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title>Backend Engineer</title>
            <link rel="enclosure" href="https://cdn.test/logo.png"/>
            <link rel="alternate" href="https://himalayas.test/jobs/9"/>
            <id>himalayas-9</id>
            <updated>2026-10-21T08:30:00Z</updated>
            <summary>Remote, worldwide.</summary>
          </entry>
        </feed>
        """;

    private sealed class FakeFeedPortal(FakeHttpHandler handler)
        : FeedPortalBase(FakeDescriptor("wwr", PortalAccessKind.Feed), new HttpClient(handler), new SilentLog())
    {
        public FakeHttpHandler Handler { get; } = handler;

        protected override string BuildFeedUrl(string query) => "https://wwr.test/remote-jobs/feed";

        protected override JobPosting MapItem(FeedItem item)
            => MakePosting(Descriptor, item.ExternalId, item.Link, item.Title) with
            {
                DescriptionMd = item.Summary,
            };
    }

    [Fact]
    public void ParseFeed_Rss_ReadsItems()
    {
        var items = FeedPortalBase.ParseFeed(RssFeed);

        Assert.Equal(2, items.Count);
        Assert.Equal("Senior C# Dev", items[0].Title);
        Assert.Equal("https://wwr.test/postings/1", items[0].Link);
        Assert.Equal("wwr-1", items[0].ExternalId);
        Assert.Equal("Build things.", items[0].Summary);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero), items[0].Published);
        Assert.Null(items[1].Published); // pubDate не задан — не роняем элемент
    }

    [Fact]
    public void ParseFeed_Atom_PicksAlternateLink()
    {
        var items = FeedPortalBase.ParseFeed(AtomFeed);

        var item = Assert.Single(items);
        Assert.Equal("https://himalayas.test/jobs/9", item.Link); // не enclosure
        Assert.Equal("himalayas-9", item.ExternalId);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.Zero), item.Published);
    }

    [Fact]
    public void ParseFeed_InvalidXml_ThrowsParseFailed()
    {
        Assert.Throws<ParseFailedException>(() => FeedPortalBase.ParseFeed("<rss><item>"));
    }

    [Fact]
    public async Task FeedPortalBase_SearchAsync_MapsViaMapItem()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of(RssFeed, "application/rss+xml"));
        var portal = new FakeFeedPortal(handler);

        var postings = await portal.SearchAsync("c#");

        var posting = Assert.Single(postings, p => p.ExternalId == "wwr-1");
        Assert.Equal("https://wwr.test/postings/1", posting.CanonicalUrl);
        Assert.Equal("Build things.", posting.DescriptionMd);
        Assert.Equal(PortalAccessKind.Feed, posting.AccessKind);
        Assert.Equal(64, posting.DedupeHash.Length);
        await Assert.ThrowsAsync<NotSupportedException>(() => portal.DetailAsync("wwr-1"));
    }

    // ------------------------------------------------------------- Scrape

    private sealed class FakeScrapePortal(FakeHttpHandler handler, ScrapePortalBase.PolitenessOptions? options = null)
        : ScrapePortalBase(FakeDescriptor("belmeta", PortalAccessKind.Scrape), new HttpClient(handler), new SilentLog(), options)
    {
        public FakeHttpHandler Handler { get; } = handler;

        public override Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default)
            => throw new NotSupportedException("fake");

        protected override string BuildFirstPageUrl(string query) => "https://fake.test/page-1";

        protected override string? GetNextPageUrl(string pageHtml, string pageUrl)
            => pageHtml.Contains("NEXT=", StringComparison.Ordinal)
                ? $"https://fake.test/{pageHtml.Split(';')[1].Replace("NEXT=", string.Empty, StringComparison.Ordinal).Trim()}"
                : null;

        protected override Task<IReadOnlyList<JobPosting>> ParsePageAsync(
            string pageHtml, string pageUrl, CancellationToken ct)
        {
            if (pageHtml.StartsWith("PARSE_FAIL", StringComparison.Ordinal))
            {
                throw new ParseFailedException($"вёрстка сменилась: {pageHtml}");
            }

            return Task.FromResult<IReadOnlyList<JobPosting>>(
            [
                MakePosting(Descriptor, null, $"https://fake.test/vac-{pageHtml.Trim()}", null),
            ]);
        }
    }

    private static FakeHttpHandler PageHandler(params string[] pages)
    {
        var page = 0;
        return new FakeHttpHandler(_ =>
        {
            var body = page < pages.Length ? pages[page] : pages[^1];
            page++;
            return FakeHttpHandler.Of(body, "text/html");
        });
    }

    private static readonly ScrapePortalBase.PolitenessOptions NoWait = new(
        MinDelay: TimeSpan.Zero,
        JitterFraction: 0.0,
        MaxPagesPerRun: 3,
        BreakerThreshold: 3);

    [Fact]
    public async Task ScrapePortalBase_MaxPagesPerRun_LimitsFetches_AndFollowsNext()
    {
        var handler = PageHandler("page-1;NEXT=page-2", "page-2;NEXT=page-3", "page-3;NEXT=page-4", "page-4");
        var portal = new FakeScrapePortal(handler, NoWait with { MaxPagesPerRun = 3 });

        var postings = await portal.SearchAsync("python");

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(3, postings.Count);
    }

    [Fact]
    public async Task ScrapePortalBase_Breaker_OpensAfterConsecutiveFailures()
    {
        // N подряд падений парсинга → breaker; сетевых запросов после — нет.
        var handler = PageHandler("PARSE_FAIL", "PARSE_FAIL", "PARSE_FAIL", "page-ok");
        var portal = new FakeScrapePortal(handler, NoWait);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("q"));
        }

        Assert.True(portal.IsBreakerOpen);
        var requestsAfterBreaker = handler.RequestCount;
        await Assert.ThrowsAsync<BreakerOpenException>(() => portal.SearchAsync("q"));
        Assert.Equal(requestsAfterBreaker, handler.RequestCount);
    }

    [Fact]
    public async Task ScrapePortalBase_SuccessResetsFailureCounter()
    {
        var handler = PageHandler("PARSE_FAIL", "PARSE_FAIL", "page-ok", "PARSE_FAIL", "page-ok2");
        var portal = new FakeScrapePortal(handler, NoWait);

        await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("q"));
        await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("q"));
        await portal.SearchAsync("q"); // успех — счётчик в ноль
        await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("q"));

        Assert.False(portal.IsBreakerOpen);
        Assert.Equal(1, portal.ConsecutiveParseFailures);
    }

    [Fact]
    public async Task ScrapePortalBase_RetryAfter_Seconds_IsRespected()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = FakeHttpHandler.Of("page-1", "text/html");
            response.Headers.Add("Retry-After", "120");
            return response;
        });
        var portal = new FakeScrapePortal(handler, NoWait);

        await portal.SearchAsync("q");

        // Окно ожидания выставлено ≈ +120 с (допуск на тики теста).
        Assert.InRange(
            (portal.RespectUntil - DateTimeOffset.UtcNow).TotalSeconds,
            118,
            125);
    }

    [Fact]
    public async Task ScrapePortalBase_RateLimitRemainingZero_OpensRespectWindow()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = FakeHttpHandler.Of("page-1", "text/html");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        });
        var portal = new FakeScrapePortal(handler, NoWait);
        var before = DateTimeOffset.UtcNow;

        await portal.SearchAsync("q");

        Assert.True(portal.RespectUntil >= before, "X-RateLimit-Remaining=0 обязан открыть окно ожидания");
    }

    [Fact]
    public async Task ScrapePortalBase_QuotaBelowDonorThreshold_StopsBeforeParsing()
    {
        // Донорская семантика (строки 89–94):
        // Remaining < 20 → «останавливаюсь до следующего раза» —
        // останавливается ДО разбора страницы (тело не парсится), прогон
        // не падает. Вербатим: страница скачана, не разобрана, вторая НЕ
        // качается, ошибок нет.
        var handler = new FakeHttpHandler(_ =>
        {
            var response = FakeHttpHandler.Of("page-1;NEXT=page-2", "text/html");
            response.Headers.Add("X-RateLimit-Remaining", "15");
            return response;
        });
        var portal = new FakeScrapePortal(handler, NoWait);

        var postings = await portal.SearchAsync("q");

        Assert.Empty(postings);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ScrapePortalBase_QuotaAboveDonorThreshold_ContinuesPaging()
    {
        // Контроль порога: Remaining = 25 (>= 20) — пагинация продолжается,
        // обе страницы собраны (стоп не срабатывает).
        var page = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            var body = page == 0 ? "page-1;NEXT=page-2" : "page-2";
            page++;
            var response = FakeHttpHandler.Of(body, "text/html");
            response.Headers.Add("X-RateLimit-Remaining", "25");
            return response;
        });
        var portal = new FakeScrapePortal(handler, NoWait with { MaxPagesPerRun = 2 });

        var postings = await portal.SearchAsync("q");

        Assert.Equal(2, postings.Count);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ScrapePortalBase_HttpError_DoesNotCountTowardBreaker()
    {
        // Ошибки сети/HTTP ≠ падение парсинга: breaker их не считает —
        // refused-ветка оркестратора разберёт.
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of("gone", "text/plain", (int)HttpStatusCode.Gone));
        var portal = new FakeScrapePortal(handler, NoWait);

        await Assert.ThrowsAsync<HttpRequestException>(() => portal.SearchAsync("q"));

        Assert.False(portal.IsBreakerOpen);
        Assert.Equal(0, portal.ConsecutiveParseFailures);
    }

    // ---------------------------------------------------------------- r20: SupportsDetail

    [Fact]
    public void SupportsDetail_FalseForFeedAndApiBases_TrueForScrape()
    {
        // Ленты и API-порталы получают описание прямо в ответе поиска —
        // DetailAsync им не нужен (NotSupportedException). Scrape-базы
        // (hh-группа) умеют открывать страницу вакансии — true.
        var feed = new FakeFeedPortal(new FakeHttpHandler(
            _ => FakeHttpHandler.Of("<rss><channel/></rss>", "application/rss+xml")));
        var api = new FakeApiPortal(new FakeHttpHandler(_ => FakeHttpHandler.Of("{}", "application/json")));
        var scrape = new FakeScrapePortal(
            new FakeHttpHandler(_ => FakeHttpHandler.Of("<html></html>", "text/html")), NoWait);

        Assert.False(feed.SupportsDetail);
        Assert.False(api.SupportsDetail);
        Assert.True(scrape.SupportsDetail);
    }
}
