using AiJobSearchBy.Portals;

namespace AiJobSearchBy.Services;

// FLOW: keys-check (RFC-003 §2.9) — on-demand диагностика ключей и пингов
// (architecture.md §9). ПОЛИНГ ЗАПРЕЩЁН (architecture.md §3.2): сервис не
// держит таймеров, вызывается кнопкой UI / при старте прогона.
// Правила §2.9: auth=none → зелёный сразу; ключевой → наличие в local.json +
// живой минимальный пинг (per_page=1); нет ключа/пинг мёртв = портал вне цепи
// С ПРИЧИНОЙ в отчёте, не краш; гео-пинг 403 «region restricted» → GeoBlocked
// (причина в UI, без сжигания ретраев — RFC-002 §3.8).
public interface IKeysCheckService
{
    // Диагностика всех зарегистрированных порталов (таблица вердиктов для UI).
    Task<IReadOnlyList<KeyCheckReport>> CheckAllAsync(CancellationToken ct = default);

    // Диагностика одного портала.
    Task<KeyCheckReport> CheckAsync(IPortalService portal, CancellationToken ct = default);
}

public enum KeyCheckStatus
{
    Ok,
    NoKey,
    PingFailed,
    GeoBlocked,
    PingNotConfigured,
}

// Вердикт по порталу; Reason — человекочитаемая причина для отчёта/UI.
public sealed record KeyCheckReport(string PortalId, KeyCheckStatus Status, string Reason);

// Исход живого пинга; реализуют адаптеры (WP-P1+): per_page=1 и т.п.
// GeoBlocked=true для 403 с «region restricted» (§2.9).
public sealed record KeyPingOutcome(bool Succeeded, bool GeoBlocked = false, string? Detail = null);

// STATE: сервис без мутируемого состояния — потокобезопасен.
public sealed class KeysCheckService : IKeysCheckService
{
    private readonly PortalRegistry _registry;
    private readonly ILocalSecrets _secrets;
    private readonly Func<PortalDescriptor, CancellationToken, Task<KeyPingOutcome>>? _ping;

    // ping — необязательный seam: живые пробы появятся вместе с адаптерами
    // (WP-P1+); до того ключевые порталы честно получают PingNotConfigured.
    public KeysCheckService(
        PortalRegistry registry,
        ILocalSecrets secrets,
        Func<PortalDescriptor, CancellationToken, Task<KeyPingOutcome>>? ping = null)
    {
        _registry = registry;
        _secrets = secrets;
        _ping = ping;
    }

    public async Task<IReadOnlyList<KeyCheckReport>> CheckAllAsync(CancellationToken ct = default)
    {
        var reports = new List<KeyCheckReport>();
        foreach (var portal in _registry.Portals)
        {
            ct.ThrowIfCancellationRequested();
            reports.Add(await CheckAsync(portal, ct).ConfigureAwait(false));
        }

        return reports;
    }

    public async Task<KeyCheckReport> CheckAsync(IPortalService portal, CancellationToken ct = default)
    {
        var descriptor = portal.Descriptor;

        if (descriptor.Auth == PortalAuthKind.None)
        {
            return new KeyCheckReport(descriptor.Id, KeyCheckStatus.Ok, "ключ не требуется (auth=none) — портал в цепи");
        }

        var key = _secrets.GetPortalKey(descriptor.Id);
        if (string.IsNullOrWhiteSpace(key))
        {
            return new KeyCheckReport(
                descriptor.Id,
                KeyCheckStatus.NoKey,
                "ключ отсутствует в local.json — портал вне цепи (включается вручную после ввода ключа)");
        }

        if (_ping is null)
        {
            return new KeyCheckReport(
                descriptor.Id,
                KeyCheckStatus.PingNotConfigured,
                "ключ найден, живой пинг не настроен — появится вместе с адаптером портала (WP-P1+)");
        }

        KeyPingOutcome outcome;
        try
        {
            outcome = await _ping(descriptor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // §2.9: пинг мёртв = вне цепи С ПРИЧИНОЙ, не краш.
            outcome = new KeyPingOutcome(Succeeded: false, Detail: e.Message);
        }

        if (outcome.GeoBlocked)
        {
            return new KeyCheckReport(
                descriptor.Id,
                KeyCheckStatus.GeoBlocked,
                $"гео-блок (403, регион ограничен) — без сжигания ретраев. {outcome.Detail}".Trim());
        }

        if (outcome.Succeeded)
        {
            return new KeyCheckReport(descriptor.Id, KeyCheckStatus.Ok, $"ключ принят, пинг живой. {outcome.Detail}".Trim());
        }

        return new KeyCheckReport(descriptor.Id, KeyCheckStatus.PingFailed, $"пинг не прошёл: {outcome.Detail}".Trim());
    }
}
