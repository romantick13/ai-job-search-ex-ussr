using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Services.Ai;
using AiJobSearchExUssr.Tests;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: ResumeAdaptationService — промпт несёт резюме + требования
// работодателя; AdaptAsync идёт по цепи ResumeAdaptation
// (TaskRoutingPolicy: сильные модели первыми), занятость — фолбэк.
// THREAD: FakeProvider вместо сети; SilentLog для проверок НИ.
public class ResumeAdaptationServiceTests
{
    // Фейковый провайдер: возвращает готовый контент, счётчик запросов.
    private sealed class FakeProvider(string name, int rank) : IAiChatProvider
    {
        public string Name => name;
        public int QualityRank => rank;
        public long? RemainingDailyTokens() => null;
        public int Requests { get; private set; }

        public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
        {
            Requests++;
            return Task.FromResult(new AiChatResult(
                "АДАПТИРОВАННОЕ РЕЗЮМЕ", name,
                new AiChatUsage(100, 50), name, TimeSpan.FromMilliseconds(10)));
        }
    }

    private static JobPosting Posting() => new()
    {
        PortalId = "remoteok",
        ExternalId = "77",
        CanonicalUrl = "https://example.org/v/77",
        Title = "Senior C# Developer",
        Company = "Acme Corp",
        Location = "Remote",
        SalaryText = "от 100 до 130 USD",
        DescriptionMd = "Требуется C# разработчик со знанием Avalonia.",
        DedupeHash = "TEST",
    };

    [Fact]
    public void BuildPrompt_ContainsResumeAndVacancyFields()
    {
        var (system, user) = ResumeAdaptationService.BuildPrompt(
            "моё резюме: 5 лет C#", Posting());

        // Правило правды: модель не выдумывает опыт.
        Assert.Contains("не выдумывай", system);

        // Требование резюме + требования работодателя в одном промпте.
        Assert.Contains("моё резюме: 5 лет C#", user);
        Assert.Contains("Senior C# Developer", user);
        Assert.Contains("Acme Corp", user);
        Assert.Contains("Требуется C# разработчик", user);
        Assert.Contains("https://example.org/v/77", user);
    }

    [Fact]
    public void BuildPrompt_Carries_ChannelReviewRules()
    {
        // r22j: ревью промпта по материалам канала Step Up Dev (@developerKiryl,
        // JobJump) — hh с октября скринит отклики ИИ до рекрутера (обязательные
        // vs желательные требования), плюс «правило трёх вопросов» для строк опыта.
        var (system, _) = ResumeAdaptationService.BuildPrompt("резюме", Posting());

        // ИИ-скрининг назван целью адаптации.
        Assert.Contains("ИИ-скрининг", system);

        // Правило трёх вопросов: строка выдерживает три уточняющих «почему».
        Assert.Contains("три уточняющих вопроса", system);

        // Честная пометка вместо выдумывания: [нет подтверждения: …].
        Assert.Contains("[нет подтверждения:", system);

        // ATS ищет дословные совпадения — названия технологий как в вакансии.
        Assert.Contains("дословно", system);

        // Буллет «что сделал → результат»; цифры не выдумываются.
        Assert.Contains("что сделал (технологии) → результат", system);
        Assert.Contains("новые не добавляй", system);
    }

    [Fact]
    public void BuildPrompt_TruncatesLongDescription()
    {
        var posting = Posting() with
        {
            DescriptionMd = new string('x', ResumeAdaptationService.MaxVacancyDescriptionChars + 500),
        };

        var (_, user) = ResumeAdaptationService.BuildPrompt("резюме", posting);

        // Обрезка: экономия дневных лимитов бесплатной цепи. Запас +700 —
        // шапка вакансии (название/компания/ссылка/язык ) фиксированная.
        Assert.True(user.Length < ResumeAdaptationService.MaxVacancyDescriptionChars + 700);
        Assert.Contains("(обрезано)", user);
    }


    // Язык адаптации = язык вакансии: если сама вакансия пишется на
    // английском — адаптация на английском, если на русском —
    // на русском. Язык определяется по тексту карточки и идёт в
    // промпт жёстким правилом.

