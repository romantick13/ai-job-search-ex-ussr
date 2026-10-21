// FLOW: оркестратор отклика: BeginResponse -> Record на каждую стадию -> EndResponse -> [cost] в протокол (НИ-5)
// LIFECYCLE: синглтон DI; гранд-итоги «за процесс» копятся от старта, стадии — сброс на каждый отклик
// STATE: список стадий текущего отклика + гранд-итоги токенов/запросов
// THREAD: все обращения к состоянию — под _gate
using System;
using System.Collections.Generic;
using System.Linq;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// [НАШЕ] ResponseCostCounter — счётчик фактического расхода на один отклик.
// Основание — пункт 1 критики Мистраля (вставка владельца, 2026-10-21):
//   «Померить фактический расход — вы уже тянете лимиты ... в рантайме;
//   добавьте счётчик запросов на один отклик, и противоречие "15–20 vs 2–4"
//   решится само».
// После каждого отклика в протокол уходит строка [cost] — в окно И на диск
// (НИ-5). Стадии: match / resume / letter.
// ============================================================================

public sealed record StageCost(
    string Stage,
    string Provider,
    long PromptTokens,
    long CompletionTokens,
    int Requests,
    TimeSpan Duration);

public sealed class ResponseCostCounter
{
    private readonly object _gate = new();
    private readonly List<StageCost> _stages = new();
    private readonly ILogService _log;
    private int _responseNumber;
    private long _grandTokens;
    private long _grandRequests;

    public ResponseCostCounter(ILogService log) => _log = log;

    /// <summary>Начало отклика: копилки стадий очищаются, номер фиксируется.</summary>
    // FLOW: очистка копилок стадий + фиксация номера отклика (всё под _gate)
    public void BeginResponse(int responseNumber)
    {
        lock (_gate)
        {
            _stages.Clear();
            _responseNumber = responseNumber;
        }
    }

    /// <summary>Записать стадию. Для локального скоринга: токены 0, запросы 0.</summary>
    // FLOW: факт стадии -> StageCost в копилку под _gate; итог выйдет строкой [cost] в EndResponse
    // STATE: единственная мутируемая коллекция _stages, доступ только под _gate
    public void Record(string stage, string provider, long promptTokens, long completionTokens,
                       int requests, TimeSpan duration)
    {
        lock (_gate)
        {
            _stages.Add(new StageCost(stage, provider, promptTokens, completionTokens, requests, duration));
        }
    }

    /// <summary>
    /// Итог отклика: строка [cost] в протокол (окно+диск) и возвращается
    /// вызывающему (например, для экспорта).
    /// </summary>
    // FLOW: копилки -> строка [cost] в протокол (окно+диск, НИ-5) -> возврат вызывающему;
    //        гранд-итоги наращиваются здесь же (под _gate)
    public string EndResponse()
    {
        lock (_gate)
        {
            var perStage = string.Join("; ",
                _stages.Select(s => $"{s.Stage}={s.Provider}({s.PromptTokens + s.CompletionTokens}т/{s.Requests}з)"));
            var totTok = _stages.Sum(x => x.PromptTokens + x.CompletionTokens);
            var totReq = _stages.Sum(x => x.Requests);
            _grandTokens += totTok;
            _grandRequests += totReq;
            var line = $"[cost] отклик #{_responseNumber}: {perStage} → " +
                       $"итого {totTok} ток / {totReq} запр; за процесс: {_grandTokens} ток / {_grandRequests} запр";
            _log.LogSuccess(line);
            return line;
        }
    }
}
