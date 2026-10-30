using System.Text;
using AiJobSearchBy.Services.Resume;

namespace AiJobSearchBy.ViewModels;

// ============================================================================
// Экспорт «в списке найденных вакансий —
// кнопка „экспорт“»: выгрузка видимых строк окна «Вакансии (перебор)» в CSV.
// Формат — под Excel без плясок: разделитель «;» (дефолт RU-локали), UTF-8
// с BOM (кириллица не ломается), кавычки по RFC 4180. Билдер чистый
// (без файловой системы) — юнит-тесты гоняют строку, файл пишет MainViewModel.
// FLOW: MainViewModel.ExportVacanciesCommand -> Build -> exports\*.csv.
// STATE: без состояния. THREAD: потокобезопасен (только чтение).
// ============================================================================
public static class VacancyCsvExport
{
    // Имя файла без пути: vacancies_2026-10-27_1432.csv (сортируется по дате).
    public static string FileName(DateTimeOffset now)
        => $"vacancies_{now:yyyy-MM-dd_HHmm}.csv";

    public static string Build(IReadOnlyList<VacancyRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Название;Работодатель;Локация;Зарплата;Режим;Гео;Совпадение, %;Ссылка;Сайт");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(";",
                Escape(row.Title),
                Escape(row.Posting.Company),
                Escape(row.Posting.Location),
                Escape(row.Salary),
                Escape(ModeOf(row.Remote)),
                Escape(row.GeoNote),
                row.MatchPercent is int pct ? pct.ToString() : "",
                Escape(row.Posting.CanonicalUrl),
                Escape(row.Site)));
        }

        return sb.ToString();
    }

    // Режим словами, без эмодзи бейджа — в Excel читается и фильтруется.
    private static string ModeOf(RemoteKind remote) => remote switch
    {
        RemoteKind.RemoteAllowed => "удалёнка",
        RemoteKind.OfficeOnly => "офис",
        _ => "гео не указано",
    };

    // RFC 4180: поле в кавычках, если содержит ; " или перевод строки;
    // кавычка внутри удваивается.
    private static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.Contains(';') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }
}
