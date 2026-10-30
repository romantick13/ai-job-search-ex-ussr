using AiJobSearchExUssr.Portals;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: пин каталога порталов: 6 международных источников без ключей
// (Core) + 4 зеркала группы hh (Беларусь/Россия/Казахстан/Узбекистан).
// Каталог и реестр не должны разъезжаться.
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
        // Старт без ключей — auth=none работают сразу.
        Assert.All(BuiltInPortals.Core, p => Assert.Equal(PortalAuthKind.None, p.Auth));
    }

    [Fact]
    public void Core_PrioritiesMatchRegistryTable()
    {
        // Приоритеты: remoteok 8, remotive 8, jobicy 7, wwr 7,
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
        // Раскладка: remoteok/remotive/jobicy — ApiPortalBase
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
        // Поиск ≠ подача: у международных источников предупреждений нет —
        // ApplyWarning появляется у площадок с ограничениями подачи.
        Assert.All(BuiltInPortals.Core, p =>
        {
            Assert.True(p.ApplySupported);
            Assert.Null(p.ApplyWarning);
        });
    }
}

// Зеркала группы hh: ровно четыре страны бывшего СССР, где у группы есть
// собственный сайт-зеркало (проверено живыми запросами 2026-10-29:
// hh.kg/hh.tj не отвечают, hh.md/hh.ge/hh.am — чужие сайты). Коды
// регионов — справочник api.hh.ru/areas/countries.
public class BuiltInPortalsHhMirrorsTests
{
    [Fact]
    public void Mirrors_AreFourExUssrCountries()
    {
        var cards = BuiltInPortals.Deferred;

        Assert.Equal(4, cards.Count);
        Assert.Equal(
            new[] { "hh-by", "hh-kz", "hh-ru", "hh-uz" },
            cards.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Mirrors_HostsAndAreas_MatchOfficialCountryList()
    {
        // (хост, код региона) каждого зеркала; area — код страны из
        // api.hh.ru/areas/countries: Беларусь 16, Казахстан 40,
        // Россия 113, Узбекистан 97.
        var byId = BuiltInPortals.HhMirrors.ToDictionary(m => m.Card.Id);

        Assert.Equal(("https://rabota.by", "16"), (byId["hh-by"].Host, byId["hh-by"].AreaId));
        Assert.Equal(("https://hh.kz", "40"), (byId["hh-kz"].Host, byId["hh-kz"].AreaId));
        Assert.Equal(("https://hh.ru", "113"), (byId["hh-ru"].Host, byId["hh-ru"].AreaId));
        Assert.Equal(("https://hh.uz", "97"), (byId["hh-uz"].Host, byId["hh-uz"].AreaId));
    }

    [Fact]
    public void Mirrors_CardsDefaults()
    {
        // Каждое зеркало: браузерная эмуляция, выключено по умолчанию (r21 —
        // все чекбоксы выключены, юзер собирает цепочку сам),
        // одинаковый приоритет 5, честный ApplyWarning (поиск ≠ подача).
        Assert.All(BuiltInPortals.Deferred, card =>
        {
            Assert.False(card.DefaultEnabled, $"{card.Id}: выключено по умолчанию (r21)");
            Assert.Equal(PortalAccessKind.Scrape, card.Access);
            Assert.Equal(PortalAuthKind.None, card.Auth);
            Assert.Equal(5, card.DefaultPriority);
            Assert.NotNull(card.ApplyWarning);
            Assert.Contains("регистрация не нужна", card.ApplyWarning);
            Assert.Contains("отклик руками на сайте", card.ApplyWarning);
        });
    }

    [Fact]
    public void Mirrors_DisplayNames_ShowCountry()
    {
        Assert.Contains("Беларусь", BuiltInPortals.HhBy.Card.DisplayName);
        Assert.Contains("Россия", BuiltInPortals.HhRu.Card.DisplayName);
        Assert.Contains("Казахстан", BuiltInPortals.HhKz.Card.DisplayName);
        Assert.Contains("Узбекистан", BuiltInPortals.HhUz.Card.DisplayName);
    }

    [Fact]
    public void Mirrors_CardsMatchCatalog_SameInstance()
    {
        // Каталог и адаптер обязаны держать ОДНИ экземпляры карточек
        // (две копии дескриптора разъезжаются при правках).
        Assert.Equal(
            BuiltInPortals.HhMirrors.Select(m => m.Card).ToArray(),
            BuiltInPortals.Deferred.ToArray());
        Assert.All(BuiltInPortals.HhMirrors, m =>
            Assert.Same(m.Card, BuiltInPortals.Deferred.Single(c => c.Id == m.Card.Id)));
    }

    [Fact]
    public void Mirrors_DoesNotOverlapCore_UnionIsTen()
    {
        var coreIds = BuiltInPortals.Core.Select(p => p.Id);
        var deferredIds = BuiltInPortals.Deferred.Select(p => p.Id);

        Assert.Empty(coreIds.Intersect(deferredIds));
        Assert.Equal(10, coreIds.Count() + deferredIds.Count());
    }
}
