using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: общий корень всех адаптеров — манифест + HTTP-клиент + каркас контракта.
// Контракт «никаких тихих игноров» (LOG-CONTRACT.md): каждый шаг адаптера —
// событие протокола (НИ-4); Log доступен всем базам и адаптерам.
// LIFECYCLE: адаптеры — синглтоны DI; собственного сетевого состояния не держат
// (HttpClient инжектится); мутируемое состояние (breaker, вежливые паузы) —
// только у ScrapePortalBase и живёт в рамках последовательного прогона (§6).
public abstract class PortalBase : IPortalService
{
    protected PortalBase(PortalDescriptor descriptor, HttpClient http, ILogService log)
    {
        Descriptor = descriptor;
        Http = http;
        Log = log;
    }

    public PortalDescriptor Descriptor { get; }

    protected HttpClient Http { get; }

    // Протокол (НИ-4): DI подставляет общий синглтон; запись — на диск + лента.
    protected ILogService Log { get; }

    public abstract Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default);

    public abstract Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default);

    // Дефолт честно сообщает, что канареечный прогон адаптером не реализован;
    // адаптеры переопределяют с golden-фикстурой (RFC-003 §2.1, карточка §2.11).
    public virtual Task<PortalHealthReport> HealthCheckAsync(CancellationToken ct = default)
        => Task.FromResult(new PortalHealthReport(
            PortalHealthStatus.Degraded,
            "health-check не реализован адаптером (канареечный запрос придёт с карточкой §2.11)"));
}
