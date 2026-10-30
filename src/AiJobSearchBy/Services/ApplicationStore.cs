using System.Globalization;
using System.IO;
using AiJobSearchBy.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiJobSearchBy.Services;

// ============================================================================
// база откликов — SQLite по канону
// architecture.md §4 (SQLite + Dapper, без EF; идемпотентные миграции
// CREATE TABLE IF NOT EXISTS + PRAGMA table_info). Таблицы Vacancies и
// Applications — 1:1 со схемой базы; недостающие поля истории откликов (Employer,
// VacancyTitle, VacancyUrl, ResumeText, векторные ссылки) доливаются
// EnsureColumn-миграциями — канон прямо разрешает расширение колонок.
// PdfDocuments — таблица «векторных ссылок на PDF»: файлы остаются на
// диске, база хранит путь + текст + вектор.
// Processes — журнал процессов («подбор хвостов», r20): поиск вакансий и
// адаптация резюме пишут старт с скользящим Id и финал; строки, оставшиеся
// 'running' после обрыва (интернет/питание/закрыли окно), закрывает при
// старте SweepStaleProcesses.
// SettingsKV — назначение: текст резюме, выбранные сайты, папка откликов.

// FLOW: CompositionRoot -> IApplicationStore (синглтон) -> Initialize при
// первом обращении; строки [db] — в протокол.
// STATE: путь базы передаётся снаружи (%APPDATA%\ai-job-search-by\data\
// ai-job-search-by.db; тесты — temp); Initialize одноразовый.
// THREAD: одна запись SQLite в момент времени — все операции под lock;
// соединение открывается на вызов и закрывается (using).
// ============================================================================

// История откликов: то, что пользователь видит в окне истории, и то, что
// разворачивается в модальном окне при клике (вариант резюме + PDF-путь).
public sealed record ApplicationRecord
{
    public Guid Id { get; init; }
    public Guid VacancyId { get; init; }
    public string Employer { get; init; } = "";
    public string? VacancyTitle { get; init; }
    public string VacancyUrl { get; init; } = "";
    public string Status { get; init; } = "sent";
    public string? CvPath { get; init; }
    public string? LetterPath { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    // Вариант резюме для отклика (модальное окно истории): текст,
    // из которого собран PDF; правится в окне отклика, копируется в буфер.
    public string? ResumeText { get; init; }

    // Векторная ссылка: JSON-массив float локального эмбеддера
    // (null — векторизация выключена/рантайм не отвечает); имя модели —
    // рядом в VectorModel.
    public string? VectorJson { get; init; }
    public string? VectorModel { get; init; }
}

// Строка индекса PDF: файл живёт на диске в папке откликов, база — его ссылка.
public sealed record PdfDocumentRecord
{
    public Guid Id { get; init; }
    public string FilePath { get; init; } = "";
    public string FileHash { get; init; } = "";
    public string? ExtractedText { get; init; }
    public string? VectorJson { get; init; }
    public string? VectorModel { get; init; }
    public DateTimeOffset IndexedAt { get; init; }
}

// Журнал процессов («подбор хвостов», r20): каждый долгий процесс —
// поиск вакансий, адаптация резюме — при старте получает скользящий Id
// и строку Status='running' в таблице Processes, при финише закрывается
// статусом done/failed/interrupted. Строки, оставшиеся 'running' после
// обрыва (приложение закрыли, комп выключили, интернет упал), при
// следующем старте закрывает SweepStaleProcesses — хвостов не остаётся.
public sealed record ProcessRunRecord
{
    public Guid Id { get; init; }
    public string Kind { get; init; } = "";   // search | adapt
    public string? Detail { get; init; }      // запрос поиска / «работодатель — вакансия»
    public string Status { get; init; } = ""; // running | done | failed | interrupted
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

public interface IApplicationStore
{
    string DbPath { get; }

    // Одноразовые идемпотентные миграции; вызывается лениво при первом обращении.
    void Initialize();

    // Vacancies: INSERT OR IGNORE по UNIQUE DedupeHash; EnsureVacancy
    // возвращает Id строки (вставленной или уже существующей).
    // записи с «крестиком» (DismissedVacancies) в базу не возвращаются.
    int UpsertVacancies(IReadOnlyList<JobPosting> items);
    Guid EnsureVacancy(JobPosting item);
    Guid? FindVacancyIdByHash(string dedupeHash);

    // r20: полный текст вакансии со страницы вакансии — в кэш:
    // DescriptionMd перезаписывается, DetailFetched = 1 (адаптация второй
    // раз в сеть за той же вакансией не ходит). true — строка обновлена,
    // false — вакансии с таким DedupeHash в базе уже нет (крестик).
    bool UpdateVacancyDetail(string dedupeHash, string descriptionMd);

    // Персистентность: вакансии живут
    // в базе — при старте возвращаются в окно (свежие первыми).
    IReadOnlyList<JobPosting> RecentVacancies(int limit = 400);

    // Крестик у строки: DELETE по DedupeHash + «камень» в
    // DismissedVacancies — юзер решил, и при следующих поисках вакансия
    // НЕ возвращается (ни в окно, ни в базу). false — строки уже не было
    // (камень всё равно ставится). Удаление честное, без статуса-зомби.
    bool DeleteVacancy(string dedupeHash);

