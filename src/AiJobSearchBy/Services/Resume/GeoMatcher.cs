using System.Text.RegularExpressions;
using AiJobSearchBy.Models;

namespace AiJobSearchBy.Services.Resume;

// ============================================================================
// Гео-фильтр поиска «страна / город» с логикой офис/удалёнка. Формула:
// у офисной вакансии с указанным городом гео обязано совпасть (удалёнка
// отпадает — не совпало, значит не показываем); «может быть удалёнка» —
// подходит вообще всё.

// Матрица:
// введено ничего -> весь мир (всё проходит)
// только страна -> удалёнка всегда; офисная — страна совпала
// город (+ страна опционально)-> удалёнка всегда; офисная — город совпал
// (и страна, если задана)
// гео у вакансии неизвестно -> показывается (серой) — чекбокс «скрывать
// без гео» прячет; явное несовпадение —
// прячется всегда
// Гибрид = «может быть удалёнка» -> подходит всегда (RemoteAllowed).

// удалёнка с ЯВНО названной страной пряталась, если страна не та
// («Remote — USA» при фильтре «Беларусь/Минск»), поэтому стран в списке —
// ВСЕ члены ООН (CountryTable, 195): пустой фильтр не режет легальные
// страны. Дефолт списка разрешённых стран — Беларусь + Россия
// (GeoMatcher.DefaultAllowedCountries, применяется в MainViewModel):
// поиск «SAP администратор Минск» без дефолта давал Ташкент/Алматы/Воронеж.
// Названные города (Ташкент, Алматы…) тоже матчатся
// в страны — таблица CityHints. Приоритет локации: страна/город из
// Location+Title АВТОРИТЕТНА — разрешена => описание не переспрашиваем
// («офисы в Ташкенте» у минской вакансии больше не прячут вакансию).

// FLOW: MainViewModel.ApplyVacancies -> DetectRemote + Match -> VacancyRow.
// STATE: без состояния; таблица стран — CountryTable (только чтение).
// THREAD: потокобезопасен (только чтение).
// ============================================================================

// Режим работы вакансии по признакам в тексте.
public enum RemoteKind
{
    // Нет ни локации, ни маркеров удалёнки — честное «гео не указано»
    // (UI рисует серым, чекбокс может спрятать).
    Unknown,

    // «Может быть удалёнка» — по формуле подходит всегда.
    RemoteAllowed,

    // Явное «в офисе / on-site» без маркеров удалёнки — гео обязано совпасть.
    OfficeOnly,
}

// Введённый пользователем фильтр (поля над строкой поиска; null/пусто = не задано).
public sealed record GeoFilter(string? Country, string? City)
{
    public static readonly GeoFilter None = new(null, null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Country) && string.IsNullOrWhiteSpace(City);
}

// Вердикт для строки списка: Pass — показывать ли; Note — пояснение в строке;
// UnknownGeo — «гео не указано» (UI рисует серым, чекбокс может спрятать).
public sealed record GeoVerdict(bool Pass, bool UnknownGeo, string Note)
{
    public static GeoVerdict Ok(string note) => new(true, false, note);
    public static GeoVerdict Fail(string note) => new(false, false, note);
    public static GeoVerdict Unknown(string note) => new(true, true, note);
}

