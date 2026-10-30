using Microsoft.Data.Sqlite;

namespace AiJobSearchBy.Services;

// ============================================================================
// (: «нафига 2 пути???? слить нафик и
// оставить один рабочий каталог и для базы и для конфигов и для протоколов
// C:\Users\<юзер>\ai-job-search-by\»; «адаптированные резюме - ложить рядом
// c EXE в подкаталог resume/»).

// ЕДИНЫЙ РАБОЧИЙ КАТАЛОГ: %USERPROFILE%\ai-job-search-by\
// local.json, keywords.json, layout.json — конфиги (уже там);
// state\ — регистры бюджета (уже там);
// data\ai-job-search-by.db — база (уже там);
// resume\ — профили резюме (уже там);
// logs\ — протокол + session-checks.log (НОВОЕ;
// раньше протокол вёл в
// %APPDATA%\ai-job-search-by\logs\);
// <exe>\resume\ — отклики/адаптированные резюме
// (НОВОЕ; раньше — Документы).

// ЧТО ДЕЛАЕТ МИГРАТОР (один раз при старте, до сборки DI):
// 1. %APPDATA%\ai-job-search-by\** → %USERPROFILE%\ai-job-search-by\**
// (logs\, data\, state\, отдельные файлы). Конфликт (файл уже есть):
// совпали байты — источник сносится; различаются — источник
// сохраняется в data\archive\roaming-<штамп>\ (ничего не теряется).
// После переноса старый каталог сносится целиком.
// 2. session-checks.log из корня рабочего каталога → logs\.
// 3. Документы\ai-job-search-by\* → <exe>\resume\, и правки в базе:
// PdfDocuments.FilePath / Applications.CvPath переименовывают префикс,
// настройка outbox.path со старым значением стирается (вернётся дефолт).

// Контракт «никаких тихих игноров» : каждый шаг — строка
// в протоколе ; сбой переноса не валит старт — LogWarn с причиной
// и файл остаётся на старом месте (/).
// THREAD: вызывается из Program.Main до контейнера — один поток, без гонок.
// ============================================================================
public static class LegacyPathMigrator
{
    public sealed record Report(
        int FromRoaming,
        int FromDocuments,
        int DbRowsFixed,
        bool RoamingRemoved,
        IReadOnlyList<string> Notes);

    // Продакшн-вход: реальные каталоги машины. Вызывается из Program.Main
    // сразу после создания bootLog — до CompositionRoot.Build.
    public static void Run(ILogService log)
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var report = Core(log,
                userProfile: userProfile,
                appDataRoot: appData,
                exeDir: AppContext.BaseDirectory,
                documentsDir: documents);

            if (report.FromRoaming == 0 && report.FromDocuments == 0
                && report.DbRowsFixed == 0 && report.RoamingRemoved == false
                && report.Notes.Count == 0)
            {
                return; // мигрировать нечего — ни строки лишней в протокол
            }

            foreach (var note in report.Notes)
            {
                log.LogWarn($"skip: [пути] {note}");
            }

