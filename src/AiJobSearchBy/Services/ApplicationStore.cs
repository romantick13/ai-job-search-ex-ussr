using System.Globalization;
using System.IO;
using AiJobSearchBy.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiJobSearchBy.Services;

// ============================================================================
// WP-U1 (решение владельца 2026-10-22): база откликов — SQLite по канону
// architecture.md §4 (SQLite + Dapper, без EF; идемпотентные миграции
// CREATE TABLE IF NOT EXISTS + PRAGMA table_info). Таблицы Vacancies и
// Applications — 1:1 с §4; недостающие поля истории откликов (Employer,
// VacancyTitle, VacancyUrl, ResumeText, векторные ссылки) доливаются
// EnsureColumn-миграциями — канон прямо разрешает расширение колонок.
// PdfDocuments — новая таблица «векторных ссылок на PDF» (требование
// владельца: файлы остаются на диске, база хранит путь + текст + вектор).
// SettingsKV (§4) — назначение: текст резюме, выбранные сайты, папка откликов.
//
// FLOW: CompositionRoot -> IApplicationStore (синглтон) -> Initialize при
//       первом обращении; строки [db] — в протокол (НИ-4).
// STATE: путь базы передаётся снаружи (%APPDATA%\ai-job-search-by\data\
//        ai-job-search-by.db; тесты — temp); Initialize одноразовый.
// THREAD: одна запись SQLite в момент времени — все операции под lock;
//        соединение открывается на вызов и закрывается (using).
// ============================================================================

// История откликов: то, что владелец видит в окне истории, и то, что
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

    // Вариант резюме, который был отправлен (модальное окно истории).
    public string? ResumeText { get; init; }

    // Векторная ссылка: JSON-массив float локального эмбеддера (WP-U2;
    // null — векторизация выключена/рантайм не отвечает), модель-владелец
    // — рядом в VectorModel.
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

public interface IApplicationStore
{
    string DbPath { get; }

    // Одноразовые идемпотентные миграции; вызывается лениво при первом обращении.
    void Initialize();

    // Vacancies (§4): INSERT OR IGNORE по UNIQUE DedupeHash; EnsureVacancy
    // возвращает Id строки (вставленной или уже существующей).
    int UpsertVacancies(IReadOnlyList<JobPosting> items);
    Guid EnsureVacancy(JobPosting item);
    Guid? FindVacancyIdByHash(string dedupeHash);

    // Applications (§4 + EnsureColumn-расширения WP-U1).
    void SaveApplication(ApplicationRecord record);
    IReadOnlyList<ApplicationRecord> RecentApplications(int limit = 50);
    ApplicationRecord? GetApplication(Guid id);

    // PdfDocuments: индекс PDF-файлов (векторные ссылки).
    IReadOnlyList<PdfDocumentRecord> ListIndexedPdfs();
    void SavePdfIndex(PdfDocumentRecord record);

    // SettingsKV (§4): персональные настройки вне репо (текст резюме, сайты, папка).
    string? GetSetting(string key);
    void SetSetting(string key, string value);
}

public sealed class ApplicationStore : IApplicationStore
{
    // Точка данных канона: личные данные — только вне репо (architecture §8).
    public static string DefaultDataDir
    {
        get
        {
            var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
            return Path.Combine(rooted ? profileDir : AppContext.BaseDirectory,
                "ai-job-search-by", "data");
        }
    }

    // Папка откликов по умолчанию — Документы юзера: оттуда он кликает PDF на сайте.
    public static string DefaultOutboxDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ai-job-search-by");

    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly ILogService _log;
    private bool _initialized;

    public string DbPath { get; }

