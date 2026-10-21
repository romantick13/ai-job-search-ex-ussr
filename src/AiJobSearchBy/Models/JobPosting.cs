namespace AiJobSearchBy.Models;

// FLOW: нормализованная вакансия — единственная валюта портального слоя.
// IPortalService.SearchAsync/DetailAsync отдают её; ScrapeOrchestrator (WP-P1+)
// кладёт её в SQLite Vacancies после DedupeHasher (architecture.md §3.3/§4).
// Поля 1:1 со схемой таблицы Vacancies (architecture.md §4).
public sealed record JobPosting
{
    // Идентификатор записи (GUID, генерирует адаптер/оркестратор при вставке).
    public string Id { get; init; } = string.Empty;

    // Портал-источник (PortalDescriptor.Id).
    public required string PortalId { get; init; }

    // Идентификатор на стороне портала; nullable по схеме — не у всех источников
    // есть стабильный id, тогда дедуп держится на canonical_url.
    public string? ExternalId { get; init; }

    // Канонический URL вакансии (NOT NULL по схеме) — участник композитного ключа.
    public required string CanonicalUrl { get; init; }

    public string? Title { get; init; }

    public string? Company { get; init; }

    public string? Location { get; init; }

    // Зарплата как на портале, строкой; разбор (BYN/USD) — RankService (§3.4).
    public string? SalaryText { get; init; }

    // Описание в Markdown. Нормализация выполняется ПОСЛЕ дедупа (§10).
    public string? DescriptionMd { get; init; }

    // SourceClass/AccessKind — строковые колонки схемы; в модели — типы канона.
    public Portals.PortalSourceClass SourceClass { get; init; }

    public Portals.PortalAccessKind AccessKind { get; init; }

    // Quality: схема фиксирует DEFAULT 'verified'; остальные значения появятся
    // вместе с RankService/AtsChecker — строка, чтобы не выдумывать enum заранее.
    public string Quality { get; init; } = "verified";

    // SHA256 композитного ключа (DedupeHasher.ComputeComposite), UNIQUE в БД.
    public required string DedupeHash { get; init; }

    public DateTimeOffset FetchedAt { get; init; }

    // Статусы Vacancy (architecture.md §5): new|shortlisted|applied|duplicate|archived.
    public VacancyStatus Status { get; init; } = VacancyStatus.New;
}

// STATE: значения канона §5; в БД хранится строкой (Vacancies.Status).
public enum VacancyStatus
{
    New,
    Shortlisted,
    Applied,
    Duplicate,
    Archived,
}
