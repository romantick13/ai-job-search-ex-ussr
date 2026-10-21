using AiJobSearchBy.Models;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер Rabota.by — ЗАГЛУШКА С ЮРИДИЧЕСКИМ ВЕРДИКТОМ, не рабочий путь.
// ПОРТАЛ ВЫКЛЮЧЕН ПО УМОЛЧАНИЮ (DefaultEnabled=false, карточка §2.11):
// официальное API (dev.rabota.by, соглашение разработчика, приказ №4-О от
// 09.02.2022) — только для организаций-работодателей (п.1.5 «Пользователем
// Сервиса могут быть только организации-работодатели»; п.1.3/1.6 — отказ без
// объяснения причин; снято дословно 2026-10-19). HTML-скрапинг исключён.
// Класс существует, чтобы: (а) реестр/keys-check/UI показывали портал с
// человекочитаемой причиной, (б) при появлении соискательского официального
// пути у вердикта была точка входа. ЛЮБОЙ вызов поиска/детали — честный
// NotSupportedException, никаких тихих пустых ответов.
// THREAD: состояния нет; вызовы оркестратора последовательны (§6).
public sealed class RabotaByPortal : ApiPortalBase
{
    public const string VerdictReason =
        "официальное API только для работодателей (соглашение dev.rabota.by, "
        + "п.1.5) — соискательского пути нет, HTML-скрапинг исключён; "
        + "адаптер не подключается (карточка §2.11, graveyard-разбор)";

    public RabotaByPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => new()
    {
        Id = "rabota-by",
        DisplayName = "Rabota.by",
        SourceClass = PortalSourceClass.Commercial,
        Access = PortalAccessKind.OfficialApi,
        Auth = PortalAuthKind.None,
        DefaultPriority = 5,
        ApplySupported = true,
        ApplyWarning = "отклик руками на сайте rabota.by; API соискателю недоступен "
            + "(только работодатели, п.1.5 соглашения) — адаптер не подключается",
        DefaultEnabled = false,
    };

    // Поиск невозможен юридически: явный отказ, не пустой список (§7:
    // причина всегда в отчёте). НИ-2/НИ-4: сам отказ — тоже событие протокола
    // (LogWarn перед исключением — отказ виден в окне, даже если вызывающий
    // не логирует).
    public override Task<IReadOnlyList<JobPosting>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        Log.LogWarn($"skip: rabota-by поиск «{query}» невозможен — причина: {VerdictReason}");
        throw new NotSupportedException(VerdictReason);
    }

    // Абстрактные члены ApiPortalBase не имеют легального тела — тот же
    // честный отказ (компилятор требует реализации, вердикт — документ).
    protected override string BuildSearchUrl(string query)
        => throw new NotSupportedException(VerdictReason);

    protected override Task<IReadOnlyList<JobPosting>> ParseSearchAsync(
        string payload, CancellationToken ct)
        => throw new NotSupportedException(VerdictReason);

    public override Task<JobPosting> DetailAsync(
        string idOrUrl, CancellationToken ct = default)
    {
        Log.LogWarn($"skip: rabota-by деталь «{idOrUrl}» невозможна — причина: {VerdictReason}");
        throw new NotSupportedException(VerdictReason);
    }

    // healthCheck-контракт карточки §2.11: канарейки нет — пинговать нечего,
    // официальный путь вне нашего сценария. Degraded с вердиктом — честное
    // состояние «портал сломан/недоступен, обновление не ожидается» (§2.1).
    public override Task<PortalHealthReport> HealthCheckAsync(CancellationToken ct = default)
        => Task.FromResult(new PortalHealthReport(PortalHealthStatus.Degraded, VerdictReason));
}
