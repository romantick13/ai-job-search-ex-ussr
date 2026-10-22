using System.Text.Json;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер Jobicy на публичном JSON API v2 (Access=OfficialApi, Auth=None,
// карточка BuiltInPortals.Core). Поиск серверный: GET /api/v2/remote-jobs
// ?count=50&tag={query} — параметр tag подтверждён живым зондом 2026-10-22.
// Схема: {jobs: [{id, url, jobTitle, companyName, jobGeo, jobType, pubDate,
// jobDescription, ...}]} (golden-фикстура в тестах).
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны (§6).
public sealed class JobicyPortal : ApiPortalBase
{
    private const string SearchEndpoint = "https://jobicy.com/api/v2/remote-jobs";

    public JobicyPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => new()
    {
        Id = "jobicy",
        DisplayName = "Jobicy",
        SourceClass = PortalSourceClass.Commercial,
        Access = PortalAccessKind.OfficialApi,
        Auth = PortalAuthKind.None,
        DefaultPriority = 7,
        ApplySupported = true,
    };

    protected override string BuildSearchUrl(string query)
        => string.IsNullOrWhiteSpace(query)
            ? $"{SearchEndpoint}?count=50"
            : $"{SearchEndpoint}?count=50&tag={Uri.EscapeDataString(query)}";

    // Детальная карточка — следующий WP (оркестратор): SearchAsync уже несёт
    // описание вакансии, честный отказ вместо выдуманной реализации.
    public override Task<JobPosting> DetailAsync(
        string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException(
            "детальная карточка jobicy придёт с оркестратором (WP-P2); "
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
            var title = item.TryGetProperty("jobTitle", out var titleEl)
                ? titleEl.GetString()
                : null;
            var company = item.TryGetProperty("companyName", out var companyEl)
                ? companyEl.GetString()
                : null;
            var location = item.TryGetProperty("jobGeo", out var geoEl)
                ? geoEl.GetString()
                : null;
            var jobType = item.TryGetProperty("jobType", out var typeEl)
                ? typeEl.GetString()
                : null;
            // Описание: jobDescription, при его отсутствии — jobExcerpt
            // (обе схемы встречаются в живом каталоге, зонд 2026-10-22).
            string? description = null;
            if (item.TryGetProperty("jobDescription", out var descEl))
            {
                description = descEl.GetString();
            }
            if (description is null && item.TryGetProperty("jobExcerpt", out var excerptEl))
            {
                description = excerptEl.GetString();
            }

            result.Add(new JobPosting
            {
                PortalId = Descriptor.Id,
                ExternalId = externalId,
                CanonicalUrl = canonicalUrl,
                Title = title,
                Company = company,
                Location = location,
                SalaryText = jobType,
                DescriptionMd = description,
                SourceClass = Descriptor.SourceClass,
                AccessKind = Descriptor.Access,
                DedupeHash = DedupeHasher.ComputeComposite(Descriptor.Id, externalId, canonicalUrl),
            });
        }

        return Task.FromResult<IReadOnlyList<JobPosting>>(result);
    }
}
