using System.Text.Json;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер Remotive на публичном JSON API (Access=OfficialApi, Auth=None,
// карточка BuiltInPortals.Core). Поиск серверный: GET /api/remote-jobs
// ?search={query}&limit=50 — параметры подтверждены живым зондом 2026-10-22.
// Схема: {jobs: [{id, url, title, company_name, candidate_required_location,
// salary, publication_date, description, ...}]} (golden-фикстура в тестах).
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны (§6).
public sealed class RemotivePortal : ApiPortalBase
{
    private const string SearchEndpoint = "https://remotive.com/api/remote-jobs";

    public RemotivePortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => new()
    {
        Id = "remotive",
        DisplayName = "Remotive",
        SourceClass = PortalSourceClass.Commercial,
        Access = PortalAccessKind.OfficialApi,
        Auth = PortalAuthKind.None,
        DefaultPriority = 8,
        ApplySupported = true,
    };

    protected override string BuildSearchUrl(string query)
        => string.IsNullOrWhiteSpace(query)
            ? $"{SearchEndpoint}?limit=50"
            : $"{SearchEndpoint}?search={Uri.EscapeDataString(query)}&limit=50";

    // Детальная карточка — следующий WP (оркестратор): SearchAsync уже несёт
    // описание вакансии, честный отказ вместо выдуманной реализации.
    public override Task<JobPosting> DetailAsync(
        string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException(
            "детальная карточка remotive придёт с оркестратором (WP-P2); "
            + "SearchAsync уже возвращает описание вакансии");

    protected override Task<IReadOnlyList<JobPosting>> ParseSearchAsync(
        string payload, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(payload);

        if (!json.RootElement.TryGetProperty("jobs", out var jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            throw new ParseFailedException("схема payload сменилась: нет jobs[]");
        }

        var result = new List<JobPosting>();
        foreach (var item in jobs.EnumerateArray())
        {
            // url обязателен (canonical_url NOT NULL схемы §4); его отсутствие
            // = смена схемы — ParseFailedException (как у hh-by).
            if (!item.TryGetProperty("url", out var urlEl)
                || urlEl.GetString() is not { Length: > 0 } canonicalUrl)
            {
                throw new ParseFailedException("схема payload сменилась: job без url");
            }

            var externalId = item.TryGetProperty("id", out var id)
                ? id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString()
                : null;
            var title = item.TryGetProperty("title", out var titleEl)
                ? titleEl.GetString()
                : null;
            var company = item.TryGetProperty("company_name", out var companyEl)
                ? companyEl.GetString()
                : null;
            var location = item.TryGetProperty("candidate_required_location", out var locationEl)
                ? locationEl.GetString()
                : null;
            var salary = item.TryGetProperty("salary", out var salaryEl)
                ? salaryEl.GetString()
                : null;
            var description = item.TryGetProperty("description", out var descriptionEl)
                ? descriptionEl.GetString()
                : null;

            result.Add(new JobPosting
            {
                PortalId = Descriptor.Id,
                ExternalId = externalId,
                CanonicalUrl = canonicalUrl,
                Title = title,
                Company = company,
                Location = location,
                SalaryText = salary,
                DescriptionMd = description,
                SourceClass = Descriptor.SourceClass,
                AccessKind = Descriptor.Access,
                DedupeHash = DedupeHasher.ComputeComposite(Descriptor.Id, externalId, canonicalUrl),
            });
        }

        return Task.FromResult<IReadOnlyList<JobPosting>>(result);
    }
}
