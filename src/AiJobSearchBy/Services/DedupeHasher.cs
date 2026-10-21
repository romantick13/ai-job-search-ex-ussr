using System.Security.Cryptography;
using System.Text;

namespace AiJobSearchBy.Services;

// FLOW: вербатим-порт из Atlas/Atlas/Services/DedupeHasher.cs (исходник
// донора получен, Task 51): тело Compute — строка в строку донора,
// SHA256.Create + UTF-8 + Convert.ToHexString → hex В ВЕРХНЕМ регистре
// (это и есть семантика Atlas; прежний «строчный» вариант был
// непреднамеренным отклонением от донора и исправлен).
// Расширение канона architecture.md §3.3 / RFC-003 §2.8 — композитный
// ключ portal_id + external_id + canonical_url.
// ВАЖНО: хэш считается ДО нормализации текста (architecture.md §10) —
// агрегаторские обёртки (belmeta-класс) схлопываются по внешнему контенту.
// STATE: static-класс без полей — состояния нет.
// THREAD: SHA256.Create на каждый вызов (как в доноре) — без общего
// мутируемого состояния, потокобезопасен.
public static class DedupeHasher
{
    // Тело — строка в строку из Atlas (исходник донора, Task 51).
    public static string Compute(string input)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input)));
    }

    // НАШЕ расширение (в Atlas только Compute от URL; донора для композита нет).
    // '\n'-разделитель исключает склейки:
    // ("ab","c","u") ≠ ("a","bc","u"). externalId может отсутствовать —
    // участвует как пустая строка; canonicalUrl обязателен (NOT NULL схемы §4).
    // Значения идут в хэш КАК ЕСТЬ, без trim/нормализации — это семантика «ДО».
    public static string ComputeComposite(string portalId, string? externalId, string canonicalUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUrl);
        return Compute(string.Join('\n', portalId, externalId ?? string.Empty, canonicalUrl));
    }
}
