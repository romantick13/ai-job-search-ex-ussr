using AiJobSearchBy.Models;
using AiJobSearchBy.Services;
using AiJobSearchBy.Tests.Logging;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiJobSearchBy.Tests;

// ============================================================================
// Поведение крестика: удаление — только «с морды», из базы строка не исчезает.
// Крестик ПЕРЕНОСИТ строку Vacancies -> DismissedVacancies целиком
// (данные сохраняются), UpsertVacancies не возвращает удалённое, повторное
// удаление обновляет снапшот, камень для неизвестного хэша тоже ставится.
// Сети нет — LogService во temp-каталоге (как в VacancyPersistenceTests).
// ============================================================================
public class DeletedVacanciesTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private (ApplicationStore Store, LogService Log) Store()
    {
        var log = new LogService(_dir, echoToConsole: false);
        var store = new ApplicationStore(Path.Combine(_dir, "test.db"), log);
        return (store, log);
    }

    private static JobPosting Posting() => new()
    {
        PortalId = "hh-by",
        ExternalId = "138718332",
        CanonicalUrl = "https://rabota.by/vacancy/138718332",
        Title = "Ведущий администратор сетей",
        Company = "ОАО АЛЕВКУРП",
        Location = "деревня Королёв Стан, Московская улица, 1А",
        SalaryText = "3 594 – 3 990 Br за месяц, до вычета налогов",
        DescriptionMd = "Опыт 3-6 лет\nВыплаты: Два раза в месяц",
        DedupeHash = DedupeHasher.ComputeComposite(
            "hh-by", "138718332", "https://rabota.by/vacancy/138718332"),
        FetchedAt = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero),
    };

    // Прямое чтение таблицы удалённых (у магазина нет читателя — данные
    // проверяются на месте, как их оставит DELETE-перенос).
    private (string? PortalId, string? CanonicalUrl, string? Company,
        string? Salary, string? Description) ReadDeletedRow(
        string dbPath, string dedupeHash)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT PortalId, CanonicalUrl, Company, SalaryText, DescriptionMd
            FROM DismissedVacancies WHERE DedupeHash = @h
            """;
        cmd.Parameters.AddWithValue("@h", dedupeHash);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null, null, null, null);
        }

        return (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    [Fact]
    public void DeleteVacancy_MovesFullRow_ToDeletedTable()
    {
        var (store, _) = Store();
        var p = Posting();
        store.UpsertVacancies([p]);

        store.DeleteVacancy(p.DedupeHash);

        // С «морды» строка ушла...
        Assert.DoesNotContain(store.RecentVacancies(), x => x.DedupeHash == p.DedupeHash);
        // ...но в таблице удалённых — ПОЛНЫЙ снапшот (из базы НЕ удалять).
        var dbPath = store.DbPath;
        var (portalId, url, company, salary, description) = ReadDeletedRow(dbPath, p.DedupeHash);
        Assert.Equal("hh-by", portalId);
        Assert.Equal("https://rabota.by/vacancy/138718332", url);
        Assert.Equal("ОАО АЛЕВКУРП", company);
        Assert.Equal("3 594 – 3 990 Br за месяц, до вычета налогов", salary);
        Assert.Equal("Опыт 3-6 лет\nВыплаты: Два раза в месяц", description);
        Assert.Equal(p.DedupeHash, Assert.Single(store.DismissedHashes()));
    }

    [Fact]
    public void NewSearch_SkipsDeleted_WithHonestLog()
    {
        // При новом поиске сравнивать найденные не только с имеющимися,
        // но и с удалёнными — и строка исхода в протоколе.
        var (store, log) = Store();
        var p = Posting();
        store.UpsertVacancies([p]);
        store.DeleteVacancy(p.DedupeHash);

        // Новый поиск нашёл то же самое — в базу НЕ вернёт (дедуп с удалёнными).
        Assert.Equal(0, store.UpsertVacancies([p]));
        Assert.DoesNotContain(store.RecentVacancies(), x => x.DedupeHash == p.DedupeHash);
        Assert.Contains(log.Logs, l => l.Contains("совпавших с таблицей удалённых"));
    }

    [Fact]
    public void DeleteTwice_AfterReinsert_RefreshesSnapshot()
    {
        // Вакансия вернулась в базу прямым INSERT (сценарий имитируется обходом
        // дедупа с удалёнными — так режим ON CONFLICT проверяется изолированно);
        // юзер снова жмёт крестик — снапшот в удалённых обновляется
        // свежими данными (ON CONFLICT DO UPDATE), а не остаётся старым.
        var (store, _) = Store();
        var p = Posting();
        store.UpsertVacancies([p]);
        store.DeleteVacancy(p.DedupeHash);

        // Прямая вставка той же вакансии (обход дедупа с удалёнными).
        using (var conn = new SqliteConnection($"Data Source={store.DbPath}"))
        {
            conn.Open();
            conn.Execute("""
                INSERT INTO Vacancies
                  (Id, PortalId, ExternalId, CanonicalUrl, Title, Company, Location,
                   SalaryText, DescriptionMd, SourceClass, AccessKind, Quality,
                   DedupeHash, FetchedAt, Status)
                VALUES (@Id, @PortalId, @ExternalId, @CanonicalUrl, @Title, @Company, @Location,
                   @SalaryText, @DescriptionMd, 'Commercial', 'Scrape', 'verified',
                   @DedupeHash, @FetchedAt, 'New')
                """, new
            {
                Id = Guid.NewGuid().ToString(),
                p.PortalId,
                p.ExternalId,
                p.CanonicalUrl,
                p.Title,
                p.Company,
                p.Location,
                SalaryText = "3 700 – 4 100 Br за месяц, до вычета налогов",
                p.DescriptionMd,
                p.DedupeHash,
                FetchedAt = DateTimeOffset.Now.ToString("O"),
            });
        }

        store.DeleteVacancy(p.DedupeHash);

        var (_, _, _, salary, _) = ReadDeletedRow(store.DbPath, p.DedupeHash);
        Assert.Equal("3 700 – 4 100 Br за месяц, до вычета налогов", salary);
    }

    [Fact]
    public void DeleteVacancy_UnknownHash_PlacesTombstone()
    {
        // Строки в базе уже нет (или хэш чужой) — камень всё равно ставится:
        // юзер сказал «удалить», будущие поиски хэш не вернут.
        var (store, _) = Store();

        store.DeleteVacancy("unknown-hash");

        Assert.Contains("unknown-hash", store.DismissedHashes());
        var (_, url, _, _, _) = ReadDeletedRow(store.DbPath, "unknown-hash");
        Assert.Null(url); // пустая строка-камень: данных не было
    }
}
