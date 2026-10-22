using System.Threading;
using System.Threading.Tasks;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// [НАШЕ] Единый интерфейс цепи ИИ-провайдеров (OpenRouter / OrcaRouter / LLM7).
// Если в репо провайдеры оформлены иначе — адаптируются обёрткой или
// реализацией этого интерфейса (точки интеграции: LLM7-INTEGRATION.md §6).
// ============================================================================

public interface IAiChatProvider
{
    /// <summary>Имя для протокола и реестра (например "llm7", "openrouter").</summary>
    string Name { get; }

    /// <summary>1 — самая сильная модель цепи. Порядок для ResumeAdaptation.</summary>
    int QualityRank { get; }

    /// <summary>
    /// Остаток дневного токен-бюджета по данным провайдера/локального регистра;
    /// null — провайдер остаток не отдаёт.
    /// </summary>
    // STATE: только чтение остатка дневного бюджета провайдера
    long? RemainingDailyTokens();

    /// <summary>
    /// Один запрос генерации. Занятость/исчерпание бюджета =
    /// AiProviderBusyException (цепь переходит к следующему); прочие сбои —
    /// обычные исключения, каждый исход кричит в протокол (НИ-1).
    /// </summary>
    // FLOW: TaskRoutingPolicy -> CompleteAsync -> AiChatResult; расход уходит в ResponseCostCounter оркестратора
    // LIFECYCLE: реализации — синглтоны DI (CompositionRoot)
    // THREAD: реализации обязаны быть потокобезопасны — общий ILogService (НИ-5) один на процесс
    Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default);
}
