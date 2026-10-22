using System.Text;
using System.Text.Json;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// WP-U2 (решение владельца 2026-10-22): Jina выбывает целиком — CC BY-NC-4.0
// на веса + экспортный риск из РБ (проверка 2026-10-22). Вместо
// неё — ЛОКАЛЬНЫЙ эмбеддер через OpenAI-совместимый POST {base}/v1/embeddings:
//   Ollama (дефолт, http://127.0.0.1:11434/v1), LM Studio (:1234/v1),
//   llama.cpp-server (--embeddings, :8080/v1) — один GGUF на все три.
// Модель по умолчанию: Qwen/Qwen3-Embedding-0.6B (Apache-2.0, 32k контекста,
// dim 1024, GGUF только q8_0/f16 — эмбеддинги агрессивную квантизацию не
// прощают). Веса приложением НЕ распространяются — их тянет рантайм юзера.
//
// Ключа нет и не нужно: «включен ли сервис» решает САМОПРОБНИК при первом
// вызове — два идентичных текста обязаны дать cos >= 0.995, разные — меньше.
// Это защита от «тихого мусора» (известная история ранних сборок llama.cpp
// на этой модели) и от лежащего рантайма одним выстрелом. Провал пробника —
// один честный WARN с решением (НИ-2), дальше null без сетевого спама;
// новая попытка — кнопка «⟳ PDF» (PdfIndexerService зовёт ResetProbe).
//
// Лестница настроек (RFC-004 §3, та же, что у ключей): env
// AIJOB_EMBEDDINGS_BASE_URL / AIJOB_EMBEDDINGS_MODEL -> local.json
// Ai.Embeddings.BaseUrl/Model -> дефолты Ollama (ConfigService.ResolveEmbeddings).
//
// «Никаких тихих игноров» (LOG-CONTRACT.md): каждый отказ сети/HTTP/схемы —
// LogError/LogWarn с причиной и решением; наружу null — векторизация не роняет
// отправку отклика и запись в базу (PDF/текст/история живут без векторов).
//
// FLOW: CompositionRoot -> PdfIndexerService / MainViewModel.SendAsync ->
//       EmbedAsync -> VectorJson.To -> ApplicationStore (VectorModel = Model).
// THREAD: HttpClient потокобезопасен; пробник — под lock с двойной проверкой.
// ============================================================================
public interface IEmbeddingService
{
    // false — самопробник провален: векторизация выключена, причина в протоколе.
    bool Enabled { get; }

    string Model { get; }

    // null — вектор построить не удалось (причина в протоколе).
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);

    // Батч-вариант для переиндексации; порядок ответа = порядку входа.
    Task<IReadOnlyList<float[]>?> EmbedBatchAsync(IReadOnlyList<string> texts,
        CancellationToken ct = default);
}

