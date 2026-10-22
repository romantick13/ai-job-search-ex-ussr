using System;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// [НАШЕ] Общие типы ИИ-цепи. Основание: решение владельца 2026-10-21 —
// добавить LLM7 третьим провайдером; роутинг стадий по критике Мистраля.
// ============================================================================

/// <summary>Стадии отклика. Роутинг — TaskRoutingPolicy.</summary>
// FLOW: типы ИИ-цепи -> TaskRoutingPolicy -> IAiChatProvider (llm7; далее цепь OpenAI-совместимых)
public enum AiTaskKind
{
    /// <summary>Оценка соответствия — ЛОКАЛЬНЫЙ скоринг, ИИ не тратится (критика Мистраля).</summary>
    MatchAssessment,

    /// <summary>Адаптация резюме — ядро продукта, лучший доступный провайдер.</summary>
    ResumeAdaptation,

    /// <summary>Сопроводительное письмо — дешёвые провайдеры первыми (llm7 turbo).</summary>
    CoverLetter
}

/// <summary>Запрос на генерацию. OpenAI-совместимая семантика сообщений.</summary>
// LIFECYCLE: создаётся на каждый запрос; STATE: иммутабельный record
// THREAD: безопасен с любого потока — только неизменяемые данные
public sealed record AiChatRequest(
    string SystemPrompt,
    string UserPrompt,
    int MaxTokens,
    double Temperature = 0.4);

/// <summary>Расход токенов (вход + выход считаются раздельно, лимитеры суммируют).</summary>
public sealed record AiChatUsage(long PromptTokens, long CompletionTokens)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>Успешный ответ провайдера с фактом расхода и длительностью.</summary>
public sealed record AiChatResult(
    string Content,
    string Model,
    AiChatUsage Usage,
    string ProviderName,
    TimeSpan Duration);

/// <summary>
/// [НАШЕ] Провайдер занят или исчерпал бюджет — НЕ сбой, а сигнал цепи
/// перейти к следующему. Причина всегда попадает в протокол (НИ-1/НИ-2).
/// </summary>
// STATE: исключение-сигнал «занят/без бюджета» для фолбэка цепи — не сбой, на выброс
public sealed class AiProviderBusyException : Exception
{
    public AiProviderBusyException(string message) : base(message) { }
}
