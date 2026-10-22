using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: гейты юридического стуба rabota-by (карточка §2.11): любые
// поиск/детали — NotSupportedException с вердиктом (п.1.5 соглашения), без
// тихих пустых ответов; health-check — Degraded с той же причиной; портал
// выключен по умолчанию, но виден реестру/keys-check (§5: причина всегда).
public class RabotaByPortalTests
{
    private static RabotaByPortal Create() =>
        new(new HttpClient(new FakeHttpHandler(_ =>
            throw new InvalidOperationException("сеть не должна вызываться"))), new SilentLog());

    [Fact]
    public void Descriptor_MatchesCardSection211()
    {
        var d = Create().Descriptor;

        Assert.Equal("rabota-by", d.Id);
        Assert.Equal("Rabota.by", d.DisplayName);
        Assert.Equal(PortalSourceClass.Commercial, d.SourceClass);
        Assert.Equal(PortalAccessKind.OfficialApi, d.Access);
        Assert.Equal(PortalAuthKind.None, d.Auth);
        Assert.Equal(5, d.DefaultPriority);
        Assert.False(d.DefaultEnabled, "карточка §2.11: портал выключен по умолчанию");
        Assert.NotNull(d.ApplyWarning);
    }

    [Fact]
    public async Task SearchAsync_ThrowsNotSupported_WithVerdict()
    {
        var portal = Create();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => portal.SearchAsync(".net"));

        Assert.Contains("п.1.5", ex.Message);
        Assert.Contains("работодател", ex.Message);
    }

    [Fact]
    public async Task DetailAsync_ThrowsNotSupported_WithVerdict()
    {
        var portal = Create();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => portal.DetailAsync("https://rabota.by/vacansii/1"));
    }

    [Fact]
    public async Task HealthCheck_ReportsDegraded_WithVerdict()
    {
        var report = await Create().HealthCheckAsync();

        Assert.Equal(PortalHealthStatus.Degraded, report.Status);
        Assert.Contains("п.1.5", report.Detail);
    }

    [Fact]
    public void Registry_DefaultChain_ExcludesRabotaBy()
    {
        var registry = new PortalRegistry([Create()]);

        Assert.Empty(registry.ResolveChain());
        Assert.Single(registry.Portals);
    }
}
