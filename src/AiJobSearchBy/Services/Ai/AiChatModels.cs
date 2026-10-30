using System;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// Общие типы ИИ-цепи: стадии, запрос/ответ, исключение-сигнал для фолбэка.
// ============================================================================

// / <summary>Стадии отклика. Роутинг — TaskRoutingPolicy.</summary>
// FLOW: типы ИИ-цепи -> TaskRoutingPolicy -> IAiChatProvider (llm7; далее цепь OpenAI-совместимых)
public enum AiTaskKind
{
    // / <summary>Оценка соответствия — ЛОКАЛЬНЫЙ скоринг, ИИ не тратится.</summary>
    MatchAssessment,

    // / <summary>Адаптация резюме — ядро продукта, лучший доступный провайдер.</summary>
    ResumeAdaptation,

    // / <summary>Сопроводительное письмо — дешёвые провайдеры первыми (llm7 turbo).</summary>
    CoverLetter,

    // / <summary>
    // / Расширение поискового запроса 1-2 смежными (ИИ называет родственные специальности). Один короткий
    // / запрос за прогон, кэш в SettingsKV — повторные поиски ИИ не платят.
    // / </summary>
    QueryExpansion
}

// / <summary>Запрос на генерацию. OpenAI-совместимая семантика сообщений.</summary>
// LIFECYCLE: создаётся на каждый запрос; STATE: иммутабельный record
// THREAD: безопасен с любого потока — только неизменяемые данные
public sealed record AiChatRequest(
    string SystemPrompt,
    string UserPrompt,
    int MaxTokens,
    double Temperature = 0.4);

// / <summary>Расход токенов (вход + выход считаются раздельно, лимитеры суммируют).</summary>
public sealed record AiChatUsage(long PromptTokens, long CompletionTokens)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

// / <summary>Успешный ответ провайдера с фактом расхода и длительностью.</summary>
public sealed record AiChatResult(
    string Content,
    string Model,
    AiChatUsage Usage,
    string ProviderName,
    TimeSpan Duration);

// / <summary>
// / Провайдер занят или исчерпал бюджет — НЕ сбой, а сигнал цепи
// / перейти к следующему. Причина всегда попадает в протокол.
// / </summary>
// STATE: исключение-сигнал «занят/без бюджета» для фолбэка цепи — не сбой, на выброс
public sealed class AiProviderBusyException : Exception
{
    public AiProviderBusyException(string message) : base(message) { }
}