    [Fact]
    public void DetectLanguage_RussianVacancy()
    {
        var posting = Posting() with
        {
            Title = "Ведущий администратор сетей",
            Company = "ОАО АЛЕВКУРП",
            DescriptionMd = "Опыт 3-6 лет. Обслуживание серверов и сетей предприятия.",
        };

        Assert.Equal(VacancyLanguage.Russian, ResumeAdaptationService.DetectLanguage(posting));
    }

    [Fact]
    public void DetectLanguage_EnglishVacancy()
    {
        var posting = Posting() with
        {
            Title = "Senior System Administrator",
            Company = "Acme Corp",
            DescriptionMd = "We are looking for an experienced administrator. " +
                            "Requirements: Windows Server, networking, VMware.",
        };

        Assert.Equal(VacancyLanguage.English, ResumeAdaptationService.DetectLanguage(posting));
    }

    [Fact]
    public void DetectLanguage_AbbreviationsDoNotFlipRussian()
    {
        // Русская карточка с латинскими аббревиатурами (C#, MS SQL, VMware)
        // должна остаться русской — кириллицы больше.
        var posting = Posting() with
        {
            Title = "Системный администратор (C#, MS SQL)",
            DescriptionMd = "Требования: Windows Server, VMware, Hyper-V.",
        };

        Assert.Equal(VacancyLanguage.Russian, ResumeAdaptationService.DetectLanguage(posting));
    }

    [Fact]
    public void BuildPrompt_LanguageRule_GoesToBothPrompts()
    {
        var (system, user) = ResumeAdaptationService.BuildPrompt("резюме", Posting());

        // Русская карточка из Posting: правило — в системе и в задаче.
        Assert.Contains("язык ответа = язык вакансии", system);
        Assert.Contains("ЯЗЫК ВАКАНСИИ: русский", user);
        Assert.Contains("на русском языке полностью", user);
    }

    [Fact]
    public void BuildPrompt_EnglishVacancy_RequiresEnglishResume()
    {
        var posting = Posting() with
        {
            Title = "Senior DevOps Engineer",
            Company = "Global Soft",
            DescriptionMd = "Build and maintain CI/CD pipelines.",
        };

        var (system, user) = ResumeAdaptationService.BuildPrompt("резюме", posting);

        Assert.Contains("на английском", system);
        Assert.Contains("ЯЗЫК ВАКАНСИИ: английский", user);
        Assert.Contains("на английском языке полностью", user);
    }

    [Fact]
    public async Task AdaptAsync_RunsResumeAdaptationStage_AcrossChain()
    {
        var log = new SilentLog();
        var weak = new FakeProvider("weak", rank: 5);
        var strong = new FakeProvider("strong", rank: 1);
        var routing = new TaskRoutingPolicy(log, new IAiChatProvider[] { weak, strong });
        var service = new ResumeAdaptationService(log, routing);

        var result = await service.AdaptAsync("резюме", Posting());

        // Цепь: сильный первый; он жив — слабый не тратится.
        Assert.NotNull(result);
        Assert.Equal("strong", result!.ProviderName);
        Assert.Equal("АДАПТИРОВАННОЕ РЕЗЮМЕ", result.Content);
        Assert.Equal(1, strong.Requests);
        Assert.Equal(0, weak.Requests);
    }

    [Fact]
    public async Task AdaptAsync_ChainExhausted_ReturnsNull_WithLog()
    {
        // исчерпание цепи — громкое событие протокола, стадия честно null.
        var log = new SilentLog();
        var busy = new BusyProvider();
        var routing = new TaskRoutingPolicy(log, new IAiChatProvider[] { busy });
        var service = new ResumeAdaptationService(log, routing);

        var result = await service.AdaptAsync("резюме", Posting());

        Assert.Null(result);
        Assert.Contains(log.Logs, line => line.Contains("ERR") && line.Contains("исчерпаны"));
    }

    private sealed class BusyProvider : IAiChatProvider
    {
        public string Name => "busy";
        public int QualityRank => 1;
        public long? RemainingDailyTokens() => 0;

        public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
            => throw new AiProviderBusyException("бюджет исчерпан");
    }
}
