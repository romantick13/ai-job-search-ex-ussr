using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services.Resume;
using Xunit;

namespace AiJobSearchExUssr.Tests.Resume;

// гейты гео-фильтра — дословно матрица:
// офисная вакансия с указанным городом — гео обязано совпасть, иначе нет;
// «может быть удалёнка» — подходит вообще всё.
public class GeoMatcherTests
{
    private static JobPosting Posting(string? title, string? location, string? description = null)
        => new()
        {
            PortalId = "test",
            CanonicalUrl = "https://test/vac/1",
            Title = title,
            Location = location,
            DescriptionMd = description,
            DedupeHash = "h",
        };

    // --- DetectRemote -----------------------------------------------------
    [Theory]
    [InlineData("C# разработчик (удалённо)", "Минск", RemoteKind.RemoteAllowed)]
    [InlineData(".NET Backend Engineer (Remote)", "Warsaw", RemoteKind.RemoteAllowed)]
    [InlineData("Сисадмин", "Минск · работа из дома", RemoteKind.RemoteAllowed)]
    [InlineData("Support", "Worldwide", RemoteKind.RemoteAllowed)]
    [InlineData("Тестировщик", "Гомель · в офисе", RemoteKind.OfficeOnly)]
    [InlineData("DevOps", "Минск, on-site", RemoteKind.OfficeOnly)]
    [InlineData("Бухгалтер 1С", "Минск", RemoteKind.OfficeOnly)]
    [InlineData("Бухгалтер 1С", null, RemoteKind.Unknown)]
    public void DetectRemote_PriorityRemoteOverOffice(string title, string? location, RemoteKind expected)
    {
        Assert.Equal(expected, GeoMatcher.DetectRemote(Posting(title, location)));
    }

    [Fact]
    public void DetectRemote_DescriptionHeadCounts()
    {
        // Маркер удалёнки в шапке описания (первые 1200 знаков) тоже считается.
        var p = Posting("Сисадмин", "Минск", "Режим: удалённая работа.\n" + new string('х', 1300));
        Assert.Equal(RemoteKind.RemoteAllowed, GeoMatcher.DetectRemote(p));
    }

    [Fact]
    public void DetectRemote_OfficeFromDescriptionCounts()
    {
        // «Работа в офисе» в описании — офисная вакансия (гео обязано совпасть).
        var p = Posting("DevOps-инженер", "Гомель", "Docker, Kubernetes. Работа в офисе.");
        Assert.Equal(RemoteKind.OfficeOnly, GeoMatcher.DetectRemote(p));
    }

    // --- пустой фильтр: весь мир -----------------------------------------
    [Fact]
    public void EmptyFilter_PassesEverything()
    {
        var f = GeoFilter.None;
        Assert.True(GeoMatcher.Match(Posting("X", "Минск"), f, RemoteKind.OfficeOnly).Pass);
        Assert.True(GeoMatcher.Match(Posting("X", null), f, RemoteKind.Unknown).Pass);
    }

    // --- удалёнка: подходит всегда (формула ) --------------------
    [Fact]
    public void RemoteAllowed_PassesAnyFilter()
    {
        var f = new GeoFilter("Беларусь", "Минск");
        var v = GeoMatcher.Match(Posting("C# (remote)", "Berlin"), f, RemoteKind.RemoteAllowed);
        Assert.True(v.Pass);
        Assert.Contains("всегда", v.Note);
    }

    // --- -> чужая страна у удалёнки ---------------------------
    [Fact]
    public void RemoteAllowed_ForeignCountry_HidesOnlyIfExcludedFromList()
    {
        // Живой кейс: «указал и беларусь и минск — всё равно
        // нашло удаленки» (Talkspace, США). Дефолт — все члены ООН разрешены,
        // ничего не прячем; прятание возвращается вычеркиванием страны
        // из списка «Страны…» (geo.allowed).
        var f = new GeoFilter("Беларусь", "Минск");
        var p = Posting("Client Support Supervisor", "Anywhere",
            "We are hiring in the United States. Remote work is fine.");

        // Дефолт: весь мир — «Remote — United States» проходит.
        Assert.True(GeoMatcher.Match(p, f, RemoteKind.RemoteAllowed).Pass);

        // США вычеркнули из списка — снова мимо, с человеческой причиной.
        var allowed = new HashSet<string>(["беларусь"]);
        var v = GeoMatcher.Match(p, f, RemoteKind.RemoteAllowed, allowed: allowed);
        Assert.False(v.Pass);
        Assert.Contains("США", v.Note);
    }

