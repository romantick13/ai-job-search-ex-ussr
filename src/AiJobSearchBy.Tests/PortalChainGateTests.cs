using System.Net;
using AiJobSearchBy;
using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.ViewModels;
using Xunit;

namespace AiJobSearchBy.Tests;

// ============================================================================
// Гейты портального слоя: ретрай порталов на 403/429/503/сеть,
// «*» как пустой запрос у API-порталов (jobicy HTTP 400), Bearer в пробе
// каталога (siliconflow 401 при живом ключе), счётчики ApplyVacancies
// («в окно 96» при 90 скрытых) и резолвер папки откликов (<exe>\resume).
// ============================================================================
public sealed class PortalChainGateTests
{
    private static PortalDescriptor FakeDescriptor(string id)
        => new()
        {
            Id = id,
            DisplayName = id,
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.OfficialApi,
            Auth = PortalAuthKind.None,
            DefaultPriority = 8,
            ApplySupported = true,
        };

    private static JobPosting MakePosting(PortalDescriptor descriptor, string url, string? title)
        => new()
        {
            PortalId = descriptor.Id,
            ExternalId = "1",
            CanonicalUrl = url,
            Title = title,
            SourceClass = descriptor.SourceClass,
            AccessKind = descriptor.Access,
            DedupeHash = DedupeHasher.ComputeComposite(descriptor.Id, "1", url),
            FetchedAt = DateTimeOffset.UtcNow,
        };

    // Фейковый API-портал с глушённой паузой ретрая (тесты без ожиданий).
    private sealed class FakeApiPortal(FakeHttpHandler handler, string id = "fake")
        : ApiPortalBase(FakeDescriptor(id), new HttpClient(handler), new SilentLog())
    {
        protected override Task RetryPauseAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default)
            => throw new NotSupportedException("fake");

        protected override string BuildSearchUrl(string query)
            => $"https://api.fake.test/search?q={Uri.EscapeDataString(query)}";

