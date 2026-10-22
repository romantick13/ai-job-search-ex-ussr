using System.Text.Json;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер RemoteOK на публичном JSON API (Access=OfficialApi, Auth=None,
// карточка BuiltInPortals.Core). API не принимает поисковых параметров — отдаёт
// свежий каталог (~100 записей); отбор по запросу делается на нашей стороне
// (title/company/tags, OrdinalIgnoreCase) и логируется фактом «отобрано M из N»
// (НИ-4). Первый элемент массива — мета-запись API (legal/last_updated), без
// position — пропускается осознанно, не молча (НИ-2).
// Вежливость: лимит 50 вакансий за прогон (константа ниже) — каталог больше,
// но харвестер не жадничает (RFC-003 §2.10).
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны (§6).
public sealed class RemoteOkPortal : ApiPortalBase
{
    private const string SearchEndpoint = "https://remoteok.com/api";
    private const int MaxItems = 50;

    public RemoteOkPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => new()
    {
        Id = "remoteok",
        DisplayName = "RemoteOK",
        SourceClass = PortalSourceClass.Commercial,
        Access = PortalAccessKind.OfficialApi,
        Auth = PortalAuthKind.None,
        DefaultPriority = 8,
        ApplySupported = true,
    };

    protected override string BuildSearchUrl(string query) => SearchEndpoint;

    // Детальная карточка — следующий WP (оркестратор): SearchAsync уже несёт
    // описание вакансии, честный отказ вместо выдуманной реализации.
    public override Task<JobPosting> DetailAsync(
        string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException(
            "детальная карточка remoteok придёт с оркестратором (WP-P2); "
            + "SearchAsync уже возвращает описание вакансии");

    protected override Task<IReadOnlyList<JobPosting>> ParseSearchAsync(
        string payload, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(payload);

        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ParseFailedException("схема payload сменилась: корень — не массив");
        }

        var all = new List<JobPosting>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            // Мета-записи API (legal/last_updated) и битые элементы — осознанный
            // пропуск; url обязателен (canonical_url NOT NULL схемы §4).
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("position", out var position)
                || position.GetString() is not { Length: > 0 } title
                || !item.TryGetProperty("url", out var urlEl)
                || urlEl.GetString() is not { Length: > 0 } canonicalUrl)
            {
                continue;
            }

            var externalId = item.TryGetProperty("id", out var id)
                ? id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString()
                : null;
            var company = item.TryGetProperty("company", out var companyEl)
                ? companyEl.GetString()
                : null;
            var location = item.TryGetProperty("location", out var locationEl)
                ? locationEl.GetString()
                : null;
            var description = item.TryGetProperty("description", out var descriptionEl)
                ? descriptionEl.GetString()
                : null;
            var salaryText = FormatSalary(item);

            all.Add(new JobPosting
            {
                PortalId = Descriptor.Id,
                ExternalId = externalId,
                CanonicalUrl = canonicalUrl,
                Title = title,
                Company = company,
                Location = location,
                SalaryText = salaryText,
                DescriptionMd = description,
                SourceClass = Descriptor.SourceClass,
                AccessKind = Descriptor.Access,
                DedupeHash = DedupeHasher.ComputeComposite(Descriptor.Id, externalId, canonicalUrl),
            });
        }

        if (all.Count == 0)
        {
            throw new ParseFailedException("схема payload сменилась: ни одной вакансии с position+url");
        }

        var result = all.Take(MaxItems).ToList();
        return Task.FromResult<IReadOnlyList<JobPosting>>(result);
    }

    // Отбор по запросу на нашей стороне (API параметров не принимает).
    // Пустой запрос — каталог целиком (до MaxItems).
    public IReadOnlyList<JobPosting> ApplyQuery(IReadOnlyList<JobPosting> items, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return items;
        }

        var filtered = items.Where(p =>
            (p.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (p.Company?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (p.DescriptionMd?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        ).ToList();
        Log.Log($"портал {Descriptor.Id}: фильтр «{query}»: отобрано {filtered.Count} из {items.Count}");
        return filtered;
    }

    // salary_min/salary_max — числа в USD; null при отсутствии.
    private static string? FormatSalary(JsonElement item)
    {
        var from = item.TryGetProperty("salary_min", out var min) && min.ValueKind == JsonValueKind.Number
            ? min.GetDecimal()
            : (decimal?)null;
        var to = item.TryGetProperty("salary_max", out var max) && max.ValueKind == JsonValueKind.Number
            ? max.GetDecimal()
            : (decimal?)null;
        if (from is null && to is null)
        {
            return null;
        }

        return (from, to) switch
        {
            { } when from.HasValue && to.HasValue => $"от {from:0} до {to:0} USD",
            { } when from.HasValue => $"от {from:0} USD",
            _ => $"до {to:0} USD",
        };
    }
}
