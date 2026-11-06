using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// ChainRuntime — живое состояние ИИ-цепи (r25).
// ДО r25: список звеньев запекался в DI при старте, а чекбоксы карточек
// влияли только на «Проверить всех» — реальный запрос уходил и в
// отключённое звено тоже (расхождение с README/PRIVACY, найдено
// 2026-11-06, вопрос владельца «отключит все наши и оставит свою»).
// r25 — цепь честная: это то, что юзер оставил включённым.
// • чекбоксы карточек глушат звенья для ВСЕХ ИИ-стадий: какие остались —
//   в такие и стучимся; пропуски кричат WARN в протокол;
// • правки настроек (добавил/удалил/поправил кастомное звено) применяются
//   на лету — Rebuild() без перезапуска приложения;
// • выбор чекбоксов персистится в SettingsKV "chain.disabled" — переживает
//   перезапуск (раньше писала VM в обход цепи, теперь единый источник
//   правды — здесь).
// STATE: единственная мутируемая ячейка — снапшот (все звенья + отключённые
// LinkId), меняется атомарно под замком; HashSet после подмены никем не
// правится — читатели делят ссылку как immutable.
// THREAD: потокобезопасен — запросы идут с фоновых потоков, чекбоксы и
// настройки правятся с UI-потока.
// ============================================================================

public sealed class ChainRuntime
{
    // Звено цепи: провайдер + идентификатор чекбокса.
    // Встроенные: LinkId = имя провайдера ("llm7", "lm-studio", ...).
    // Кастомные: LinkId = Id записи Chain.Custom (стабилен при переименовании;
    // имя провайдера = имя звена из настроек — так видно в протоколе).
    public sealed record ChainEntry(string LinkId, IAiChatProvider Provider);

    private const string DisabledKey = "chain.disabled";

    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<ChainEntry>> _source;
    private readonly ILogService _log;
    private readonly IApplicationStore? _store;

    private (IReadOnlyList<ChainEntry> All, HashSet<string> Disabled) _state;

    // FLOW: старт — собрать полный список звеньев из источника, восстановить
    // выбор чекбоксов из SettingsKV, честно сказать в протоколе, кто в цепи.
    // LIFECYCLE: синглтон DI (CompositionRoot); source — замыкание на полный
    // список (встроенные DI + кастомные из local.json), вызывается на старте
    // и на каждом Rebuild.
    public ChainRuntime(ILogService log, Func<IReadOnlyList<ChainEntry>> source,
        IApplicationStore? store = null)
    {
        _log = log;
        _source = source;
        _store = store;
        _state = (source(), new HashSet<string>());

        // Выбор чекбоксов переживает перезапуск: JSON-массив LinkId.
        try
        {
            var raw = _store?.GetSetting(DisabledKey);
            if (!string.IsNullOrWhiteSpace(raw)
                && JsonSerializer.Deserialize<string[]>(raw) is { Length: > 0 } ids)
            {
                lock (_gate)
                {
                    _state = (_state.All, new HashSet<string>(ids));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: выбор чекбоксов цепи не восстановлен — причина: {ex.Message}; " +
                         "решение: переключить любой чекбокс — выбор перезапишется");
        }

        var (all, disabled) = _state;
        var enabledCount = all.Count(e => !disabled.Contains(e.LinkId));
        _log.Log(disabled.Count == 0
            ? $"[chain] цепь собрана: {all.Count} звеньев, все включены"
            : $"[chain] цепь собрана: {all.Count} звеньев, включено {enabledCount}, " +
              $"отключены чекбоксами: {OffNames(all, disabled)}");
    }

    // Пересборка на лету: подхватить звенья, добавленные/удалённые/правленные
    // в настройках — без перезапуска приложения. Вызывается из
    // SettingsViewModel.Save после записи Chain.Custom в local.json.
    public void Rebuild()
    {
        var fresh = _source();
        lock (_gate)
        {
            var before = _state.All.Count;
            _state = (fresh, _state.Disabled);
            _log.Log($"[chain] цепь пересобрана на лету: {fresh.Count} звеньев (было {before}) — " +
                     "правки настроек применились без перезапуска");
        }
    }

    // Чекбоксы карточек: передаём список выключенных LinkId. Runtime сам
    // пишет chain.disabled в SettingsKV (переживает перезапуск) и кричит
    // в протокол, кто остался в цепи.
    public void SetDisabled(IEnumerable<string>? linkIds)
    {
        var ids = new HashSet<string>(linkIds ?? Array.Empty<string>());
        lock (_gate)
        {
            _state = (_state.All, ids);
        }

        var (all, _) = ReadState();
        var enabledCount = all.Count(e => !ids.Contains(e.LinkId));
        _log.Log(enabledCount == all.Count
            ? $"[chain] чекбоксы: в цепи все {all.Count} звеньев"
            : $"[chain] чекбоксы: в цепи {enabledCount} из {all.Count}; " +
              $"отключены: {OffNames(all, ids)} — в них не стучимся");
        Persist(ids);
    }

    // Снимок для роутинга (TaskRoutingPolicy): включённые провайдеры +
    // имена отключённых (для честного WARN) + всего звеньев. Один вызов —
    // один согласованный срез: без гонок между фильтром и отчётом.
    public (IReadOnlyList<IAiChatProvider> Enabled,
            IReadOnlyList<string> DisabledNames,
            int Total) Snapshot()
    {
        lock (_gate)
        {
            var (all, disabled) = _state;
            var enabled = all.Where(e => !disabled.Contains(e.LinkId))
                .Select(e => e.Provider).ToList();
            var off = all.Where(e => disabled.Contains(e.LinkId))
                .Select(e => e.Provider.Name).ToList();
            return (enabled, off, all.Count);
        }
    }

    private (IReadOnlyList<ChainEntry> All, HashSet<string> Disabled) ReadState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    private void Persist(HashSet<string> ids)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            _store.SetSetting(DisabledKey, JsonSerializer.Serialize(ids.ToArray()));
        }
        catch (Exception ex)
        {
            _log.LogError($"[chain] выбор чекбоксов не сохранён: {ex.Message}; " +
                          "решение: переключить чекбокс ещё раз — состояние перезапишется");
        }
    }

    private static string OffNames(IReadOnlyList<ChainEntry> all, HashSet<string> disabled)
        => all.Where(e => disabled.Contains(e.LinkId)).Select(e => e.Provider.Name)
            .DefaultIfEmpty("нет").Aggregate((a, b) => $"{a}, {b}");
}