    public ApplicationStore(string dbPath, ILogService log)
    {
        DbPath = dbPath;
        _log = log;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

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

            // Схема §4 — дословно; расширения Applications — EnsureColumn ниже.
            conn.Execute("""
                CREATE TABLE IF NOT EXISTS Vacancies (
                  Id TEXT PRIMARY KEY, PortalId TEXT NOT NULL, ExternalId TEXT,
                  CanonicalUrl TEXT NOT NULL, Title TEXT, Company TEXT, Location TEXT,
                  SalaryText TEXT, DescriptionMd TEXT, SourceClass TEXT NOT NULL,
                  AccessKind TEXT NOT NULL, Quality TEXT NOT NULL DEFAULT 'verified',
                  DedupeHash TEXT NOT NULL UNIQUE, FetchedAt TEXT NOT NULL,
                  Status TEXT NOT NULL DEFAULT 'new' );
                CREATE TABLE IF NOT EXISTS Applications (
                  Id TEXT PRIMARY KEY, VacancyId TEXT NOT NULL REFERENCES Vacancies(Id),
                  Status TEXT NOT NULL DEFAULT 'draft', CvPath TEXT, LetterPath TEXT,
                  SentAt TEXT, FollowUpAt TEXT, Notes TEXT, CreatedAt TEXT NOT NULL );
                CREATE TABLE IF NOT EXISTS PdfDocuments (
                  Id TEXT PRIMARY KEY, FilePath TEXT NOT NULL UNIQUE, FileHash TEXT NOT NULL,
                  ExtractedText TEXT, VectorJson TEXT, VectorModel TEXT, IndexedAt TEXT NOT NULL );
                CREATE TABLE IF NOT EXISTS SettingsKV (
                  Key TEXT PRIMARY KEY, Value TEXT NOT NULL );
                """);

            // EnsureColumn-миграции: недостающие поля истории откликов.
            foreach (var (column, ddl) in new[]
                     {
                         ("Employer", "ALTER TABLE Applications ADD COLUMN Employer TEXT"),
                         ("VacancyTitle", "ALTER TABLE Applications ADD COLUMN VacancyTitle TEXT"),
                         ("VacancyUrl", "ALTER TABLE Applications ADD COLUMN VacancyUrl TEXT"),
                         ("ResumeText", "ALTER TABLE Applications ADD COLUMN ResumeText TEXT"),
                         ("VectorJson", "ALTER TABLE Applications ADD COLUMN VectorJson TEXT"),
                         ("VectorModel", "ALTER TABLE Applications ADD COLUMN VectorModel TEXT"),
                     })
            {
                if (!ColumnExists(conn, "Applications", column))
                {
                    conn.Execute(ddl);
                    _log.Log($"[db] схема Applications дополнена колонкой {column} (миграция WP-U1)");
                }
            }

            _initialized = true;
            _log.Log($"[db] база откликов готова: {DbPath}");
        }
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
        => conn.Query<(int cid, string name)>($"PRAGMA table_info({table})")
            .Any(c => string.Equals(c.name, column, StringComparison.OrdinalIgnoreCase));

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ------------------------------------------------------------------
    // Vacancies
    // ------------------------------------------------------------------
    public int UpsertVacancies(IReadOnlyList<JobPosting> items)
    {
        Initialize();
        lock (_gate)
        {
            using var conn = Open();
            var inserted = 0;
            foreach (var item in items)
            {
                // Id канонично Guid (§4: «генерирует адаптер/оркестратор при вставке»);
                // адаптеры WP-U1 его не заполняют — добираем здесь.
                var rowId = string.IsNullOrEmpty(item.Id) ? Guid.NewGuid().ToString() : item.Id;
                inserted += conn.Execute("""
                    INSERT OR IGNORE INTO Vacancies
                      (Id, PortalId, ExternalId, CanonicalUrl, Title, Company, Location,
                       SalaryText, DescriptionMd, SourceClass, AccessKind, Quality,
                       DedupeHash, FetchedAt, Status)
                    VALUES
                      (@Id, @PortalId, @ExternalId, @CanonicalUrl, @Title, @Company, @Location,
                       @SalaryText, @DescriptionMd, @SourceClass, @AccessKind, @Quality,
                       @DedupeHash, @FetchedAt, @Status)
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
                });
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

    // ------------------------------------------------------------------
    // Applications
    // ------------------------------------------------------------------
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
                VacancyId = record.VacancyId.ToString(),
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

    // ------------------------------------------------------------------
    // PdfDocuments
    // ------------------------------------------------------------------
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

    // ------------------------------------------------------------------
    // SettingsKV
    // ------------------------------------------------------------------
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
}
