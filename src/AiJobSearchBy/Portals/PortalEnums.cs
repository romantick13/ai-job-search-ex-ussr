// FLOW: типы-каноны портального слоя; consumed всеми адаптерами и реестром,
// сами никуда не обращаются — это словарь домена.
// LIFECYCLE: инстансов не создаёт; значения живут в PortalDescriptor.Id/Access/Auth
// и в отчётах PortalRunReport.
// STATE: неизменяемые константы — хранения состояния нет.
// THREAD: enum потокобезопасен по определению.
namespace AiJobSearchBy.Portals;

// Канонические перечисления портального слоя: architecture.md §3.3/§5,
// RFC-003 (DECISIONS.md). Значения хранятся в БД строкой — имена не переименовывать.

// SourceClass (§3.3): commercial | government | aggregator.
public enum PortalSourceClass
{
    Commercial,
    Government,
    Aggregator,
}

// Access (§3.3): official_api | feed | scrape — определяет базовый класс адаптера
// (ApiPortalBase / FeedPortalBase / ScrapePortalBase).
public enum PortalAccessKind
{
    OfficialApi,
    Feed,
    Scrape,
}

// Auth (§3.3): none | public_key | personal_key | oauth — определяет ветку keys-check.
public enum PortalAuthKind
{
    None,
    PublicKey,
    PersonalKey,
    OAuth,
}

// Вердикт прогона по порталу (architecture.md §5): отказ портала ≠ крах прогона (§7).
public enum PortalRunStatus
{
    Ok,
    NoKey,
    Refused,
    Breaker,
    ParseFail,
    Disabled,
}

// Health-check (RFC-003 §2.1): Degraded = бейдж «портал сломан, обновление ожидается».
public enum PortalHealthStatus
{
    Ok,
    Degraded,
}
