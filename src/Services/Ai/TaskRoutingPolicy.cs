using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// [НАШЕ] TaskRoutingPolicy — роутинг стадий отклика по провайдерам.
// Основание — критика Мистраля (вставка владельца, 2026-10-21):
//   • поиск/фильтры/экспорт/ATS — работают без ИИ (коммодити);
//   • ИИ-оценка соответствия — самая сомнительная трата → стадия match
//     ИИ НЕ тратит: RunAsync(MatchAssessment) отказывает с LogWarn,
//     вместо неё — LocalMatchScorer;
//   • адаптация резюме — «вот здесь ИИ создаёт реальную ценность... это и
//     есть продукт» → порядок по качеству (QualityRank);
//   • сопроводительное письмо — «средняя ценность, но стоит дёшево» →
//     порядок по дешевизне: llm7 (turbo, free) первым, остальные фолбэком.
// Ни один исход не молчит: отказ политики — LogWarn, фолбэк — LogWarn,
// исчерпание цепи — LogError (НИ-1/НИ-2).
// ============================================================================

public sealed class TaskRoutingPolicy
{
    private readonly ILogService _log;
    private readonly List<IAiChatProvider> _providers;
    private readonly IReadOnlyList<string>? _coverLetterOrder;

    /// <param name="providers">Зарегистрированные провайдеры цепи.</param>
    /// <param name="coverLetterOrder">
    /// Имена провайдеров письма по возрастанию цены, например ["llm7"].
    /// null → порядок по качеству (llm7 последним).
    /// </param>
    public TaskRoutingPolicy(ILogService log,
                             IEnumerable<IAiChatProvider> providers,
                             IReadOnlyList<string>? coverLetterOrder = null)
    {
        _log = log;
        _providers = providers.ToList();
        _coverLetterOrder = coverLetterOrder;
    }

    /// <summary>Порядок провайдеров для стадии. Пустой = стадия без ИИ (кричит в протокол).</summary>
    public IReadOnlyList<IAiChatProvider> OrderFor(AiTaskKind kind)
    {
        switch (kind)
        {
            case AiTaskKind.MatchAssessment:
                // Осознанный отказ с причиной (НИ-2): стадия уходит в локальный скоринг.
                _log.LogWarn("skip: ИИ-оценка соответствия не выполняется — причина: политика бюджета " +
                             "(критика Мистраля, 2026-10-21), используется LocalMatchScorer; " +
                             "решение: 0 ИИ-запросов на стадию match");
                return Array.Empty<IAiChatProvider>();

            case AiTaskKind.ResumeAdaptation:
                return _providers.OrderBy(p => p.QualityRank).ToList();

            case AiTaskKind.CoverLetter:
                return LetterOrder();

            default:
                // Неизвестная стадия — не молчим (НИ-2).
                _log.LogError($"[chain] неизвестная стадия {kind}; решение: отказ, без ИИ-запроса");
                return Array.Empty<IAiChatProvider>();
        }
    }

    /// <summary>
    /// Прогон стадии по цепи: первый доступный провайдер побеждает,
    /// занятость/бюджет → фолбэк, исчерпание всех → LogError + null
    /// (громко: причины в протоколе, тишины нет).
    /// </summary>
    public async Task<AiChatResult?> RunAsync(AiTaskKind kind, AiChatRequest request, CancellationToken ct = default)
    {
        var chain = OrderFor(kind);
        if (chain.Count == 0) return null; // отказ уже прозвучал в OrderFor (НИ-2)

        var reasons = new List<string>();
        for (var i = 0; i < chain.Count; i++)
        {
            var provider = chain[i];
            try
            {
                return await provider.CompleteAsync(request, ct);
            }
            catch (AiProviderBusyException ex)
            {
                reasons.Add($"{provider.Name}: {ex.Message}");
                var next = i + 1 < chain.Count
                    ? $"следующий в цепи: {chain[i + 1].Name}"
                    : "дальше провайдеров нет";
                _log.LogWarn($"skip: {provider.Name} недоступен ({kind}) — причина: {ex.Message}; решение: {next}");
            }
        }
        _log.LogError($"[chain] провайдеры исчерпаны для {kind} — причины: {string.Join(" | ", reasons)}; " +
                      "решение: стадия отклика остаётся невыполненной, помечаем для ручного прогона");
        return null;
    }

    private IReadOnlyList<IAiChatProvider> LetterOrder()
    {
        if (_coverLetterOrder is null)
            return _providers.OrderBy(p => p.QualityRank).ToList();

        var list = new List<IAiChatProvider>();
        foreach (var name in _coverLetterOrder)
        {
            var p = _providers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p is null)
                _log.LogWarn($"skip: провайдер '{name}' из coverLetterOrder не найден среди зарегистрированных — " +
                             "причина: опечатка или не зарегистрирован; решение: пропускаю имя, порядок достраивается по качеству");
            else
                list.Add(p);
        }
        foreach (var p in _providers.Where(x => !list.Contains(x)).OrderBy(x => x.QualityRank))
            list.Add(p);
        return list;
    }
}