    // Список «крестиков» (DedupeHash) — MainViewModel держит его в памяти
    // и не пускает помеченные вакансии ни в окно, ни в базу.
    IReadOnlySet<string> DismissedHashes();

    // Applications (канон + EnsureColumn-расширения).
    void SaveApplication(ApplicationRecord record);
    IReadOnlyList<ApplicationRecord> RecentApplications(int limit = 50);
    ApplicationRecord? GetApplication(Guid id);

    // Точечное обновление отправленного отклика:
    // правленный текст резюме + пересобранный PDF. Возвращает false, если
    // строки с таким Id нет (вызывающий код обязан отреагировать — контракт
    // «никаких тихих игноров»).
    bool UpdateApplicationResume(Guid id, string resumeText, string? cvPath);

    // PdfDocuments: индекс PDF-файлов (векторные ссылки).
    IReadOnlyList<PdfDocumentRecord> ListIndexedPdfs();
    void SavePdfIndex(PdfDocumentRecord record);

    // Журнал процессов (r20): долгие операции (поиск, адаптация) пишут в
    // базу старт со скользящим Id и закрывают его на финише. Crash-safe:
    // строка, оставшаяся 'running' после обрыва, закрывается при следующем
    // старте SweepStaleProcesses — «подбор хвостов».
    Guid StartProcess(string kind, string? detail);
    void FinishProcess(Guid id, string status);

    // Последние записи журнала (диагностика и тесты; UI не читает).
    IReadOnlyList<ProcessRunRecord> RecentProcesses(int limit = 50);

    // «Подбор хвостов» при старте: все 'running' → 'interrupted'
    // (+ FinishedAt); возвращает закрытые строки — вызывающий логирует
    // каждую (тихих исходов нет). Идемпотентно: второй вызов вернёт пусто.
    IReadOnlyList<ProcessRunRecord> SweepStaleProcesses();

    // SettingsKV: персональные настройки вне репо (текст резюме, сайты, папка).
    string? GetSetting(string key);
    void SetSetting(string key, string value);
}

public sealed class ApplicationStore : IApplicationStore
{
    // Точка данных канона: личные данные — только вне репо (architecture §8).
    // Каталог данных один: %USERPROFILE%\ai-job-search-by\data\ (раньше
    // тут был %APPDATA% — второй путь снесён, переносит LegacyPathMigrator).
    public static string DefaultDataDir
    {
        get
        {
            var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
            return Path.Combine(rooted ? profileDir : AppContext.BaseDirectory,
                "ai-job-search-by", "data");
        }
    }

    // Папка откликов по умолчанию: адаптированные резюме — рядом с EXE,
    // подкаталог resume/;
    // раньше — Документы\ai-job-search-by (старые файлы переносит
    // LegacyPathMigrator, пути в базе правит он же).
    public static string DefaultOutboxDir
        => Path.Combine(AppContext.BaseDirectory, "resume");

    // Прежний дефолт — для распознавания «унаследованной» настройки
    // outbox.path в базе и старых значений в конфиге.
    public static string LegacyOutboxDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ai-job-search-by");

    // Общий резолвер папки откликов: пусто или старое значение → новый дефолт;
    // кастомный путь юзера — как есть. Один шов для MainViewModel,
    // SettingsViewModel, DocxEditWindow и PdfIndexerService (CompositionRoot).
    public static string ResolveOutbox(string? configured)
        => string.IsNullOrWhiteSpace(configured)
            ? DefaultOutboxDir
            : IsLegacyOutbox(configured!)
                ? DefaultOutboxDir
                : configured!.Trim();

