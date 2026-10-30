using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiJobSearchBy.Services.Resume;

// ============================================================================
// база профилей резюме — SQLite + Dapper
// по канону architecture.md §4 (как база откликов ApplicationStore:
// идемпотентные CREATE TABLE IF NOT EXISTS, lock на операцию, соединение
// открывается на вызов). Файл базы — РЯДОМ С КОНФИГАМИ:
// C:\Users\<юзер>\ai-job-search-by\resume\resume.db
// C:\Users\<юзер>\ai-job-search-by\resume\files\<профиль>\... (исходник + DOCX)

// Схема:
// resume_profiles(id, name, is_active, source_path, source_hash, canon_md,
// docx_path, created_at, updated_at)
// resume_keywords(id, profile_id -> CASCADE, kind: my|extracted,
// grp, value, norm, accepted)

// FLOW: CompositionRoot (синглтон IResumeStore) -> ResumeProfilesViewModel.
// STATE: путь базы снаружи (прод/тесты-temp); Initialize одноразовый.
// THREAD: все операции под _gate.
// ============================================================================

// Профиль резюме: один парс при загрузке, дальше правится MD-канон.
public sealed record ResumeProfileRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public bool IsActive { get; init; }
    public string SourcePath { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public string CanonMd { get; init; } = "";
    public string? DocxPath { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

// Ключевое слово профиля: kind "my" — ручные, "extracted" — принятые из парсера.
public sealed record ResumeKeywordRecord
{
    public int Id { get; init; }
    public int ProfileId { get; init; }
    public string Kind { get; init; } = "my";
    public string Group { get; init; } = "";
    public string Value { get; init; } = "";
    public string Norm { get; init; } = "";

    public bool Accepted { get; init; } = true;
}

public interface IResumeStore
{
    string DbPath { get; }
    void Initialize();

    IReadOnlyList<ResumeProfileRecord> ListProfiles();
    ResumeProfileRecord? GetProfile(int id);
    ResumeProfileRecord AddProfile(string name, string sourcePath, string sourceHash,
        string canonMd, string? docxPath);
    void UpdateCanon(int id, string canonMd);
    void UpdateDocxPath(int id, string docxPath);
    void SetActive(int id);
    void DeleteProfile(int id);

    IReadOnlyList<ResumeKeywordRecord> GetKeywords(int profileId);
    void ReplaceKeywords(int profileId, IEnumerable<ResumeKeywordRecord> items);
    void AddKeyword(int profileId, ResumeKeywordRecord item);
}

public sealed class ResumeStore : IResumeStore
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly ILogService _log;
    private bool _initialized;

    public string DbPath { get; }

    public ResumeStore(string dbPath, ILogService log)
    {
        DbPath = dbPath;
        _log = log;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    // Каталог файлов профиля (исходник + DOCX-копия) — рядом с базой.
    public static string FilesDir(string dbPath, int profileId)
        => Path.Combine(Path.GetDirectoryName(dbPath)!, "files", profileId.ToString());

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
            conn.Execute("""
                CREATE TABLE IF NOT EXISTS resume_profiles (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    name        TEXT NOT NULL,
                    is_active   INTEGER NOT NULL DEFAULT 0,
                    source_path TEXT NOT NULL DEFAULT '',
                    source_hash TEXT NOT NULL DEFAULT '',
                    canon_md    TEXT NOT NULL DEFAULT '',
                    docx_path   TEXT,
                    created_at  TEXT NOT NULL,
                    updated_at  TEXT NOT NULL
                );
                """);
            conn.Execute("""
                CREATE TABLE IF NOT EXISTS resume_keywords (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    profile_id INTEGER NOT NULL REFERENCES resume_profiles(id) ON DELETE CASCADE,
                    kind       TEXT NOT NULL DEFAULT 'my',
                    grp        TEXT NOT NULL DEFAULT '',
                    value      TEXT NOT NULL,
                    norm       TEXT NOT NULL,
                    accepted   INTEGER NOT NULL DEFAULT 1
                );
                """);
            conn.Execute("CREATE INDEX IF NOT EXISTS ix_resume_keywords_profile ON resume_keywords(profile_id);");
            _initialized = true;
            _log.Log($"[резюме] база профилей готова: {DbPath}");
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");
        return conn;
    }

    public IReadOnlyList<ResumeProfileRecord> ListProfiles()
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            return conn.Query<ProfileRow>("""
                SELECT id AS Id, name AS Name, is_active AS IsActive,
                       source_path AS SourcePath, source_hash AS SourceHash,
                       canon_md AS CanonMd, docx_path AS DocxPath,
                       created_at AS CreatedAt, updated_at AS UpdatedAt
                FROM resume_profiles ORDER BY is_active DESC, updated_at DESC
                """).Select(Map).ToList();
        }
    }

    public ResumeProfileRecord? GetProfile(int id)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            var r = conn.QueryFirstOrDefault<ProfileRow>("""
                SELECT id AS Id, name AS Name, is_active AS IsActive,
                       source_path AS SourcePath, source_hash AS SourceHash,
                       canon_md AS CanonMd, docx_path AS DocxPath,
                       created_at AS CreatedAt, updated_at AS UpdatedAt
                FROM resume_profiles WHERE id = @id
                """, new { id });
            return r is null ? null : Map(r);
        }
    }

    // Dapper не маппит TEXT -> DateTimeOffset: плоская проекция строк +
    // ParseSafe — тот же канон, что у ApplicationStore.
    private static ResumeProfileRecord Map(ProfileRow r) => new()
    {
        Id = r.Id,
        Name = r.Name ?? "",
        IsActive = r.IsActive != 0,
        SourcePath = r.SourcePath ?? "",
        SourceHash = r.SourceHash ?? "",
        CanonMd = r.CanonMd ?? "",
        DocxPath = r.DocxPath,
        CreatedAt = ParseSafe(r.CreatedAt),
        UpdatedAt = ParseSafe(r.UpdatedAt),
    };

    private static DateTimeOffset ParseSafe(string? value)
        => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private sealed class ProfileRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public long IsActive { get; set; }
        public string? SourcePath { get; set; }
        public string? SourceHash { get; set; }
        public string? CanonMd { get; set; }
        public string? DocxPath { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    public ResumeProfileRecord AddProfile(string name, string sourcePath, string sourceHash,
        string canonMd, string? docxPath)
    {
        lock (_gate)
        {
            Initialize();
            var now = DateTimeOffset.Now.ToString("O");
            using var conn = Open();
            var id = conn.ExecuteScalar<int>("""
                INSERT INTO resume_profiles (name, is_active, source_path, source_hash,
                                             canon_md, docx_path, created_at, updated_at)
                VALUES (@name, 0, @sourcePath, @sourceHash, @canonMd, @docxPath, @now, @now);
                SELECT last_insert_rowid();
                """, new { name, sourcePath, sourceHash, canonMd, docxPath, now });
            _log.LogSuccess($"[резюме] профиль «{name}» сохранён (#{id})");
            return new ResumeProfileRecord
            {
                Id = id,
                Name = name,
                SourcePath = sourcePath,
                SourceHash = sourceHash,
                CanonMd = canonMd,
                DocxPath = docxPath,
                CreatedAt = DateTimeOffset.Parse(now),
                UpdatedAt = DateTimeOffset.Parse(now),
            };
        }
    }

    public void UpdateCanon(int id, string canonMd)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            var rows = conn.Execute(
                "UPDATE resume_profiles SET canon_md = @canonMd, updated_at = @now WHERE id = @id",
                new { id, canonMd, now = DateTimeOffset.Now.ToString("O") });
            if (rows == 0)
            {
                _log.LogError($"[резюме] канон не обновлён — профиля #{id} нет в базе; " +
                              "решение: обновить список профилей (кнопка «Перечитать»)");
            }
        }
    }

    public void UpdateDocxPath(int id, string docxPath)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            var rows = conn.Execute(
                "UPDATE resume_profiles SET docx_path = @docxPath, updated_at = @now WHERE id = @id",
                new { id, docxPath, now = DateTimeOffset.Now.ToString("O") });
            if (rows == 0)
            {
                _log.LogError($"[резюме] DOCX-путь не сохранён — профиля #{id} нет; " +
                              "решение: обновить список профилей");
            }
        }
    }

    // Активный профиль — ровно один: сначала гасим всех, потом зажигаем.
    public void SetActive(int id)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            conn.Execute("UPDATE resume_profiles SET is_active = 0 WHERE is_active = 1;");
            var rows = conn.Execute("UPDATE resume_profiles SET is_active = 1 WHERE id = @id", new { id });
            if (rows == 0)
            {
                _log.LogError($"[резюме] профиль #{id} не найден — активный не назначен; " +
                              "решение: обновить список профилей");
            }
        }
    }

    public void DeleteProfile(int id)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            // Каскад сработает (foreign_keys ON): keywords снесутся сами.
            var rows = conn.Execute("DELETE FROM resume_profiles WHERE id = @id", new { id });
            if (rows > 0)
            {
                try
                {
                    var dir = FilesDir(DbPath, id);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    // Строку профиля снесли — файлы могли не удалиться (заняты);
                    // не молчим: путь остаётся в протоколе, юзер подотрёт руками.
                    _log.LogWarn($"[резюме] файлы профиля #{id} не удалены: {ex.Message}; " +
                                 "решение: удалить папку files/" + id + " вручную — база чиста");
                }

                _log.Log($"[резюме] профиль #{id} удалён (ключевые ушли каскадом)");
            }
        }
    }

    public IReadOnlyList<ResumeKeywordRecord> GetKeywords(int profileId)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            return conn.Query<ResumeKeywordRecord>("""
                SELECT id AS Id, profile_id AS ProfileId, kind AS Kind, grp AS [Group],
                       value AS Value, norm AS Norm, accepted AS Accepted
                FROM resume_keywords WHERE profile_id = @profileId ORDER BY id
                """, new { profileId }).ToList();
        }
    }

    // Полная перезапись набора профиля (UI: принял правки / снял галочки).
    public void ReplaceKeywords(int profileId, IEnumerable<ResumeKeywordRecord> items)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            conn.Execute("DELETE FROM resume_keywords WHERE profile_id = @profileId",
                new { profileId }, tx);
            foreach (var item in items)
            {
                conn.Execute("""
                    INSERT INTO resume_keywords (profile_id, kind, grp, value, norm, accepted)
                    VALUES (@ProfileId, @Kind, @Group, @Value, @Norm, @Accepted)
                    """, item with { ProfileId = profileId }, tx);
            }

            tx.Commit();
        }
    }

    public void AddKeyword(int profileId, ResumeKeywordRecord item)
    {
        lock (_gate)
        {
            Initialize();
            using var conn = Open();
            var norm = string.IsNullOrWhiteSpace(item.Norm) ? GeoMatcher.Normalize(item.Value) : item.Norm;
            var already = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM resume_keywords WHERE profile_id = @profileId AND norm = @norm",
                new { profileId, norm });
            if (already > 0)
            {
                return; // дубликат — тихо идемпотентно (UI и так не даст)
            }

            conn.Execute("""
                INSERT INTO resume_keywords (profile_id, kind, grp, value, norm, accepted)
                VALUES (@ProfileId, @Kind, @Group, @Value, @Norm, @Accepted)
                """, item with { ProfileId = profileId, Norm = norm });
        }
    }
}
