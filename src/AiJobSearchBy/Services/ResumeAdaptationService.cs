using System.Text;
using AiJobSearchBy.Models;
using AiJobSearchBy.Services.Ai;

namespace AiJobSearchBy.Services;

// ============================================================================
// WP-U1 (решение владельца 2026-10-22): адаптация резюме под конкретную
// вакансию. Стадия ResumeAdaptation — ядро продукта (AiChatModels.cs): цепь
// идёт от сильных моделей к дешёвым (TaskRoutingPolicy.OrderFor), расход —
// в [cost] протокола провайдера.
//
// Контракт владельца: «окно с адаптированным резюме должно обновляться после
// отправки модели своего варианта резюме + требований работодателя». Промпт
// строится из базы резюме (SettingsKV resume.text) и полей JobPosting;
// описание вакансии обрезается до 6000 знаков — экономия дневных лимитов
// (llm7 100K ток/сутки, ModelScope 2000 запр/сутки).
//
// Правда важнее подгонки: системный промпт прямо запрещает выдумывать опыт
// (критика «AI-галлюцинаций в резюме» — base docs).
//
// FLOW: MainViewModel (клик по вакансии) -> AdaptAsync -> TaskRoutingPolicy ->
//       первый живой провайдер -> текст в окно «Адаптированное резюме».
// STATE: без состояния — потокобезопасен.
// THREAD: вызов с фонового потока (Task.Run в VM).
// ============================================================================
public sealed class ResumeAdaptationService
{
    public const int MaxVacancyDescriptionChars = 6000;

    private readonly ILogService _log;
    private readonly TaskRoutingPolicy _routing;

    public ResumeAdaptationService(ILogService log, TaskRoutingPolicy routing)
    {
        _log = log;
        _routing = routing;
    }

    public static (string SystemPrompt, string UserPrompt) BuildPrompt(
        string resume, JobPosting posting)
    {
        var system =
            "Ты — карьерный консультант. Твоя задача: адаптировать резюме кандидата " +
            "под конкретную вакансию. Правила: (1) сохраняй факты и правду — не выдумывай " +
            "опыт, навыки и даты; (2) переупорядочивай и переформулировай существующий опыт " +
            "под требования вакансии; (3) используй ключевые слова вакансии там, где они " +
            "честно соответствуют опыту; (4) верни ТОЛЬКО текст адаптированного резюме, " +
            "без пояснений и markdown-разметки заголовков.";

        var sb = new StringBuilder();
        sb.AppendLine("РЕЗЮМЕ КАНДИДАТА:");
        sb.AppendLine(resume.Trim());
        sb.AppendLine();
        sb.AppendLine("ВАКАНСИЯ:");
        sb.AppendLine($"Название: {posting.Title ?? "—"}");
        sb.AppendLine($"Компания: {posting.Company ?? "—"}");
        sb.AppendLine($"Локация: {posting.Location ?? "—"}");
        sb.AppendLine($"Зарплата: {posting.SalaryText ?? "—"}");
        sb.AppendLine($"Ссылка: {posting.CanonicalUrl}");
        sb.AppendLine("Описание:");
        var description = posting.DescriptionMd?.Trim() ?? "—";
        if (description.Length > MaxVacancyDescriptionChars)
        {
            description = description[..MaxVacancyDescriptionChars] + "… (обрезано)";
        }
        sb.AppendLine(description);
        sb.AppendLine();
        sb.Append("Задача: адаптируй резюме под эту вакансию. Если резюме пустое — верни " +
                  "скелет резюме под вакансию с пометками в квадратных скобках, что кандидату " +
                  "нужно заполнить.");
        return (system, sb.ToString());
    }

    public async Task<AiChatResult?> AdaptAsync(string resume, JobPosting posting,
        CancellationToken ct = default)
    {
        var (system, user) = BuildPrompt(resume, posting);
        _log.Log($"[adapt] адаптация резюме: вакансия «{posting.Title ?? posting.CanonicalUrl}», " +
                 $"резюме {resume.Length} зн., описание {Math.Min(posting.DescriptionMd?.Length ?? 0, MaxVacancyDescriptionChars)} зн.");
        return await _routing.RunAsync(AiTaskKind.ResumeAdaptation,
            new AiChatRequest(system, user, MaxTokens: 2048, Temperature: 0.3), ct)
            .ConfigureAwait(false);
    }
}
