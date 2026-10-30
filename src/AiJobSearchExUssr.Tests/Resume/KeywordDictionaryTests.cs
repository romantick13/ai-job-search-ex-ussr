using AiJobSearchExUssr.Services.Resume;
using Xunit;

namespace AiJobSearchExUssr.Tests.Resume;

// гейты словаря — файл keywords.json в конфиг-каталоге, формула
// «правишь один раз под себя»: дефолт создаётся ровно
// один раз, юзерский файл НЕ перетирается, битый не валит приложение.
public class KeywordDictionaryTests : IDisposable
{
    private readonly string _dir;
    private readonly SilentLog _log = new();
    private readonly string _path;

    public KeywordDictionaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kwdict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "keywords.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp — не критично */ }
    }

    [Fact]
    public void FirstStart_CreatesDefaultFile_Including1C()
    {
        var dict = new KeywordDictionary(_path, _log);
        var created = dict.Load();

        Assert.True(created);
        Assert.True(File.Exists(_path));
        // Решение «бухгалтер 1С из коробки» — учётный блок в дефолте.
        Assert.Contains(dict.Flat(), f => f.Value.Contains("1С:Бухгалтерия"));
        // И промышленный альпинизм — по заказу )))
        Assert.Contains(dict.Flat(), f => f.Value.Contains("Промышленный альпинизм"));
        Assert.True(dict.TotalItems > 100);
    }

    [Fact]
    public void ExistingFile_IsNeverOverwritten()
    {
        var userJson = """
            {"version":1,"categories":[{"name":"Моё","items":["Zabbix","ESXi"]}]}
            """;
        File.WriteAllText(_path, userJson);

        var dict = new KeywordDictionary(_path, _log);
        dict.Load();

        Assert.Equal(2, dict.TotalItems);
        // Перезагрузка не потерла юзерский файл.
        dict.Load();
        Assert.Contains(dict.Flat(), f => f.Value == "ESXi");
        var onDisk = File.ReadAllText(_path);
        Assert.Contains("\"Моё\"", onDisk);
    }

    [Fact]
    public void AddItem_PersistsAtomically()
    {
        var dict = new KeywordDictionary(_path, _log);
        dict.Load();

        Assert.True(dict.AddItem("Моё", "Veeam"));
        Assert.False(dict.AddItem("Моё", "veeam")); // дубль по нормализации

        var fresh = new KeywordDictionary(_path, _log);
        fresh.Load();
        Assert.Contains(fresh.Flat(), f => f.Value == "Veeam");
    }

    [Fact]
    public void BrokenJson_FallsBackToDefault_KeepsUserFile()
    {
        File.WriteAllText(_path, "{ это не json ]");

        var dict = new KeywordDictionary(_path, _log);
        dict.Load();

        // Работаем на дефолте, юзерский файл остаётся на диске как был.
        Assert.True(dict.TotalItems > 100);
        Assert.Contains(_log.Logs, l => l.Contains("ERR"));
        Assert.Contains("не json", File.ReadAllText(_path));
    }
}