    [Fact]
    public void RemoteAllowed_HomeCountryMentioned_Passes()
    {
        var f = new GeoFilter("Беларусь", "Минск");
        var p = Posting("Сисадмин (удалённо)", "Минск / remote");
        var v = GeoMatcher.Match(p, f, RemoteKind.RemoteAllowed);
        Assert.True(v.Pass);
    }

    [Fact]
    public void RemoteAllowed_AnywhereNoCountry_Passes()
    {
        var f = new GeoFilter("Беларусь", "Минск");
        var p = Posting("QA инженер (remote)", "Anywhere", "Полностью удалённая работа, гибкий график.");
        Assert.True(GeoMatcher.Match(p, f, RemoteKind.RemoteAllowed).Pass);
    }

    [Fact]
    public void RemoteAllowed_ForeignCountry_PassesWithoutFilter()
    {
        // Гео не задано (весь мир) — чужая страна не проверяется: всё показываем.
        var p = Posting("Support (remote)", "Anywhere", "Based in the USA.");
        Assert.True(GeoMatcher.Match(p, GeoFilter.None, RemoteKind.RemoteAllowed).Pass);
    }

    // --- офис + город: совпадает/нет --------------------------------------
    [Fact]
    public void OfficeOnly_CityMatch_Falls()
    {
        var f = new GeoFilter("Беларусь", "Минск");
        var v = GeoMatcher.Match(Posting("Сисадмин", "Минск, ул. Немига"), f, RemoteKind.OfficeOnly);
        Assert.True(v.Pass);
        Assert.Contains("Минск", v.Note);
    }

    [Fact]
    public void OfficeOnly_CityMismatch_Hides()
    {
        var f = new GeoFilter("Беларусь", "Минск");
        // Страна совпадает (РБ), город — нет: причина скрытия — город.
        var v = GeoMatcher.Match(Posting("Сисадмин", "Гомель, РБ"), f, RemoteKind.OfficeOnly);
        Assert.False(v.Pass);
        Assert.Contains("Минск", v.Note);
    }

    // --- офис + только страна ---------------------------------------------
    [Fact]
    public void OfficeOnly_CountryOnly_MatchesByAlias()
    {
        var f = new GeoFilter("Беларусь", null);
        Assert.True(GeoMatcher.Match(Posting("X", "Минск"), f, RemoteKind.OfficeOnly).Pass);
        Assert.False(GeoMatcher.Match(Posting("X", "Москва"), f, RemoteKind.OfficeOnly).Pass);
    }

    [Fact]
    public void OfficeOnly_AliasesBelarus()
    {
        var f = new GeoFilter("Белоруссия", null);
        Assert.True(GeoMatcher.Match(Posting("X", "Гомель, РБ"), f, RemoteKind.OfficeOnly).Pass);
    }

    // --- гео не указано: показываем серой, чекбокс решает ------------------
    [Fact]
    public void UnknownGeo_ShownByDefault_HiddenByCheckbox()
    {
        var f = new GeoFilter("Беларусь", "Минск");
        var p = Posting("Junior C#", null);
        var shown = GeoMatcher.Match(p, f, RemoteKind.Unknown, hideUnknown: false);
        Assert.True(shown.Pass);
        Assert.True(shown.UnknownGeo);
        var hidden = GeoMatcher.Match(p, f, RemoteKind.Unknown, hideUnknown: true);
        Assert.False(hidden.Pass);
    }

    // --- нормализация: ё/е, регистр, падежи --------------------------------
    [Fact]
    public void Normalize_YoAndCase()
    {
        Assert.Equal("черный кот", GeoMatcher.Normalize("Чёрный  КОТ"));
    }

    [Fact]
    public void CityMatches_FlexibleDeclension()
    {
        Assert.True(GeoMatcher.CityMatches("в Минске, центр", "Минск"));
        Assert.True(GeoMatcher.CityMatches("Минск", "Минске"));
        Assert.False(GeoMatcher.CityMatches("Гомель", "Минск"));
    }

    // --- таблица стран ООН ------
    [Fact]
    public void CountryTable_HasAllUnMembers()
    {
        // 193 члена ООН + Ватикан + Тайвань = 195 записей; без дублей канонов.
        Assert.Equal(195, CountryTable.Entries.Count);
        Assert.Equal(195, CountryTable.Entries.Select(e => e.Canon).Distinct().Count());
    }

