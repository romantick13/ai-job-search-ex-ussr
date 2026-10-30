using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// LocalMatchScorer — замена ИИ-оценки соответствия: ИИ-запросы на матч
// — самая дорогая трата бюджета цепи, их снимает локальный скоринг.
// V1: ключевые слова (покрытие вакансийных токенов токенами резюме).
// 0 ИИ-запросов, 0 токенов. Эмбеддинги — точка расширения: сигнатура
// Score(...) и вердикт не меняются, меняется реализация.
// Пороги: >=70 высокий, >=40 средний, ниже — низкий (конфигурируются).
// ============================================================================

public sealed record LocalMatchVerdict(
    int Score,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Missing)
{
    public string Level => Score >= 70 ? "высокий" : Score >= 40 ? "средний" : "низкий";
}

public sealed class LocalMatchScorer
{
    private static readonly Regex TokenRx = new(@"[a-z0-9а-яё#+.]{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Мини-стоп-лист ru/en: служебные и шумовые слова не участвуют в покрытии.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "и","в","на","с","со","для","от","до","по","за","из","у","же","не","что","это","как","но","или","к","о","об","при","без","через","мы","вы","наш","ваш","будет","есть","быть","или",
        "the","a","an","and","or","of","to","in","for","with","on","at","by","is","are","be","as","we","you","our","your","will","from","that","this","have","has","were","was",
        "опыт","работа","работы","лет","год","года","компания","компании","требования","требованию","навыки","навык","знание","знания","обязанности","умение","кандидат","кандидата",
        "вакансия","вакансии","резюме","experience","years","year","work","working","team","role","job","jobs","skills","skill","requirements","requirement","strong","plus","ability","etc"
    };

    private readonly ILogService _log;
    private readonly int _highThreshold;
    private readonly int _mediumThreshold;

    // LIFECYCLE: синглтон DI; состояния между вызовами нет
    // THREAD: безопасен — стоп-лист и regex static readonly, остальное — локальные переменные
    public LocalMatchScorer(ILogService log, int highThreshold = 70, int mediumThreshold = 40)
    {
        _log = log;
        _highThreshold = highThreshold;
        _mediumThreshold = mediumThreshold;
    }

    // / <summary>
    // / Оценка соответствия резюме вакансии. Каждый вызов протоколируется
    // / строкой [scorer] с вердиктом и нулевым расходом (действие=событие).
    // / </summary>
    // FLOW: оркестратор отклика -> Score(резюме, вакансия) -> LocalMatchVerdict + строка [scorer] в протокол;
    // два текста -> токенизация (стоп-лист) -> покрытие вакансийных токенов резюменными
    // -> процент -> уровень (пороги ctor) -> [scorer] в протокол -> вердикт
    // STATE: мутируемого нет — всё локальные переменные (детерминированный порядок вывода)
    public LocalMatchVerdict Score(string resumeText, string vacancyText)
    {
        var sw = Stopwatch.StartNew();
        var resume = Tokens(resumeText);
        // Детерминированный порядок: длинные токены (технологии) первыми в списках.
        var vacancy = Tokens(vacancyText).OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).ToList();
        var matched = new List<string>();
        var missing = new List<string>();
        foreach (var t in vacancy)
        {
            if (resume.Contains(t)) matched.Add(t);
            else missing.Add(t);
        }
        var score = vacancy.Count == 0
            ? 0
            : (int)Math.Round(100.0 * matched.Count / vacancy.Count);
        var level = score >= _highThreshold ? "высокий"
                  : score >= _mediumThreshold ? "средний" : "низкий";
        sw.Stop();
        _log.LogSuccess($"[scorer] локальная оценка соответствия: {score}/100 ({level}) — " +
                        $"совпало {matched.Count}/{vacancy.Count}; недостаёт: {Top(missing)}; " +
                        $"расход: 0 ИИ-запросов, 0 токенов, {sw.Elapsed.TotalMilliseconds:F0}мс");
        return new LocalMatchVerdict(score, matched, missing);
    }

    private static HashSet<string> Tokens(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (Match m in TokenRx.Matches(text.ToLowerInvariant()))
        {
            var t = m.Value.Trim('.'); // ".net" → "net", "c#"/"c++" сохраняются
            if (t.Length < 2 || StopWords.Contains(t)) continue;
            set.Add(t);
        }
        return set;
    }

    private static string Top(List<string> items)
    {
        if (items.Count == 0) return "—";
        var head = string.Join(", ", items.Take(8));
        return items.Count > 8 ? $"{head} (+{items.Count - 8})" : head;
    }
}
