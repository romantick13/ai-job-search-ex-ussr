using AiJobSearchBy.Portals;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: гейты PortalRegistry (architecture.md §3.3): конфиг
// сильнее приоритетов, хвост — по DefaultPriority, disabled вне цепи,
// снапшот изолирован между вызовами.
public class PortalRegistryTests
{
    private static PortalRegistry CreateRegistry(params FakePortal[] portals)
        => new(portals);

    [Fact]
    public void Ctor_DuplicatePortalId_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateRegistry(
            new FakePortal("remoteok", 8),
            new FakePortal("remoteok", 8)));
    }

    [Fact]
    public void ResolveChain_NoConfig_OrdersByPriorityThenId()
    {
        var registry = CreateRegistry(
            new FakePortal("c", 5),
            new FakePortal("b", 2),
            new FakePortal("a", 2));

        var chain = registry.ResolveChain();

        Assert.Equal(["a", "b", "c"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void ResolveChain_ConfigOrderWins()
    {
        var registry = CreateRegistry(
            new FakePortal("a", 2),
            new FakePortal("b", 9));

        var chain = registry.ResolveChain(
        [
            new PortalRegistry.PortalOrderEntry("b", Enabled: true),
            new PortalRegistry.PortalOrderEntry("a", Enabled: true),
        ]);

        Assert.Equal(["b", "a"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void ResolveChain_UnmentionedPortals_AppendedToTailByPriority()
    {
        var registry = CreateRegistry(
            new FakePortal("a", 2),
            new FakePortal("b", 3),
            new FakePortal("c", 1));

        var chain = registry.ResolveChain(
        [
            new PortalRegistry.PortalOrderEntry("c", Enabled: true),
        ]);

        Assert.Equal(["c", "a", "b"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void ResolveChain_DisabledEntry_ExcludedFromChain()
    {
        var registry = CreateRegistry(
            new FakePortal("a", 2),
            new FakePortal("b", 3));

        var chain = registry.ResolveChain(
        [
            new PortalRegistry.PortalOrderEntry("a", Enabled: false),
        ]);

        Assert.Equal(["b"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void ResolveChain_UnknownOrDuplicateConfigIds_Ignored()
    {
        var registry = CreateRegistry(new FakePortal("a", 2));

        var chain = registry.ResolveChain(
        [
            new PortalRegistry.PortalOrderEntry("ghost", Enabled: true),
            new PortalRegistry.PortalOrderEntry("a", Enabled: true),
            new PortalRegistry.PortalOrderEntry("a", Enabled: true),
        ]);

        Assert.Equal(["a"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void ResolveChain_ReturnsIndependentSnapshot()
    {
        // Снапшот порядка фиксируется на старте прогона; правки
        // «посреди прогона» (в тесте — мутация выданного списка) не должны
        // трогать следующие вызовы реестра.
        var registry = CreateRegistry(new FakePortal("a", 2), new FakePortal("b", 3));

        var first = (List<IPortalService>)registry.ResolveChain();
        first.Clear();

        var second = registry.ResolveChain();
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void Portals_Property_IsStablyOrdered()
    {
        var registry = CreateRegistry(
            new FakePortal("z", 1),
            new FakePortal("a", 1),
            new FakePortal("m", 0));

        var first = registry.Portals.Select(p => p.Descriptor.Id);
        var second = registry.Portals.Select(p => p.Descriptor.Id);

        Assert.Equal(["m", "a", "z"], first);
        Assert.Equal(first, second);
    }
}

// Выключатель карточки DefaultEnabled — зеркало
// default_enabled registry.json; конфиг сильнее дефолта.
public class PortalRegistryDefaultEnabledTests
{
    [Fact]
    public void ResolveChain_DefaultDisabled_ExcludedFromTail()
    {
        var registry = new PortalRegistry(
        [
            new FakePortal("core", 8),
            new FakePortal("rabota-by", 5, defaultEnabled: false),
        ]);

        var chain = registry.ResolveChain();

        Assert.Equal(["core"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void ResolveChain_ConfigEnabledTrue_OverridesDefaultDisabled()
    {
        // «Включение отдельным решением»: запись конфига Enabled=true сильнее
        // карточки — выключенный по умолчанию портал входит в цепь.
        var registry = new PortalRegistry(
        [
            new FakePortal("core", 8),
            new FakePortal("hh-by", 5, defaultEnabled: false),
        ]);

        var chain = registry.ResolveChain(
        [
            new PortalRegistry.PortalOrderEntry("hh-by", Enabled: true),
        ]);

        Assert.Equal(["hh-by", "core"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void ResolveChain_ConfigEnabledFalse_StillExcluded()
    {
        var registry = new PortalRegistry(
        [
            new FakePortal("core", 8),
            new FakePortal("hh-by", 5, defaultEnabled: false),
        ]);

        var chain = registry.ResolveChain(
        [
            new PortalRegistry.PortalOrderEntry("hh-by", Enabled: false),
        ]);

        Assert.Equal(["core"], chain.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void Portals_Listing_IncludesDefaultDisabledPortals()
    {
        // Вне цепи ≠ выкинут из реестра: keys-check/UI видят портал
        // с причиной, отчёт прогона сможет пометить disabled.
        var registry = new PortalRegistry(
        [
            new FakePortal("core", 8),
            new FakePortal("rabota-by", 5, defaultEnabled: false),
        ]);

        Assert.Equal(
            ["rabota-by", "core"],
            registry.Portals.Select(p => p.Descriptor.Id));
    }
}
