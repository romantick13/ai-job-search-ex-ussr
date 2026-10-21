namespace AiJobSearchBy.Portals;

// FLOW: каталог коробки v1 — 6 порталов без ключей (RFC-003 §2.3, международные
// фиды/API; РБ-источники исключены владельцем — RFC-008, коробка remote-first)
// + Deferred: два РБ-портала WP-P1 (rabota-by/hh-by), живущие в реестре с
// DefaultEnabled=false («включение отдельным решением»). Истина по статусам —
// config/registry.json (schema v3); этот каталог — пин манифестов:
// Core remoteok/remotive/jobicy и Deferred hh-by — на ApiPortalBase,
// Wwr/Himalayas/Nodesk — на FeedPortalBase (раскладка §2.1/§2.3); rabota-by —
// юридический стуб. Тест держит сверку с таблицей §2.3 и карточками §2.11.
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
        },
    };

    // Отложенные карточки §2.11 (WP-P1): манифесты в реестре, поиск выключен.
    // hh-by — код готов к возвращению анонимного поиска (перепроверка раз
    // в квартал); rabota-by — юридический стуб (API только работодателям).
    public static IReadOnlyList<PortalDescriptor> Deferred { get; } = new PortalDescriptor[]
    {
        new()
        {
            Id = "hh-by",
            DisplayName = "HeadHunter.by",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.OfficialApi,
            Auth = PortalAuthKind.None,
            DefaultPriority = 5,
            ApplySupported = true,
            ApplyWarning = "поиск закрыт платформой (04.2026, 403) — адаптер выключен, "
                + "отклик руками на сайте hh.by; включение — отдельным решением",
            DefaultEnabled = false,
        },
        new()
        {
            Id = "rabota-by",
            DisplayName = "Rabota.by",
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.OfficialApi,
            Auth = PortalAuthKind.None,
            DefaultPriority = 5,
            ApplySupported = true,
            ApplyWarning = "отклик руками на сайте rabota.by; API соискателю недоступен "
                + "(только работодатели, п.1.5 соглашения) — адаптер не подключается",
            DefaultEnabled = false,
        },
    };
}
