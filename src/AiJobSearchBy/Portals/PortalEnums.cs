namespace AiJobSearchBy.Portals;

// Канонические перечисления портального слоя: architecture.md §3.3/§5.
// Значения хранятся в БД строкой — имена не переименовывать.

// FLOW: типы-каноны портального слоя; consumed всеми адаптерами и реестром,
// сами никуда не обращаются — это словарь домена.
// LIFECYCLE: инстансов не создаёт; значения живут в PortalDescriptor.Id/Access/Auth
// и в отчётах PortalRunReport.
// STATE: неизменяемые константы — хранения состояния нет.
// THREAD: enum потокобезопасен по определению.

// SourceClass: commercial | government | aggregator.
public enum PortalSourceClass
{
    Commercial,
    Government,
    Aggregator,
}

// Access: official_api | feed | scrape — определяет базовый класс адаптера
// (ApiPortalBase / FeedPortalBase / ScrapePortalBase).
public enum PortalAccessKind
{
    OfficialApi,
    Feed,
    Scrape,
}

// Auth: none | public_key | personal_key | oauth — определяет ветку keys-check.
public enum PortalAuthKind
{
    None,
    PublicKey,
    PersonalKey,
    OAuth,
}

// Вердикт прогона по порталу (architecture.md §5): отказ портала ≠ крах прогона.
public enum PortalRunStatus
{
    Ok,
    NoKey,
    Refused,
    Breaker,
    ParseFail,
    Disabled,
}

// Health-check: Degraded = бейдж «портал сломан, обновление ожидается».
public enum PortalHealthStatus
{
    Ok,
    Degraded,
}
