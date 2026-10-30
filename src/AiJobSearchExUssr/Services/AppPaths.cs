namespace AiJobSearchExUssr.Services;

// ============================================================================
// AppPaths — ЕДИНАЯ точка путей личных данных приложения (статическая
// обёртка, расчёт — здесь же). Корень: %USERPROFILE%\ai-job-search-ex-ussr\
//
// Перенос со старых адресов (папка/база/конфиг с прежним именем проекта)
// ВЫПОЛНЕН и закончился: код переезда снесён (решение владельца 2026-10-30,
// r22c) — осталось посчитать пути по текущим именам, ничего на диске
// не двигая.
//
// Initialize зовётся ОДИН РАЗ в Program.Main (после замка единственности,
// ДО создания протокола): пересчитывает Root и DbFile по факту окружения.
// Заметки — в Notes; Program.Main выливает их в протокол первыми строками.
//
// БЕЗ Initialize (дизайнер XAML, тесты): пути считаются сразу по текущим
// именам без побочных эффектов на диске. Поэтому Initialize НЕ зовётся
// из статиков классов — только из Program.Main.
//
// THREAD: Initialize — один вызов из Program.Main до контейнера, один
// поток; дальше свойства только читаются.
// ============================================================================
public static class AppPaths
{
    public const string CurrentFolderName = "ai-job-search-ex-ussr";
    public const string DbFileName = "ai-job-search-ex-ussr.db";

    private static readonly List<string> _notes = new();

    // Заметки старта (фолбэк путей) — Program.Main пишет их в протокол первым делом.
    public static IReadOnlyList<string> Notes => _notes;

    // Корень личных данных. До Initialize — расчёт без эффектов на диске
    // (дизайнер/тесты).
    public static string Root { get; private set; }

    // Каталог базы и конфигов состояния: <Root>\data\.
    public static string DataDir => Path.Combine(Root, "data");

    // Полный путь к базе откликов: <Root>\data\<DbFileName>.
    public static string DbFile { get; private set; }

    static AppPaths()
    {
        Root = ComputeRoot();
        DbFile = ComputeDbFile(Root);
    }

    private static string BaseDir()
    {
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
        return rooted ? profileDir : AppContext.BaseDirectory;
    }

    private static string ComputeRoot()
    {
        var root = Path.Combine(BaseDir(), CurrentFolderName);
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathRooted(profileDir))
        {
            _notes.Add("[пути] профильный каталог не резолвится — данные лягут рядом с exe " +
                       $"(фолбэк AppContext.BaseDirectory): {root}");
        }
        return root;
    }

    private static string ComputeDbFile(string root)
        => Path.Combine(root, "data", DbFileName);

    // Продакшн-вход: пересчитать пути по факту окружения. Только из
    // Program.Main (после замка единственности — второй экземпляр не трогает папку).
    public static void Initialize()
    {
        Root = ComputeRoot();
        DbFile = ComputeDbFile(Root);
    }
}
