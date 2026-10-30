namespace AiJobSearchBy.Portals;

// FLOW: каталог встроенных порталов. Шесть международных источников без
// ключей (Core: API/RSS-фиды) + четыре зеркала группы hh для стран
// бывшего СССР (браузерная эмуляция HTML-выдачи).
// praca.by: r21 владелец подключал адаптер — в тот же день решение
// отменено, адаптер вырезан (сайт низкого качества для IT-поиска);
// запреты их соглашений в силе — docs/portals-graveyard.md.
// Истина по статусам площадок — config/registry.json; этот каталог — пин
// состава и раскладки по базовым классам: remoteok/remotive/jobicy —
// ApiPortalBase, wwr/himalayas/nodesk — FeedPortalBase, hh-* —
// BrowserEmulationPortalBase. Тесты держат сверку каталога с раскладкой.
// r21: ВСЕ чекбоксы порталов по умолчанию ВЫКЛЮЧЕНЫ (решение владельца —
// «сделай все выключены по-дефолту»): юзер сам собирает свою цепочку
// галочками, PersistSitesChecked запоминает выбор в базе.

// ЗЕРКАЛА ГРУППЫ hh (проверено живыми запросами):
// hh.ru — Россия (area 113)
// rabota.by — Беларусь (area 16; hh.by 302 -> rabota.by — одно
// зеркало, второй чекбокс не нужен)
// hh.kz — Казахстан (area 40)
// hh.uz — Узбекистан (area 97)
// Коды регионов — справочник api.hh.ru/areas/countries. Другие бывшие
// республики СССР зеркал группы НЕ имеют (hh.kg/hh.tj не отвечают,
// hh.md/hh.ge/hh.am — чужие сайты), поэтому поиск универсален для четырёх
// стран; вакансии остальных республик попадают в выдачу зеркал как
// удалённые и сортируются гео-фильтром приложения.
// Поиск ≠ подача: отклик всегда руками на сайте (ApplyWarning).
public static class BuiltInPortals
{
    public static IReadOnlyList<PortalDescriptor> Core { get; } = new PortalDescriptor[]
    {
        new()
        {
            Id = "remoteok",
            DisplayName = "RemoteOK",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.OfficialApi,
            Auth = PortalAuthKind.None,
            DefaultPriority = 8,
            ApplySupported = true,
            DefaultEnabled = false,
        },
        new()
        {
            Id = "remotive",
            DisplayName = "Remotive",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.OfficialApi,
            Auth = PortalAuthKind.None,
            DefaultPriority = 8,
            ApplySupported = true,
            DefaultEnabled = false,
        },
        new()
        {
            Id = "jobicy",
            DisplayName = "Jobicy",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.OfficialApi,
            Auth = PortalAuthKind.None,
            DefaultPriority = 7,
            ApplySupported = true,
            DefaultEnabled = false,
        },
        new()
        {
            Id = "wwr",
            DisplayName = "WeWorkRemotely",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.Feed,
            Auth = PortalAuthKind.None,
            DefaultPriority = 7,
            ApplySupported = true,
            DefaultEnabled = false,
        },
        new()
        {
            Id = "himalayas",
            DisplayName = "Himalayas",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.Feed,
            Auth = PortalAuthKind.None,
            DefaultPriority = 6,
            ApplySupported = true,
            DefaultEnabled = false,
        },
        new()
        {
            Id = "nodesk",
            DisplayName = "NoDesk",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.Feed,
            Auth = PortalAuthKind.None,
            DefaultPriority = 6,
            ApplySupported = true,
            DefaultEnabled = false,
        },
    };


    // r21: карточки Core — единый источник истины для каталога И адаптеров
    // (раньше адаптеры дублировали карточку своими BuildDescriptor —
    // дефолты разъезжались).
    public static PortalDescriptor CoreCard(string id)
        => Core.Single(c => c.Id == id);

    // Зеркало группы hh: карточка портала + хост + код региона страны
    // (area) для поискового URL. Один дескриптор — единый источник истины
    // и для каталога, и для адаптера (два дескриптора на одно зеркало
    // разъезжались бы при правках).
    public sealed record HhMirror(PortalDescriptor Card, string Host, string AreaId);

    private static PortalDescriptor HhCard(string id, string displayName) => new()
    {
        Id = id,
        DisplayName = displayName,
        SourceClass = PortalSourceClass.Commercial,
        Access = PortalAccessKind.Scrape,
        Auth = PortalAuthKind.None,
        DefaultPriority = 5,
        ApplySupported = true,
        ApplyWarning = "поиск — имитация браузера в темпе человека (медленно и аккуратно); "
            + "отклик руками на сайте; регистрация не нужна",
        DefaultEnabled = false, // r21: все чекбоксы по умолчанию выключены
    };

    public static HhMirror HhRu { get; } =
        new(HhCard("hh-ru", "HH.ru (Россия)"), "https://hh.ru", "113");

    public static HhMirror HhBy { get; } =
        new(HhCard("hh-by", "HH.by + rabota.by (Беларусь)"), "https://rabota.by", "16");

    public static HhMirror HhKz { get; } =
        new(HhCard("hh-kz", "HH.kz (Казахстан)"), "https://hh.kz", "40");

    public static HhMirror HhUz { get; } =
        new(HhCard("hh-uz", "HH.uz (Узбекистан)"), "https://hh.uz", "97");

    // Все зеркала группы — в порядке приоритета в цепи (при равном
    // DefaultPriority реестр сортирует по Id: by, kz, ru, uz).
    public static IReadOnlyList<HhMirror> HhMirrors { get; } =
        new[] { HhBy, HhKz, HhRu, HhUz };

    // Отложенные карточки (§2.11): только зеркала hh — других площадок
    // с зеркалами по бывшим республикам СССР нет (см. шапку файла).
    public static IReadOnlyList<PortalDescriptor> Deferred { get; } =
        HhMirrors.Select(m => m.Card).ToArray();
}
