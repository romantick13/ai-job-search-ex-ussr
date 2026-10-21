namespace AiJobSearchBy.Portals;

// FLOW: реестр порталов (architecture.md §3.3, RFC-003 §2.2): порядок = массив
// конфига Portals[] (порядок = приоритет, чекбокс Enabled); порталы вне массива
// дописываются в хвост по DefaultPriority, НО только те, у кого карточка
// разрешает дефолт (PortalDescriptor.DefaultEnabled, зеркало default_enabled
// registry.json — WP-P1): «выключенные по умолчанию» (rabota-by/hh-by)
// в цепь без явного конфига не попадают. Конфиг сильнее: запись Enabled=true
// включает и выключенный по умолчанию портал. Снапшот порядка фиксируется НА
// СТАРТЕ прогона (оркестратор, следующий WP): правки конфига посреди прогона
// не перетасовывают живую цепочку — ResolveChain каждый раз отдаёт свежий
// независимый список, а оркестратор держит свой снапшот до конца прогона.
// STATE: реестр неизменяем после конструктора — потокобезопасен.
public sealed class PortalRegistry
{
    private readonly Dictionary<string, IPortalService> _byId;

    // WP-P0: адаптеров ещё нет — реестр создаётся пустым (DI) и жив:
    // порядок и keys-check покрыты тестами на фейках; наполнение — WP-P1+.
    public PortalRegistry(IEnumerable<IPortalService>? portals = null)
    {
        _byId = new Dictionary<string, IPortalService>(StringComparer.Ordinal);
        foreach (var portal in portals ?? Enumerable.Empty<IPortalService>())
        {
            if (!_byId.TryAdd(portal.Descriptor.Id, portal))
            {
                throw new ArgumentException($"дубликат PortalId '{portal.Descriptor.Id}' в реестре");
            }
        }
    }

    // Все зарегистрированные порталы, стабильно упорядоченные по приоритету.
    public IReadOnlyList<IPortalService> Portals
        => _byId.Values
            .OrderBy(p => p.Descriptor.DefaultPriority)
            .ThenBy(p => p.Descriptor.Id, StringComparer.Ordinal)
            .ToList();

    // Запись конфига Portals[] (config/app.json): порядок и чекбокс Enabled (§3.3).
    public sealed record PortalOrderEntry(string PortalId, bool Enabled);

    // Цепь прогона: конфиг сначала (в порядке массива), хвост — вне конфига
    // по DefaultPriority при DefaultEnabled=true (выключатель карточки §2.11,
    // WP-P1). Записи с Enabled=false в цепь не попадают (§5: отключённый
    // портал = PortalRunStatus.disabled на стороне отчёта).
    // Неизвестные/дублирующиеся id конфига игнорируются, не краш (§7).
    public IReadOnlyList<IPortalService> ResolveChain(IReadOnlyList<PortalOrderEntry>? configured = null)
    {
        var chain = new List<IPortalService>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (configured is not null)
        {
            foreach (var entry in configured)
            {
                if (!_byId.TryGetValue(entry.PortalId, out var portal) || !seen.Add(entry.PortalId))
                {
                    continue;
                }

                if (entry.Enabled)
                {
                    chain.Add(portal);
                }
            }
        }

        chain.AddRange(_byId.Values
            .Where(p => !seen.Contains(p.Descriptor.Id))
            .Where(p => p.Descriptor.DefaultEnabled)
            .OrderBy(p => p.Descriptor.DefaultPriority)
            .ThenBy(p => p.Descriptor.Id, StringComparer.Ordinal));

        return chain;
    }
}
