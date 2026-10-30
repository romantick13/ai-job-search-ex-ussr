using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: официальный JSON API: BuildSearchUrl → GET → Parse.
// Цикл «источник → fetch → нормализация → дедуп → вставка» переписан заново
// под наши домены — код
// не копируется. Одна попытка, без ретраев — ретраи делает оркестратор
// (канон: backoff/кулдауны персистятся на его уровне).
// Контракт «никаких тихих игноров»: каждый шаг — событие
// протокола; catch без записи в протокол запрещён.
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны.
public abstract class ApiPortalBase : PortalBase
{
    protected ApiPortalBase(
        PortalDescriptor descriptor,
        HttpClient http,
        ILogService log)
        : base(descriptor, http, log)
    {
    }

    // Официальный API отдаёт описание прямо в ответе поиска — DetailAsync
    // не нужен (адаптеры-наследники честно бросают NotSupportedException).
    public override bool SupportsDetail => false;

    // URL поиска по строке запроса (endpoint знает адаптер).
    protected abstract string BuildSearchUrl(string query);

    // Разбор payload → нормализованные вакансии. Битый JSON или сменившаяся
    // схема = ParseFailedException (оркестратор засчитает parse_fail; для
    // API-порталов breaker не ведётся — канон вводит его для scrape).
    protected abstract Task<IReadOnlyList<JobPosting>> ParseSearchAsync(string payload, CancellationToken ct);

    public override async Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = BuildSearchUrl(query);
        Log.Log($"портал {Descriptor.Id}: поиск «{query}» → GET {url}");

        try
        {
            // общий GET с одним ретраем (PortalBase.GetPayloadAsync):
            // 403/429/503/сеть — пауза 4 с и вторая попытка (щиты/лимиты часто
            // отпускают); не-2xx — человекочитаемая причина в протоколе
            // (урок remotive 403: «почему упал,
            // что с ним?» — видно без гаданий).
            var payload = await GetPayloadAsync(url, $"поиск «{query}»", ct).ConfigureAwait(false);
            var items = await ParseSearchAsync(payload, ct).ConfigureAwait(false);
            Log.LogSuccess($"портал {Descriptor.Id}: поиск отработал — вакансий: {items.Count}");
            return items;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // /отмена пользователем — осознанный пропуск, не ошибка.
            Log.LogWarn($"skip: портал {Descriptor.Id} — поиск «{query}» отменён пользователем (ct)");
            throw;
        }
        catch (Exception ex)
        {
            // что делали / что поймали / что решили. Таймаут
            // (TaskCanceledException без отмены ct) и сетевые ошибки — тоже сюда:
            // причина в протоколе, решение — человеческое (прогон продолжается).
            Log.LogError($"портал {Descriptor.Id}: поиск «{query}» упал — "
                + $"{ex.GetType().Name}: {ex.Message}; решение: этот портал пропускаю, "
                + "поиск продолжается по остальным сайтам — вернись к нему в следующем прогоне");
            throw;
        }
    }
}
