namespace AiJobSearchBy.Portals;

// FLOW: манифест портала — неизменяемые данные карточки §2.11; объявляются
// адаптером и читаются реестром / keys-check / UI без инстанцирования сетевой
// логики. Контракт §3.3: Id, DisplayName, SourceClass, Access, Auth,
// DefaultPriority, ApplySupported, ApplyWarning (RFC-003 §2.7 «поиск ≠ подача»).
public sealed record PortalDescriptor
{
    // Стиль реестра: «remoteok», «wwr» (config/registry.json, schema v3).
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required PortalSourceClass SourceClass { get; init; }

    public required PortalAccessKind Access { get; init; }

    public required PortalAuthKind Auth { get; init; }

    // Меньше = раньше в цепи дефолта; порядок конфига Portals[] сильнее (§3.3).
    public required int DefaultPriority { get; init; }

    // Поиск ≠ подача (RFC-003 §2.7): UI Apply показывает ApplyWarning и требует
    // подтверждения на подачу в «мёртвые» площадки; Search работает полноценно.
    public required bool ApplySupported { get; init; }

    public string? ApplyWarning { get; init; }

    // Дефолтный выключатель карточки (config/registry.json default_enabled,
    // WP-P1): false = портал вне цепи по умолчанию; запись конфига Portals[]
    // сильнее — Enabled=true включает и «выключенный по умолчанию» портал
    // («включение отдельным решением», PLAN WP-P1).
    public bool DefaultEnabled { get; init; } = true;
}
