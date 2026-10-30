using AiJobSearchExUssr.Services.Resume;
using Xunit;

namespace AiJobSearchExUssr.Tests.Resume;

// извлечение ключевых слов — «прогнать и посмотреть на варианты правок»:
// словарные вхождения + эвристика секций навыков.
public class KeywordExtractorTests
{
    private const string CanonMd = """
        # Пётр Сидоров — системный администратор

        ## Навыки
        Windows Server, Active Directory, VMware ESXi, Hyper-V, Zabbix,
        Veeam, PowerShell, Bash, DNS/DHCP/IIS, PostgreSQL (администрирование), 1С:Предприятие 8

        ## Опыт
        ООО «Холдинг» — парк 300+ машин, миграция на ESXi 8, Docker.

        ## Языки
        русский — родной, english — B1
        """;

    private static List<(string, string)> Dict(params (string, string)[] items) => items.ToList();

    [Fact]
    public void Extract_FindsDictionaryEntries_CaseInsensitive()
    {
        var dict = Dict(("Windows Server", "Администрирование"), ("zabbix", "Администрирование"));
        var found = KeywordExtractor.Extract(CanonMd, dict);

        Assert.Contains(found, f => f.Value == "Windows Server" && f.InDictionary);
        Assert.Contains(found, f => f.Value == "zabbix" && f.InDictionary);
    }

    [Fact]
    public void Extract_PicksNonDictionaryCandidatesFromSkills()
    {
        var dict = Dict(("PostgreSQL", "Базы данных"));
        var found = KeywordExtractor.Extract(CanonMd, dict);

        // Эвристика: «VMware ESXi» из секции Навыки — кандидат (в словаре нет).
        var esxi = found.FirstOrDefault(f => f.Value.Contains("VMware ESXi"));
        Assert.NotNull(esxi);
        Assert.False(esxi!.InDictionary);
    }

    [Fact]
    public void Extract_HonorsYoNormalization()
    {
        var md = "# X\n\n## Навыки\nВёрстка, чёрный текст, SQL";
        var dict = Dict(("Верстка", "Стеки"), ("SQL", "Базы данных"));
        var found = KeywordExtractor.Extract(md, dict);

        // «Вёрстка» с ё совпало со словарным «Верстка» (нормализация ё->е).
        Assert.Contains(found, f => GeoMatcher.Normalize(f.Value) == "верстка");
    }

    [Fact]
    public void Extract_NoDuplicates()
    {
        var dict = Dict(("Zabbix", "Администрирование"), ("Zabbix", "Администрирование"));
        var found = KeywordExtractor.Extract(CanonMd, dict);
        Assert.Equal(1, found.Count(f => GeoMatcher.Normalize(f.Value) == "zabbix"));
    }

    [Fact]
    public void FindSection_SlicesByHeading()
    {
        var section = KeywordExtractor.FindSection(CanonMd, "Навыки");
        Assert.NotNull(section);
        Assert.Contains("Zabbix", section);
        Assert.DoesNotContain("ООО «Холдинг»", section);
    }
}
