using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: гейты адаптера hh-by (карточка §2.11 в portals-catalog.md): парсинг
// документированной схемы API группы hh (golden-фикстура), честный
// ParseFailed при смене схемы, канарейка health-check с вердиктом 403,
// выключенность по умолчанию (DefaultEnabled=false). Сети нет — FakeHttpHandler.
public class HhByPortalTests
{
    private static (HhByPortal Portal, FakeHttpHandler Handler) Create(
        FakeHttpHandler? handler = null)
    {
        handler ??= new FakeHttpHandler(_ =>
            FakeHttpHandler.Of("""{"items": [], "found": 0}""", "application/json"));
        return (new HhByPortal(new HttpClient(handler), new SilentLog()), handler);
    }

    // Golden-фикстура: документированная схема /vacancies группы hh.
    private const string FixtureJson = """
    {
      "items": [
        {
          "id": "12345678",
          "name": "Senior .NET Developer",
          "area": { "id": "1002", "name": "Минск" },
          "salary": { "from": 3000, "to": 4500, "currency": "USD" },
          "employer": { "id": "98765", "name": "ООО Ромашка" },
          "alternate_url": "https://hh.by/vacancy/12345678"
        },
        {
          "id": "12345679",
          "name": "QA Engineer",
          "area": { "id": "1002", "name": "Минск" },
          "salary": { "from": 1500, "currency": "BYN" },
          "employer": { "id": "98766", "name": "ООО Тюльпан" },
          "alternate_url": "https://hh.by/vacancy/12345679"
        },
        {
          "id": "12345680",
          "name": "DevOps",
          "area": { "id": "1002", "name": "Минск" },
          "employer": { "id": "98767", "name": "ООО Ландыш" },
          "alternate_url": "https://hh.by/vacancy/12345680"
        }
      ],
      "found": 3,
      "page": 0,
      "pages": 1,
      "per_page": 100
    }
    """;

    private static FakeHttpHandler FixtureHandler() => new(_ =>
        FakeHttpHandler.Of(FixtureJson, "application/json"));

    [Fact]
    public async Task Descriptor_MatchesCardSection211()
    {
        var (portal, _) = Create();

        var d = portal.Descriptor;
        Assert.Equal("hh-by", d.Id);
        Assert.Equal("HeadHunter.by", d.DisplayName);
        Assert.Equal(PortalSourceClass.Commercial, d.SourceClass);
        Assert.Equal(PortalAccessKind.OfficialApi, d.Access);
        Assert.Equal(PortalAuthKind.None, d.Auth);
        Assert.Equal(5, d.DefaultPriority);
        Assert.False(d.DefaultEnabled, "карточка §2.11: портал выключен по умолчанию");
        Assert.NotNull(d.ApplyWarning);
    }

    [Fact]
    public async Task ParseSearchAsync_MapsDocumentedSchema()
    {
        var (portal, _) = Create(FixtureHandler());

        var postings = await portal.SearchAsync(".net");

        Assert.Equal(3, postings.Count);

        var first = postings[0];
        Assert.Equal("hh-by", first.PortalId);
        Assert.Equal("12345678", first.ExternalId);
        Assert.Equal("https://hh.by/vacancy/12345678", first.CanonicalUrl);
        Assert.Equal("Senior .NET Developer", first.Title);
        Assert.Equal("ООО Ромашка", first.Company);
        Assert.Equal("Минск", first.Location);
        Assert.Equal("от 3000 до 4500 USD", first.SalaryText);
        Assert.Equal(PortalAccessKind.OfficialApi, first.AccessKind);

        // Дедуп обязателен и детерминирован (композит §2.8).
        Assert.Equal(
            DedupeHasher.ComputeComposite("hh-by", "12345678", "https://hh.by/vacancy/12345678"),
            first.DedupeHash);
    }

    [Fact]
    public async Task ParseSearchAsync_SalaryVariants()
    {
        var (portal, _) = Create(FixtureHandler());

        var postings = await portal.SearchAsync("qa");

        Assert.Equal("от 1500 BYN", postings[1].SalaryText);
        Assert.Null(postings[2].SalaryText);
    }

    [Fact]
    public async Task ParseSearchAsync_SchemaChanged_ThrowsParseFailed()
    {
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of("""{"vacancies": []}""", "application/json"));
        var (portal, _) = Create(handler);

        var ex = await Assert.ThrowsAsync<ParseFailedException>(
            () => portal.SearchAsync("q"));
        Assert.Contains("items", ex.Message);
    }

    [Fact]
    public async Task ParseSearchAsync_ItemWithoutAlternateUrl_ThrowsParseFailed()
    {
        // alternate_url — NOT NULL канона §4: её пропажа = смена схемы.
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of("""{"items": [{"id": "1", "name": "X"}]}""", "application/json"));
        var (portal, _) = Create(handler);

        await Assert.ThrowsAsync<ParseFailedException>(() => portal.SearchAsync("q"));
    }

    [Fact]
    public async Task HealthCheck_Canary403_ReportsClosedWithRecheckHint()
    {
        // healthCheck-контракт карточки: 403 = платформа закрыла анонимный
        // поиск; Degraded с датой-ориентиром, не Ok и не краш.
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of("", "application/json", status: 403));
        var (portal, _) = Create(handler);

        var report = await portal.HealthCheckAsync();

        Assert.Equal(PortalHealthStatus.Degraded, report.Status);
        Assert.Contains("403", report.Detail);
        Assert.Contains("квартал", report.Detail);
    }

    [Fact]
    public async Task HealthCheck_Canary200_ReportsOkWithCardHint()
    {
        // Поиск вернулся → Ok + «пересмотреть карточку» (включение — решение
        // владельца).
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of("""{"items": []}""", "application/json"));
        var (portal, _) = Create(handler);

        var report = await portal.HealthCheckAsync();

        Assert.Equal(PortalHealthStatus.Ok, report.Status);
        Assert.Contains("карточку", report.Detail);
    }

    [Fact]
    public async Task HealthCheck_NetworkFailure_ReportsDegradedNotThrow()
    {
        // Исключение канарейки не крашит keys-check/отчёт (§2.9).
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("dns dead"));
        var (portal, _) = Create(handler);

        var report = await portal.HealthCheckAsync();

        Assert.Equal(PortalHealthStatus.Degraded, report.Status);
        Assert.Contains("dns dead", report.Detail);
    }

    [Fact]
    public async Task Registry_DefaultChain_ExcludesHhBy()
    {
        var (portal, _) = Create();
        var registry = new PortalRegistry([portal]);

        Assert.Empty(registry.ResolveChain());
        Assert.Single(registry.Portals);
    }

    [Fact]
    public async Task Registry_ConfigOverride_IncludesHhBy()
    {
        var (portal, _) = Create();
        var registry = new PortalRegistry([portal]);

        var chain = registry.ResolveChain(
        [
            new PortalRegistry.PortalOrderEntry("hh-by", Enabled: true),
        ]);

        Assert.Single(chain, p => p.Descriptor.Id == "hh-by");
    }
}
