using System.Threading;
using System.Threading.Tasks;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// Единый интерфейс цепи ИИ-провайдеров (OpenRouter / OrcaRouter / LLM7).
// Если в репо провайдеры оформлены иначе — адаптируются обёрткой или
// реализацией этого интерфейса.
// ============================================================================

public interface IAiChatProvider
{
    // Имя для протокола и реестра (например "llm7", "openrouter").
    string Name { get; }

    // / <summary>1 — самая сильная модель цепи. Порядок для ResumeAdaptation.</summary>
    int QualityRank { get; }

    // / <summary>
    // / Остаток дневного токен-бюджета по данным провайдера/локального регистра;
    // / null — провайдер остаток не отдаёт.
    // / </summary>
    // STATE: только чтение остатка дневного бюджета провайдера
    long? RemainingDailyTokens();

    // / <summary>
    // / Один запрос генерации. Занятость/исчерпание бюджета =
    // / AiProviderBusyException (цепь переходит к следующему); прочие сбои —
    // / обычные исключения, каждый исход кричит в протокол .
    // / </summary>
    // FLOW: TaskRoutingPolicy -> CompleteAsync -> AiChatResult; расход уходит в ResponseCostCounter оркестратора
    // LIFECYCLE: реализации — синглтоны DI (CompositionRoot)
    // THREAD: реализации обязаны быть потокобезопасны — общий ILogService один на процесс
    Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default);
}
