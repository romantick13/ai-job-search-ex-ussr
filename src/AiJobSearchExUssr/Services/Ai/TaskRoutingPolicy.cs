using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// TaskRoutingPolicy — роутинг стадий отклика по провайдерам.
// Логика — бюджет и осмысленность трат:
// • поиск/фильтры/экспорт/ATS — работают без ИИ (комодити);
// • ИИ-оценка соответствия — самая сомнительная трата → стадия match
// ИИ НЕ тратит: RunAsync(MatchAssessment) отказывает с LogWarn,
// вместо неё — LocalMatchScorer;
// • адаптация резюме — здесь ИИ создаёт реальную ценность (ядро
// продукта) → порядок по качеству (QualityRank);
// • сопроводительное письмо — средняя ценность, но стоит дёшево →
// порядок по дешевизне: llm7 (turbo, free) первым, остальные фолбэком.
// Ни один исход не молчит: отказ политики — LogWarn, фолбэк — LogWarn,
// исчерпание цепи — LogError.
// r25 — честная цепь: роутинг берёт звенья из ChainRuntime, значит
// • снятые чекбоксом звенья НЕ участвуют ни в одной ИИ-стадии
//   (какие остались — в такие и стучимся; пропуски — WARN в протокол);
// • если включённых не осталось — стадия честно не выполнена (LogError);
// • правки настроек применяются на лету — список берётся свежим на
//   каждый запрос (Rebuild ChainRuntime без перезапуска).
// ============================================================================

public sealed class TaskRoutingPolicy
{
    private readonly ILogService _log;

    // Статический путь (тесты, обратная совместимость): явный список без чекбоксов.
    private readonly List<IAiChatProvider> _providers;

    // Живой путь (DI, r25): ChainRuntime — чекбоксы + правки на лету.
    private readonly ChainRuntime? _runtime;
    private readonly IReadOnlyList<string>? _coverLetterOrder;

    // DI-ctor (CompositionRoot): живая цепь — источник звеньев ChainRuntime.
    // LIFECYCLE: синглтон DI; порядок письма фиксируется при регистрации.
    public TaskRoutingPolicy(ILogService log, ChainRuntime chain,
        IReadOnlyList<string>? coverLetterOrder = null)
    {
        _log = log;
        _runtime = chain;
        _providers = new List<IAiChatProvider>();
        _coverLetterOrder = coverLetterOrder;
    }

    // / <param name="providers">Фиксированный список зарегистрированных провайдеров (тесты).</param>
    // / <param name="coverLetterOrder">
    // / Имена провайдеров письма по возрастанию цены, например ["llm7"].
    // / null → порядок по качеству (llm7 последним).
    // / </param>
    // Обратная совместимость (тесты): фиксированный список — чекбоксы не действуют.
    public TaskRoutingPolicy(ILogService log, IEnumerable<IAiChatProvider> providers,
        IReadOnlyList<string>? coverLetterOrder = null)
    {
        _log = log;
        _runtime = null;
        _providers = providers.ToList();
        _coverLetterOrder = coverLetterOrder;
    }

    // / <summary>Порядок провайдеров для стадии. Пустой = стадия без ИИ (кричит в протокол).</summary>
    // FLOW: AiTaskKind -> switch -> порядок цепи; MatchAssessment = осознанный отказ (LogWarn) -> LocalMatchScorer
    public IReadOnlyList<IAiChatProvider> OrderFor(AiTaskKind kind)
    {
        switch (kind)
        {
            case AiTaskKind.MatchAssessment:
                // Осознанный отказ с причиной: стадия уходит в локальный скоринг.
                _log.LogWarn("skip: ИИ-оценка соответствия не выполняется — причина: политика бюджета " +
                             "(критика Мистраля, 2026-10-21), используется LocalMatchScorer; " +
                             "решение: 0 ИИ-запросов на стадию match");
                return Array.Empty<IAiChatProvider>();

            case AiTaskKind.ResumeAdaptation:
            case AiTaskKind.QueryExpansion:
                // расширение запроса идёт тем же порядком качества —
                // объём копеечный (короткий JSON-ответ), важнее честный фолбэк цепи.
                return EnabledByQuality();

            case AiTaskKind.CoverLetter:
                return LetterOrder();

            default:
                // Неизвестная стадия — не молчим.
                _log.LogError($"[chain] неизвестная стадия {kind}; решение: отказ, без ИИ-запроса");
                return Array.Empty<IAiChatProvider>();
        }
    }