    private static bool IsLegacyOutbox(string path)
        => string.Equals(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            LegacyOutboxDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly ILogService _log;
    private bool _initialized;

    public string DbPath { get; }

    public ApplicationStore(string dbPath, ILogService log)
    {
        DbPath = dbPath;
        _log = log;
        // FK включаем ЯВНО в строке подключения. По умолчанию
        // Microsoft.Data.Sqlite включает FK, но полагаться на умолчание —
        // хрупко: состояние PRAGMA живёт на физическом соединении и
        // ПРОТЕКАЕТ ЧЕРЕЗ ПУЛ (соединение уходит в пул с чужим состоянием).
        // Явный флаг + PRAGMA в Open ниже делают enforcement детерминированным.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    // Мостик для юнит-тестов (InternalsVisibleTo AiJobSearchBy.Tests):
    // тест пула-протечек FK должен отравить ИМЕННО тот bucket пула,
    // откуда магазин берёт соединения — то есть ту же строку подключения.
    internal string ConnectionStringForTests => _connectionString;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            using var conn = Open();
            conn.Execute("PRAGMA journal_mode=WAL;");

// Схема — architecture.md §4 дословно; расширения Applications — EnsureColumn ниже.
            conn.Execute("""
                CREATE TABLE IF NOT EXISTS Vacancies (
                  Id TEXT PRIMARY KEY, PortalId TEXT NOT NULL, ExternalId TEXT,
                  CanonicalUrl TEXT NOT NULL, Title TEXT, Company TEXT, Location TEXT,
                  SalaryText TEXT, DescriptionMd TEXT, SourceClass TEXT NOT NULL,
                  AccessKind TEXT NOT NULL, Quality TEXT NOT NULL DEFAULT 'verified',
                  DedupeHash TEXT NOT NULL UNIQUE, FetchedAt TEXT NOT NULL,
                  Status TEXT NOT NULL DEFAULT 'new',
                  DetailFetched INTEGER NOT NULL DEFAULT 0 );
                CREATE TABLE IF NOT EXISTS Applications (
                  Id TEXT PRIMARY KEY,
                  -- FK с ON DELETE SET NULL — крестик по вакансии с
                  -- отправленными откликами больше не падает FOREIGN KEY,
                  -- история откликов остаётся (Employer/VacancyTitle свои).
                  VacancyId TEXT REFERENCES Vacancies(Id) ON DELETE SET NULL,
                  Status TEXT NOT NULL DEFAULT 'draft', CvPath TEXT, LetterPath TEXT,
                  SentAt TEXT, FollowUpAt TEXT, Notes TEXT, CreatedAt TEXT NOT NULL );
                CREATE TABLE IF NOT EXISTS PdfDocuments (
                  Id TEXT PRIMARY KEY, FilePath TEXT NOT NULL UNIQUE, FileHash TEXT NOT NULL,
                  ExtractedText TEXT, VectorJson TEXT, VectorModel TEXT, IndexedAt TEXT NOT NULL );
                CREATE TABLE IF NOT EXISTS Processes (
                  Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, Detail TEXT,
                  Status TEXT NOT NULL, StartedAt TEXT NOT NULL, FinishedAt TEXT );
                CREATE TABLE IF NOT EXISTS SettingsKV (
                  Key TEXT PRIMARY KEY, Value TEXT NOT NULL );
                """);

// «Крестики» юзера — вакансии, удалённые из окна намеренно.
// INSERT OR IGNORE по первичному ключу — идемпотентно.
            conn.Execute("""
                CREATE TABLE IF NOT EXISTS DismissedVacancies (
                  DedupeHash TEXT PRIMARY KEY, Title TEXT, DismissedAt TEXT NOT NULL );
                """);

            // EnsureColumn-миграции: недостающие колонки (таблица — параметр:
            // добавил колонку и в Vacancies — проверять надо ЕЁ).
            foreach (var (table, column, ddl) in new[]
                     {
                         ("Applications", "Employer", "ALTER TABLE Applications ADD COLUMN Employer TEXT"),
                         ("Applications", "VacancyTitle", "ALTER TABLE Applications ADD COLUMN VacancyTitle TEXT"),
                         ("Applications", "VacancyUrl", "ALTER TABLE Applications ADD COLUMN VacancyUrl TEXT"),
                         ("Applications", "ResumeText", "ALTER TABLE Applications ADD COLUMN ResumeText TEXT"),
                         ("Applications", "VectorJson", "ALTER TABLE Applications ADD COLUMN VectorJson TEXT"),
                         ("Applications", "VectorModel", "ALTER TABLE Applications ADD COLUMN VectorModel TEXT"),
                         // вакансия «hh сам подобрал» (блок «Вам подойдут»),
                         // из базы строка НЕ удаляется — крестик только переносит её
                         // в таблицу удалённых: DismissedVacancies становится ПОЛНЫМ
                         // хранилищем удалённых — все колонки Vacancies доливаются,
                         // крестик ПЕРЕНОСИТ строку, данные сохраняются.
                         // Идемпотентно (EnsureColumn канона).
                         ("Vacancies", "IsSimilar", "ALTER TABLE Vacancies ADD COLUMN IsSimilar INTEGER NOT NULL DEFAULT 0"),
                         // r20: полный текст вакансии со страницы вакансии закэширован
                         // в DescriptionMd (адаптация второй раз в сеть не ходит).
                         ("Vacancies", "DetailFetched", "ALTER TABLE Vacancies ADD COLUMN DetailFetched INTEGER NOT NULL DEFAULT 0"),
                         ("DismissedVacancies", "DetailFetched", "ALTER TABLE DismissedVacancies ADD COLUMN DetailFetched INTEGER"),
                         ("DismissedVacancies", "PortalId", "ALTER TABLE DismissedVacancies ADD COLUMN PortalId TEXT"),
                         ("DismissedVacancies", "ExternalId", "ALTER TABLE DismissedVacancies ADD COLUMN ExternalId TEXT"),
                         ("DismissedVacancies", "CanonicalUrl", "ALTER TABLE DismissedVacancies ADD COLUMN CanonicalUrl TEXT"),
                         ("DismissedVacancies", "Company", "ALTER TABLE DismissedVacancies ADD COLUMN Company TEXT"),
                         ("DismissedVacancies", "Location", "ALTER TABLE DismissedVacancies ADD COLUMN Location TEXT"),
                         ("DismissedVacancies", "SalaryText", "ALTER TABLE DismissedVacancies ADD COLUMN SalaryText TEXT"),
                         ("DismissedVacancies", "DescriptionMd", "ALTER TABLE DismissedVacancies ADD COLUMN DescriptionMd TEXT"),
                         ("DismissedVacancies", "SourceClass", "ALTER TABLE DismissedVacancies ADD COLUMN SourceClass TEXT"),
                         ("DismissedVacancies", "AccessKind", "ALTER TABLE DismissedVacancies ADD COLUMN AccessKind TEXT"),
                         ("DismissedVacancies", "Quality", "ALTER TABLE DismissedVacancies ADD COLUMN Quality TEXT"),
                         ("DismissedVacancies", "FetchedAt", "ALTER TABLE DismissedVacancies ADD COLUMN FetchedAt TEXT"),
                         ("DismissedVacancies", "Status", "ALTER TABLE DismissedVacancies ADD COLUMN Status TEXT"),
                         ("DismissedVacancies", "IsSimilar", "ALTER TABLE DismissedVacancies ADD COLUMN IsSimilar INTEGER"),
                     })
            {
                if (!ColumnExists(conn, table, column))
                {
                    conn.Execute(ddl);
                    _log.Log($"[db] схема {table} дополнена колонкой {column} (миграция)");
                }
            }

            // миграция FK Applications.VacancyId -> ON DELETE SET NULL
            // (после EnsureColumn: переносим таблицу уже в полном каноне колонок).
            EnsureApplicationVacancyFkSetNull(conn);

            _initialized = true;
            _log.Log($"[db] база откликов готова: {DbPath}");
        }
    }


