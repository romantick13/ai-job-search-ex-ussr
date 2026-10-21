using System.Text.Json;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер HeadHunter.by (группа hh) на официальном JSON API
// (Access=OfficialApi, Auth=None). ПОРТАЛ ВЫКЛЮЧЕН ПО УМОЛЧАНИЮ
// (DefaultEnabled=false, карточка §2.11 в portals-catalog.md + registry.json):
// анонимный поиск группы hh закрыт платформой с 04.2026 (GET /vacancies → 403
// без авторизации; зафиксировано 2026-10-19, перепроверка раз в квартал).
// Код держится наготове: когда поиск вернётся — включение отдельным решением
// владельца (запись конфига Portals[] Enabled=true), код не меняется.
// Схема парсинга — публичная документация API группы hh (items[] с
// id/name/area/salary/employer/alternate_url); golden-фикстура в тестах.
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны (§6).
public sealed class HhByPortal : ApiPortalBase
{
    private const string SearchEndpoint = "https://api.hh.by/vacancies";

    public HhByPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => new()
    {
        Id = "hh-by",
        DisplayName = "HeadHunter.by",
        SourceClass = PortalSourceClass.Commercial,
        Access = PortalAccessKind.OfficialApi,
        Auth = PortalAuthKind.None,
        DefaultPriority = 5,
        ApplySupported = true,
        ApplyWarning = "поиск закрыт платформой (04.2026, 403) — адаптер выключен, "
            + "отклик руками на сайте hh.by; включение — отдельным решением",
        DefaultEnabled = false,
    };

    // Endpoint по публичной документации группы hh; пока поиск закрыт — 403,
    // оркестратор отнесёт к refused (§7), адаптер не меняется.
    protected override string BuildSearchUrl(string query)
        => $"{SearchEndpoint}?text={Uri.EscapeDataString(query)}&per_page=100";

    // Детальная карточка (/vacancies/{id}) — вместе с возвращением поиска
    // (следующий шаг после включения, карточка §2.11): честный отказ.
    public override Task<JobPosting> DetailAsync(
        string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException(
            "детальная карточка hh-by появится вместе с включением портала "
            + "(поиск закрыт платформой с 04.2026, карточка §2.11)");

    // Разбор items[] по документированной схеме. Битый JSON/сменившаяся схема
    // = ParseFailedException (§2.10). salary/employer/area — nullable по схеме.
    protected override Task<IReadOnlyList<JobPosting>> ParseSearchAsync(
        string payload, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(payload);

        if (!json.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new ParseFailedException("схема payload сменилась: нет items[]");
        }

        var result = new List<JobPosting>();
        foreach (var item in items.EnumerateArray())
        {
            // alternate_url — человекочитаемая ссылка вакансии: обязательна
            // (canonical_url NOT NULL схемы §4); её отсутствие = смена схемы.
            if (!item.TryGetProperty("alternate_url", out var altUrl)
                || altUrl.GetString() is not { Length: > 0 } canonicalUrl)
            {
                throw new ParseFailedException("схема payload сменилась: item без alternate_url");
            }

            var externalId = item.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;
            var title = item.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;
            var company = item.TryGetProperty("employer", out var employer)
                && employer.TryGetProperty("name", out var employerName)
                    ? employerName.GetString()
                    : null;
            var location = item.TryGetProperty("area", out var area)
                && area.TryGetProperty("name", out var areaName)
                    ? areaName.GetString()
                    : null;
            var salaryText = FormatSalary(item);

            result.Add(new JobPosting
            {
                PortalId = Descriptor.Id,
                ExternalId = externalId,
                CanonicalUrl = canonicalUrl,
                Title = title,
                Company = company,
                Location = location,
                SalaryText = salaryText,
                SourceClass = Descriptor.SourceClass,
                AccessKind = Descriptor.Access,
                DedupeHash = DedupeHasher.ComputeComposite(Descriptor.Id, externalId, canonicalUrl),
            });
        }

        return Task.FromResult<IReadOnlyList<JobPosting>>(result);
    }

    // salary: {from,to,currency} → «от X до Y CUR» / «от X CUR» / «X CUR»;
    // null при отсутствии блока — зарплата необязательна (схема §4).
    private static string? FormatSalary(JsonElement item)
    {
        if (!item.TryGetProperty("salary", out var salary) || salary.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var from = salary.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Number
            ? (decimal?)f.GetDecimal()
            : null;
        var to = salary.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Number
            ? (decimal?)t.GetDecimal()
            : null;
        if (from is null && to is null)
        {
            return null;
        }

        var currency = salary.TryGetProperty("currency", out var c)
            ? c.GetString()
            : null;
        return (from, to) switch
        {
            { } when from.HasValue && to.HasValue
                => $"от {from} до {to} {currency}".TrimEnd(),
            { } when from.HasValue => $"от {from} {currency}".TrimEnd(),
            _ => $"{to} {currency}".TrimEnd(),
        };
    }

    // healthCheck-контракт карточки §2.11: канарейка GET /vacancies?per_page=1.
    // 200 = поиск вернулся (Ok — пора пересматривать карточку); 403 = закрыт
    // платформой (ожидаемо до перепроверки); прочее/исключение = причина в отчёте.
    public override async Task<PortalHealthReport> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"{SearchEndpoint}?per_page=1", ct).ConfigureAwait(false);
            return (int)response.StatusCode switch
            {
                200 => new PortalHealthReport(PortalHealthStatus.Ok,
                    "анонимный поиск вернулся — пересмотреть карточку §2.11"),
                403 => new PortalHealthReport(PortalHealthStatus.Degraded,
                    "анонимный поиск закрыт платформой (403) с 04.2026 — перепроверка раз в квартал"),
                _ => new PortalHealthReport(PortalHealthStatus.Degraded,
                    $"канарейка неожиданный статус {(int)response.StatusCode}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException)
        {
            // НИ-1: catch не молчит — причина в протоколе, вердикт — в отчёте.
            Log.LogWarn($"skip: hh-by канарейка недоступна — {ex.GetType().Name}: {ex.Message}; "
                + "решение: Degraded-отчёт (причина и в отчёте)");
            return new PortalHealthReport(PortalHealthStatus.Degraded,
                $"канарейка недоступна: {ex.Message}");
        }
    }
}
