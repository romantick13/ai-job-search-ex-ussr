using AiJobSearchBy.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiJobSearchBy.Tests;

// ============================================================================
// (: «слить нафик и оставить один рабочий
// каталог… C:\Users\<юзер>\ai-job-search-by\», «адаптированные резюме — рядом
// с EXE в подкаталог resume/»): LegacyPathMigrator переносит старые каталоги
// в единый корень, конфликты сохраняет в data\archive\, правит пути в базе
// и сносит %APPDATA%\ai-job-search-by. Тесты — на временных каталогах.
// ============================================================================
public sealed class LegacyPathMigratorTests : IDisposable
{
    private readonly string _root;

    public LegacyPathMigratorTests()
    {
        _root = Directory.CreateTempSubdirectory("aijob-migr-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка тестов */ }
    }

    private string UserProfile => Path.Combine(_root, "home");
    private string AppData => Path.Combine(_root, "appdata");
    private string Exe => Path.Combine(_root, "exe");
    private string Documents => Path.Combine(_root, "docs");

    private LegacyPathMigrator.Report Run(ILogService? log = null)
    {
        Directory.CreateDirectory(UserProfile);
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(Exe);
        Directory.CreateDirectory(Documents);
        return LegacyPathMigrator.Core(log ?? new SilentLog(),
            userProfile: UserProfile,
            appDataRoot: AppData,
            exeDir: Exe,
            documentsDir: Documents);
    }

    private static void WriteFile(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ------------------------------------------------------- Roaming-слияние

    [Fact]
    public void Roaming_MergesIntoRoot_AndRemovesOldDir()
    {
        var oldRoot = Path.Combine(AppData, "ai-job-search-by");
        WriteFile(Path.Combine(oldRoot, "logs", "ai-job-search-by-20261026.log"), "старый протокол");
        WriteFile(Path.Combine(oldRoot, "state", "llm7-budget.json"), "{}");
        WriteFile(Path.Combine(oldRoot, "local.json"), "{}"); // loose-файл

        var report = Run();

        Assert.True(File.Exists(Path.Combine(UserProfile, "ai-job-search-by", "logs", "ai-job-search-by-20261026.log")));
        Assert.True(File.Exists(Path.Combine(UserProfile, "ai-job-search-by", "state", "llm7-budget.json")));
        Assert.True(File.Exists(Path.Combine(UserProfile, "ai-job-search-by", "local.json")));
        Assert.False(Directory.Exists(oldRoot)); // старый каталог снесён
        Assert.True(report.RoamingRemoved);
        Assert.Equal(3, report.FromRoaming);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Roaming_ConflictingDb_GoesToArchive_TargetKept()
    {
        var oldRoot = Path.Combine(AppData, "ai-job-search-by");
        WriteFile(Path.Combine(oldRoot, "data", "ai-job-search-by.db"), "СТАРАЯ база");

        // Новая база — валидный SQLite (после слияния мигратор правит в ней
        // пути; битая база дала бы лишний note — тут проверяем именно конфликт).
        var newDb = Path.Combine(UserProfile, "ai-job-search-by", "data", "ai-job-search-by.db");
        Directory.CreateDirectory(Path.GetDirectoryName(newDb)!);
        using (var conn = new SqliteConnection($"Data Source={newDb}"))
        {
            conn.Open();
            conn.Execute("CREATE TABLE SettingsKV (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);");
        }

        var report = Run();

        // Текущая база не тронута (валидна); старая — в archive (ничего не потеряно).
        Assert.True(File.Exists(newDb));
        var archiveDir = Path.Combine(UserProfile, "ai-job-search-by", "data", "archive");
        Assert.True(Directory.Exists(archiveDir));
        var archived = Directory.GetFiles(archiveDir, "*.db", SearchOption.AllDirectories);
        var archivedFile = Assert.Single(archived);
        Assert.Equal("СТАРАЯ база", File.ReadAllText(archivedFile));
        Assert.False(Directory.Exists(oldRoot));
        Assert.Single(report.Notes); // конфликт — событие протокола
    }

    [Fact]
    public void Roaming_IdenticalFile_Deduplicated()
    {
        var oldRoot = Path.Combine(AppData, "ai-job-search-by");
        WriteFile(Path.Combine(oldRoot, "keywords.json"), "одинаковый");
        WriteFile(Path.Combine(UserProfile, "ai-job-search-by", "keywords.json"), "одинаковый");

        var report = Run();

        Assert.Equal(0, report.FromRoaming); // дубль — не «перенос»
        Assert.True(File.Exists(Path.Combine(UserProfile, "ai-job-search-by", "keywords.json")));
        Assert.Empty(report.Notes);
    }

    // ------------------------------------------------------ session-checks

    [Fact]
    public void SessionChecksLog_MovesIntoLogsSubdir()
    {
        WriteFile(Path.Combine(UserProfile, "ai-job-search-by", "session-checks.log"), "проверки");

        Run();

        Assert.True(File.Exists(Path.Combine(UserProfile, "ai-job-search-by", "logs", "session-checks.log")));
        Assert.False(File.Exists(Path.Combine(UserProfile, "ai-job-search-by", "session-checks.log")));
    }

    // ------------------------------------------ отклики: Документы → resume\

    [Fact]
    public void DocumentsOutbox_MovesToExeResume_AndPatchesDb()
    {
        var docsOutbox = Path.Combine(Documents, "ai-job-search-by");
        var pdfOld = Path.Combine(docsOutbox, "Talkspace_20261026_092045.pdf");
        WriteFile(pdfOld, "%PDF-1.7 fake");

        var dbPath = Path.Combine(UserProfile, "ai-job-search-by", "data", "ai-job-search-by.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            conn.Execute("""
                CREATE TABLE PdfDocuments (Id TEXT PRIMARY KEY, FilePath TEXT NOT NULL UNIQUE);
                CREATE TABLE Applications (Id TEXT PRIMARY KEY, CvPath TEXT);
                CREATE TABLE SettingsKV (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO PdfDocuments VALUES ('pdf-1', @p);
                INSERT INTO Applications  VALUES ('app-1', @p);
                INSERT INTO SettingsKV    VALUES ('outbox.path', @p);
                """, new { p = pdfOld });
        }

        var report = Run();

        var pdfNew = Path.Combine(Exe, "resume", "Talkspace_20261026_092045.pdf");
        Assert.True(File.Exists(pdfNew)); // рядом с EXE
        Assert.False(Directory.Exists(docsOutbox));
        Assert.Equal(1, report.FromDocuments);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.Equal(pdfNew, Scalar(conn, "SELECT FilePath FROM PdfDocuments WHERE Id='pdf-1'"));
            Assert.Equal(pdfNew, Scalar(conn, "SELECT CvPath FROM Applications WHERE Id='app-1'"));
            // Унаследованная настройка стёрта — вернётся дефолт <exe>\resume.
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM SettingsKV WHERE Key='outbox.path'"));
        }

        Assert.Equal(2, report.DbRowsFixed);
    }

    [Fact]
    public void CustomOutboxSetting_KeptByMigrator()
    {
        var dbPath = Path.Combine(UserProfile, "ai-job-search-by", "data", "ai-job-search-by.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            conn.Execute("""
                CREATE TABLE SettingsKV (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO SettingsKV VALUES ('outbox.path', 'D:\мои-отклики');
                """);
        }

        Run();

        using var conn2 = new SqliteConnection($"Data Source={dbPath}");
        conn2.Open();
        Assert.Equal(@"D:\мои-отклики", Scalar(conn2, "SELECT Value FROM SettingsKV WHERE Key='outbox.path'"));
    }

    // ----------------------------------------------------------- без старья

    [Fact]
    public void FreshMachine_DoesNothing()
    {
        var report = Run();

        Assert.Equal(0, report.FromRoaming);
        Assert.Equal(0, report.FromDocuments);
        Assert.Equal(0, report.DbRowsFixed);
        Assert.False(report.RoamingRemoved);
        Assert.Empty(report.Notes);
    }

    private static object? Scalar(SqliteConnection conn, string sql)
        => new SqliteCommand(sql, conn).ExecuteScalar();
}

// Мини-хелпер: обёртка Execute без Dapper (в тестах не тянем).
file static class SqliteCmdExtensions
{
    public static void Execute(this SqliteConnection conn, string sql, object? args = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (args is not null)
        {
            foreach (var prop in args.GetType().GetProperties())
            {
                cmd.Parameters.AddWithValue(prop.Name, prop.GetValue(args) ?? DBNull.Value);
            }
        }
        cmd.ExecuteNonQuery();
    }
}