            log.LogSuccess($"[пути] один рабочий каталог: {Path.Combine(userProfile, "ai-job-search-by")} — " +
                           $"логи logs\\, база data\\, отклики resume\\ рядом с exe; " +
                           $"перенесено из %APPDATA%: {report.FromRoaming}, из Документов: {report.FromDocuments}, " +
                           $"строк в базе поправлено: {report.DbRowsFixed}" +
                           (report.RoamingRemoved ? ", старый каталог %APPDATA% снесён" : ""));
        }
        catch (Exception ex)
        {
            // миграция не должна валить старт; причина — в протоколе.
            log.LogError($"[пути] миграция не удалась — {ex.GetType().Name}: {ex.Message}; " +
                         "решение: старт продолжается со старыми путями, файлы не потеряны");
        }
    }

    // Ядро с инъекцией корней — тестируется на временных каталогах.
    public static Report Core(
        ILogService log,
        string userProfile,
        string appDataRoot,
        string exeDir,
        string documentsDir)
    {
        var notes = new List<string>();
        var newRoot = Path.Combine(userProfile, "ai-job-search-by");
        var oldRoot = Path.Combine(appDataRoot, "ai-job-search-by");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        // -- 1. %APPDATA%\ai-job-search-by → %USERPROFILE%\ai-job-search-by ----
        var fromRoaming = 0;
        var roamingRemoved = false;
        if (Directory.Exists(oldRoot) &&
            !string.Equals(
                Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            var archive = Path.Combine(newRoot, "data", "archive", $"roaming-{stamp}");
            fromRoaming = MoveTree(oldRoot, newRoot, archive, notes, prefix: "%APPDATA%");
            try
            {
                Directory.Delete(oldRoot, recursive: true);
                roamingRemoved = true;
            }
            catch (Exception ex)
            {
                notes.Add($"старый каталог {oldRoot} не снесён — {ex.GetType().Name}: {ex.Message}; " +
                          "решение: закрыть программы, держащие файлы там, и удалить руками");
            }
        }

        // -- 2. session-checks.log: корень рабочего каталога → logs\ -----------
        var rootSessionLog = Path.Combine(newRoot, "session-checks.log");
        if (File.Exists(rootSessionLog))
        {
            var logsDir = Path.Combine(newRoot, "logs");
            Directory.CreateDirectory(logsDir);
            File.Move(rootSessionLog, Path.Combine(logsDir, "session-checks.log"), overwrite: true);
            fromRoaming++; // перенос в logs\ считаем в ту же счётную строку
        }

        // -- 3. Документы\ai-job-search-by → <exe>\resume (+ правки базы) ------
        var docsOutbox = Path.Combine(documentsDir, "ai-job-search-by");
        var newOutbox = Path.Combine(exeDir, "resume");
        var fromDocuments = 0;
        if (Directory.Exists(docsOutbox) &&
            !string.Equals(
                Path.GetFullPath(docsOutbox).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(newOutbox).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(newOutbox);
            foreach (var file in Directory.GetFiles(docsOutbox, "*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(newOutbox, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    if (SameBytes(file, target))
                    {
                        File.Delete(file);
                        continue;
                    }

                    var stamped = Path.Combine(newOutbox,
                        $"{Path.GetFileNameWithoutExtension(file)}-from-documents-{stamp}{Path.GetExtension(file)}");
                    File.Move(file, stamped);
                    notes.Add($"«{Path.GetFileName(file)}» из Документов переименован в «{Path.GetFileName(stamped)}» " +
                              "— в resume\\ уже был файл с таким именем");
                    fromDocuments++;
                    continue;
                }

                File.Move(file, target);
                fromDocuments++;
            }

            try
            {
                Directory.Delete(docsOutbox, recursive: true);
            }
            catch (Exception ex)
            {
                notes.Add($"каталог откликов {docsOutbox} не снесён — {ex.Message}; " +
                          "решение: удалить руками, файлы уже переехали");
            }
        }

        // Правки путей в базе — когда база уже существует (после её возможного
        // переезда из Roaming выше) и старая папка откликов существовала.
        var dbRowsFixed = 0;
        var dbPath = Path.Combine(newRoot, "data", "ai-job-search-by.db");
        if (File.Exists(dbPath) && Directory.Exists(documentsDir))
        {
            dbRowsFixed = PatchDb(dbPath, docsOutbox, newOutbox, notes);
        }

        return new Report(fromRoaming, fromDocuments, dbRowsFixed, roamingRemoved, notes);
    }

    // Перенос дерева src → dstRoot с сохранением относительных подкаталогов.
    // Конфликт: одинаковые байты → источник сносится; разные → в archiveDir.
    private static int MoveTree(string src, string dstRoot, string archiveDir,
        List<string> notes, string prefix)
    {
        var moved = 0;
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(dstRoot, Path.GetFileName(file));
            moved += MoveFileWithConflictPolicy(file, target, archiveDir, notes, prefix);
        }

        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir);
            var targetDir = Path.Combine(dstRoot, name);
            var subArchive = Path.Combine(archiveDir, name);
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dir, file);
                var target = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                moved += MoveFileWithConflictPolicy(file, target, subArchive, notes, prefix);
            }
        }

        return moved;
    }

    private static int MoveFileWithConflictPolicy(string source, string target,
        string archiveDir, List<string> notes, string prefix)
    {
        try
        {
            if (File.Exists(target))
            {
                if (SameBytes(source, target))
                {
                    File.Delete(source);
                    return 0; // дубль — новый файл не нужен
                }

                Directory.CreateDirectory(archiveDir);
                var archived = Path.Combine(archiveDir, Path.GetFileName(target));
                File.Move(source, archived, overwrite: true);
                notes.Add($"{prefix}\\{Path.GetFileName(target)} различается с уже лежащим — " +
                          $"старый сохранён в {archiveDir}");
                return 0;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            return 1;
        }
        catch (Exception ex)
        {
            notes.Add($"«{source}» не перенесён — {ex.GetType().Name}: {ex.Message}; " +
                      "решение: файл остался на месте, перенести руками");
            return 0;
        }
    }

    private static bool SameBytes(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length)
            {
                return false;
            }

            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            var ha = System.Security.Cryptography.SHA256.HashData(sa);
            var hb = System.Security.Cryptography.SHA256.HashData(sb);
            return ha.AsSpan().SequenceEqual(hb);
        }
        catch
        {
            return false; // не смогли сравнить — считаем разными (безопаснее)
        }
    }

    // Правки базы после переезда откликов: префикс путей PdfDocuments.FilePath
    // и Applications.CvPath; настройка outbox.path со старым значением — вон.
    private static int PatchDb(string dbPath, string oldOutbox, string newOutbox, List<string> notes)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
            conn.Open();

            var fixedRows = 0;
            fixedRows += RewritePrefix(conn, "PdfDocuments", "FilePath", oldOutbox, newOutbox);
            fixedRows += RewritePrefix(conn, "Applications", "CvPath", oldOutbox, newOutbox);

            // outbox.path, указывающий в Документы, стираем — вернётся дефолт
            // (<exe>\resume). Кастомные пути юзера не трогаем.
            var settingsTable = TableExists(conn, "SettingsKV");
            if (settingsTable)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT Value FROM SettingsKV WHERE Key = 'outbox.path'";
                    var value = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrWhiteSpace(value) &&
                        value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .StartsWith(oldOutbox.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        cmd.CommandText = "DELETE FROM SettingsKV WHERE Key = 'outbox.path'";
                        cmd.ExecuteNonQuery();
                        notes.Add("настройка «папка откликов» указывала в Документы — стёрта, " +
                                  "вернётся дефолт (resume\\ рядом с exe)");
                    }
                }
            }

            return fixedRows;
        }
        catch (Exception ex)
        {
            notes.Add($"правки путей в базе не сделаны — {ex.GetType().Name}: {ex.Message}; " +
                      "решение: пути в базе поправит «⟳ PDF» или пересборка отклика");
            return 0;
        }
    }

    private static int RewritePrefix(SqliteConnection conn, string table, string column,
        string oldPrefix, string newPrefix)
    {
        if (!TableExists(conn, table) || !ColumnExists(conn, table, column))
        {
            return 0;
        }

        var fixedRows = 0;
        var cleanOld = oldPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using (var select = conn.CreateCommand())
        {
            select.CommandText = $"SELECT rowid, {column} FROM {table} WHERE {column} IS NOT NULL";
            var updates = new List<(long RowId, string NewPath)>();
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var rowId = reader.GetInt64(0);
                var path = reader.GetString(1);
                var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalized.StartsWith(cleanOld, StringComparison.OrdinalIgnoreCase))
                {
                    var newPath = newPrefix.TrimEnd(Path.DirectorySeparatorChar) +
                                  path[cleanOld.Length..];
                    updates.Add((rowId, newPath));
                }
            }

            foreach (var (rowId, newPath) in updates)
            {
                using var update = conn.CreateCommand();
                update.CommandText = $"UPDATE {table} SET {column} = @p WHERE rowid = @r";
                update.Parameters.AddWithValue("@p", newPath);
                update.Parameters.AddWithValue("@r", rowId);
                update.ExecuteNonQuery();
                fixedRows++;
            }
        }

        return fixedRows;
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