public sealed class LocalEmbeddingService : IEmbeddingService
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434/v1";
    public const string DefaultModel = "qwen3-embedding:0.6b";

    // Порог самопробника: идентичные тексты — косинус ~1.0 (допуск на float),
    // разные — заметно меньше. Если рантайм отдал «всё на всё» ~1.0 — мусор.
    private const double IdenticalCosineFloor = 0.995;

    private enum ProbeState { NotRun, Ok, Failed }

    private readonly HttpClient _http;
    private readonly ILogService _log;
    private readonly string _endpoint;
    private readonly object _probeGate = new();
    private ProbeState _probe = ProbeState.NotRun;

    public bool Enabled => _probe != ProbeState.Failed;

    public string Model { get; }

    public LocalEmbeddingService(HttpClient http, ILogService log, string baseUrl, string model)
    {
        _http = http;
        _log = log;
        _endpoint = $"{baseUrl.Trim().TrimEnd('/')}/embeddings";
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    // НИ-2: осознанный сброс самопробника — новая попытка по явному действию
    // юзера (кнопка «⟳ PDF»): рантайм могли поднять/перезапустить.
    public void ResetProbe()
    {
        lock (_probeGate)
        {
            _probe = ProbeState.NotRun;
        }
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        => (await EmbedBatchAsync([text], ct).ConfigureAwait(false)) is { Count: > 0 } list
            ? list[0]
            : null;

    public async Task<IReadOnlyList<float[]>?> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false))
        {
            return null; // причина — единственный WARN самопробника (не шумим на каждый вызов)
        }

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = Model,
                input = texts,
            });

            using var response = await _http.PostAsync(_endpoint,
                new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogError($"[emb] embeddings HTTP {(int)response.StatusCode}: {Truncate(detail)}; " +
                              "решение: вектор = null (ссылка на файл и текст в базе остаются)");
                return null;
            }

            return Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), texts.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogWarn("skip: [emb] запрос векторов отменён пользователем (ct)");
            throw;
        }
        catch (Exception ex)
        {
            // НИ-1: что делали / что поймали / что решили — вектор не строится,
            // отправка отклика и запись в базу продолжаются.
            _log.LogError($"[emb] embeddings упал — {ex.GetType().Name}: {ex.Message}; " +
                          "решение: вектор = null (индексация продолжается без векторов)");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Самопробник: один POST на три текста [same, same, other]. Идентичная
    // пара обязана склеиться (cos >= 0.995), «other» — отлипнуть. Кэш
    // результата: Ok — больше не дёргаем; Failed — один WARN, дальше null.
    // ------------------------------------------------------------------
    private async Task<bool> EnsureReadyAsync(CancellationToken ct)
    {
        if (_probe == ProbeState.Ok)
        {
            return true;
        }
        lock (_probeGate)
        {
            if (_probe == ProbeState.Ok)
            {
                return true;
            }
            if (_probe == ProbeState.Failed)
            {
                return false;
            }
        }

        var ok = await RunProbeAsync(ct).ConfigureAwait(false);
        lock (_probeGate)
        {
            _probe = ok ? ProbeState.Ok : ProbeState.Failed;
        }
        return ok;
    }

    private async Task<bool> RunProbeAsync(CancellationToken ct)
    {
        const string same = "aijsb-embed-probe-same";
        const string other = "aijsb-embed-probe-other";
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = Model,
                input = new[] { same, same, other },
            });
            using var response = await _http.PostAsync(_endpoint,
                new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarn($"skip: [emb] самопробник не прошёл — {_endpoint} HTTP {(int)response.StatusCode}: {Truncate(detail)}; " +
                             "решение: запусти локальный рантайм и подтяни эмбеддинг-модель " +
                             "(Ollama: ollama pull qwen3-embedding:0.6b; LM Studio: загрузи модель во вкладке Developer), " +
                             "проверь BaseUrl в настройках; PDF/текст/история пишутся без векторов");
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var vectors = ParseData(stream, expected: 3);
            if (vectors is null || vectors.Count != 3)
            {
                return false; // причина — LogError внутри ParseData
            }

            var dimension = vectors[0].Length;
            if (dimension == 0 || vectors.Any(v => v.Length != dimension))
            {
                _log.LogWarn("skip: [emb] самопробник не прошёл — векторы разной размерности; " +
                             "решение: проверь, что рантайм загрузил именно эмбеддинг-модель, а не чат-модель; " +
                             "PDF/текст/история пишутся без векторов");
                return false;
            }

            var identical = Cosine(vectors[0], vectors[1]);
            var different = Cosine(vectors[0], vectors[2]);
            if (identical < IdenticalCosineFloor || different >= IdenticalCosineFloor)
            {
                // Урок ранних сборок llama.cpp на этой модели: «идеальные» векторы
                // на разные тексты = мусор. Такое в базу не пускаем.
                _log.LogWarn($"skip: [emb] самопробник не прошёл — рантайм отдаёт некорректные векторы " +
                             $"(cos(идентичных)={identical:0.000}, cos(разных)={different:0.000}); " +
                             "решение: обнови рантайм (llama.cpp/Ollama) и перезапусти модель; " +
                             "PDF/текст/история пишутся без векторов");
                return false;
            }

            _log.LogSuccess($"[emb] самопробник ок: модель {Model}, dim={dimension}, " +
                            $"cos(идентичных)={identical:0.0000}, cos(разных)={different:0.0000}");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: [emb] самопробник не прошёл — {ex.GetType().Name}: {ex.Message}; " +
                         $"решение: рантайм не отвечает на {_endpoint} — запусти Ollama/LM Studio/llama-server " +
                         "и проверь BaseUrl в настройках; PDF/текст/история пишутся без векторов");
            return false;
        }
    }

    // Разбор ответа OpenAI-совместимого /v1/embeddings: data[].embedding
    // (+ data[].index — если у всех элементов есть, сортируем по нему).
    // Несовпадение количества и входа — смена схемы, LogError + null.
    private IReadOnlyList<float[]>? Parse(Stream stream, int expected)
    {
        var vectors = ParseData(stream, expected);
        return vectors is { Count: > 0 } ? vectors : null;
    }

    private List<float[]>? ParseData(Stream stream, int expected)
    {
        try
        {
            using var json = JsonDocument.Parse(stream);
            if (!json.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                _log.LogError("[emb] схема ответа сменилась: нет data[]; " +
                              "решение: вектор = null, индексация продолжается без векторов");
                return null;
            }

            var items = new List<(int? Index, float[] Vector)>();
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("embedding", out var embedding)
                    || embedding.ValueKind != JsonValueKind.Array)
                {
                    _log.LogError("[emb] элемент data[] без embedding — пропущен; " +
                                  "решение: вектор = null для этого текста");
                    return null;
                }
                int? index = item.TryGetProperty("index", out var idx)
                             && idx.ValueKind == JsonValueKind.Number
                    ? idx.GetInt32()
                    : null;
                items.Add((index, embedding.EnumerateArray().Select(v => v.GetSingle()).ToArray()));
            }

            if (items.Count != expected)
            {
                _log.LogError($"[emb] схема ответа: data[] = {items.Count} при {expected} входах; " +
                              "решение: вектор = null, индексация продолжается без векторов");
                return null;
            }
            if (items.All(i => i.Index.HasValue))
            {
                items.Sort((a, b) => a.Index!.Value.CompareTo(b.Index!.Value));
            }
            return items.Select(i => i.Vector).ToList();
        }
        catch (JsonException ex)
        {
            _log.LogError($"[emb] ответ не разбирается: {ex.Message}; " +
                          "решение: вектор = null, индексация продолжается без векторов");
            return null;
        }
    }

    private static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static string Truncate(string value)
        => value.Length <= 200 ? value.ReplaceLineEndings(" ") : value[..200].ReplaceLineEndings(" ") + "…";
}
