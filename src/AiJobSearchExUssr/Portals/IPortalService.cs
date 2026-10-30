using AiJobSearchExUssr.Models;

namespace AiJobSearchExUssr.Portals;

// FLOW: контракт портала (architecture.md §3.3). Реализации —
// адаптеры на одном из четырёх базовых классов (Api/Feed/Scrape/Government);
// ScrapeOrchestrator (+) обходит их строго последовательно по снапшоту
// порядка, отказ одного портала ≠ крах прогона (architecture.md §7).
public interface IPortalService
{
    // Манифест — данные карточки портала; истина по статусам — config/registry.json.
    PortalDescriptor Descriptor { get; }

    // Поиск → нормализованные вакансии.
    Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default);

    // Карточка вакансии по id/URL → нормализованный JobPosting.
    Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default);

    // r20: умеет ли адаптер доставать ПОЛНЫЙ текст вакансии со страницы
    // вакансии (DetailAsync). false — у лент/API (описание приходит прямо в
    // ответе поиска); true — у scrape-порталов (hh-группа). Кнопка
    // «Адаптировать» ходит в страницу вакансии только по true.
    bool SupportsDetail => true;

    // Канареечный health-check: golden-фикстура разметки в тестах,
    // провал → Degraded + бейдж «портал сломан, обновление ожидается».
    Task<PortalHealthReport> HealthCheckAsync(CancellationToken ct = default);
}

// Вердикт health-check; Detail — человекочитаемая причина для UI/Adapter Doctor.
public sealed record PortalHealthReport(PortalHealthStatus Status, string? Detail);
