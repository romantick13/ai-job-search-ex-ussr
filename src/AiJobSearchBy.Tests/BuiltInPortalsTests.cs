using AiJobSearchBy.Portals;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: пин коробки v1 (RFC-003 §2.3): 6 порталов без ключей; генератор README
// (tools/gen_readme_counters.py) держит «порталов в цепочке по умолчанию: 6» —
// каталог и реестр не должны разъезжаться.
public class BuiltInPortalsTests
{
    [Fact]
    public void Core_BoxIsSixPortals()
    {
        Assert.Equal(6, BuiltInPortals.Core.Count);
    }

    [Fact]
    public void Core_IdsAreUnique_AndRegistryStyle()
    {
        var ids = BuiltInPortals.Core.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(
            new[] { "remoteok", "remotive", "jobicy", "wwr", "himalayas", "nodesk" },
            ids);
    }

    [Fact]
    public void Core_AllAuthNone_StartWithoutKeys()
    {
        // §2.9/§3.3: старт без ключей — auth=none работают сразу.
        Assert.All(BuiltInPortals.Core, p => Assert.Equal(PortalAuthKind.None, p.Auth));
    }

    [Fact]
    public void Core_PrioritiesMatchRegistryTable()
    {
        // Таблица RFC-003 §2.3: remoteok 8, remotive 8, jobicy 7, wwr 7,
        // himalayas 6, nodesk 6.
        var byId = BuiltInPortals.Core.ToDictionary(p => p.Id, p => p.DefaultPriority);

        Assert.Equal(8, byId["remoteok"]);
        Assert.Equal(8, byId["remotive"]);
        Assert.Equal(7, byId["jobicy"]);
        Assert.Equal(7, byId["wwr"]);
        Assert.Equal(6, byId["himalayas"]);
        Assert.Equal(6, byId["nodesk"]);
    }

    [Fact]
    public void Core_AccessMatchesBaseClassLayout()
    {
        // Раскладка §2.1/§2.3: remoteok/remotive/jobicy — ApiPortalBase
        // (official_api); wwr/himalayas/nodesk — FeedPortalBase (feed).
        var byId = BuiltInPortals.Core.ToDictionary(p => p.Id, p => p.Access);

        Assert.Equal(PortalAccessKind.OfficialApi, byId["remoteok"]);
        Assert.Equal(PortalAccessKind.OfficialApi, byId["remotive"]);
        Assert.Equal(PortalAccessKind.OfficialApi, byId["jobicy"]);
        Assert.Equal(PortalAccessKind.Feed, byId["wwr"]);
        Assert.Equal(PortalAccessKind.Feed, byId["himalayas"]);
        Assert.Equal(PortalAccessKind.Feed, byId["nodesk"]);
    }

    [Fact]
    public void Core_SearchWithoutApplyWarnings()
    {
        // Поиск ≠ подача (§2.7): у коробки предупреждений нет — «мёртвые»
        // площадки появляются вместе с их WP (hh-исторический пример).
        Assert.All(BuiltInPortals.Core, p =>
        {
            Assert.True(p.ApplySupported);
            Assert.Null(p.ApplyWarning);
        });
    }
}

// WP-P1 (PLAN §7): отложенные карточки §2.11 — манифесты в реестре, поиск
// выключен по умолчанию («включение отдельным решением»).
public class BuiltInPortalsDeferredTests
{
    [Fact]
    public void Deferred_IsTwoRbPortals()
    {
        Assert.Equal(
            new[] { "hh-by", "rabota-by" },
            BuiltInPortals.Deferred.Select(p => p.Id));
    }

    [Fact]
    public void Deferred_AllDisabledByDefault()
    {
        Assert.All(BuiltInPortals.Deferred, p =>
        {
            Assert.False(p.DefaultEnabled, $"{p.Id}: карточка §2.11 требует выключенного дефолта");
            Assert.NotNull(p.ApplyWarning);
            Assert.Equal(5, p.DefaultPriority);
            Assert.Equal(PortalAccessKind.OfficialApi, p.Access);
        });
    }

    [Fact]
    public void Deferred_DoesNotOverlapCore_UnionIsEight()
    {
        var coreIds = BuiltInPortals.Core.Select(p => p.Id);
        var deferredIds = BuiltInPortals.Deferred.Select(p => p.Id);

        Assert.Empty(coreIds.Intersect(deferredIds));
        Assert.Equal(8, coreIds.Count() + deferredIds.Count());
    }
}
