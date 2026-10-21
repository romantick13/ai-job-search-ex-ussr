using AiJobSearchBy.Models;

namespace AiJobSearchBy.Portals;

// FLOW: контракт портала (architecture.md §3.3, RFC-003 §2.1). Реализации —
// адаптеры на одном из четырёх базовых классов (Api/Feed/Scrape/Government);
// ScrapeOrchestrator (WP-P1+) обходит их строго последовательно по снапшоту
// порядка, отказ одного портала ≠ крах прогона (architecture.md §7).
public interface IPortalService
{
    // Манифест — данные карточки портала; истина по статусам — config/registry.json.
    PortalDescriptor Descriptor { get; }

    // Поиск → нормализованные вакансии.
    Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default);

    // Карточка вакансии по id/URL → нормализованный JobPosting.
    Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default);

    // Канареечный health-check (RFC-003 §2.1): golden-фикстура разметки в тестах,
    // провал → Degraded + бейдж «портал сломан, обновление ожидается» (§2.1).
    Task<PortalHealthReport> HealthCheckAsync(CancellationToken ct = default);
}

// Вердикт health-check; Detail — человекочитаемая причина для UI/Adapter Doctor.
public sealed record PortalHealthReport(PortalHealthStatus Status, string? Detail);
