using AiJobSearchExUssr.Portals;
using AiJobSearchExUssr.Services;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: гейты keys-check: auth=none зелёный сразу; ключевой —
// наличие ключа + живой пинг; нет ключа/мёртвый пинг = вне цепи С ПРИЧИНОЙ,
// не краш; гео-пинг 403 → GeoBlocked без сжигания ретраев.
public class KeysCheckServiceTests
{
    private static KeysCheckService Create(
        ILocalSecrets secrets,
        PortalRegistry? registry = null,
        Func<PortalDescriptor, CancellationToken, Task<KeyPingOutcome>>? ping = null)
        => new(
            registry ?? new PortalRegistry([
                new FakePortal("remoteok", 8, PortalAuthKind.None),
                new FakePortal("adzuna", 6, PortalAuthKind.PersonalKey),
            ]),
            secrets,
            ping);

    [Fact]
    public async Task AuthNone_IsGreen_WithoutKeyOrPing()
    {
        var pings = 0;
        var service = Create(
            new FakeLocalSecrets(new Dictionary<string, string>()),
            ping: (_, _) =>
            {
                pings++;
                return Task.FromResult(new KeyPingOutcome(Succeeded: true));
            });

        var report = await service.CheckAsync(new FakePortal("remoteok"));

        Assert.Equal(KeyCheckStatus.Ok, report.Status);
        Assert.Contains("auth=none", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, pings); // ключевой ветки не трогаем
    }

    [Fact]
    public async Task PersonalKey_MissingKey_IsNoKey_WithReason()
    {
        var service = Create(new FakeLocalSecrets(new Dictionary<string, string>()));

        var report = await service.CheckAsync(new FakePortal("adzuna", auth: PortalAuthKind.PersonalKey));

        Assert.Equal(KeyCheckStatus.NoKey, report.Status);
        Assert.Contains("local.json", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalKey_KeyPresent_PingOk_IsOk()
    {
        var service = Create(
            new FakeLocalSecrets(new Dictionary<string, string> { ["adzuna"] = "test-key" }),
            ping: (_, _) => Task.FromResult(new KeyPingOutcome(Succeeded: true, Detail: "per_page=1")));

        var report = await service.CheckAsync(new FakePortal("adzuna", auth: PortalAuthKind.PersonalKey));

        Assert.Equal(KeyCheckStatus.Ok, report.Status);
        Assert.Contains("per_page=1", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalKey_PingFails_IsPingFailed_WithReason()
    {
        var service = Create(
            new FakeLocalSecrets(new Dictionary<string, string> { ["adzuna"] = "test-key" }),
            ping: (_, _) => Task.FromResult(new KeyPingOutcome(Succeeded: false, Detail: "503 Service Unavailable")));

        var report = await service.CheckAsync(new FakePortal("adzuna", auth: PortalAuthKind.PersonalKey));

        Assert.Equal(KeyCheckStatus.PingFailed, report.Status);
        Assert.Contains("503", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalKey_PingThrows_IsPingFailed_NotCrash()
    {
        // «Не краш» — любое исключение пинга превращается в вердикт.
        var service = Create(
            new FakeLocalSecrets(new Dictionary<string, string> { ["adzuna"] = "test-key" }),
            ping: (_, _) => throw new HttpRequestException("connection refused"));

        var report = await service.CheckAsync(new FakePortal("adzuna", auth: PortalAuthKind.PersonalKey));

        Assert.Equal(KeyCheckStatus.PingFailed, report.Status);
        Assert.Contains("connection refused", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalKey_PingGeoBlocked_IsGeoBlocked()
    {
        var service = Create(
            new FakeLocalSecrets(new Dictionary<string, string> { ["adzuna"] = "test-key" }),
            ping: (_, _) => Task.FromResult(new KeyPingOutcome(
                Succeeded: false, GeoBlocked: true, Detail: "403 region restricted")));

        var report = await service.CheckAsync(new FakePortal("adzuna", auth: PortalAuthKind.PersonalKey));

        Assert.Equal(KeyCheckStatus.GeoBlocked, report.Status);
        Assert.Contains("регион", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalKey_KeyPresent_PingNotConfigured_IsHonestSkeletonStatus()
    {
        // Адаптеров нет — ключ найден, но живой пинг не настроен.
        var service = Create(new FakeLocalSecrets(new Dictionary<string, string> { ["adzuna"] = "test-key" }));

        var report = await service.CheckAsync(new FakePortal("adzuna", auth: PortalAuthKind.PersonalKey));

        Assert.Equal(KeyCheckStatus.PingNotConfigured, report.Status);
        Assert.Contains("адаптером портала", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAll_CoversEveryRegisteredPortal()
    {
        var registry = new PortalRegistry([
            new FakePortal("a", 2),
            new FakePortal("b", 3, PortalAuthKind.PersonalKey),
        ]);
        var service = new KeysCheckService(
            registry,
            new FakeLocalSecrets(new Dictionary<string, string>()));

        var reports = await service.CheckAllAsync();

        Assert.Equal(2, reports.Count);
        Assert.Equal(["a", "b"], reports.Select(r => r.PortalId));
        Assert.All(reports, r => Assert.True(r.Reason.Length > 0, "причина обязательна"));
    }
}
