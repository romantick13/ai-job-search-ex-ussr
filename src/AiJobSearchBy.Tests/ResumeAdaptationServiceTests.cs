using AiJobSearchBy.Models;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: ResumeAdaptationService (WP-U1) — промпт несёт резюме + требования
// работодателя (решение владельца); AdaptAsync идёт по цепи ResumeAdaptation
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

        // Требование владельца: резюме + требования работодателя в одном промпте.
        Assert.Contains("моё резюме: 5 лет C#", user);
        Assert.Contains("Senior C# Developer", user);
        Assert.Contains("Acme Corp", user);
        Assert.Contains("Требуется C# разработчик", user);
        Assert.Contains("https://example.org/v/77", user);
    }

    [Fact]
    public void BuildPrompt_TruncatesLongDescription()
    {
        var posting = Posting() with
        {
            DescriptionMd = new string('x', ResumeAdaptationService.MaxVacancyDescriptionChars + 500),
        };

        var (_, user) = ResumeAdaptationService.BuildPrompt("резюме", posting);

        // Обрезка: экономия дневных лимитов бесплатной цепи.
        Assert.True(user.Length < ResumeAdaptationService.MaxVacancyDescriptionChars + 400);
        Assert.Contains("(обрезано)", user);
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
        // НИ-1: исчерпание цепи — громкое событие протокола, стадия честно null.
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