    [Theory]
    [InlineData("Warsaw, Poland", "польша")]
    [InlineData("Berlin, Germany", "германия")]
    [InlineData("Remote — United States", "сша")]
    [InlineData("Remote (USA only)", "сша")]
    [InlineData("South Korea / 한국", "южная корея")]
    [InlineData("New Zealand", "новая зеландия")]
    [InlineData("Минск, Беларусь", "беларусь")]
    [InlineData("Working with our German team", "германия")]
    [InlineData("Poland (Warsaw office)", "польша")]
    [InlineData("г. Гомель, РБ", "беларусь")]
    [InlineData("Based in Switzerland", "швейцария")]
    [InlineData("Must be based in the EU", null)] // ЕС — не страна
    public void DetectCountry_PhrasesAndWords(string hay, string? canon)
    {
        Assert.Equal(canon, GeoMatcher.DetectCountry(hay));
    }

    [Fact]
    public void DetectCountries_RegionalLists_ReturnAllNamed()
    {
        // WWR-кейс из лога «Poland or Lithuania» — обе страны
        // названы; ForeignCountry прячет, если хоть одна вычеркнута из списка.
        var both = GeoMatcher.DetectCountries("candidates in Poland or Lithuania");
        Assert.Contains("польша", both);
        Assert.Contains("литва", both);
    }

    [Fact]
    public void DetectCountry_DeclensionsAndStems()
    {
        // Падежи русские: «Германии/германский», «Польши/польскую».
        Assert.Equal("германия", GeoMatcher.DetectCountry("open to candidates from Germany and German-speaking countries"));
        Assert.Equal("германия", GeoMatcher.DetectCountry("офис в Германии"));
        Assert.Equal("польша", GeoMatcher.DetectCountry("работа в Польше"));
        Assert.Equal("польша", GeoMatcher.DetectCountry("польская компания"));
        Assert.Equal("россия", GeoMatcher.DetectCountry("российская компания"));
        Assert.Equal("беларусь", GeoMatcher.DetectCountry("белорусский подрядчик"));
    }

    [Fact]
    public void DetectCountry_ExactOnlyGuardsFalsePositives()
    {
        // «мали» — только точное слово: «малина» не страна.
        Assert.Null(GeoMatcher.DetectCountry("малиновый сироп"));
        // «куба» — точное слово: «кубанцы»/«кубань» не Куба (и не Краснодар).
        Assert.Null(GeoMatcher.DetectCountry("кубанцы и кубань"));
        // «чад» — точное: «чадный» не Чад.
        Assert.Null(GeoMatcher.DetectCountry("чадный угар"));
        // «сша» — точное короткое: «сша» матчится, «сшатель» нет.
        Assert.Equal("сша", GeoMatcher.DetectCountry("сша"));
        Assert.Null(GeoMatcher.DetectCountry("сшатель"));
    }

    [Fact]
    public void DetectCountry_LongestMatchWins()
    {
        // «нигер» vs «нигерия»: длинный стем побеждает.
        Assert.Equal("нигерия", GeoMatcher.DetectCountry("Отделение в Нигерии"));
        Assert.Equal("нигер", GeoMatcher.DetectCountry("Отделение в Нигере"));
        // «north korea» (фраза) бьёт слово «korea» (южная).
        Assert.Equal("кндр", GeoMatcher.DetectCountry("Based in North Korea"));
        Assert.Equal("южная корея", GeoMatcher.DetectCountry("Based in Korea"));
    }

    [Fact]
    public void CountryMatches_UsesFullTable()
    {
        // 193 страны: офисные вакансии фильтруются по любой из них.
        Assert.True(GeoMatcher.CountryMatches("Warsaw, Poland", "Польша"));
        Assert.True(GeoMatcher.CountryMatches("Бразилия, Сан-Паулу", "Бразилия"));
        Assert.False(GeoMatcher.CountryMatches("Warsaw, Poland", "Германия"));
        Assert.True(GeoMatcher.CountryMatches("Tokyo / Japan", "Япония"));
    }

    [Fact]
    public void ForeignCountry_DefaultWorldPassesExcludedHides()
    {
        var f = new GeoFilter("Беларусь", null);
        var us = Posting("X", "Anywhere", "Remote — USA only. Great team.");

        // Дефолт (allowed = null): все страны ООН — ничего не прячем.
        Assert.True(GeoMatcher.Match(us, f, RemoteKind.RemoteAllowed).Pass);

        // Вычеркнули США/Швейцарию/Германию — эти прячутся, остальные нет.
        var allowed = new HashSet<string>(["беларусь", "польша"]);
        Assert.False(GeoMatcher.Match(us, f, RemoteKind.RemoteAllowed, allowed: allowed).Pass);
        var pl = Posting("X", "Anywhere", "Remote — Poland based company.");
        Assert.True(GeoMatcher.Match(pl, f, RemoteKind.RemoteAllowed, allowed: allowed).Pass);
    }

