using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Tests.Logging;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// ============================================================================
// Гейты фикса «FOREIGN KEY constraint
// failed» при крестике. Строка из живого прогона: «[крестик] удалить из базы не удалось —
// SqliteException: SQLite Error 19: FOREIGN KEY constraint failed» — вакансию,
// на которую уже отправляли отклик, нельзя было удалить: Applications.VacancyId
// держал FK без ON DELETE. Решение: FK -> ON DELETE SET NULL (свежая схема +
// миграция пересборкой для старых баз); строка истории откликов ПЕРЕЖИВАЕТ
// удаление вакансии (Employer/VacancyTitle/VacancyUrl свои).
// Сети и диспетчера нет — паттерн как в VacancyPersistenceTests.
// ============================================================================
public class VacancyDeleteFkTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private LogService Log() => new(_dir, echoToConsole: false);

    private static JobPosting Posting(string url, string title)
        => new()
        {
            PortalId = "hh-by",
            ExternalId = new string(url.Where(char.IsDigit).ToArray()),
            CanonicalUrl = url,
            Title = title,
            Company = "ООО Ромашка",
            Location = "Минск",
            DedupeHash = $"h-{url}",
            FetchedAt = DateTimeOffset.Now,
        };

    private static ApplicationRecord Application(Guid vacancyId, string title, string url)
        => new()
        {
            Id = Guid.NewGuid(),
            VacancyId = vacancyId,
            Status = "sent",
            Employer = "ООО Ромашка",
            VacancyTitle = title,
            VacancyUrl = url,
            ResumeText = "адаптированное резюме под вакансию",
            SentAt = DateTimeOffset.Now,
            CreatedAt = DateTimeOffset.Now,
        };

    [Fact]
    public void DeleteVacancy_WithSentApplication_NoForeignKeyError_HistorySurvives()
    {
        // Крестик по вакансии с отправленным откликом падал
        // SQLite Error 19. Теперь вакансия уходит, ссылка отпускается (SET NULL),
        // запись истории откликов остаётся читаемой.
        var store = new ApplicationStore(Path.Combine(_dir, "test.db"), Log());
        var p = Posting("https://rabota.by/vacancy/111", "SAP Basis administrator");
        store.UpsertVacancies([p]);
        var vacancyId = store.FindVacancyIdByHash(p.DedupeHash)
            ?? throw new InvalidOperationException("вакансия не попала в базу");
        store.SaveApplication(Application(vacancyId, p.Title!, p.CanonicalUrl));

        store.DeleteVacancy(p.DedupeHash); // раньше здесь был SqliteException 19

        Assert.DoesNotContain(store.RecentVacancies(), x => x.DedupeHash == p.DedupeHash);
        var app = Assert.Single(store.RecentApplications());
        Assert.Equal("SAP Basis administrator", app.VacancyTitle);
        Assert.Equal("ООО Ромашка", app.Employer);
        Assert.Equal("адаптированное резюме под вакансию", app.ResumeText);
        // Ссылка на удалённую вакансию отпущена: читается как «без вакансии».
        Assert.Equal(Guid.Empty, app.VacancyId);
    }

    [Fact]
    public void DeleteVacancy_FkEnforced_AfterPoolPoisonedWithForeignKeysOff()
    {
        // Пул Microsoft.Data.Sqlite сохраняет состояние PRAGMA на
        // физическом соединении между вызовами. Отравляем ИМЕННО тот bucket
        // пула, откуда магазин берёт соединения (его внутренняя строка
        // подключения — мостик ConnectionStringForTests), выключив в нём FK.
        // Крестик обязан отработать ЧЁТКО: SET NULL сработал => FK был включён.
        var store = new ApplicationStore(Path.Combine(_dir, "test.db"), Log());
        var p = Posting("https://rabota.by/vacancy/333", "DevOps engineer");
        store.UpsertVacancies([p]);
        var vacancyId = store.FindVacancyIdByHash(p.DedupeHash)
            ?? throw new InvalidOperationException("вакансия не попала в базу");
        store.SaveApplication(Application(vacancyId, p.Title!, p.CanonicalUrl));

        using (var poison = new SqliteConnection(store.ConnectionStringForTests))
        {
            poison.Open();
            poison.Execute("PRAGMA foreign_keys = OFF;");
        }

        store.DeleteVacancy(p.DedupeHash);

        var app = Assert.Single(store.RecentApplications());
        Assert.Equal(Guid.Empty, app.VacancyId); // NULL в базе — FK реально работал
        Assert.Equal("DevOps engineer", app.VacancyTitle);
        Assert.Equal("ООО Ромашка", app.Employer);
    }

    [Fact]
    public void FreshStore_Fk_IsSetNull_MigrationSkipped()
    {
        // Свежая база сразу создана с правильным FK — пересборка не нужна
        // (идемпотентность: второй Initialize ничего не ломает).
        var store = new ApplicationStore(Path.Combine(_dir, "test.db"), Log());
        _ = store.RecentVacancies(); // тянет Initialize
        _ = store.RecentVacancies(); // второй Initialize — без ошибок

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "test.db")}");
        conn.Open();
        // У Applications ровно один FK — на Vacancies(VacancyId); алиасы совпа-
        // дают с именами элементов кортежа (Dapper мапит по именам колонок).
        var fk = conn.QuerySingle<(string frm, string act)>(
            "SELECT \"from\" AS frm, on_delete AS act "
            + "FROM pragma_foreign_key_list('Applications')");
        Assert.Equal("VacancyId", fk.frm);
        Assert.Equal("SET NULL", fk.act);
    }

    [Fact]
    public void Initialize_MigratesOldApplicationsTable_DataSurvives_DeleteThenWorks()
    {
        // Старая база: Applications.VacancyId NOT NULL, FK без действия.
        // Новый ApplicationStore на том же файле обязан пересобрать таблицу,
        // сохранив строки; после пересборки крестик работает, история остаётся.
        var path = Path.Combine(_dir, "old.db");
        var log = Log();
        var store = new ApplicationStore(path, log);
        var p = Posting("https://rabota.by/vacancy/222", "Системный администратор");
        store.UpsertVacancies([p]);
        var vacancyId = store.FindVacancyIdByHash(p.DedupeHash)
            ?? throw new InvalidOperationException("вакансия не попала в базу");
        var record = Application(vacancyId, p.Title!, p.CanonicalUrl);
        store.SaveApplication(record);

        // Ломаем таблицу до старой формы (NOT NULL FK без действия).
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            conn.Execute("""
                PRAGMA foreign_keys = OFF;
                CREATE TABLE Applications_old (
                  Id TEXT PRIMARY KEY, VacancyId TEXT NOT NULL REFERENCES Vacancies(Id),
                  Status TEXT NOT NULL DEFAULT 'draft', CvPath TEXT, LetterPath TEXT,
                  SentAt TEXT, FollowUpAt TEXT, Notes TEXT, CreatedAt TEXT NOT NULL,
                  Employer TEXT, VacancyTitle TEXT, VacancyUrl TEXT, ResumeText TEXT,
                  VectorJson TEXT, VectorModel TEXT );
                INSERT INTO Applications_old SELECT * FROM Applications;
                DROP TABLE Applications;
                ALTER TABLE Applications_old RENAME TO Applications;
                PRAGMA foreign_keys = ON;
                """);
        }

        // Новый экземпляр стора — Initialize пересобирает FK (миграция).
        var migrated = new ApplicationStore(path, log);
        _ = migrated.RecentVacancies(); // тянет Initialize — пересборка
        _ = migrated.RecentVacancies(); // второй Initialize — УЖЕ БЕЗ пересборки
        Assert.Equal(1, log.Logs.Count(l => l.Contains("Applications пересобрана")));

        // Данные пережили пересборку, ссылка ещё жива (вакансию не удаляли).
        var app = Assert.Single(migrated.RecentApplications());
        Assert.Equal(record.Id, app.Id);
        Assert.Equal(vacancyId, app.VacancyId);

        // Крестик больше не падает: вакансия уходит, история остаётся.
        migrated.DeleteVacancy(p.DedupeHash);
        Assert.Empty(migrated.RecentVacancies());
        var kept = Assert.Single(migrated.RecentApplications());
        Assert.Equal("Системный администратор", kept.VacancyTitle);
        Assert.Equal(Guid.Empty, kept.VacancyId);
    }
}
