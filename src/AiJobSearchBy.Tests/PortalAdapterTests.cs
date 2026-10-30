using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;
using AiJobSearchBy.Tests;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: Core-адаптеры (remoteok/remotive/jobicy — JSON API, wwr/
// himalayas/nodesk — RSS). Golden-фикстуры сняты живым зондом —
// смена схемы источника делает тест красным. Сеть выключена —
// FakeHttpHandler; URL-строители проверяются на перехваченном запросе.
public class PortalAdapterTests
{
    private static (PortalAdapter Portal, FakeHttpHandler Handler) Make<PortalAdapter>(
        string payload, string? expectedUrlPart = null)
        where PortalAdapter : PortalBase
    {
        FakeHttpHandler? handler = null;
        handler = new FakeHttpHandler(request =>
        {
            if (expectedUrlPart is not null)
            {
                Assert.Contains(expectedUrlPart, request.RequestUri!.ToString());
            }
            return FakeHttpHandler.Of(payload, "application/json");
        });
        var http = new HttpClient(handler);
        PortalAdapter portal = typeof(PortalAdapter).Name switch
        {
            nameof(RemoteOkPortal) => (PortalAdapter)(object)new RemoteOkPortal(http, new SilentLog()),
            nameof(RemotivePortal) => (PortalAdapter)(object)new RemotivePortal(http, new SilentLog()),
            nameof(JobicyPortal) => (PortalAdapter)(object)new JobicyPortal(http, new SilentLog()),
            _ => throw new InvalidOperationException("не тот адаптер"),
        };
        return (portal, handler);
    }

    [Fact]
    public async Task RemoteOk_ParsesItems_SkipsMetaRecord()
    {
// Первый элемент массива — мета-запись API (legal) без position: пропуск.
        const string payload = """
            [
              {"legal":"API Terms","last_updated":1792656004},
              {"slug":"senior-dev","id":1137550,"epoch":1792656006,"date":"2026-10-21T00:00:06+00:00",
               "position":"Clinical Data Manager","company":"Penumbra",
               "location":"Your Remote US Home Office",
               "url":"https://remoteok.com/remote-jobs/remote-clinical-1137550",
               "description":"Duties: data","salary_min":90000,"salary_max":130000},
              {"position":"No Url Job","company":"X"}
            ]
            """;
        var (portal, _) = Make<RemoteOkPortal>(payload);

        var items = await portal.SearchAsync("clinical");

        // Мета-запись и item без url — осознанные пропуски; валидная — одна.
        var item = Assert.Single(items);
        Assert.Equal("remoteok", item.PortalId);
        Assert.Equal("Clinical Data Manager", item.Title);
        Assert.Equal("Penumbra", item.Company);
        Assert.Equal("от 90000 до 130000 USD", item.SalaryText);
        Assert.NotEmpty(item.DedupeHash);

        // Клиентский фильтр по запросу (API параметров не принимает).
        var filtered = portal.ApplyQuery(items, "developer");
        Assert.Empty(filtered);
        Assert.Single(portal.ApplyQuery(items, "clinical"));
    }

    [Fact]
    public async Task RemoteOk_EmptyCatalog_IsParseFailed()
    {
        var (portal, _) = Make<RemoteOkPortal>("[]");

        await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("query"));
    }

    [Fact]
    public async Task Remotive_SearchesByQuery_ParsesJobs()
    {
        const string payload = """
            {"0-legal-notice":"ok","job-count":1,
             "jobs":[{"id":2091158,"url":"https://remotive.com/remote-jobs/software-engineer-2091158",
               "title":"Software Engineer, AI Coding","company_name":"Askable",
               "category":"Software Development","job_type":"full_time",
               "candidate_required_location":"Worldwide","salary":"$1000 per task",
               "publication_date":"2026-10-17T16:04:29","description":"<p>build things</p>"}]}
            """;
        var (portal, handler) = Make<RemotivePortal>(payload);

        var items = await portal.SearchAsync("engineer");

        var item = Assert.Single(items);
        Assert.Equal("remotive", item.PortalId);
        Assert.Equal("Software Engineer, AI Coding", item.Title);
        Assert.Equal("Askable", item.Company);
        Assert.Equal("Worldwide", item.Location);
        Assert.Equal("$1000 per task", item.SalaryText);
        Assert.Contains("api/remote-jobs?", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("search=engineer", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Jobicy_SearchesByTag_ParsesJobs()
    {
        const string payload = """
            {"apiVersion":"2.2.19","jobCount":1,
             "jobs":[{"id":150754,"url":"https://jobicy.com/jobs/150754-business-development-manager",
               "jobTitle":"Business Development Manager","companyName":"Thoughtworks",
               "jobGeo":"Singapore","jobType":"Full Time",
               "pubDate":"2026-10-22T06:50:07+00:00",
               "jobExcerpt":"Grow the pipeline"}]}
            """;
        var (portal, handler) = Make<JobicyPortal>(payload);

        var items = await portal.SearchAsync("business");

        var item = Assert.Single(items);
        Assert.Equal("jobicy", item.PortalId);
        Assert.Equal("Business Development Manager", item.Title);
        Assert.Equal("Thoughtworks", item.Company);
        Assert.Equal("Singapore", item.Location);
        Assert.Contains("tag=business", handler.Requests[0].RequestUri!.ToString());
        // jobExcerpt идёт в описание, когда jobDescription пуст.
        Assert.Equal("Grow the pipeline", item.DescriptionMd);
    }

    [Fact]
    public async Task Wwr_SplitsCompanyFromTitle()
    {
// Формат ленты «Компания: Должность» (зонд).
        const string payload = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel>
              <item>
                <title>Affirm: Analytics Engineer II</title>
                <link>https://weworkremotely.com/remote-jobs/affirm-analytics-engineer-ii</link>
                <guid>https://weworkremotely.com/remote-jobs/affirm-analytics-engineer-ii</guid>
                <pubDate>Thu, 22 Oct 2026 07:31:25 +0000</pubDate>
                <category>Full-Stack Programming</category>
                <description>&lt;p&gt;Headquarters: NYC&lt;/p&gt;</description>
              </item>
            </channel></rss>
            """;
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of(payload, "application/rss+xml"));
        var portal = new WwrPortal(new HttpClient(handler), new SilentLog());

        var items = await portal.SearchAsync("developer");

        var item = Assert.Single(items);
        Assert.Equal("wwr", item.PortalId);
        // «Компания: Должность» раскладывается честно.
        Assert.Equal("Affirm", item.Company);
        Assert.Equal("Analytics Engineer II", item.Title);
        Assert.NotEmpty(item.DedupeHash);
    }

    [Fact]
    public async Task Himalayas_ParsesCdataFeed()
    {
// Title в CDATA; компания/регион в ленте отсутствуют — честно null.
        const string payload = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel>
              <item>
                <title><![CDATA[Patient Access Representative - Remote]]></title>
                <link>https://himalayas.app/companies/mercy/jobs/patient-access-rep</link>
                <guid>https://himalayas.app/companies/mercy/jobs/patient-access-rep</guid>
                <pubDate>Thu, 22 Oct 2026 10:50:50 GMT</pubDate>
                <description><![CDATA[Find your calling at Mercy!]]></description>
              </item>
            </channel></rss>
            """;
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of(payload, "application/rss+xml"));
        var portal = new HimalayasPortal(new HttpClient(handler), new SilentLog());

        var items = await portal.SearchAsync("developer");

        var item = Assert.Single(items);
        Assert.Equal("himalayas", item.PortalId);
        Assert.Equal("Patient Access Representative - Remote", item.Title);
        Assert.Null(item.Company);
    }

    [Fact]
    public async Task Nodesk_SplitsCompanyFromAtTitle()
    {
// Формат ленты «Должность at Компания» (зонд).
        const string payload = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel>
              <item>
                <title>Senior Meta Strategist at Directive Consulting</title>
                <link>https://nodesk.co/remote-jobs/directive-consulting-senior-meta-strategist</link>
                <guid>https://nodesk.co/remote-jobs/directive-consulting-senior-meta-strategist</guid>
                <pubDate>Tue, 20 Oct 2026 08:00:00 +0200</pubDate>
                <description>B2B marketing agency</description>
              </item>
            </channel></rss>
            """;
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of(payload, "application/rss+xml"));
        var portal = new NodeskPortal(new HttpClient(handler), new SilentLog());

        var items = await portal.SearchAsync("marketing");

        var item = Assert.Single(items);
        Assert.Equal("nodesk", item.PortalId);
        Assert.Equal("Directive Consulting", item.Company);
        Assert.Equal("Senior Meta Strategist", item.Title);
    }
}
