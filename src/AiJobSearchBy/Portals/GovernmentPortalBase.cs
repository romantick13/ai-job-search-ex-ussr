namespace AiJobSearchBy.Portals;

// FLOW: госсектор (WP-P7, RFC-003 §2.1): строгий polite-crawl + обязательный
// дисклеймер. Отличается от коммерческого scrape увеличенными паузами, меньшим
// лимитом страниц и более капризным breaker'ом; точные числа уточнит карточка
// §2.11 конкретного госисточника (gsz_gov_by, minsk_komtrud).
public abstract class GovernmentPortalBase : ScrapePortalBase
{
    // Канон §3.3: «официальный источник, данные сверять с первоисточником».
    public const string DisclaimerText =
        "Данные получены из открытого официального источника. Сверяйте с первоисточником.";

    protected GovernmentPortalBase(
        PortalDescriptor descriptor,
        HttpClient http,
        AiJobSearchBy.Services.ILogService log,
        PolitenessOptions? options = null)
        : base(descriptor, http, log, options ?? Strict)
    {
    }

    // Строгий режим по умолчанию: медленнее и капризнее к сбоям.
    private static PolitenessOptions Strict { get; } = new(
        MinDelay: TimeSpan.FromSeconds(5),
        JitterFraction: 0.30,
        MaxPagesPerRun: 3,
        BreakerThreshold: 2);
}