    // --- города -> страны + приоритет локации (SAP-) --
    [Theory]
    [InlineData("Ташкент", "узбекистан")]
    [InlineData("Ташкент, узбекистан", "узбекистан")]
    [InlineData("Алматы", "казахстан")]
    [InlineData("Астана", "казахстан")]
    [InlineData("в Ташкенте", "узбекистан")]
    [InlineData("Ташкента", "узбекистан")]
    [InlineData("Минск, улица Притыцкого", "беларусь")]
    [InlineData("Гомель", "беларусь")]
    [InlineData("Москва", "россия")]
    [InlineData("в Москве", "россия")]
    [InlineData("Воронеж", "россия")]
    [InlineData("Домодедово", "россия")]
    [InlineData("Варшава", "польша")]
    [InlineData("Warsaw", null)] // латиницей город не пишем — страну ловит слово «poland»
    public void DetectCountry_CityHints(string hay, string? canon)
    {
        Assert.Equal(canon, GeoMatcher.DetectCountry(hay));
    }

    [Fact]
    public void RemoteAllowed_ForeignCityInLocation_HidesByDefaultList()
    {
        // Лог (SAP-прогон): удалёнка из Ташкента при списке
        // «Беларусь + Россия» — мимо, с человеческой причиной.
        var f = new GeoFilter("Беларусь", "Минск");
        var allowed = GeoMatcher.DefaultAllowedCountries();
        var p = Posting("SAP Expert (remote)", "Ташкент", "Режим: можно удалённо.");
        var v = GeoMatcher.Match(p, f, RemoteKind.RemoteAllowed, allowed: allowed);
        Assert.False(v.Pass);
        Assert.Contains("Узбекистан", v.Note);
    }

    [Fact]
    public void Unknown_ForeignCityInLocation_HidesToo()
    {
        // «SAP Expert», локация «Алматы», режима в тексте нет — раньше такое
        // показывалось серой («гео не указано»); страна-то названа городом — прячем.
        var f = new GeoFilter("Беларусь", "Минск");
        var allowed = GeoMatcher.DefaultAllowedCountries();
        var v = GeoMatcher.Match(Posting("SAP Expert", "Алматы"), f, RemoteKind.Unknown,
            allowed: allowed);
        Assert.False(v.Pass);
        Assert.Contains("Казахстан", v.Note);
    }

    [Fact]
    public void ForeignCountry_LocationWinsOverDescription()
    {
        // локация АВТОРИТЕТНА. Минская вакансия с упоминанием
        // ташкентских офисов в описании НЕ прячется.
        var f = new GeoFilter("Беларусь", "Минск");
        var allowed = GeoMatcher.DefaultAllowedCountries();
        var p = Posting("SAP Basis administrator (remote)", "Минск",
            "У компании офисы в Ташкенте и Алматы. Можно удалённо.");
        Assert.True(GeoMatcher.Match(p, f, RemoteKind.RemoteAllowed, allowed: allowed).Pass);
    }

    [Fact]
    public void RussiaCity_Remote_PassesByDefaultList()
    {
        // Россия в дефолтном списке: «Воронеж (удалённо)» остаётся.
        var f = new GeoFilter("Беларусь", "Минск");
        var allowed = GeoMatcher.DefaultAllowedCountries();
        var p = Posting("Администратор SAP (удалённо)", "Воронеж");
        Assert.True(GeoMatcher.Match(p, f, RemoteKind.RemoteAllowed, allowed: allowed).Pass);
    }

    [Fact]
    public void OfficeOnly_ForeignCity_CityCheckHides()
    {
        // Офис в Ташкенте при поиске по Минску — прятал и раньше («город не
        // совпал»); город-подсказка меняет только причину на страну.
        var f = new GeoFilter("Беларусь", "Минск");
        var allowed = GeoMatcher.DefaultAllowedCountries();
        var v = GeoMatcher.Match(Posting("Администратор", "Ташкент, в офисе"),
            f, RemoteKind.OfficeOnly, allowed: allowed);
        Assert.False(v.Pass);
    }

    [Fact]
    public void DefaultAllowedCountries_IsBelarusAndRussia()
    {
        // формула «отсекать неМинск/неБеларусь/неРоссия».
        var def = GeoMatcher.DefaultAllowedCountries();
        Assert.Equal(2, def.Count);
        Assert.Contains("беларусь", def);
        Assert.Contains("россия", def);
    }
}
