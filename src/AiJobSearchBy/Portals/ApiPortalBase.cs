using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: официальный JSON API (RFC-003 §2.1): BuildSearchUrl → GET → Parse.
// Цикл «источник → fetch → нормализация → дедуп → вставка» (donor-map §2,
// паттерн Atlas-харвестеров) переписан заново под наши домены — донорский код
// не копируется. Одна попытка, без ретраев — владелец ретраев оркестратор
// (канон §3.2, backoff/кулдауны персистятся на уровне WP-P1+).
// Контракт «никаких тихих игноров» (LOG-CONTRACT.md): каждый шаг — событие
// протокола (НИ-4); catch без записи в протокол запрещён (НИ-1).
// THREAD: HttpClient потокобезопасен; вызовы оркестратора последовательны (§6).
public abstract class ApiPortalBase : PortalBase
{
    protected ApiPortalBase(
        PortalDescriptor descriptor,
        HttpClient http,
        ILogService log)
        : base(descriptor, http, log)
    {
    }

    // URL поиска по строке запроса (endpoint знает адаптер).
    protected abstract string BuildSearchUrl(string query);

    // Разбор payload → нормализованные вакансии. Битый JSON или сменившаяся
    // схема = ParseFailedException (оркестратор засчитает parse_fail; для
    // API-порталов breaker не ведётся — канон вводит его для scrape, §2.10).
    protected abstract Task<IReadOnlyList<JobPosting>> ParseSearchAsync(string payload, CancellationToken ct);

    public override async Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = BuildSearchUrl(query);
        Log.Log($"портал {Descriptor.Id}: поиск «{query}» → GET {url}");

        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);

        try
        {
            // Одна попытка: не-2xx уходит оркестратору как refused (§7), backoff
            // по 429/503 и персист кулдауна — его зона (§2.10).
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var items = await ParseSearchAsync(payload, ct).ConfigureAwait(false);
            Log.LogSuccess($"портал {Descriptor.Id}: поиск отработал — вакансий: {items.Count}");
            return items;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // НИ-1/НИ-2: отмена пользователем — осознанный пропуск, не ошибка.
            Log.LogWarn($"skip: портал {Descriptor.Id} — поиск «{query}» отменён пользователем (ct)");
            throw;
        }
        catch (Exception ex)
        {
            // НИ-1: что делали / что поймали / что решили. Таймаут
            // (TaskCanceledException без отмены ct) и сетевые ошибки — тоже сюда:
            // причина в протоколе, решение (refused/parse_fail) — за оркестратором.
            Log.LogError($"портал {Descriptor.Id}: поиск «{query}» упал — "
                + $"{ex.GetType().Name}: {ex.Message}; решение: наверх (оркестратор решит refused/parse_fail)");
            throw;
        }
    }
}