public static class GeoMatcher
{
    // --- маркеры удалёнки (Ru/En; порталы кладут режим в шапку/описание) ---
    private static readonly Regex RemoteRx = new(
        @"удал[её]н|дистанц|из дома|домашн\w*\s*(офис|работа)|\bremote\b|\bwfh\b|work\s*from\s*home|anywhere|\bworldwide\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Явное «в офисе» без удалёнки: office/on-site/в офисе/в нашем офисе.
    private static readonly Regex OfficeRx = new(
        @"в\s+офисе|офисн|\bon[\s-]?site\b|в\s+нашем\s+офисе|только\s+офис",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Многословные города — только в PhraseCanon (скан слов их не видит).
    // ОБЪЯВЛЕНИЕ ДО WordCanon/PhraseCanon: статические поля инициализируются
    // по порядку деклараций — BuildPhraseCanon читает эту таблицу при старте.
    private static readonly (string Phrase, string Canon)[] CityPhrases =
    {
        ("санкт петербург", "россия"), ("нижний новгород", "россия"),
        ("нижнем новгороде", "россия"), ("ростов на дону", "россия"),
        ("нью йорк", "сша"), ("лос анджелес", "сша"), ("сан франциско", "сша"),
        ("тель авив", "израиль"),
    };


    // словари стран из CountryTable. Алиасы и канон стемуются
    // (концовка «ия/я/а/ь» отрезается у слов длиной >= 5 — «германия» ->
    // «германи»): падежи «Германии/германский» ловятся префиксом слова,
    // короткие (сша/чад/мали) — точным равенством слова. Фразы
    // (united states, new zealand…) ищутся подстрокой в тексте с уже
    // выброшенной пунктуацией (en-фразы не склоняются).

    private static string Stem(string alias)
    {
        if (alias.Length >= 5 && (alias.EndsWith("ия") || alias.EndsWith("я")
                || alias.EndsWith("а") || alias.EndsWith("ь")))
        {
            return alias[..^1];
        }

        return alias;
    }

    // Пунктуация -> пробел: «Warsaw, Poland» -> «warsaw poland» (фразы и
    // слова совпадают без оглядки на запятые/скобки). Для detect-хэев.
    private static string Prep(string? text) => Regex.Replace(
        Normalize(text), @"[^\p{L}\s]", " ").Trim();

    // Однословные стемы -> канон (включая канон-имена и exact-алиасы).
    private static readonly IReadOnlyDictionary<string, (string Canon, bool Exact)> WordCanon =
        BuildWordCanon();

    // Многословные алиасы -> канон (сравнение подстрокой Prep-текста).
    private static readonly IReadOnlyDictionary<string, string> PhraseCanon =
        BuildPhraseCanon();

    private static IReadOnlyDictionary<string, (string Canon, bool Exact)> BuildWordCanon()
    {
        var map = new Dictionary<string, (string, bool)>();
        void Add(string alias, string canon, bool exact)
        {
            // Exact-страны (куба/мали/нигер/чад) — точные формы БЕЗ стеминга:
            // защита от «малина/кубанцы/чадный», падежи — вручную в таблице.
            if (exact)
            {
                if (!map.ContainsKey(alias))
                {
                    map[alias] = (canon, true);
                }

                return;
            }

            var stem = Stem(alias);
            // Коллизии стемов: первая запись таблицы выигрывает (стабильно).
            if (!map.ContainsKey(stem))
            {
                map[stem] = (canon, false);
            }
        }

        foreach (var entry in CountryTable.Entries)
        {
            Add(entry.Canon, entry.Canon, entry.Exact);
            foreach (var alias in entry.Aliases)
            {
                if (alias.Contains(' '))
                {
                    continue; // фразы — в PhraseCanon
                }

                Add(alias, entry.Canon, entry.Exact);
            }
        }

        // Разговорные/городские добавки сверх таблицы (алиасы-предшественники).
        Add("минск", "беларусь", exact: false); // столицу часто пишут вместо страны
        Add("белорус", "беларусь", exact: false); // «белорусский/белоруссия» — префиксом
        Add("рб", "беларусь", exact: true);
        Add("рф", "россия", exact: true);
        Add("америка", "сша", exact: false);
        Add("северокорее", "кндр", exact: false);
        AddCityHints(map);
        return map;
    }


    // Живой кейс hh-выдачи: в
    // локации ГОРОД без страны («Ташкент», «Алматы») — карта городов ->
    // канон страны. Только проверенные стемы без ложных префиксов
    // («самар»/«перм»-класс конфликтов нет: Самарканд и Пермь — точными
    // формами или исключены). Пополняется руками, по одному городу.

    private static void AddCityHints(Dictionary<string, (string, bool)> map)
    {
        void Add(string alias, string canon, bool exact = false)
        {
            var stem = Stem(alias);
            if (!map.ContainsKey(stem))
            {
                map[stem] = (canon, exact);
            }
        }

        void AddExact(string canon, params string[] forms)
        {
            foreach (var form in forms)
            {
                if (!map.ContainsKey(form))
                {
                    map[form] = (canon, true);
                }
            }
        }

        // Узбекистан/Казахстан/Киргизия/Таджикистан/Туркменистан (утечка hh).
        AddExact("узбекистан", "ташкент", "ташкенте", "ташкента", "ташкенту");
        Add("алматы", "казахстан", exact: true);
        AddExact("казахстан", "астана", "астане", "астану", "караганда", "караганде", "шымкент", "шымкенте");
        AddExact("киргизия", "бишкек", "бишкеке", "бишкека");
        AddExact("таджикистан", "душанбе", "душанбе");
        AddExact("туркменистан", "ашхабад", "ашхабаде", "ашхабада");

        // Закавказье/Молдова/Украина/Прибалтика.
        AddExact("грузия", "тбилиси", "батуми");
        AddExact("армения", "ереван", "ереване", "еревана");
        AddExact("азербайджан", "баку");
        AddExact("молдова", "кишинев", "кишиневе", "кишинева");
        AddExact("украина", "киев", "киеве", "киева", "харьков", "харькове",
            "одесса", "одессе", "днепр", "днепре", "львов", "львове",
            "запорожье", "винница", "полтава");
        AddExact("латвия", "рига", "риге");
        AddExact("литва", "вильнюс", "вильнюсе");
        AddExact("эстония", "таллин", "таллине", "таллина");

        // Беларусь (город = страна — как «Минск» выше).
        Add("гомель", "беларусь");
        Add("гродно", "беларусь");
        Add("брест", "беларусь");
        Add("витебск", "беларусь");
        Add("могилев", "беларусь");

        // Россия (разрешена дефолтом; пригодится, если её вычеркнут из допустимых).
        Add("москва", "россия");
        AddExact("россия", "москве", "москву", "москвой");
        Add("домодедово", "россия");
        Add("воронеж", "россия");
        Add("казань", "россия");
        Add("краснодар", "россия");
        Add("екатеринбург", "россия");
        Add("новосибирск", "россия");
        Add("самара", "россия"); // стем «самар» — поэтому Самарканд ниже точной формой
        Add("ростов", "россия"); // «ростов-на-дону» ловится словом «ростов»
        AddExact("россия", "спб", "питер");
        AddExact("узбекистан", "самарканд", "самарканде"); // бьёт стем «самар» по длине

        // Европа/Америка/Азия — крупные города (пишутся и без страны).
        Add("варшава", "польша");
        Add("краков", "польша");
        Add("вроцлав", "польша");
        Add("берлин", "германия");
        Add("мюнхен", "германия");
        Add("гамбург", "германия");
        Add("франкфурт", "германия");
        Add("париж", "франция");
        Add("амстердам", "нидерланды");
        Add("брюссель", "бельгия");
        Add("лондон", "великобритания");
        Add("мадрид", "испания");
        Add("барселона", "испания");
        Add("милан", "италия");
        Add("вена", "австрия");
        Add("стокгольм", "швеция");
        Add("копенгаген", "дания");
        Add("стамбул", "турция");
        Add("дубай", "оаэ", exact: true);
        Add("лимассол", "кипр");
        Add("бостон", "сша");
        Add("чикаго", "сша");
        Add("сиэтл", "сша");
        Add("майами", "сша");
        Add("торонто", "канада");
        Add("ванкувер", "канада");
        Add("монреаль", "канада");
    }

    // Многословные города (см. CityPhrases выше) — источник уже перенесён
    // наверх ради порядка статической инициализации.

    private static IReadOnlyDictionary<string, string> BuildPhraseCanon()
    {
        var map = new Dictionary<string, string>();
        foreach (var entry in CountryTable.Entries)
        {
            // Канон-фразы (южная корея, новая зеландия…) — тоже фразы:
            // ru-название в им.падеже матчится без словаря.
            if (entry.Canon.Contains(' '))
            {
                map[entry.Canon] = entry.Canon;
            }

            foreach (var alias in entry.Aliases)
            {
                if (alias.Contains(' '))
                {
                    map[alias] = entry.Canon;
                }
            }
        }

        // многословные города (санкт-петербург, нью-йорк…) — Prep
        // выбрасывает дефисы, фразы совпадают без оглядки на пунктуацию.
        foreach (var (phrase, canon) in CityPhrases)
        {
            if (!map.ContainsKey(phrase))
            {
                map[phrase] = canon;
            }
        }

        return map;
    }

    // Человекочитаемые имена канонов — для строки вердикта («страна другая (США)»)
    // и списка стран в настройках.
    public static string DisplayOf(string canon)
        => CountryTable.Entries.FirstOrDefault(e => e.Canon == canon)?.Display ?? canon;

    // Все display-имена (для текстового списка стран в настройках, ).
    public static IReadOnlyList<string> AllDisplays()
        => CountryTable.Entries.Select(e => e.Display).ToList();

    // display/канон -> канон (парсинг списка стран из настроек).
    public static string? CanonOfDisplay(string display)
    {
        var wanted = Normalize(display);
        if (wanted.Length == 0)
        {
            return null;
        }

        return CountryTable.Entries
            .FirstOrDefault(e => e.Canon == wanted || Normalize(e.Display) == wanted)?.Canon
            ?? wanted; // незнакомое — оставляем как есть (юзер ввёл вручную)
    }

    // --- детект стран в тексте вакансии (локация/заголовок/описание).
    // Возвращает ВСЕ найденные (региональные списки «Poland or Lithuania» —
    // реальность WWR): ForeignCountry прячет, если хоть одна вычеркнута.
    public static IReadOnlySet<string> DetectCountries(string? hay)
    {
        var text = Prep(hay);
        var found = new HashSet<string>();
        if (text.Length == 0)
        {
            return found;
        }

        foreach (var (phrase, canon) in PhraseCanon)
        {
            if (text.Contains(phrase, StringComparison.Ordinal))
            {
                found.Add(canon);
            }
        }

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var (stem, (canon, exact)) in WordCanon)
            {
                // Не-exact короткие стемы (<4: сша, рб, рф, uk, уae) — только
                // точное слово, иначе «сшатель» ловил бы США префиксом.
                var hit = exact
                    ? word == stem
                    : stem.Length >= 4
                        ? word.StartsWith(stem, StringComparison.Ordinal)
                        : word == stem;
                if (hit)
                {
                    found.Add(canon);
                }
            }
        }

        return found;
    }