        protected override Task<IReadOnlyList<JobPosting>> ParseSearchAsync(string payload, CancellationToken ct)
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            var result = new List<JobPosting>();
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                result.Add(MakePosting(Descriptor,
                    item.GetProperty("url").GetString()!,
                    item.GetProperty("title").GetString()));
            }

            return Task.FromResult<IReadOnlyList<JobPosting>>(result);
        }
    }

    private static readonly string Items = """{"items":[{"external_id":"42","url":"https://fake.test/v/42","title":"Vue Dev"}]}""";

    // ------------------------------------------------------------ ретрай

    [Fact]
    public async Task Retry_SecondAttemptPasses_TwoRequests()
    {
        // Лог: remotive 403 два прогона подряд — щиты и
        // лимиты часто отпускают после паузы. Одна пауза → вторая попытка.
        var calls = 0;
        var handler = new FakeHttpHandler(_ => ++calls == 1
            ? FakeHttpHandler.Of("forbidden", "text/plain", 403)
            : FakeHttpHandler.Of(Items, "application/json"));
        var portal = new FakeApiPortal(handler);

        var postings = await portal.SearchAsync("vue");

        var posting = Assert.Single(postings);
        Assert.Equal("Vue Dev", posting.Title);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_Exhausted_ThrowsHumanizedReason()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of("rate", "text/plain", 429));
        var portal = new FakeApiPortal(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => portal.SearchAsync("vue"));

        Assert.Contains("429", ex.Message);
        Assert.Equal(2, handler.RequestCount); // одна пауза — максимум
    }

    [Fact]
    public async Task NonRetryableStatus_FailsImmediately()
    {
        // 410 Gone — не ретраится (ретраи оркестратора — сверх одной паузы базы).
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of("gone", "text/plain", 410));
        var portal = new FakeApiPortal(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => portal.SearchAsync("vue"));
        Assert.Equal(1, handler.RequestCount);
    }

    // --------------------------------------- «*» = каталог целиком, без параметра

    [Fact]
    public async Task Jobicy_StarQuery_SendsNoTagParam()
    {
        // Лог: поиск «*» → GET …tag=%2A → HTTP 400.
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of("""{"jobs":[]}""", "application/json"));
        var portal = new JobicyPortal(new HttpClient(handler), new SilentLog());

        await portal.SearchAsync("*");

        var url = Assert.Single(handler.Requests).RequestUri!.ToString();
        Assert.EndsWith("/api/v2/remote-jobs?count=50", url);
        Assert.DoesNotContain("tag=", url);
    }

    [Fact]
    public async Task Remotive_StarQuery_SendsNoSearchParam()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Of("""{"jobs":[]}""", "application/json"));
        var portal = new RemotivePortal(new HttpClient(handler), new SilentLog());

        await portal.SearchAsync("*");

        var url = Assert.Single(handler.Requests).RequestUri!.ToString();
        Assert.EndsWith("/api/remote-jobs?limit=50", url);
        Assert.DoesNotContain("search=", url);
    }

    // ------------------------------------------------- проба каталога с ключом

    private sealed class AuthCapturingHandler : HttpMessageHandler
    {
        public string? LastAuthorization;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"Qwen/Qwen3-8B"}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ProbeModels_SendsBearer_WhenKeyPresent()
    {
        // Лог: siliconflow — проба на старте 401 при живом
        // ключе (GET models без Bearer), «Проверить всех» тем временем ОК.
        var handler = new AuthCapturingHandler();
        var provider = new OpenAiCompatibleProvider(
            new SilentLog(), name: "siliconflow",
            OpenAiCompatibleProvider.SiliconFlowBaseUrl,
            model: "Qwen/Qwen3-8B", apiKey: "test-key", requireApiKey: true,
            httpClient: new HttpClient(handler)
            {
                BaseAddress = new Uri(OpenAiCompatibleProvider.SiliconFlowBaseUrl),
            });

        var alive = await provider.ProbeModelsAsync();

        Assert.True(alive);
        Assert.Equal("Bearer test-key", handler.LastAuthorization);
    }

    // -------------------------------------------------- счётчики ApplyVacancies

    [Fact]
    public void ApplyVacancies_ReturnsShownAndHiddenCounts()
    {
        // Лог: «в окно 96» при ~90 скрытых — итоговая
        // строка обязана показывать, сколько реально дошло до окна.
        var log = new SilentLog();
        var vm = new MainViewModel(log, providers: null)
        {
            SearchCountry = "Беларусь",
        };

        var postings = new[]
        {
            Make("Vue Developer", location: "Remote"),
            // Дефолтный список стран — Беларусь + Россия (всё вне Минска/
            // Беларуси/России отсекается), поэтому «Remote — USA» теперь ГЕО-скрытие (страна
            // не в списке), а не релевантность.
            Make("Customer Support Manager", location: "Remote — USA"),
            Make("Sales Engineer", location: "Remote"), // ни одного слова запроса
        };

        var (shown, geoHidden, irrelevant) = vm.ApplyVacancies(postings, "vue");

        if (shown != 1 || geoHidden != 1 || irrelevant != 1)
        {
            Assert.Fail($"shown={shown}, geoHidden={geoHidden}, irrelevant={irrelevant}; " +
                        "протокол: " + string.Join(" | ", log.Logs));
        }
    }

    private static JobPosting Make(string title, string location)
        => new()
        {
            PortalId = "fake",
            ExternalId = title,
            CanonicalUrl = $"https://fake.test/v/{Uri.EscapeDataString(title)}",
            Title = title,
            Location = location,
            SourceClass = PortalSourceClass.Commercial,
            AccessKind = PortalAccessKind.OfficialApi,
            DedupeHash = DedupeHasher.ComputeComposite("fake", title, $"https://fake.test/v/{title}"),
            FetchedAt = DateTimeOffset.UtcNow,
        };

    // --------------------------------------------------- папка откликов

    [Fact]
    public void ResolveOutbox_LegacyDocuments_FallsBackToExeResume()
    {
        var exeResume = ApplicationStore.DefaultOutboxDir;
        Assert.EndsWith("resume", exeResume); // папка рядом с exe

        Assert.Equal(exeResume, ApplicationStore.ResolveOutbox(null));
        Assert.Equal(exeResume, ApplicationStore.ResolveOutbox(""));
        Assert.Equal(exeResume, ApplicationStore.ResolveOutbox(ApplicationStore.LegacyOutboxDir));
        // Кастомный путь юзера живёт как есть.
        Assert.Equal(@"D:\мои-отклики", ApplicationStore.ResolveOutbox(@"D:\мои-отклики"));
    }
}
