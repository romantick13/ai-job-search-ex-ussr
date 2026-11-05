using System.Text;
using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services.Ai;

namespace AiJobSearchExUssr.Services;

// ============================================================================
// адаптация резюме под конкретную
// вакансию. Стадия ResumeAdaptation — ядро продукта (AiChatModels.cs): цепь
// идёт от сильных моделей к дешёвым (TaskRoutingPolicy.OrderFor), расход —
// в [cost] протокола провайдера.

// Контракт «окно с адаптированным резюме должно обновляться после
// отправки модели своего варианта резюме + требований работодателя». Промпт
// строится из базы резюме (SettingsKV resume.text) и полей JobPosting;
// описание вакансии обрезается до 6000 знаков — экономия дневных лимитов
// (llm7 100K ток/сутки, ModelScope 2000 запр/сутки).

// Язык адаптации = язык вакансии: если сама вакансия
// пишется на английском — адаптация на английском, если на русском —
// на русском. Язык вакансии определяется по тексту карточки (название +
// описание + компания): кого букв больше, тот и язык; вердикт идёт в промпт
// ЖЁСТКИМ правилом (модели любят отвечать языком резюме — правило повторено
// и в системе, и в задаче).

// Правда важнее подгонки: системный промпт прямо запрещает выдумывать опыт
// и навыки (защита от AI-галлюцинаций в резюме).

// РЕВЬЮ ПРОМПТА r22j (2026-11-05, по материалам канала Step Up Dev /
// @developerKiryl — команда JobJump, jobjump.app): hh.ru с октября подключил
// ИИ-скрининг откликов ДО рекрутера — ИИ сверяет резюме с требованиями,
// делит их на обязательные/желательные, при несоответствии обязательному
// роняет приоритет отклика и может задать до 5 уточняющих вопросов; из
// канала же — «правило трёх вопросов»: в резюме держи только то, о чём
// выдержишь три уточняющих «почему» подряд. Отсюда усиление промпта:
// подтверждать обязательные требования фактами (задача → действия →
// технологии → результат), честные пометки [нет подтверждения: …] вместо
// выдумывания, дословные названия технологий (ATS ищет точные совпадения),
// буллеты «что сделал → результат», совпадения с обязательными — выше.

// FLOW: MainViewModel (кнопка «Адаптировать») -> AdaptAsync ->
// TaskRoutingPolicy -> первый живой провайдер -> текст в окно
// «Адаптированное резюме» + PDF на диск.
// STATE: без состояния — потокобезопасен.
// THREAD: вызов с фонового потока (Task.Run в VM).
// ============================================================================
public sealed class ResumeAdaptationService
{
    public const int MaxVacancyDescriptionChars = 6000;

    private readonly ILogService _log;
    private readonly TaskRoutingPolicy _routing;

    public ResumeAdaptationService(ILogService log, TaskRoutingPolicy routing)
    {
        _log = log;
        _routing = routing;
    }

    // Язык адаптации : вакансия на английском — резюме на английском,
    // на русском — на русском. Счёт кириллицы против латыни по тексту
    // карточки (название + компания + описание). Порог — «латиницы втрое
    // больше кириллицы»: русская выдача hh насыщена латинскими
    // аббревиатурами (C#, MS SQL, VMware, Windows Server) — они НЕ должны
    // переворачивать язык (кириллица побеждает и при равенстве); а у
    // англоязычной вакансии кириллицы либо нет вовсе, либо только шум
    // наших шапок («Опыт…», «Выплаты…») — латыни заведомо втрое больше.
    // Ни букв вовсе (карточка из цифр) — русский дефолт (сайт русскоязычный).
    public static VacancyLanguage DetectLanguage(JobPosting posting)
    {
        var text = $"{posting.Title} {posting.Company} {posting.DescriptionMd}";
        var cyrillic = 0;
        var latin = 0;
        foreach (var ch in text)
        {
            if (ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё')
            {
                cyrillic++;
            }
            else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                latin++;
            }
        }

        if (cyrillic == 0 && latin == 0)
        {
            return VacancyLanguage.Russian;
        }

        return latin > cyrillic * 3 ? VacancyLanguage.English : VacancyLanguage.Russian;
    }