    // Крестик по вакансии с отправленным откликом ловил
    // SqliteException «SQLite Error 19: FOREIGN KEY constraint failed»:
    // у баз, созданных раньше этого фикса, Applications.VacancyId был NOT NULL
    // с FK без действия — крестик по вакансии, на которую уже отправляли
    // отклик, падал FOREIGN KEY. SQLite не умеет ALTER CONSTRAINT — канонич-
    // ная пересборка таблицы: VacancyId допускает NULL, FK — ON DELETE SET
    // NULL: вакансия уходит, строка истории ОСТАЁТСЯ (Employer/VacancyTitle/
    // VacancyUrl свои — в модалке истории всё читается).
    // Идемпотентно: у свежих баз FK уже SET NULL — пересборки нет.

    private void EnsureApplicationVacancyFkSetNull(SqliteConnection conn)
    {
        var fk = conn.Query<FkRow>("PRAGMA foreign_key_list(Applications)").ToList();
        if (fk.Any(f => f.table == "Vacancies" && f.from == "VacancyId"
                        && string.Equals(f.on_delete, "SET NULL", StringComparison.OrdinalIgnoreCase)))
        {
            return; // уже пересобрано (или свежая база)
        }

        // Переносим ВСЕ существующие колонки старой базы (EnsureColumn выше
        // уже довёл их до канона, но возраст базы не предполагаем).
        var cols = conn.Query<(int cid, string name)>("PRAGMA table_info(Applications)")
            .Select(c => c.name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        const string CanonColumns =
            "Id, VacancyId, Status, CvPath, LetterPath, SentAt, FollowUpAt, Notes, "
            + "CreatedAt, Employer, VacancyTitle, VacancyUrl, ResumeText, VectorJson, VectorModel";

        // PRAGMA foreign_keys не действует внутри транзакции — выключаем ДО,
        // включаем после; соединение закрывается вызывавшим (using снаружи).
        conn.Execute("PRAGMA foreign_keys = OFF;");
        using var tx = conn.BeginTransaction();
        try
        {
            conn.Execute($"""
                CREATE TABLE Applications_fk17 (
                  Id TEXT PRIMARY KEY,
                  VacancyId TEXT REFERENCES Vacancies(Id) ON DELETE SET NULL,
                  Status TEXT NOT NULL DEFAULT 'draft', CvPath TEXT, LetterPath TEXT,
                  SentAt TEXT, FollowUpAt TEXT, Notes TEXT, CreatedAt TEXT NOT NULL,
                  Employer TEXT, VacancyTitle TEXT, VacancyUrl TEXT, ResumeText TEXT,
                  VectorJson TEXT, VectorModel TEXT );
                """, transaction: tx);
            foreach (var col in CanonColumns.Split(", ")
                         .Where(c => !cols.Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                // У совсем старой базы нет колонок — добавляем пустыми.
                conn.Execute($"ALTER TABLE Applications_fk17 ADD COLUMN {col} TEXT", transaction: tx);
            }

            var colList = string.Join(", ", cols);
            conn.Execute(
                $"INSERT INTO Applications_fk17 ({colList}) SELECT {colList} FROM Applications",
                transaction: tx);
            conn.Execute("DROP TABLE Applications", transaction: tx);
            conn.Execute("ALTER TABLE Applications_fk17 RENAME TO Applications", transaction: tx);
            tx.Commit();
            _log.Log("[db] Applications пересобрана: FK VacancyId теперь "
                     + "ON DELETE SET NULL — крестик не падает FOREIGN KEY, история "
                     + "откликов остаётся");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            conn.Execute("PRAGMA foreign_keys = ON;");
        }
    }

    // Строка PRAGMA foreign_key_list (имена колонок — как в SQLite, Dapper
    // матчит по имени; используем только нужные поля).
    private sealed class FkRow
    {
        public string table { get; set; } = "";
        public string from { get; set; } = "";
        public string on_delete { get; set; } = "";
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
        => conn.Query<(int cid, string name)>($"PRAGMA table_info({table})")
            .Any(c => string.Equals(c.name, column, StringComparison.OrdinalIgnoreCase));

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // FK — на КАЖДОМ соединении, независимо от состояния пула
        // (пул Microsoft.Data.Sqlite сохраняет состояние PRAGMA между
        // вызовами: соединение, вернувшееся в пул после чужого
        // «foreign_keys = OFF», иначе отдалось бы следующим вызовам с FK
        // выключенным — и крестик снова ловил бы SQLite Error 19).
        conn.Execute("PRAGMA foreign_keys = ON;");
        return conn;
    }


    // Vacancies

    public int UpsertVacancies(IReadOnlyList<JobPosting> items)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            // «крестики» юзера сильнее дедупа — помеченное в базу
            // не возвращается, сколько бы поиски его ни находили.
            // это и есть сравнение с ТАБЛИЦЕЙ УДАЛЁННЫХ (там теперь лежит
            // вся строка), исход — строкой в протокол (тихих фильтров нет).
            var dismissed = conn.Query<string>(
                    "SELECT DedupeHash FROM DismissedVacancies").ToHashSet(StringComparer.Ordinal);
            var inserted = 0;
            var dismissedHits = 0;
            foreach (var item in items)
            {
                if (dismissed.Contains(item.DedupeHash))
                {
                    dismissedHits++;
                    continue;
                }

                // Id канонично Guid: генерируется при вставке,
                // адаптеры его не заполняют — добираем здесь.
                var rowId = string.IsNullOrEmpty(item.Id) ? Guid.NewGuid().ToString() : item.Id;
                inserted += conn.Execute("""
                    INSERT OR IGNORE INTO Vacancies
                      (Id, PortalId, ExternalId, CanonicalUrl, Title, Company, Location,
                       SalaryText, DescriptionMd, SourceClass, AccessKind, Quality,
                       DedupeHash, FetchedAt, Status, IsSimilar, DetailFetched)
                    VALUES
                      (@Id, @PortalId, @ExternalId, @CanonicalUrl, @Title, @Company, @Location,
                       @SalaryText, @DescriptionMd, @SourceClass, @AccessKind, @Quality,
                       @DedupeHash, @FetchedAt, @Status, @IsSimilar, @DetailFetched)
                    """, new
                {
                    Id = rowId,
                    item.PortalId,
                    item.ExternalId,
                    item.CanonicalUrl,
                    item.Title,
                    item.Company,
                    item.Location,
                    item.SalaryText,
                    item.DescriptionMd,
                    SourceClass = item.SourceClass.ToString(),
                    AccessKind = item.AccessKind.ToString(),
                    item.Quality,
                    item.DedupeHash,
                    FetchedAt = (item.FetchedAt == default
                        ? DateTimeOffset.Now
                        : item.FetchedAt).ToString("O"),
                    Status = item.Status.ToString(),
                    item.IsSimilar,
                    item.DetailFetched,
                });
            }
            if (dismissedHits > 0)
            {
                _log.Log($"[db] найденных, совпавших с таблицей удалённых (крестик): " +
                         $"{dismissedHits} — в базу не вернул (проверка на дубликаты)");
            }
            return inserted;
        }
    }

