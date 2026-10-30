using System.Security.Cryptography;
using System.Text;

namespace AiJobSearchBy.Services;

// FLOW: тело Compute — строка в строку
// SHA256.Create + UTF-8 + Convert.ToHexString → hex В ВЕРХНЕМ регистре
// (это и есть каноничная семантика; прежний «строчный» вариант был
// непреднамеренным отклонением и исправлен).
// Расширение канона architecture.md §3.3 — композитный
// ключ portal_id + external_id + canonical_url.
// ВАЖНО: хэш считается ДО нормализации текста (architecture.md §10) —
// агрегаторские обёртки (belmeta-класс) схлопываются по внешнему контенту.
// STATE: static-класс без полей — состояния нет.
// THREAD: SHA256.Create на каждый вызов — без общего
// мутируемого состояния, потокобезопасен.
public static class DedupeHasher
{
    // Тело — прямое хэширование строки: UTF-8 -> SHA256 -> hex.
    public static string Compute(string input)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input)));
    }

    // Наше расширение (в каноне только Compute от URL; композита там нет).
    // '\n'-разделитель исключает склейки:
    // ("ab","c","u") ≠ ("a","bc","u"). externalId может отсутствовать —
    // участвует как пустая строка; canonicalUrl обязателен (NOT NULL схемы базы).
    // Значения идут в хэш КАК ЕСТЬ, без trim/нормализации — это семантика «ДО».
    public static string ComputeComposite(string portalId, string? externalId, string canonicalUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUrl);
        return Compute(string.Join('\n', portalId, externalId ?? string.Empty, canonicalUrl));
    }


    // Живой кейс: админ-вакансия в 7 одинаковых экземплярах —
    // hh хранит перепосты одной вакансии
    // под разными id, а сеть филиалов — под именами «Логика молока. Логистика»,
    // «Логика молока. Офис». Ключ «одинаковости» для окна:
    // название + работодатель ДО ПЕРВОЙ ТОЧКИ (филиал не делает вакансию
    // новой). Нормализация: нижний регистр, ё→е, пунктуация/пробелы
    // схлопываются — «ООО „Ромашка“» == «ооо ромашка».
    // Пустое название — пустой ключ: дедупа нет (карточка без названия всё
    // равно мимо канона NOT NULL по URL). Локация в ключ НЕ входит:
    // «одинаковыми» считаются и перепосты в разных городах.

    public static string TitleKey(string? title, string? company)
    {
        var t = NormalizeKey(title);
        if (t.Length == 0)
        {
            return string.Empty;
        }

        // Филиал режется по точке в СЫРОЙ строке: NormalizeKey превращает
        // знаки в пробелы — после него точки уже нет («Логика молока.
        // Офис» стало бы «логика молока офис» и семья бы не схлопнулась).
        var raw = company?.Trim() ?? string.Empty;
        var dot = raw.IndexOf('.');
        if (dot > 0)
        {
            raw = raw[..dot];
        }

        return $"{t}|{NormalizeKey(raw)}";
    }

    // Нормализация ключа: буквы/цифры остаются, остальное — один пробел.
    private static string NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        var wasSeparator = false;
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch == 'ё' ? 'е' : ch))
            {
                sb.Append(ch == 'ё' ? 'е' : ch);
                wasSeparator = false;
            }
            else if (!wasSeparator && sb.Length > 0)
            {
                sb.Append(' ');
                wasSeparator = true;
            }
        }

        return sb.ToString().Trim();
    }
}