    public static (string SystemPrompt, string UserPrompt) BuildPrompt(
        string resume, JobPosting posting)
    {
        var language = DetectLanguage(posting);
        var answerLanguage = language == VacancyLanguage.English
            ? "английском"
            : "русском";

        var system =
            "Ты — карьерный консультант. Твоя задача: адаптировать резюме кандидата " +
            "под конкретную вакансию так, чтобы отклик прошёл ATS-фильтр и ИИ-скрининг " +
            "площадки (ИИ-скринер сравнивает резюме с требованиями ещё до рекрутера и " +
            "делит требования на обязательные и желательные). Правила: " +
            "(1) сохраняй факты и правду — не выдумывай опыт, навыки, даты и цифры; каждая " +
            "строка опыта должна выдержать три уточняющих вопроса подряд — конкретный " +
            "проект, роль, технологии, вклад и результат вместо общих «участвовал в " +
            "разработке»; " +
            "(2) сначала выдели обязательные требования вакансии и подтверди каждое явным " +
            "фактом из опыта кандидата (задача → твои действия → технологии → результат); " +
            "если подтверждения в резюме нет — не выдумывай, а оставь пометку в квадратных " +
            "скобках [нет подтверждения: требование] на месте этой строки; " +
            "(3) названия технологий и навыков пиши дословно так, как они написаны в " +
            "вакансии, там, где они честно соответствуют опыту (ATS ищет дословные " +
            "совпадения); " +
            "(4) пункты опыта переформулируй по схеме «что сделал (технологии) → " +
            "результат»; цифры переноси только из резюме, новые не добавляй; совпадения " +
            "с обязательными требованиями ставь выше остальных; " +
            "(5) верни ТОЛЬКО текст адаптированного резюме, без пояснений и " +
            "markdown-разметки заголовков; (6) язык ответа = язык вакансии: " +
            $"вакансия на английском — всё резюме на английском, вакансия на русском — " +
            $"всё резюме на русском. Сейчас вакансия требует ответ на {answerLanguage} языке.";

        var sb = new StringBuilder();
        sb.AppendLine("РЕЗЮМЕ КАНДИДАТА:");
        sb.AppendLine(resume.Trim());
        sb.AppendLine();
        sb.AppendLine("ВАКАНСИЯ:");
        sb.AppendLine($"Название: {posting.Title ?? "—"}");
        sb.AppendLine($"Компания: {posting.Company ?? "—"}");
        sb.AppendLine($"Локация: {posting.Location ?? "—"}");
        sb.AppendLine($"Зарплата: {posting.SalaryText ?? "—"}");
        sb.AppendLine($"Ссылка: {posting.CanonicalUrl}");
        sb.AppendLine($"ЯЗЫК ВАКАНСИИ: {(language == VacancyLanguage.English ? "английский" : "русский")} " +
                      $"— адаптированное резюме написать на {answerLanguage} языке полностью " +
                      "(и заголовки, и опыт, и навыки).");
        sb.AppendLine("Описание:");
        var description = posting.DescriptionMd?.Trim() ?? "—";
        if (description.Length > MaxVacancyDescriptionChars)
        {
            description = description[..MaxVacancyDescriptionChars] + "… (обрезано)";
        }
        sb.AppendLine(description);
        sb.AppendLine();
        sb.Append("Задача: адаптируй резюме под эту вакансию. Если резюме пустое — верни " +
                  "скелет резюме под вакансию с пометками в квадратных скобках, что кандидату " +
                  "нужно заполнить.");
        return (system, sb.ToString());
    }

    public async Task<AiChatResult?> AdaptAsync(string resume, JobPosting posting,
        CancellationToken ct = default)
    {
        var (system, user) = BuildPrompt(resume, posting);
        var language = DetectLanguage(posting);
        _log.Log($"[adapt] адаптация резюме: вакансия «{posting.Title ?? posting.CanonicalUrl}», " +
                 $"язык вакансии: {(language == VacancyLanguage.English ? "английский — адаптирую на английском" : "русский — адаптирую на русском")}, " +
                 $"резюме {resume.Length} зн., описание {Math.Min(posting.DescriptionMd?.Length ?? 0, MaxVacancyDescriptionChars)} зн.");
        return await _routing.RunAsync(AiTaskKind.ResumeAdaptation,
            new AiChatRequest(system, user, MaxTokens: 2048, Temperature: 0.3), ct)
            .ConfigureAwait(false);
    }
}

// язык адаптации резюме — по языку вакансии.
public enum VacancyLanguage
{
    Russian,
    English,
}
