namespace AiJobSearchBy.Portals;

// FLOW: типизированные ошибки портального слоя. Оркестратор (+) отображает
// их на PortalRunStatus (architecture.md §5/§7): отказ портала ≠ крах прогона.

// Падение парсинга (сайт сменил вёрстку, битый payload) — вход счётчика breaker
// (авто-выключение scrape-портала после N подряд падений).
public sealed class ParseFailedException : Exception
{
    public ParseFailedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

// Breaker открыт: портал временно вне цепи; сетевых запросов быть не должно.
public sealed class BreakerOpenException : Exception
{
    public BreakerOpenException(string portalId, int threshold)
        : base($"breaker открыт для портала '{portalId}' после {threshold} подряд падений парсинга")
    {
    }
}
