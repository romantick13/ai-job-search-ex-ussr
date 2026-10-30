using System.Collections.ObjectModel;
using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Tests;

// FLOW: общие фейки тестов портального каркаса — без сети (FakeHttpHandler)
// и без конфиг-слоя (FakeLocalSecrets).

// Фейковый портал для реестра/keys-check.
public sealed class FakePortal : IPortalService
{
    public FakePortal(string id, int priority = 5, PortalAuthKind auth = PortalAuthKind.None,
        bool defaultEnabled = true)
    {
        Descriptor = new PortalDescriptor
        {
            Id = id,
            DisplayName = id,
            SourceClass = PortalSourceClass.Commercial,
            Access = PortalAccessKind.OfficialApi,
            Auth = auth,
            DefaultPriority = priority,
            ApplySupported = true,
            DefaultEnabled = defaultEnabled,
        };
    }

    public PortalDescriptor Descriptor { get; }

    public Task<IReadOnlyList<JobPosting>> SearchAsync(string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<JobPosting>>([]);

    public Task<JobPosting> DetailAsync(string idOrUrl, CancellationToken ct = default)
        => throw new NotSupportedException("fake");

    // фейк ведёт себя как API-портал: детали не достаёт (адаптация в тестах
    // не должна ходить в сеть за текстом вакансии).
    public bool SupportsDetail => false;

    public Task<PortalHealthReport> HealthCheckAsync(CancellationToken ct = default)
        => Task.FromResult(new PortalHealthReport(PortalHealthStatus.Ok, "fake"));
}

// Считает запросы и отдаёт заготовленные ответы — сетевой слой выключен.
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    private readonly List<HttpRequestMessage> _requests = new();

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public int RequestCount { get; private set; }

    // адаптерные тесты сверяют URL-строители по перехваченным запросам.
    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestCount++;
        _requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Of(string body, string mediaType, int status = 200)
        => new((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
        };
}

// Словарь ключей вместо local.json.
public sealed class FakeLocalSecrets : ILocalSecrets
{
    private readonly IReadOnlyDictionary<string, string> _keys;

    public FakeLocalSecrets(IReadOnlyDictionary<string, string> keys)
    {
        _keys = keys;
    }

    public string? GetPortalKey(string portalId)
        => _keys.TryGetValue(portalId, out var key) ? key : null;
}

// Немоий протокол для фикстур: кольцо в памяти, диска нет — тесты не мусорят
// на диске и в консоли; при необходимости тесты читают Logs для проверок НИ.
public sealed class SilentLog : ILogService
{
    public ObservableCollection<string> Logs { get; } = new();

    public void Log(string message) => Logs.Add($"[..] {message}");
    public void LogSuccess(string message) => Logs.Add($"[..] OK {message}");
    public void LogError(string message) => Logs.Add($"[..] ERR {message}");
    public void LogWarn(string message) => Logs.Add($"[..] WARN {message}");
    public void Clear() => Logs.Clear();

    // у фейка диска нет — каталог пустой строки (никем не читается)
    public string LogDirectory => string.Empty;
}
