using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Portals;

// FLOW: общий корень всех адаптеров — манифест + HTTP-клиент + каркас контракта.
// Контракт «никаких тихих игноров»: каждый шаг адаптера —
// событие протокола; Log доступен всем базам и адаптерам.
// LIFECYCLE: адаптеры — синглтоны DI; собственного сетевого состояния не держат
// (HttpClient инжектится); мутируемое состояние (breaker, вежливые паузы) —
// только у ScrapePortalBase и живёт в рамках последовательного прогона.
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

    // Протокол: DI подставляет общий синглтон; запись — на диск + лента.
    protected ILogService Log { get; }

    // Умеет ли адаптер доставать ПОЛНЫЙ текст вакансии со страницы вакансии
    // (DetailAsync). r20: кнопка «Адаптировать» кормит модель описанием, и
    // для scrape-порталов (hh-группа) карточки листинга мало — там только
    // «шапка» (опыт/выплаты/удалёнка). API-порталы и RSS-ленты отдают
    // описание прямо в ответе поиска — им DetailAsync не нужен (честный
    // NotSupportedException), потому false. Дефолт true — scrape-базы.
    public virtual bool SupportsDetail => true;

    public abstract Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default);

    public abstract Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default);


    // Живые кейсы (протокол: remotive 403 два прогона подряд,
    // nodesk «не разбирается как XML» — под HTML-щитом): общий GET с ОДНИМ
    // повтором через 4 с. 403/429/503 и сетевые сбои часто снимаются паузой
    // (лимит частоты/щит отпускает); second attempt — последняя, очередь
    // оркестратора и его backoff это не отменяет. Каждая попытка —
    // событие протокола. Заодно человекочитаемые не-2xx стали общими
    // и для RSS-лент (раньше только API-порталы получали расшифровку).

    protected async Task<string> GetPayloadAsync(string url, string what, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // отмена пользователем — не сбой, наверх без ретрая
            }
            catch (Exception ex) when (attempt == 1 && ex is HttpRequestException or TaskCanceledException)
            {
                // Сеть/таймаут: щит мог притормозить соединение — одна пауза.
                Log.LogWarn($"портал {Descriptor.Id}: {what} — повтор через паузу " +
                            $"(попытка 1: {ex.GetType().Name}: {Shorten(ex.Message)})");
                await RetryPauseAsync(ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                var code = (int)response.StatusCode;
                if (attempt == 1 && response.StatusCode is
                    System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.TooManyRequests
                    or System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Log.LogWarn($"портал {Descriptor.Id}: {what} — повтор через паузу " +
                                $"(попытка 1: HTTP {code})");
                    await RetryPauseAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // Одна попытка истрачена / код не отдаётся ретраем: не-2xx —
                // внятная причина в протокол (
                // после remotive 403: «почему упал, что с ним?» — без гаданий).
                throw new HttpRequestException(
                    $"{HumanizeStatus(response.StatusCode)}; проверь портал руками в браузере — " +
                    "если тот же ответ, дело в самом сайте");
            }
        }
    }

    // Пауза перед повтором (4 с в проде; тесты глушат переопределением).
    protected virtual Task RetryPauseAsync(CancellationToken ct) => Task.Delay(4000, ct);

    private static string Shorten(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    private static string HumanizeStatus(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Forbidden =>
            "сайт отклонил запрос (403 Forbidden) — это блокировка НА СТОРОНЕ ПОРТАЛА " +
            "(антибот-щит, гео-лимит или лимит частоты обращений); программа тут бессильна",
        System.Net.HttpStatusCode.Unauthorized =>
            "сайт требует авторизацию (401) — для этого портала ключ не предусмотрен или отозван",
        System.Net.HttpStatusCode.TooManyRequests =>
            "лимит частоты обращений (429) — слишком частые запросы с этого адреса",
        System.Net.HttpStatusCode.ServiceUnavailable =>
            "сайт временно недоступен (503) — перегрузка/обслуживание",
        _ => $"не-2xx ответ (HTTP {(int)status})",
    };

    // Дефолт честно сообщает, что канареечный прогон адаптером не реализован;
    // адаптеры переопределяют с golden-фикстурой (карточка портала).
    public virtual Task<PortalHealthReport> HealthCheckAsync(CancellationToken ct = default)
        => Task.FromResult(new PortalHealthReport(
            PortalHealthStatus.Degraded,
            "health-check не реализован адаптером (канареечный запрос придёт с карточкой портала)"));
}