    public Guid EnsureVacancy(JobPosting item)
    {
        Initialize();
        UpsertVacancies([item]);
        lock (_gate)
        {
            using var conn = Open();
            var id = conn.QueryFirstOrDefault<string>(
                "SELECT Id FROM Vacancies WHERE DedupeHash = @h", new { h = item.DedupeHash });
            return Guid.Parse(id!);
        }
    }

    public Guid? FindVacancyIdByHash(string dedupeHash)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            var id = conn.QueryFirstOrDefault<string>(
                "SELECT Id FROM Vacancies WHERE DedupeHash = @h", new { h = dedupeHash });
            return id is null ? null : Guid.Parse(id);
        }
    }

    // r20: кэш полного текста вакансии (кнопка «Адаптировать» достаёт
    // страницу вакансии один раз): перезапись DescriptionMd + флаг
    // DetailFetched. INSERT OR IGNORE из UpsertVacancies существующую строку
    // НЕ перезаписывает — поэтому отдельный UPDATE. false — вакансии уже нет
    // (крестик): кэшировать некуда, молча пропускаем.
    public bool UpdateVacancyDetail(string dedupeHash, string descriptionMd)
    {
        Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeHash);
        lock (_gate)
        {
            using var conn = Open();
            var updated = conn.Execute("""
                UPDATE Vacancies
                SET DescriptionMd = @d, DetailFetched = 1
                WHERE DedupeHash = @h
                """, new { h = dedupeHash, d = descriptionMd });
            return updated > 0;
        }
    }


    // вакансии живут в базе — окно переживает перезапуск.

    public IReadOnlyList<JobPosting> RecentVacancies(int limit = 400)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            var rows = conn.Query<VacancyRow>("""
                SELECT Id, PortalId, ExternalId, CanonicalUrl, Title, Company, Location,
                       SalaryText, DescriptionMd, SourceClass, AccessKind, Quality,
                       DedupeHash, FetchedAt, Status, IsSimilar, DetailFetched
                FROM Vacancies
                ORDER BY FetchedAt DESC LIMIT @limit
                """, new { limit });
            return rows.Select(r => new JobPosting
            {
                Id = r.Id ?? "",
                PortalId = r.PortalId ?? "",
                ExternalId = r.ExternalId,
                CanonicalUrl = r.CanonicalUrl ?? "",
                Title = r.Title,
                Company = r.Company,
                Location = r.Location,
                SalaryText = r.SalaryText,
                DescriptionMd = r.DescriptionMd,
                SourceClass = ParseEnum(r.SourceClass, Portals.PortalSourceClass.Commercial),
                AccessKind = ParseEnum(r.AccessKind, Portals.PortalAccessKind.Feed),
                Quality = r.Quality ?? "verified",
                DedupeHash = r.DedupeHash ?? "",
                FetchedAt = ParseSafe(r.FetchedAt),
                Status = ParseEnum(r.Status, VacancyStatus.New),
                IsSimilar = r.IsSimilar != 0,
                DetailFetched = r.DetailFetched != 0,
            }).ToList();
        }
    }


    // Удаление — только «с морды», из базы строка не исчезает:
    // строка ПЕРЕНОСИТСЯ из Vacancies в DismissedVacancies целиком
    // (ON CONFLICT — обновляем снапшот свежими данными), из Vacancies
    // удаляется; при новых поисках UpsertVacancies сверяется с таблицей
    // удалённых и не возвращает (см. выше). false — строки уже не было.

    public bool DeleteVacancy(string dedupeHash)
    {
        Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeHash);
        lock (_gate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
// Перенос целиком (для неизвестного хэша — пустая строка-камень:
// юзер сказал «удалить» — ни окно, ни база, ни будущие поиски
// не возвращают).
            conn.Execute("""
                INSERT INTO DismissedVacancies
                  (DedupeHash, Title, DismissedAt, PortalId, ExternalId, CanonicalUrl,
                   Company, Location, SalaryText, DescriptionMd, SourceClass, AccessKind,
                   Quality, FetchedAt, Status, IsSimilar, DetailFetched)
                SELECT DedupeHash, Title, @at, PortalId, ExternalId, CanonicalUrl,
                   Company, Location, SalaryText, DescriptionMd, SourceClass, AccessKind,
                   Quality, FetchedAt, Status, IsSimilar, DetailFetched
                FROM Vacancies WHERE DedupeHash = @h
                ON CONFLICT(DedupeHash) DO UPDATE SET
                  Title = excluded.Title, DismissedAt = excluded.DismissedAt,
                  PortalId = excluded.PortalId, ExternalId = excluded.ExternalId,
                  CanonicalUrl = excluded.CanonicalUrl, Company = excluded.Company,
                  Location = excluded.Location, SalaryText = excluded.SalaryText,
                  DescriptionMd = excluded.DescriptionMd, SourceClass = excluded.SourceClass,
                  AccessKind = excluded.AccessKind, Quality = excluded.Quality,
                  FetchedAt = excluded.FetchedAt, Status = excluded.Status,
                  IsSimilar = excluded.IsSimilar, DetailFetched = excluded.DetailFetched;
                INSERT OR IGNORE INTO DismissedVacancies (DedupeHash, Title, DismissedAt)
                VALUES (@h, NULL, @at);
                DELETE FROM Vacancies WHERE DedupeHash = @h;
                """, new { h = dedupeHash, at = DateTimeOffset.Now.ToString("O") }, tx);
            tx.Commit();
            return true;
        }
    }

    public IReadOnlySet<string> DismissedHashes()
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            return conn.Query<string>("SELECT DedupeHash FROM DismissedVacancies")
                .Where(h => !string.IsNullOrEmpty(h))
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    // Enum-колонка строкой -> значение; битое значение не валит чтение.
    private static T ParseEnum<T>(string? raw, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) ? parsed : fallback;


    // Applications

    public void SaveApplication(ApplicationRecord record)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            conn.Execute("""
                INSERT INTO Applications
                  (Id, VacancyId, Status, CvPath, LetterPath, SentAt, CreatedAt,
                   Employer, VacancyTitle, VacancyUrl, ResumeText, VectorJson, VectorModel)
                VALUES
                  (@Id, @VacancyId, @Status, @CvPath, @LetterPath, @SentAt, @CreatedAt,
                   @Employer, @VacancyTitle, @VacancyUrl, @ResumeText, @VectorJson, @VectorModel)
                """, new
            {
                Id = record.Id.ToString(),
                // Guid.Empty пишем как NULL — FK включён, а пустого
                // Guid в Vacancies не существует; NULL = «отклик без вакансии»
                // (RecentApplications читает его обратно как Guid.Empty).
                VacancyId = record.VacancyId == Guid.Empty
                    ? null
                    : record.VacancyId.ToString(),
                record.Status,
                record.CvPath,
                record.LetterPath,
                SentAt = record.SentAt.ToString("O"),
                CreatedAt = record.CreatedAt.ToString("O"),
                record.Employer,
                record.VacancyTitle,
                record.VacancyUrl,
                record.ResumeText,
                record.VectorJson,
                record.VectorModel,
            });
        }
    }

    public IReadOnlyList<ApplicationRecord> RecentApplications(int limit = 50)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            var rows = conn.Query<Row>("""
                SELECT Id, VacancyId, Status, CvPath, LetterPath, SentAt, CreatedAt,
                       Employer, VacancyTitle, VacancyUrl, ResumeText, VectorJson, VectorModel
                FROM Applications ORDER BY SentAt DESC LIMIT @limit
                """, new { limit });
            return rows.Select(r => new ApplicationRecord
            {
                Id = Guid.Parse(r.Id),
                VacancyId = Guid.TryParse(r.VacancyId, out var vacancyId) ? vacancyId : Guid.Empty,
                Status = r.Status ?? "draft",
                CvPath = r.CvPath,
                LetterPath = r.LetterPath,
                SentAt = ParseSafe(r.SentAt),
                CreatedAt = ParseSafe(r.CreatedAt),
                Employer = r.Employer ?? "",
                VacancyTitle = r.VacancyTitle,
                VacancyUrl = r.VacancyUrl ?? "",
                ResumeText = r.ResumeText,
                VectorJson = r.VectorJson,
                VectorModel = r.VectorModel,
            }).ToList();
        }
    }

    public ApplicationRecord? GetApplication(Guid id)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            var r = conn.QueryFirstOrDefault<Row>(@"
                SELECT Id, VacancyId, Status, CvPath, LetterPath, SentAt, CreatedAt,
                       Employer, VacancyTitle, VacancyUrl, ResumeText, VectorJson, VectorModel
                FROM Applications WHERE Id = @id", new { id = id.ToString() });
            if (r is null)
            {
                return null;
            }
            return new ApplicationRecord
            {
                Id = Guid.Parse(r.Id),
                VacancyId = Guid.TryParse(r.VacancyId, out var vacancyId) ? vacancyId : Guid.Empty,
                Status = r.Status ?? "draft",
                CvPath = r.CvPath,
                LetterPath = r.LetterPath,
                SentAt = ParseSafe(r.SentAt),
                CreatedAt = ParseSafe(r.CreatedAt),
                Employer = r.Employer ?? "",
                VacancyTitle = r.VacancyTitle,
                VacancyUrl = r.VacancyUrl ?? "",
                ResumeText = r.ResumeText,
                VectorJson = r.VectorJson,
                VectorModel = r.VectorModel,
            };
        }
    }

    // Точечное обновление отправленного отклика.
    // UPDATE по Id: меняются только ResumeText и CvPath — статус, векторные
    // ссылки и метаданные отправки остаются как были.
    public bool UpdateApplicationResume(Guid id, string resumeText, string? cvPath)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            var rows = conn.Execute("""
                UPDATE Applications
                SET ResumeText = @resume, CvPath = @cv
                WHERE Id = @id
                """, new { id = id.ToString(), resume = resumeText, cv = cvPath });
            return rows > 0;
        }
    }


    // PdfDocuments

    public IReadOnlyList<PdfDocumentRecord> ListIndexedPdfs()
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            return conn.Query<Row>("""
                SELECT Id, FilePath, FileHash, ExtractedText, VectorJson, VectorModel, IndexedAt
                FROM PdfDocuments
                """).Select(r => new PdfDocumentRecord
            {
                Id = Guid.Parse(r.Id),
                FilePath = r.FilePath ?? "",
                FileHash = r.FileHash ?? "",
                ExtractedText = r.ExtractedText,
                VectorJson = r.VectorJson,
                VectorModel = r.VectorModel,
                IndexedAt = ParseSafe(r.IndexedAt),
            }).ToList();
        }
    }

    public void SavePdfIndex(PdfDocumentRecord record)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            conn.Execute("""
                INSERT INTO PdfDocuments
                  (Id, FilePath, FileHash, ExtractedText, VectorJson, VectorModel, IndexedAt)
                VALUES (@Id, @FilePath, @FileHash, @ExtractedText, @VectorJson, @VectorModel, @IndexedAt)
                ON CONFLICT(FilePath) DO UPDATE SET
                  FileHash = excluded.FileHash,
                  ExtractedText = excluded.ExtractedText,
                  VectorJson = excluded.VectorJson,
                  VectorModel = excluded.VectorModel,
                  IndexedAt = excluded.IndexedAt
                """, new
            {
                Id = record.Id.ToString(),
                record.FilePath,
                record.FileHash,
                record.ExtractedText,
                record.VectorJson,
                record.VectorModel,
                IndexedAt = record.IndexedAt.ToString("O"),
            });
        }
    }


    // Журнал процессов («подбор хвостов», r20).

    public Guid StartProcess(string kind, string? detail)
    {
        Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var id = Guid.NewGuid();
        lock (_gate)
        {
            using var conn = Open();
            conn.Execute("""
                INSERT INTO Processes (Id, Kind, Detail, Status, StartedAt, FinishedAt)
                VALUES (@Id, @Kind, @Detail, 'running', @StartedAt, NULL)
                """, new
            {
                Id = id.ToString(),
                Kind = kind,
                Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
                StartedAt = DateTimeOffset.Now.ToString("O"),
            });
        }
        return id;
    }

    public void FinishProcess(Guid id, string status)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            // UPDATE только по 'running': повторный финал (двойное закрытие)
            // не перетирает уже записанный исход.
            conn.Execute("""
                UPDATE Processes
                SET Status = @status, FinishedAt = @at
                WHERE Id = @id AND Status = 'running'
                """, new { id = id.ToString(), status, at = DateTimeOffset.Now.ToString("O") });
        }
    }

    public IReadOnlyList<ProcessRunRecord> RecentProcesses(int limit = 50)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            return conn.Query<ProcessRow>("""
                SELECT Id, Kind, Detail, Status, StartedAt, FinishedAt
                FROM Processes ORDER BY StartedAt DESC LIMIT @limit
                """, new { limit }).Select(ToProcessRecord).ToList();
        }
    }

    // «Подбор хвостов» (r20): процесс, чей финал не успел записаться
    // (приложение закрыли / комп выключили / интернет умер посреди запроса),
    // остаётся в базе со статусом 'running'. Эта заметает хвосты одним
    // UPDATE'ом в транзакции: сначала читаем, что закрываем (для протокола),
    // потом закрываем. Никакой автодокатки ИИ-запросов нет намеренно:
    // адаптация тратит дневные лимиты провайдеров — автозапуск без юзера
    // недопустим; собранное поиском НЕ теряется — вакансии пишутся в базу
    // по ходу прогона, повторный поиск продолжает без дублей.
    public IReadOnlyList<ProcessRunRecord> SweepStaleProcesses()
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var stale = conn.Query<ProcessRow>("""
                SELECT Id, Kind, Detail, Status, StartedAt, FinishedAt
                FROM Processes WHERE Status = 'running' ORDER BY StartedAt
                """, tx).ToList();
            if (stale.Count == 0)
            {
                return Array.Empty<ProcessRunRecord>();
            }

            var closedAt = DateTimeOffset.Now;
            conn.Execute("""
                UPDATE Processes
                SET Status = 'interrupted', FinishedAt = @at
                WHERE Status = 'running'
                """, new { at = closedAt.ToString("O") }, tx);
            tx.Commit();
            // Возвращаем уже ЗАКРЫТЫЕ строки: статус и время закрытия —
            // как в базе (читали строки ДО обновления — дописываем финал).
            return stale.Select(r => ToProcessRecord(r) with
            {
                Status = "interrupted",
                FinishedAt = closedAt,
            }).ToList();
        }
    }

    private static ProcessRunRecord ToProcessRecord(ProcessRow r) => new()
    {
        Id = Guid.TryParse(r.Id, out var id) ? id : Guid.Empty,
        Kind = r.Kind ?? "",
        Detail = r.Detail,
        Status = r.Status ?? "",
        StartedAt = ParseSafe(r.StartedAt),
        FinishedAt = string.IsNullOrEmpty(r.FinishedAt) ? null : ParseSafe(r.FinishedAt),
    };

    // проекция строки Processes (Dapper-плоская, как Row/VacancyRow).
    private sealed class ProcessRow
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Detail { get; set; }
        public string? Status { get; set; }
        public string? StartedAt { get; set; }
        public string? FinishedAt { get; set; }
    }


    // SettingsKV

    public string? GetSetting(string key)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            return conn.QueryFirstOrDefault<string>(
                "SELECT Value FROM SettingsKV WHERE Key = @key", new { key });
        }
    }

    public void SetSetting(string key, string value)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            conn.Execute("""
                INSERT INTO SettingsKV (Key, Value) VALUES (@key, @value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
                """, new { key, value });
        }
    }

    // ISO-строки базы -> DateTimeOffset; битое значение не валит чтение.
    private static DateTimeOffset ParseSafe(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    // Dapper-плоская проекция строк (без pname-магии маппинга record'ов).
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string? VacancyId { get; set; }
        public string? Status { get; set; }
        public string? CvPath { get; set; }
        public string? LetterPath { get; set; }
        public string? SentAt { get; set; }
        public string? CreatedAt { get; set; }
        public string? Employer { get; set; }
        public string? VacancyTitle { get; set; }
        public string? VacancyUrl { get; set; }
        public string? ResumeText { get; set; }
        public string? VectorJson { get; set; }
        public string? VectorModel { get; set; }
        public string? FilePath { get; set; }
        public string? FileHash { get; set; }
        public string? ExtractedText { get; set; }
        public string? IndexedAt { get; set; }
    }

    // проекция строки Vacancies (имя не конфликтует с VM-строкой — другой namespace).
    private sealed class VacancyRow
    {
        public string? Id { get; set; }
        public string? PortalId { get; set; }
        public string? ExternalId { get; set; }
        public string? CanonicalUrl { get; set; }
        public string? Title { get; set; }
        public string? Company { get; set; }
        public string? Location { get; set; }
        public string? SalaryText { get; set; }
        public string? DescriptionMd { get; set; }
        public string? SourceClass { get; set; }
        public string? AccessKind { get; set; }
        public string? Quality { get; set; }
        public string? DedupeHash { get; set; }
        public string? FetchedAt { get; set; }
        public string? Status { get; set; }
        public long IsSimilar { get; set; }
        public long DetailFetched { get; set; }
    }
}
