using AiJobSearchExUssr.Models;

namespace AiJobSearchExUssr.Services.Resume;

// ============================================================================

// релевантность вакансии строке запроса. Раньше ApplyVacancies применял
// только гео-фильтр — порталы-каталоги (и сайты, игнорирующие query)
// валили в окно всё подряд: саппорт-вакансии США по запросу «гардеробщик».

// Правила (простые и предсказуемые — тихой магии нет):
// одиночный запрос («гардеробщик», «SAP») -> хотя бы ОДНО
// слово запроса (длина >= 2, не стоп-слово) встречается в заголовке
// ИЛИ в тексте/компании вакансии (пример: «SAP» дал
// 0 из 20 на himalayas: слово только в описании, каталог-лента
// отдаёт 20 свежих вакансий без слова в заголовке; скрывать
// релевантное по тексту — хуже, чем показать слабо-релевантное);
// список ключевых через запятую («c#, sql, англ») -> считаем, сколько
// слов списка встретилось в заголовке + описании; порог
// max(2, 20% списка) — ниже порога вакансия скрыта;
// пустой запрос или «*» (каталог) -> не фильтруем.
// Каждое скрытие — строка в протоколе «[поиск] скрыл …», ни один тихий
// фильтр не разрешён (канон проекта).

// FLOW: MainViewModel.SearchAsync -> ApplyVacancies(postings, query).
// STATE: без состояния; нормализация — GeoMatcher.Normalize (регистр/ё).
// THREAD: потокобезопасен (только чтение).
// ============================================================================

public static class RelevanceFilter
{
    // Союзы/предлоги, по которым вакансию искать не надо.
    private static readonly HashSet<string> StopWords = new()
    {
        "и", "в", "на", "с", "по", "для", "от", "до", "или", "без", "the", "a", "an", "of", "and",
    };

    // Слова запроса: запятые и пробелы — разделители; нормализация регистр/ё.
    public static IReadOnlyList<string> Words(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim() == "*")
        {
            return Array.Empty<string>();
        }

        return query
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(GeoMatcher.Normalize)
            .Where(w => w.Length >= 2 && !StopWords.Contains(w))
            .Distinct()
            .ToList();
    }

    // Список ключевых (через запятую) ищем по заголовку+описанию с порогом;
    // одиночный запрос — заголовок ИЛИ текст вакансии .
    public static bool IsListQuery(string? query)
        => !string.IsNullOrWhiteSpace(query) && query.Contains(',');

    // Вердикт: true — вакансия показывается.
    public static bool Pass(JobPosting posting, string? query)
    {
        var words = Words(query);
        if (words.Count == 0)
        {
            return true; // пусто или «*» — каталог, релевантность не применяем
        }

        var title = GeoMatcher.Normalize(posting.Title);

        if (IsListQuery(query))
        {
            var hay = title + " " + GeoMatcher.Normalize(posting.DescriptionMd);
            var hits = words.Count(w => hay.Contains(w, StringComparison.Ordinal));
            var threshold = Math.Max(2, (int)Math.Ceiling(words.Count * 0.2));
            return hits >= threshold;
        }

        // заголовок сильнее, но слово, названное только в описании
        // или в компании («SAP Consultant» у Yopeso: SAP в тексте и фирме),
        // больше не прячет релевантное — himalayas/wwr/nodesk — каталог-ленты,
        // у которых заголовок не обязан содержать запрос.
        if (words.Any(w => title.Contains(w, StringComparison.Ordinal)))
        {
            return true;
        }

        var body = GeoMatcher.Normalize(
            $"{posting.Company} {posting.DescriptionMd}");
        return words.Any(w => body.Contains(w, StringComparison.Ordinal));
    }

    // Человекочитаемая причина для протокола: какие слова искали.
    public static string Reason(string? query)
        => IsListQuery(query)
            ? $"меньше {Math.Max(2, (int)Math.Ceiling(Words(query).Count * 0.2))} слов списка в тексте"
            : "ни одного слова запроса ни в заголовке, ни в тексте";
}
