using System.Text.Json;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: LocalEmbeddingService (WP-U2) — самопробник (идентичная пара обязана
// склеиться, разные тексты — отлипнуть), разбор data[].embedding (+index),
// «никаких тихих игноров»: отказ/мусор/смена схемы — событие протокола + null.
// THREAD: сетевой слой выключен — FakeHttpHandler (паттерн WP-P0).
public class LocalEmbeddingServiceTests
{
    private const string Base = "http://emb.test/v1";
    private const string Model = "emb-test-model";

    private const string Same = "aijsb-embed-probe-same";
    private const string Other = "aijsb-embed-probe-other";

    private static LocalEmbeddingService Make(FakeHttpHandler handler, SilentLog? log = null)
        => log is null
            ? new LocalEmbeddingService(new HttpClient(handler), new SilentLog(), Base, Model)
            : new LocalEmbeddingService(new HttpClient(handler), log, Base, Model);

    private static HttpResponseMessage EmbeddingsResponse(params float[][] vectors)
        => FakeHttpHandler.Of(JsonSerializer.Serialize(new
        {
            data = vectors.Select((v, i) => new { embedding = v, index = i }).ToArray(),
        }), "application/json");

    private static string[] ParseInputs(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("input")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

    // Универсальный ответчик: пробник (3 входа) различает same/other, обычный
    // батч строит вектор из длины текста — детерминированно и без сети.
    private static FakeHttpHandler OkHandler()
        => new(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var inputs = ParseInputs(body);
            return inputs.Length == 3
                ? EmbeddingsResponse(
                    inputs.Select(t => t == Same ? new float[] { 1f, 0f } : new float[] { 0f, 1f }).ToArray())
                : EmbeddingsResponse(
                    inputs.Select(t => new float[] { t.Length, 1f }).ToArray());
        });

    [Fact]
    public async Task Probe_Passes_ServiceEnabled_PostsCorrectShape()
    {
        var log = new SilentLog();
        var handler = OkHandler();
        var service = Make(handler, log);

        // До пробника — честный оптимизм (ключа нет, рантайм локальный).
        Assert.True(service.Enabled);
        Assert.Equal(Model, service.Model);

        var vectors = await service.EmbedAsync("любой текст");

        Assert.NotNull(vectors);
        Assert.True(service.Enabled);
        // OpenAI-совместимая форма: POST {base}/embeddings, model+input, без Bearer.
        Assert.Equal($"{Base}/embeddings", handler.Requests[0].RequestUri!.ToString());
        Assert.Null(handler.Requests[0].Headers.Authorization);
        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("\"model\":\"emb-test-model\"", body);
        Assert.Contains(Same, body);
        Assert.Contains(log.Logs, line => line.Contains("OK") && line.Contains("самопробник ок"));
    }

    [Fact]
    public async Task Probe_DeadEndpoint_Disables_WithoutNetworkSpam()
    {
        var log = new SilentLog();
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Of("{\"error\":\"model not found\"}", "application/json", 404));
        var service = new LocalEmbeddingService(new HttpClient(handler), log, Base, Model);

        Assert.Null(await service.EmbedBatchAsync(["текст один"]));

        Assert.False(service.Enabled);
        // НИ-2: отказ с решением, не с молчанием.
        Assert.Contains(log.Logs, line => line.Contains("WARN") && line.Contains("решение") && line.Contains("ollama pull"));

        // Провал кэшируется: повторный вызов без сети (никакого спама).
        var before = handler.RequestCount;
        Assert.Null(await service.EmbedBatchAsync(["текст два"]));
        Assert.Equal(before, handler.RequestCount);
    }

    [Fact]
    public async Task Probe_DegenerateVectors_Fail()
    {
        // Урок ранних сборок llama.cpp: «идеальные» одинаковые векторы на
        // разные тексты — мусор; такой рантайм в базу не пускаем.
        var log = new SilentLog();
        var handler = new FakeHttpHandler(_ => EmbeddingsResponse([1f, 0f], [1f, 0f], [1f, 0f]));
        var service = Make(handler, log);

        Assert.Null(await service.EmbedBatchAsync(["текст"]));

        Assert.False(service.Enabled);
        Assert.Contains(log.Logs, line => line.Contains("WARN") && line.Contains("некорректные векторы"));
    }

    [Fact]
    public async Task EmbedBatch_ParsesVectors_InInputOrder()
    {
        var handler = OkHandler();
        var service = Make(handler);

        var vectors = await service.EmbedBatchAsync(["два", "слова"]);

        Assert.NotNull(vectors);
        Assert.Equal(2, vectors!.Count);
        Assert.Equal([3f, 1f], vectors[0]); // "два" — 3 символа
        Assert.Equal([5f, 1f], vectors[1]); // "слова" — 5 символов
    }

    [Fact]
    public async Task EmbedBatch_ResponseSortedByIndex()
    {
        // Открытая схема: data[] приходит перепутанным, но с index — порядок
        // ответа обязан совпасть с порядком входа.
        var handler = new FakeHttpHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var inputs = ParseInputs(body);
            if (inputs.Length == 3)
            {
                // Перепутанный порядок: [other, same, same] при индексах 2, 0, 1.
                var payload = JsonSerializer.Serialize(new
                {
                    data = new[]
                    {
                        new { embedding = new float[] { 0f, 1f }, index = 2 },
                        new { embedding = new float[] { 1f, 0f }, index = 0 },
                        new { embedding = new float[] { 1f, 0f }, index = 1 },
                    },
                });
                return FakeHttpHandler.Of(payload, "application/json");
            }
            return EmbeddingsResponse(inputs.Select(t => new float[] { t.Length, 1f }).ToArray());
        });
        var service = Make(handler);

        var vectors = await service.EmbedBatchAsync(["один", "два"]);

        Assert.NotNull(vectors);
        Assert.Equal([4f, 1f], vectors![0]);
        Assert.Equal([3f, 1f], vectors[1]);
    }

    [Fact]
    public async Task EmbedBatch_HttpError_ReturnsNull_WithLog()
    {
        var calls = 0;
        var log = new SilentLog();
        var handler = new FakeHttpHandler(request =>
        {
            calls++;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return ParseInputs(body).Length == 3
                ? EmbeddingsResponse([1f, 0f], [1f, 0f], [0f, 1f])
                : FakeHttpHandler.Of("{\"detail\":\"overloaded\"}", "application/json", 503);
        });
        _ = calls;
        var service = Make(handler, log);

        var vectors = await service.EmbedBatchAsync(["текст"]);

        Assert.Null(vectors);
        Assert.Contains(log.Logs, line => line.Contains("ERR") && line.Contains("503"));
    }

    [Fact]
    public async Task EmbedBatch_CountMismatch_ReturnsNull_WithLog()
    {
        var log = new SilentLog();
        var handler = new FakeHttpHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return ParseInputs(body).Length == 3
                ? EmbeddingsResponse([1f, 0f], [1f, 0f], [0f, 1f])
                : EmbeddingsResponse([1f, 2f]); // один вектор на два входа
        });
        var service = Make(handler, log);

        Assert.Null(await service.EmbedBatchAsync(["a", "b"]));

        Assert.Contains(log.Logs, line => line.Contains("ERR") && line.Contains("2 входах"));
    }

    [Fact]
    public async Task ResetProbe_AllowsRetry_AfterFailure()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(request =>
        {
            calls++;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var inputs = ParseInputs(body);
            if (calls == 1)
            {
                return FakeHttpHandler.Of("{}", "application/json", 503); // первый пробник — рантайм «лежит»
            }
            return inputs.Length == 3
                ? EmbeddingsResponse([1f, 0f], [1f, 0f], [0f, 1f])
                : EmbeddingsResponse(inputs.Select(t => new float[] { 1f, 2f }).ToArray());
        });
        var service = Make(handler);

        Assert.Null(await service.EmbedBatchAsync(["a"]));
        Assert.False(service.Enabled);

        // Явное действие юзера (кнопка «⟳ PDF») — новая попытка самопробника.
        service.ResetProbe();
        var vectors = await service.EmbedBatchAsync(["a"]);

        Assert.NotNull(vectors);
        Assert.True(service.Enabled);
        Assert.Equal(3, handler.RequestCount); // пробник-неудача + пробник-ок + батч
    }

    [Fact]
    public void VectorJson_RoundTrip()
    {
        var vector = new float[] { 0.5f, -1.25f, 3f };
        var json = VectorJson.To(vector);

        Assert.Equal(vector, VectorJson.From(json));
        Assert.Null(VectorJson.From(null));
        Assert.Null(VectorJson.From("не json"));
    }
}
