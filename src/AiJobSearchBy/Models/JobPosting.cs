namespace AiJobSearchBy.Models;

// FLOW: нормализованная вакансия — единственная валюта портального слоя.
// IPortalService.SearchAsync/DetailAsync отдают её; ScrapeOrchestrator (+)
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

    // Зарплата как на портале, строкой; разбор (BYN/USD) — в RankService.
    public string? SalaryText { get; init; }

    // Описание в Markdown. Нормализация выполняется ПОСЛЕ дедупа (architecture.md §10).
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

    // «hh сам подбирает похожие»: true —
    // вакансия взята из блока «Вам подойдут эти вакансии» на странице другой
    // вакансии (hh сам назвал её смежной). Такие строки не режутся
    // релевант-гейтом строки запроса (hh подобрал их сам), гео-фильтр — общий.
    // Колонка Vacancies.IsSimilar (EnsureColumn-миграция, канон разрешает).
    public bool IsSimilar { get; init; }

    // r20: полный текст вакансии УЖЕ доставался со страницы вакансии и
    // закэширован в DescriptionMd (кнопка «Адаптировать» второй раз в сеть
    // за той же вакансией не ходит). Колонка Vacancies.DetailFetched
    // (EnsureColumn-миграция).
    public bool DetailFetched { get; init; }

    // Статусы Vacancy (architecture.md §5): new|shortlisted|applied|duplicate|archived.
    public VacancyStatus Status { get; init; } = VacancyStatus.New;
}

// STATE: значения из architecture.md §5; в БД хранится строкой (Vacancies.Status).
public enum VacancyStatus
{
    New,
    Shortlisted,
    Applied,
    Duplicate,
    Archived,
}
