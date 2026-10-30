using System.Text.Json;
using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Portals;

// FLOW: адаптер Jobicy на публичном JSON API v2 (Access=OfficialApi, Auth=None,
// карточка BuiltInPortals.Core). Поиск серверный: GET /api/v2/remote-jobs
// ?count=50&tag={query} — параметр tag подтверждён живым зондом.
// Схема: {jobs: [{id, url, jobTitle, companyName, jobGeo, jobType, pubDate,
// jobDescription, ...}]} (golden-фикстура в тестах).
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны.
public sealed class JobicyPortal : ApiPortalBase
{
    private const string SearchEndpoint = "https://jobicy.com/api/v2/remote-jobs";

    public JobicyPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => BuiltInPortals.CoreCard("jobicy");

    // Живой кейс: поиск «*» → HTTP 400 — jobicy не
    // понимает tag=%2A. «*» (маркер «каталог целиком» из оркестратора)
    // считается пустым запросом — параметр tag вообще не уходит.
    protected override string BuildSearchUrl(string query)
        => string.IsNullOrWhiteSpace(query) || query.Trim() == "*"
            ? $"{SearchEndpoint}?count=50"
            : $"{SearchEndpoint}?count=50&tag={Uri.EscapeDataString(query)}";

    // Детальная карточка — задача оркестратора: SearchAsync уже несёт
    // описание вакансии, честный отказ вместо выдуманной реализации.
    // (SupportsDetail=false наследуется от ApiPortalBase.)
    public override Task<JobPosting> DetailAsync(
        string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException(
            "детальная карточка jobicy придёт с оркестратором; "
            + "SearchAsync уже возвращает описание вакансии");


    // jobicy отдаёт jobType/jobIndustry МАССИВАМИ («["Full-Time"]») — GetString
    // на массиве падает и ронял ВЕСЬ каталог. Читаем терпимо: String → текст;
    // Array → join «, »; Number/True/False → raw; Object/null → null.
    private static string? SafeText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        JsonValueKind.Array => string.Join(", ", element.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
            .Where(s => !string.IsNullOrWhiteSpace(s))),
        _ => null,
    };

    // Полевые чтения одним вызовом: нет свойства / нечитаемый вид — null.
    private static string? Field(JsonElement item, string name)
        => item.TryGetProperty(name, out var el) ? SafeText(el) : null;

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
            // url обязан быть, но битый ОДИН item больше не валит весь
            // каталог (был ParseFailedException на первом же кривом элементе —
            // 45 вакансий в мусор из-за одной). Пропуск — с событием .
            if (item.TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String
                && urlEl.GetString() is { Length: > 0 } canonicalUrl)
            {
                var externalId = Field(item, "id");
                var title = Field(item, "jobTitle");
                var company = Field(item, "companyName");
                var location = Field(item, "jobGeo");
                var jobType = Field(item, "jobType");
                // Описание: jobDescription, при его отсутствии — jobExcerpt
                // (обе схемы встречаются в живом каталоге, зонд).
                var description = Field(item, "jobDescription") ?? Field(item, "jobExcerpt");

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
            else
            {
                Log.LogWarn($"skip: {Descriptor.Id} — item без url пропущен (title: "
                            + $"{Field(item, "jobTitle") ?? "—"}); остальные элементы каталога обрабатываются");
            }
        }

        return Task.FromResult<IReadOnlyList<JobPosting>>(result);
    }
}