    // Наиболее конкретная страна текста (длиннейший стем) — для офисной
    // ветки CountryMatches, где страна одна.
    public static string? DetectCountry(string? hay)
    {
        var text = Prep(hay);
        if (text.Length == 0)
        {
            return null;
        }

        string? best = null;
        var bestLen = -1;
        foreach (var (phrase, canon) in PhraseCanon)
        {
            if (text.Contains(phrase, StringComparison.Ordinal) && phrase.Length + 2 > bestLen)
            {
                best = canon;
                bestLen = phrase.Length + 2; // фразы приоритетнее своих слов
            }
        }

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var (stem, (canon, exact)) in WordCanon)
            {
                var hit = exact
                    ? word == stem
                    : stem.Length >= 4
                        ? word.StartsWith(stem, StringComparison.Ordinal)
                        : word == stem;
                if (hit && stem.Length > bestLen)
                {
                    best = canon;
                    bestLen = stem.Length;
                }
            }
        }

        return best;
    }

    // Дефолт в списке
    // разрешённых стран — Беларусь и Россия; остальное (Ташкент, Алматы…)
    // прячется. Полный список («Вернуть все страны») = весь мир.
    public static IReadOnlySet<string> DefaultAllowedCountries()
        => new HashSet<string>(["беларусь", "россия"]);

    // + + страна удалёнки прячется, если allowed-набор
    // задан и названа страна НЕ из него. Порядок проверки :
    // 1) Location+Title — АВТОРИТЕТНАЯ локация (карточка hh показывает
    // город выдачи; WWR — страны в заголовке). Названа и разрешена ->
    // ПУСК: описание не переспрашиваем («офисы в Ташкенте» у минской
    // вакансии не повод прятать).
    // 2) Локация молчит -> шапка описания (первые 400 знаков):
    // «Remote — USA», региональные списки «Poland or Lithuania».
    // allowed == null/пусто (весь мир) -> ничего не прячем.
    public static string? ForeignCountry(JobPosting posting, GeoFilter filter,
        IReadOnlySet<string>? allowed = null)
    {
        if (allowed is not { Count: > 0 })
        {
            return null;
        }

        var located = DetectCountries($"{posting.Location} {posting.Title}");
        if (located.Count > 0)
        {
            foreach (var detected in located)
            {
                if (!allowed.Contains(detected))
                {
                    return DisplayOf(detected);
                }
            }

            return null;
        }

        var desc = posting.DescriptionMd?.Length > 400
            ? posting.DescriptionMd[..400]
            : posting.DescriptionMd;
        foreach (var detected in DetectCountries(desc))
        {
            if (!allowed.Contains(detected))
            {
                return DisplayOf(detected);
            }
        }

        return null;
    }

    // Ё -> е, нижний регистр, схлопывание пробелов — норма для сравнений.
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'ё' => 'е',
                '\u00a0' or '\u2007' or '\u202f' => ' ',
                _ => ch,
            });
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    // Признак «удалёнка»: заголовок+локация + шапка описания (первые 1200
    // знаков — маркеры режима живут там). Приоритет: удалёнка бьёт «офис» —
    // по формуле лучше пропустить, чем спрятать. Дальше явное «в офисе»;
    // маркеров нет, но ЛОКАЦИЯ указана — это вакансия по месту (офис):
    // «Минск, Минский район» не имеет права называться «гео не указано»
    // (плашка «?» висела на каждой строке кроме удалёнки — 2026-10-29).
    // «Неизвестно» остаётся только для строк вовсе без локации.
    public static RemoteKind DetectRemote(JobPosting posting)
    {
        var head = $"{Normalize(posting.Title)} {Normalize(posting.Location)}";
        var description = Normalize(posting.DescriptionMd);
        if (description.Length > 1200)
        {
            description = description[..1200];
        }

        if (RemoteRx.IsMatch(head) || RemoteRx.IsMatch(description))
        {
            return RemoteKind.RemoteAllowed;
        }

        // Явный офис ищем и в шапке, и в шапке описания («Работа в офисе»).
        if (OfficeRx.IsMatch(head) || OfficeRx.IsMatch(description))
        {
            return RemoteKind.OfficeOnly;
        }

        return string.IsNullOrWhiteSpace(posting.Location)
            ? RemoteKind.Unknown
            : RemoteKind.OfficeOnly;
    }

    // Страна совпала: детект страны в локации vs введённая (алиасы/стем).
    public static bool CountryMatches(string? location, string? countryInput)
    {
        var wanted = Normalize(countryInput);
        if (wanted.Length == 0)
        {
            return true;
        }

        var detected = DetectCountry(location);
        if (detected is not null)
        {
            return detected == CanonOfInput(countryInput);
        }

        // Не из словаря: подстрока в обе стороны («Минск» <-> «Минску»).
        return TwoWayContains(Normalize(location), wanted);
    }

    // Город совпал: подстрока в обе стороны (падежи: «Минск»/«в Минске»).
    public static bool CityMatches(string? location, string? cityInput)
    {
        var wanted = Normalize(cityInput);
        if (wanted.Length == 0)
        {
            return true;
        }

        return TwoWayContains(Normalize(location), wanted);
    }

    private static bool TwoWayContains(string hay, string needle)
    {
        if (hay.Length == 0 || needle.Length == 0)
        {
            return false;
        }

        return hay.Contains(needle, StringComparison.Ordinal)
               || needle.Contains(hay, StringComparison.Ordinal);
    }

    // Ввод юзера -> канон страны: точное имя/алиас; незнакомое — как есть.
    private static string CanonOfInput(string? input)
    {
        var wanted = Normalize(input);
        if (wanted.Length == 0)
        {
            return wanted;
        }

        // Фраза целиком.
        if (PhraseCanon.TryGetValue(wanted, out var canon))
        {
            return canon;
        }

        // Точное слово / стем слова.
        var words = Prep(wanted).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1 && WordCanon.TryGetValue(words[0], out var hit))
        {
            return hit.Canon;
        }

        if (words.Length == 1)
        {
            foreach (var (stem, (c, _)) in WordCanon)
            {
                if (stem.Length >= 4 && words[0].StartsWith(stem, StringComparison.Ordinal))
                {
                    return c;
                }
            }
        }

        return wanted;
    }

    // Главная функция: матрица . hideUnknown — чекбокс
    // «скрывать вакансии без гео» (по умолчанию выключен: лучше увидеть,
    // чем пропустить — так и пишет протокол). allowed — разрешённые страны
    // (null/пусто = весь мир, ; дефолт Беларусь + Россия — ).
    public static GeoVerdict Match(JobPosting posting, GeoFilter filter,
        RemoteKind remote, bool hideUnknown = false, IReadOnlySet<string>? allowed = null)
    {
        // чужая страна (не из разрешённых) прячется у ЛЮБОГО режима —
        // удалёнка, офис, «режим не распознан» — и независимо от заполненности
        // гео-полей. Раньше у удалёнок это работало только при
        // заполненных полях «Страна/Город», и Ташкент с Алматы протекали в
        // окно. allowed == null (весь мир) — ничего не прячем, как и раньше.
        // Страна из локации АВТОРИТЕТНА (ForeignCountry): минская вакансия с
        // «офисами в Ташкенте» в описании не прячется.
        var foreign = ForeignCountry(posting, filter, allowed);
        if (foreign is not null)
        {
            return remote == RemoteKind.RemoteAllowed
                ? GeoVerdict.Fail($"удалёнка, но страна не в списке ({foreign})")
                : GeoVerdict.Fail($"страна не в списке ({foreign})");
        }

        // Правило «может быть удалёнка — тогда подходит вообще всё».
        if (remote == RemoteKind.RemoteAllowed)
        {
            return GeoVerdict.Ok("удалёнка — подходит всегда");
        }

        if (filter.IsEmpty)
        {
            return remote == RemoteKind.Unknown
                ? GeoVerdict.Unknown("весь мир — гео не проверялось")
                : GeoVerdict.Ok("весь мир");
        }

        var location = $"{posting.Location} {posting.Title}";
        var wantCountry = Normalize(filter.Country);
        var wantCity = Normalize(filter.City);

        if (remote == RemoteKind.Unknown)
        {
            // Гео/режим неизвестны: это не «не совпало», а «неизвестно» —
            // показываем серой, чекбокс решает (чужая страна уже отсеяна выше).
            if (hideUnknown)
            {
                return GeoVerdict.Fail("гео не указано — скрыто чекбоксом");
            }

            return GeoVerdict.Unknown("гео не указано — показываю по умолчанию");
        }

        // --- OfficeOnly: строго по матрице ---
        if (wantCity.Length > 0)
        {
            if (wantCountry.Length > 0 && !CountryMatches(location, filter.Country))
            {
                return GeoVerdict.Fail($"страна не совпала ({filter.Country})");
            }

            return CityMatches(location, filter.City)
                ? GeoVerdict.Ok($"гео ✓ {filter.City}")
                : GeoVerdict.Fail($"город не совпал ({filter.City})");
        }

        // Только страна.
        return CountryMatches(location, filter.Country)
            ? GeoVerdict.Ok($"гео ✓ {filter.Country}")
            : GeoVerdict.Fail($"страна не совпала ({filter.Country})");
    }
}
