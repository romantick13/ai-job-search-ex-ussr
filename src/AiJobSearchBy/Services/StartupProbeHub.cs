using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AiJobSearchBy.Services;

// ============================================================================
// StartupProbeHub: мост между
// фоновой пробой каталогов на старте (Program.ProbeCatalogsInBackground) и
// лампами карточек ИИ-цепи (MainViewModel.Providers).

// Задача: агрегаторы проверяются на старте, исход виден не только
// в протоколе — лампочки
// на плашках загораются после успеха (зелёные) и после неудачи (красные),
// а не только после принудительной проверки по кнопке.

// FLOW: Program.cs публикует исход каждой пробы (Publish) -> VM, уже живая,
// получает событие и красит лампу; исходы, пришедшие ДО создания окна,
// VM дочитывает при старте (Snapshot) — гонка старт/проба закрыта.
// STATE: ConcurrentDictionary имя-звена -> исход; событие без очереди —
// подписчик сам маршалит в UI-поток (RunOnUi у VM).
// THREAD: Publish — из фоновой пробы; Snapshot — из UI-потока при старте VM.
// ============================================================================
public static class StartupProbeHub
{
    // Исходы проб, пришедшие до создания окна (VM дочитывает их при старте)
    private static readonly ConcurrentDictionary<string, bool> Results = new();

    // / <summary>(имя звена, ок) — после каждого исхода стартовой пробы.</summary>
    public static event Action<string, bool>? Finished;

    // / <summary>Публикация исхода пробы звена (Program.cs, фоновый поток).</summary>
    public static void Publish(string name, bool ok)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Results[name] = ok;
        Finished?.Invoke(name, ok);
    }

    // / <summary>Снимок исходов на момент вызова (VM при старте; уже прошедшие пробы).</summary>
    public static IReadOnlyDictionary<string, bool> Snapshot() => Results;
}