    // Прогон стадии по цепи: первый доступный провайдер побеждает,
    // занятость/бюджет/сбой → фолбэк, исчерпание всех → LogError + null
    // (громко: причины в протоколе, тишины нет).
    // FLOW: оркестратор: RunAsync(стадия) -> OrderFor -> цепь провайдеров -> AiChatResult|null;
    // OrderFor(kind) -> по цепи CompleteAsync -> первый успех возвращается;
    // AiProviderBusyException -> LogWarn причины -> следующий; все заняты -> LogError + null.
    // Перебирать надо ВСЕ сбои провайдера, не только занятость. Провайдер честно пишет
    // «исключение наружу — цепь решает» (HTTP 401/500, таймаут, сеть) — но
    // catch ниже ловил только AiProviderBusyException, и HttpRequestException
    // вылетал наверх, убивая стадию целиком (ModelScope 401 при живом ключе =
    // адаптация падала, фолбэк не запускался). Теперь цепь решает ДО конца:
    // любой провал -> протокол -> следующий провайдер; наверх идёт только
    // «Отмена» юзера. Исчерпание всех -> LogError + null (стадия честно не сделана).
    // THREAD: безопасен — последовательный прогон, состояние только локальные переменные
    public async Task<AiChatResult?> RunAsync(AiTaskKind kind, AiChatRequest request, CancellationToken ct = default)
    {
        var chain = OrderFor(kind);
        if (chain.Count == 0)
        {
            return null; // отказ уже прозвучал в OrderFor (policy/чекбоксы)
        }

        var reasons = new List<string>();
        for (var i = 0; i < chain.Count; i++)
        {
            var provider = chain[i];
            try
            {
                return await provider.CompleteAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // «Отмена» юзера — не сбой провайдера, наверх без фолбэка
            }
            catch (AiProviderBusyException ex)
            {
                reasons.Add($"{provider.Name}: {ex.Message}");
                var next = i + 1 < chain.Count
                    ? $"следующий в цепи: {chain[i + 1].Name}"
                    : "дальше провайдеров нет";
                _log.LogWarn($"skip: {provider.Name} недоступен ({kind}) — причина: {ex.Message}; решение: {next}");
            }
            catch (Exception ex)
            {
                // Сбой провайдера (HTTP 401/500, таймаут, сеть) — не стадию хоронить,
                // а идти дальше: у может быть живой ключ у следующего.
                reasons.Add($"{provider.Name}: {ex.Message}");
                var next = i + 1 < chain.Count
                    ? $"следующий в цепи: {chain[i + 1].Name}"
                    : "дальше провайдеров нет";
                _log.LogWarn($"skip: {provider.Name} сбой ({kind}) — причина: " +
                             $"{ex.GetType().Name}: {ex.Message}; решение: {next}");
            }
        }
        _log.LogError($"[chain] провайдеры исчерпаны для {kind} — причины: {string.Join(" | ", reasons)}; " +
                      "решение: стадия отклика остаётся невыполненной, помечаем для ручного прогона");
        return null;
    }

    // r25: включённые звенья по качеству. Снятые чекбоксом — WARN с полным
    // списком пропущенных и оставшихся; пусто — LogError (стадия честно
    // не выполнена, а не тихий ноль).
    private IReadOnlyList<IAiChatProvider> EnabledByQuality()
    {
        var (enabled, disabledNames, total) = EnabledSnapshot();
        WarnDisabled(enabled, disabledNames, total);
        return enabled.OrderBy(p => p.QualityRank).ToList();
    }

    // FLOW: coverLetterOrder (по дешевизне) -> имена сверяются с ВКЛЮЧЁННЫМИ
    // (r25); ненайденные кричат, порядок достраивается по QualityRank.
    private IReadOnlyList<IAiChatProvider> LetterOrder()
    {
        var (enabled, disabledNames, total) = EnabledSnapshot();
        WarnDisabled(enabled, disabledNames, total);

        if (_coverLetterOrder is null)
        {
            return enabled.OrderBy(p => p.QualityRank).ToList();
        }

        var list = new List<IAiChatProvider>();
        foreach (var name in _coverLetterOrder)
        {
            var p = enabled.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                _log.LogWarn($"skip: провайдер '{name}' из coverLetterOrder не найден среди включённых — " +
                             "причина: опечатка, не зарегистрирован или отключён чекбоксом; " +
                             "решение: пропускаю имя, порядок достраивается по качеству");
            }
            else
            {
                list.Add(p);
            }
        }
        foreach (var p in enabled.Where(x => !list.Contains(x)).OrderBy(x => x.QualityRank))
        {
            list.Add(p);
        }
        return list;
    }

    // Срез цепи: живой путь — ChainRuntime (чекбоксы + правки на лету),
    // статический — фиксированный список из ctor (тесты, чекбоксов нет).
    private (IReadOnlyList<IAiChatProvider> Enabled,
             IReadOnlyList<string> DisabledNames,
             int Total) EnabledSnapshot()
        => _runtime is null
            ? (_providers, Array.Empty<string>(), _providers.Count)
            : _runtime.Snapshot();

    private void WarnDisabled(IReadOnlyList<IAiChatProvider> enabled,
        IReadOnlyList<string> disabledNames, int total)
    {
        if (disabledNames.Count == 0)
        {
            return;
        }

        _log.LogWarn("skip: звенья отключены чекбоксами: " + string.Join(", ", disabledNames) +
                     " — в них не стучимся (chain.disabled); в цепи остались: " +
                     (enabled.Count == 0 ? "никто" : string.Join(", ", enabled.Select(p => p.Name))));
        if (enabled.Count == 0)
        {
            _log.LogError($"[chain] включённых звеньев нет (всего {total}) — ИИ-стадия не выполнена; " +
                          "решение: включить хотя бы одно звено чекбоксом на карточке цепи");
        }
    }
}
