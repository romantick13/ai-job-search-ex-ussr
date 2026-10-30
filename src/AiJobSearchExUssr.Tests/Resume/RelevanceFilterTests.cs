using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services.Resume;
using Xunit;

namespace AiJobSearchExUssr.Tests.Resume;

// Мотивация: по запросу «гардеробщик» фильтр раньше
// находил и «сап админ», и «удалёнка» — полную чушь. Гейт — релевантность
// вакансии строке запроса. Правила:
// одиночный запрос — хотя бы одно слово в заголовке;
// список ключевых (запятые) — порог max(2, 20% списка) в тексте;
// пусто/«*» — каталог, не фильтруем.
public class RelevanceFilterTests
{
    private static JobPosting Posting(string? title, string? description = null)
        => new()
        {
            PortalId = "test",
            CanonicalUrl = "https://test/vac/1",
            Title = title,
            DescriptionMd = description,
            DedupeHash = "h",
        };

    // --- одиночный запрос: строго по заголовку ----------------------------
    [Fact]
    public void SingleQuery_TitleHit_Passes()
    {
        Assert.True(RelevanceFilter.Pass(Posting("Гардеробщик в театр"), "гардеробщик"));
    }

    [Fact]
    public void SingleQuery_TitleMiss_Hides()
    {
        // Кейс каталог отдал саппорт-вакансию по запросу «гардеробщик».
        Assert.False(RelevanceFilter.Pass(
            Posting("Client Support Supervisor", "Answer tickets, remote work"), "гардеробщик"));
    }

    [Fact]
    public void SingleQuery_AnyWordOfQueryInTitle_Passes()
    {
        // «бухгалтер 1С»: достаточно «бухгалтер» в заголовке.
        Assert.True(RelevanceFilter.Pass(Posting("Бухгалтер по зарплате"), "бухгалтер 1С"));
    }

    [Fact]
    public void SingleQuery_NormalizesCaseAndYo()
    {
        Assert.True(RelevanceFilter.Pass(Posting("СЕКРЕТАРЬ"), "секретарь"));
        Assert.True(RelevanceFilter.Pass(Posting("Чернорабочий"), "чернорабочий"));
    }

    [Fact]
    public void SingleQuery_StopWordsIgnored()
    {
        // «в», «и» и короткие слова запроса не участвуют.
        Assert.True(RelevanceFilter.Pass(Posting("Сисадмин"), "сисадмин в офис и на стройке"));
    }

    // --- одиночный запрос матчится и по тексту/компании -------------
    [Fact]
    public void SingleQuery_DescriptionHit_Passes()
    {
        // Лог: «SAP» дал 0 из 20 на himalayas — слово
        // только в описании (каталог-лента без слова в заголовке). Теперь
        // совпадение по тексту вакансии тоже пропускает.
        Assert.True(RelevanceFilter.Pass(
            Posting("Senior Meta Strategist",
                "We need experience with SAP RE/FX consulting modules."),
            "SAP"));
    }

    [Fact]
    public void SingleQuery_CompanyHit_Passes()
    {
        var p = new JobPosting
        {
            PortalId = "test",
            CanonicalUrl = "https://test/vac/1",
            Title = "Consultant",
            Company = "SAP Ukraine",
            DescriptionMd = "Implementation of ERP systems.",
            DedupeHash = "h",
        };
        Assert.True(RelevanceFilter.Pass(p, "SAP"));
    }

    [Fact]
    public void SingleQuery_NowhereStill_Hides()
    {
        Assert.False(RelevanceFilter.Pass(
            Posting("Sales Engineer", "Grow our pipeline, remote work."), "SAP"));
    }

    // --- список ключевых (запятые): порог в тексте ------------------------
    [Fact]
    public void ListQuery_ThresholdMet_Passes()
    {
        var q = "sql, linux, nginx, английский";
        // 4 слова, порог max(2, ceil(0.8)) = 2; в тексте sql+linux — прошли.
        Assert.True(RelevanceFilter.Pass(
            Posting("Инженер сопровождения", "Нужны sql и linux, nginx будет плюсом"), q));
    }

    [Fact]
    public void ListQuery_BelowThreshold_Hides()
    {
        var q = "sql, linux, nginx, английский";
        // Только один хит — меньше порога 2.
        Assert.False(RelevanceFilter.Pass(
            Posting("Менеджер по продажам", "Знает слово sql"), q));
    }

    // --- пустой запрос и «*» — каталог не фильтруем -----------------------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void EmptyQuery_PassesEverything(string? query)
    {
        Assert.True(RelevanceFilter.Pass(Posting("Anything"), query));
    }

    // --- служебное --------------------------------------------------------
    [Fact]
    public void IsListQuery_DetectsComma()
    {
        Assert.True(RelevanceFilter.IsListQuery("c#, sql, linux"));
        Assert.False(RelevanceFilter.IsListQuery("гардеробщик"));
    }

    [Fact]
    public void Reason_IsHumanReadable()
    {
        Assert.Contains("заголовке", RelevanceFilter.Reason("гардеробщик"));
        Assert.Contains("слов списка", RelevanceFilter.Reason("c#, sql, linux"));
    }

    [Fact]
    public void Words_FiltersShortAndStops()
    {
        var words = RelevanceFilter.Words("C# и ВАШИ the a на стройке");
        Assert.DoesNotContain("и", words);
        Assert.DoesNotContain("the", words);
        Assert.Contains("c#", words);
        Assert.Contains("ваши", words);
    }
}
