using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// OpenAiCompatibleProvider — один класс на все OpenAI-совместимые
// эндпоинты продуктовой цепи: ModelScope,
// SiliconFlow, OpenRouter, OrcaRouter, lm-studio (Bionic). Паттерн Llm7Provider: локальные лимит-регистры + 429/402/403
// = AiProviderBusyException (фолбэк цепи) + громкий протокол каждого исхода.

// УРОКИ ПРОТОКОЛА 2026-10-30 (прогон с провалом всей цепи на QueryExpansion):
// 1) free-каталог OpenRouter звал «наилучшей» музыкальную lyria (pricing
//    prompt "0", выход платный) -> 402. Бесплатность = ноль на входе И выходе,
//    архитектура — только текст (музыка/audio/embeddings мимо).
// 2) 403 «only available on agentic harnesses» — отказ МОДЕЛИ, не ключа
//    (401 был бы про ключ): Busy -> перебор продолжается следующей моделью.
//    Прежний текст зря обвинял ключ и звал выпускать новый.
// 3) reasoning-модели (qwen3.5 локально, gpt-oss на llm7) при мелком
//    max_tokens сжигают бюджет на размышления — content пуст. Лечится
//    каскадом: /no_think qwen-семейству, Reasoning: low для gpt-oss,
//    бутстрап lm-studio обходит reasoning-семьи, MaxTokens стадии с запасом.

// ПЕРВИОИСТОЧНИКИ ЛИМИТОВ (цитаты с официальных страниц провайдеров):
// • ModelScope: «2,000 API Inference calls per day» на аккаунт, «200 calls
// per model per day» максимум, «single-request concurrency for every
// developer», сброс ежедневно UTC+8 00:00 (= 19:00 по Минску).
// base: https://api-inference.modelscope.ai/v1/ — ключ ОБЯЗАТЕЛЕН.
// Живая эмпирика: Read-only токен с
// международной modelscope.ai работает ТОЛЬКО на .ai — китайский .cn
// отвечает 401 «Authentication failed» (площадки независимы, токены
// НЕ взаимозаменяемы); Magicubes требует подтверждённый email (403
// «complete your profile via email verification»); гибридным Qwen
// слать enable_thinking=false, иначе шлюз Tengine отдаёт 504 Gateway
// Timeout; бесплатный каталог .ai узкий: Qwen3-8B и
// DeepSeek-R1-Distill-Qwen-7B = 400 «has no provider supported»,
// Qwen/Qwen3.5-397B-A17B живой (тест «pong» при enable_thinking=false).
// • SiliconFlow: «The Rate Limits for free models are fixed» — чат
// RPM 1000–10000, TPM 50 000–5 000 000; тир L0 (старт): RPM 1000 / TPM
// 40 000. Суточные TPD/RPD free-моделей в доках НЕ публикуются —
// фиксируем фактически строками [cost]. base: https://api.siliconflow.com/v1/
// (international; .cn совместим) — ключ ОБЯЗАТЕЛЕН.
// • Ollama локально: «Local server requires no auth... unlimited (bounded
// by local hardware)». base: http://localhost:11434/v1/ — без ключа,
// безлимит; сервер не поднят = Busy с причиной (фолбэк цепи, не краш).
// • LM Studio / Bionic: тот же контракт на http://localhost:1234/v1/.

// ДЕФОЛТНЫЕ МОДЕЛИ — ModelScope и SiliconFlow подтверждены живыми тестами
// («pong» на .ai и 80 моделей каталога). Каталоги
// ротируются: ProbeModelsAsync на старте сверяет модель
// с живым GET models и кричит при расхождении; точный id ставится env'ом
// AIJOB_MODELSCOPE_MODEL / AIJOB_SILICONFLOW_MODEL / AIJOB_OLLAMA_MODEL.

// КОНФИГ (env, паттерн llm7):
// AIJOB_<ИМЯ>_API_KEY — ключ (облачным обязателен, локальным игнорируется)
// AIJOB_<ИМЯ>_MODEL — id модели (иначе дефолт из константы профиля)
// ============================================================================
public sealed class OpenAiCompatibleProvider : IAiChatProvider
{
    // --- Профили: ModelScope и SiliconFlow —
    // в цепь; ollama-local и lm-studio — локальные ступени «для тех, у кого
    // есть GPU». Капы — с официальных страниц провайдеров, без выдумок. ---
    public const string ModelScopeBaseUrl = "https://api-inference.modelscope.ai/v1/";
    // Живой тест: «pong» на .ai; мелкие модели в каталоге .ai
    // отсутствуют (400 «has no provider supported»), Qwen3-32B не проверена.
    public const string ModelScopeDefaultModel = "Qwen/Qwen3.5-397B-A17B";
    public const long ModelScopeDailyRequestCap = 2000; // на аккаунт, все модели суммарно

    public const string SiliconFlowBaseUrl = "https://api.siliconflow.com/v1/";
    public const string SiliconFlowDefaultModel = "Qwen/Qwen3-8B";

    public const string LmStudioBaseUrl = "http://localhost:1234/v1/";

    // OpenRouter и OrcaRouter — в
    // дефолтной цепи: основные поставщики «100-лимитного» промта
    // (free-тарифы: OpenRouter 50 запр/сутки, депозит $10 → 1000/сутки;
    // OrcaRouter ступени 50 → 800/сутки — README «ИИ-провайдеры», живой чек
    // evidence/orcorouter-free-package.json). Локальных кап НЕТ:
    // 429 провайдера = AiProviderBusyException → фолбэк цепи (заведомую
    // цифру в локальный регистр не пишем — она зависит от баланса юзера).
    // Дефолтные модели ротируются у обоих: точный id — env *_MODEL;
    // проба каталога при старте и «Проверить всех» честно покажут расхождение.
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1/";
    public const string OpenRouterDefaultModel = "deepseek/deepseek-chat-v3.1:free"; // суффикс :free — слот бесплатного тира
    public const string OrcaRouterBaseUrl = "https://api.orcarouter.ai/v1/";
    // free_alias из живого чека (пул ротируется — см. README).
    public const string OrcaRouterDefaultModel = "deepseek/deepseek-v4.1-flash-free";

    public string Name { get; }
    public int QualityRank { get; }

    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly string _model;
    private volatile string? _modelOverride;  // lm-studio: модель, выбранная бутстрапом (OverrideModel)
    private readonly string _apiKey;
    private readonly bool _requireApiKey;
    private readonly bool _isLocal;
    private readonly string _baseLabel;
    private readonly bool _ownsHttpClient;
    private readonly AiBudgetLedger? _reqLedger;   // капа в ЗАПРОСАХ/сутки (ModelScope)
    private readonly AiBudgetLedger? _tokenLedger; // капа в ТОКЕНАХ/сутки (если задана)
    private readonly SemaphoreSlim? _serialGate;   // одновременные запросы (ModelScope: 1)
    private readonly bool _disableThinking;        // гибридным Qwen шлём enable_thinking=false (иначе 504 шлюза)
    // Режим агрегатора: список бесплатных ->
    // наилучшая -> отказ = следующая -> удачная («липучая») в файл.
    // Активен только когда юзер НЕ зафиксировал модель (env/conf _MODEL).
    private readonly bool _freeCatalogActive;
    private readonly StickyModelLedger? _sticky;
    private List<string>? _freeCatalogCache;       // каталог free-моделей — один GET на процесс
    private readonly bool _explicitModel;          // модель задана явно (конфиг/env) — каталог не трогаем

    // STATE: имена полей — как в анонимном типе (без camelCase-политики);
    // null-члены не сериализуются — enable_thinking уходит только при выключенном thinking.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsKeyMissing { get; }

    // / <summary>Локальный провайдер (ollama/lm-studio): без ключа, без сетевых проб на старте.</summary>
    public bool IsLocal => _isLocal;

    // модель для карточки дашборда (морда вчерне); IAiChatProvider
    // сознательно не расширяли — сурфейс только у конкретных типов.
    // OverrideModel бутстрапа lm-studio показывает актуальную (загруженную) модель.
    public string Model => _modelOverride ?? _model;

    // / <summary>Подмена модели бутстрапом lm-studio после загрузки через lms
    // / (идентификатор из кавычек ответа «Model loaded successfully...»).
    // / Потокобезопасно: volatile-поле, читается в payload каждого запроса.</summary>
    public void OverrideModel(string model)
    {
        _modelOverride = model;
        _log.Log($"[{Name}] модель подменена бутстрапом: {model} (было: {_model})");
    }

    // «Проверить всех»: фактический адрес провайдера — проба GET
    // {base}models идёт по нему (цепочка знаковых констант CompositionRoot).
    public string BaseLabel => _baseLabel;

    // / <param name="dailyRequestCap">Дневная капа запросов (ModelScope: 2000); null — безлимит.</param>
    // / <param name="dailyTokenCap">Дневная капа токенов, если публична; null — не ведём.</param>
    // / <param name="maxConcurrent">Одновременных запросов (ModelScope: 1); null — без сериализации.</param>
    // / <param name="requireApiKey">Облачные — true (пустой ключ = Busy без HTTP), локальные — false.</param>
    // / <param name="disableThinking">true — в payload уходит enable_thinking=false:
    // / гибридные Qwen на ModelScope/SiliconFlow без него «думают» дольше таймаута
    // / шлюза (504 Gateway Timeout, эмпирика). false — поле не отправляется.</param>
    // LIFECYCLE: экземпляры — синглтоны DI (CompositionRoot); лимит-регистры
    // персистятся в state/*.json
    // THREAD: регистры под своими _gate; _serialGate сериализует вызовы (ModelScope: 1 одновременный)
    public OpenAiCompatibleProvider(
        ILogService log,
        string name,
        string baseUrl,
        string? model = null,
        string? apiKey = null,
        string? modelEnvVar = null,
        string? apiKeyEnvVar = null,
        int qualityRank = 9,
        long? dailyRequestCap = null,
        long? dailyTokenCap = null,
        int? maxConcurrent = null,
        bool requireApiKey = true,
        string? requestLedgerPath = null,
        string? tokenLedgerPath = null,
        string? signupHint = null,
        bool disableThinking = false,
        bool freeCatalog = false,
        string? stickyFilePath = null,
        HttpClient? httpClient = null)
    {
        _log = log;
        Name = name;
        QualityRank = qualityRank;
        _requireApiKey = requireApiKey;
        _isLocal = !requireApiKey;
        _ownsHttpClient = httpClient is null;
        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(120) };
            _baseLabel = _http.BaseAddress!.ToString();
        }
        else
        {
            _http = httpClient; // тесты/переиспользование: адрес задан снаружи
            _baseLabel = baseUrl;
        }

        _model = model ?? (modelEnvVar is null ? null : Environment.GetEnvironmentVariable(modelEnvVar))
                        ?? DefaultModelFor(name);
        var key = apiKey ?? (apiKeyEnvVar is null ? null : Environment.GetEnvironmentVariable(apiKeyEnvVar));
        // Агрегатор с free-каталогом: активен, пока юзер не зафиксировал модель
        // руками (параметр model или env *_MODEL) — фикс важнее автоматики.
        _explicitModel = model is not null
                         || (modelEnvVar is not null &&
                             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(modelEnvVar)));
        _freeCatalogActive = freeCatalog && !_explicitModel;
        _sticky = stickyFilePath is null ? null : new StickyModelLedger(stickyFilePath, log);
        IsKeyMissing = _requireApiKey && string.IsNullOrWhiteSpace(key);
        // Локальным ключ не нужен — Authorization не шлём вовсе (Ollama/LM Studio его игнорируют).
        _apiKey = IsKeyMissing || _isLocal ? "" : key!.Trim();

        if (IsKeyMissing)
        {
            // провайдер известен цепи, но вне её до ввода ключа — кричим причину и путь.
            _log.LogWarn($"skip: {Name} без ключа — причина: {apiKeyEnvVar ?? "ключ"} не задан; " +
                         $"решение: бесплатный ключ на {signupHint ?? "сайте провайдера"}, " +
                         $"после ввода провайдер сам встанет в цепь");
        }

        _reqLedger = dailyRequestCap is null ? null : new AiBudgetLedger(dailyRequestCap.Value, requestLedgerPath, log);
        _tokenLedger = dailyTokenCap is null ? null : new AiBudgetLedger(dailyTokenCap.Value, tokenLedgerPath, log);
        _serialGate = maxConcurrent is null ? null : new SemaphoreSlim(maxConcurrent.Value, maxConcurrent.Value);
        _disableThinking = disableThinking;

        _log.Log($"[{Name}] провайдер готов: base={_baseLabel}, model={_model}, " +
                 $"капа: {(dailyRequestCap is null ? "запросы без лимита" : $"{dailyRequestCap:N0} запр/сутки")}" +
                 $"{(dailyTokenCap is null ? "" : $" / {dailyTokenCap:N0} ток/сутки")}, " +
                 $"ключ: {(IsKeyMissing ? "НЕТ (вне цепи)" : _isLocal ? "не требуется" : "токен")}" +
                 $"{(maxConcurrent is null ? "" : $", одновременных: {maxConcurrent}")}, " +
                 $"регистры: {(requestLedgerPath ?? tokenLedgerPath ?? "в памяти процесса")}" +
                 (_disableThinking ? ", thinking: выключен (enable_thinking=false)" : "") +
                 (_freeCatalogActive ? ", режим: free-каталог (список бесплатных -> наилучшая -> отказ = следующая -> липучесть в файл)" : ""));
    }

    public long? RemainingDailyTokens() => _tokenLedger?.Remaining;

    // FLOW: TaskRoutingPolicy -> CompleteAsync -> AiChatResult (расход уходит в ResponseCostCounter оркестратора);
    // ключ/регистры на месте? -> фейл-фаст Busy (без HTTP); chat/completions под
    // _serialGate; 429/402 = Busy (фолбэк цепи); локальный сервер не отвечает = Busy;
    // прочие не-2xx = исключение наружу; usage -> регистры -> AiChatResult
    // STATE: единственные мутации — расход в _reqLedger/_tokenLedger (под их _gate); _serialGate — вход/выход
    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        if (IsKeyMissing)
        {
            var msg = "ключ не задан (см. WARN при старте) — провайдер вне цепи до ввода ключа";
            _log.LogWarn($"skip: {Name} — {msg}; решение: бесплатный ключ на сайте провайдера, " +
                         "ввести в конфиг — провайдер встанет в цепь сам");
            throw new AiProviderBusyException(msg);
        }

        if (_reqLedger is { IsExhausted: true })
        {
            var msg = $"локальный регистр: дневная капа {_reqLedger.Cap:N0} запросов исчерпана " +
                      $"(сделано {_reqLedger.UsedToday:N0}); сброс лимита — по расписанию провайдера";
            _log.LogWarn($"skip: {Name} — {msg}; решение: фолбэк к следующему провайдеру цепи " +
                         "(у ModelScope: 2000 запр/сутки на аккаунт И ≤200 на модель — смените модель)");
            throw new AiProviderBusyException(msg);
        }

        if (_tokenLedger is { IsExhausted: true })
        {
            var msg = $"локальный регистр: дневная капа {_tokenLedger.Cap:N0} токенов исчерпана " +
                      $"(потрачено {_tokenLedger.UsedToday:N0})";
            _log.LogWarn($"skip: {Name} — {msg}; решение: фолбэк к следующему провайдеру цепи");
            throw new AiProviderBusyException(msg);
        }

        if (_serialGate is not null)
            await _serialGate.WaitAsync(ct); // ModelScope: «single-request concurrency» — держим контракт

        try
        {
            return await SendChatAsync(request, ct);
        }
        finally
        {
            _serialGate?.Release();
        }
    }

    // FLOW: оркестратор: обычный профиль -> один POST; агрегатор с free-каталогом ->
    // sticky первой -> перебор бесплатных (до 3): 429/402/403 на модель = сброс
    // липучести и следующая; успех -> липучесть обновлена. Прочие сбои — наружу
    // STATE: _freeCatalogCache — каталог один GET на процесс; _sticky — файл под её _gate
    private async Task<AiChatResult> SendChatAsync(AiChatRequest request, CancellationToken ct)
    {
        if (!_freeCatalogActive)
            return await PostChatAsync(request, _modelOverride ?? _model, ct,
                "фолбэк к следующему провайдеру цепи");

        var attempts = await ResolveFreeAttemptsAsync(ct);
        if (attempts.Count == 0)
        {
            _log.LogWarn($"skip: {Name} — в каталоге нет бесплатных моделей (фильтр :free/pricing=0 пуст); " +
                         "решение: фолбэк к следующему провайдеру цепи");
            throw new AiProviderBusyException($"{Name}: free-моделей в каталоге нет");
        }

        var tried = new List<string>();
        for (var i = 0; i < attempts.Count; i++)
        {
            var model = attempts[i];
            try
            {
                var result = await PostChatAsync(request, model, ct,
                    i + 1 < attempts.Count
                        ? $"пробую следующую бесплатную модель каталога ({attempts[i + 1]})"
                        : "больше бесплатных моделей нет — фолбэк к следующему провайдеру цепи");
                _sticky?.Save(model, "chat/completions OK");
                return result;
            }
            catch (AiProviderBusyException)
            {
                // сюда попадают и 429/402, и 403 «модель недоступна аккаунту»,
                // и пустой content — во всех случаях перебор идёт дальше.
                tried.Add(model);
                _sticky?.Clear($"429/402/403 на {model}"); // перебор начнётся заново со следующей
            }
        }
        var msg = $"{Name}: бесплатные модели исчерпаны ({string.Join(" -> ", tried)})";
        _log.LogWarn($"skip: {Name} — {msg}; решение: фолбэк к следующему провайдеру цепи");
        throw new AiProviderBusyException(msg);
    }

    // / <summary>Порядок попыток free-каталога: свежая липучесть первой, затем
    // / каталог (наилучшая первая), до 3 попыток на запрос.</summary>
    private async Task<List<string>> ResolveFreeAttemptsAsync(CancellationToken ct)
    {
        var attempts = new List<string>();
        StickyModelLedger? sticky = _sticky;
        if (sticky is { IsFresh: true } && sticky.Model is not null)
        {
            attempts.Add(sticky.Model);
            _log.Log($"[{Name}] липучесть: начинаю с удачной модели {sticky.Model} " +
                     "(без перебора каталога)");
        }

        foreach (var id in await GetFreeCatalogAsync(ct))
        {
            if (attempts.Count >= 3) break;
            if (!attempts.Contains(id)) attempts.Add(id);
        }
        return attempts;
    }

    // / <summary>Бесплатные модели каталога (GET models, один GET на процесс):
    // / id с суффиксом :free или pricing (prompt и completion оба "0");
    // / только чат-модели (музыка/audio/embeddings мимо — урок lyria);
    // / «наилучшая» = больший context_length. Пусто = фолбэк цепи
    // / (наверх решает SendChatAsync).</summary>
    // STATE: _freeCatalogCache после первого успеха; сеть/парсинг — ловим в WARN
    private async Task<List<string>> GetFreeCatalogAsync(CancellationToken ct)
    {
        var cached = _freeCatalogCache;
        if (cached is not null) return cached;

        try
        {
            _log.Log($"[{Name}] запрос списка бесплатных моделей: GET models ({_baseLabel})");
            using var resp = await _http.GetAsync("models", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarn($"skip: каталог {Name} — HTTP {(int)resp.StatusCode}; " +
                             "решение: перебор бесплатных недоступен, фолбэк цепи");
                return new List<string>();
            }

            using var doc = JsonDocument.Parse(body);
            var free = new List<(string Id, long Ctx)>();
            var skipped = 0;
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString() ?? "";
                if (id.Length == 0) continue;
                // Решение: суффикс «:free» — фишка OpenRouter
                // («это у опенрутера есть»). orcarouter выведен из free-каталога
                // (CompositionRoot: freeCatalog: false), -матч «-free» снят.
                // Урок 2026-10-30: бесплатность = ноль на входе И на выходе.
                // lyria прошла с prompt "0" и платным выходом -> 402 на первом же
                // запросе. Суффикс :free честнее — шлюз сам гарантирует оба нуля.
                var isFree = id.EndsWith(":free", StringComparison.OrdinalIgnoreCase);
                long ctx = 0;
                if (!isFree && m.TryGetProperty("pricing", out var pricing) &&
                    pricing.TryGetProperty("prompt", out var prompt) &&
                    prompt.ValueKind == JsonValueKind.String &&
                    prompt.GetString() is "0" or "0.0" or "0.0000" &&
                    pricing.TryGetProperty("completion", out var completion) &&
                    completion.ValueKind == JsonValueKind.String &&
                    completion.GetString() is "0" or "0.0" or "0.0000")
                    isFree = true;
                if (!isFree) continue;
                // Урок 2026-10-30: только ЧАТ-модели — музыкальная lyria дошла
                // до «наилучшей» и отработала 402. Смотрим архитектуру каталога
                // (музыка/audio/embeddings мимо), по имени — страховочный список.
                if (!IsChatArchitecture(m, id))
                {
                    skipped++;
                    continue;
                }
                if (m.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number)
                    ctx = cl.TryGetInt64(out var v) ? v : 0;
                free.Add((id, ctx));
            }

            free.Sort((a, b) => b.Ctx.CompareTo(a.Ctx)); // наилучшая = больший контекст
            var ids = free.Select(f => f.Id).ToList();
            if (ids.Count > 0)
            {
                _freeCatalogCache = ids;
                _log.LogSuccess($"[{Name}] бесплатных моделей в каталоге: {ids.Count}; " +
                                $"наилучшая: {ids[0]}" + (ids.Count > 1 ? $", далее: {string.Join(", ", ids.Skip(1).Take(2))}" : "") +
                                (skipped > 0 ? $"; чат-фильтр убрал {skipped} (музыка/audio/embeddings/платный выход)" : ""));
            }
            return ids;
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: каталог {Name} не прочитан — причина: {ex.Message}; " +
                         "решение: перебор бесплатных недоступен, фолбэк цепи");
            return new List<string>();
        }
    }

    // FLOW: payload (enable_thinking по профилю + /no_think qwen) -> POST
    // chat/completions -> разбор choices/usage; 429/402/403 =
    // AiProviderBusyException с подсказкой busySolution (цикл free-каталога перехватывает)
    private async Task<AiChatResult> PostChatAsync(AiChatRequest request, string model, CancellationToken ct,
        string busySolution)
    {
        var sw = Stopwatch.StartNew();
        _log.Log($"[{Name}] запрос chat/completions: model={model}; " +
                 $"промпт: система {request.SystemPrompt.Length} симв + пользователь {request.UserPrompt.Length} симв" +
                 (_reqLedger is null ? "" : $"; запросов сегодня: {_reqLedger.UsedToday:N0}/{_reqLedger.Cap:N0}"));

        // r22h: гибридные Qwen (qwen3/3.5) думают по умолчанию и при мелком
        // max_tokens уносят ВЕСЬ ответ в reasoning_content (живой лог
        // 2026-10-30: qwen3.5-9b в LM Studio, 120 ток — content пуст).
        // Поле enable_thinking читают не все серверы (LM Studio молча
        // игнорирует); /no_think — софт-ключ из самого шаблона Qwen,
        // работает через любой OpenAI-совместимый сервер. Клеим к системному
        // промпту ТОЛЬКО qwen-семейству и только в профилях с выключенным
        // thinking — у llama/R1 маркер был бы просто лишним текстом.
        var system = request.SystemPrompt;
        if (_disableThinking && model.Contains("qwen", StringComparison.OrdinalIgnoreCase))
            system += "\n/no_think";

        var payload = JsonSerializer.Serialize(new
        {
            model = model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = request.UserPrompt }
            },
            max_tokens = request.MaxTokens,
            temperature = request.Temperature,
            // Гибридные Qwen (ModelScope .ai): thinking по умолчанию жжёт таймаут шлюза
            // (504, эмпирика). null — поле не пишется вовсе (локальные
            // серверы лишний параметр не видят).
            enable_thinking = _disableThinking ? (bool?)false : null
        }, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        if (_apiKey.Length > 0)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            if (_isLocal)
            {
                // Локальный сервер не поднят/модель не загружена — это конфигурация, не сбой сети:
                // цепь честно фолбэкит, пользователь видит причину и решение .
                var msg = $"локальный сервер {_baseLabel} не отвечает: {ex.Message}";
                _log.LogWarn($"skip: {Name} — {msg}; решение: поднять сервер (Ollama/LM Studio), " +
                             "загрузить модель и включить галочку провайдера — до этого цепь идёт дальше");
                throw new AiProviderBusyException(msg);
            }
            // что делали / что поймали / что решили.
            _log.LogError($"[{Name}] сеть недоступна: {ex.Message}; решение: исключение наружу — цепь решает");
            throw;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogError($"[{Name}] таймаут 120с; решение: исключение наружу — цепь решает");
            throw new TimeoutException($"{Name}: таймаут запроса chat/completions");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var shortBody = body.Length > 200 ? body[..200] + "…" : body;

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
            {
                var ra = resp.Headers.RetryAfter?.Delta is { } d
                    ? $" (Retry-After: {(int)d.TotalSeconds}с)" : "";
                var perModel = Name == "modelscope"
                    ? "; у ModelScope капа ≤200 запр/сутки НА МОДЕЛЬ — смените модель (AIJOB_MODELSCOPE_MODEL)"
                    : "";
                var msg = $"HTTP {(int)resp.StatusCode}{ra} — лимит/бюджет исчерпан на стороне {Name}{perModel}";
                // 429/402 — не сбой, а сигнал фолбэка: осознанный skip с причиной .
                _log.LogWarn($"skip: {Name} отклонил запрос — {msg}; тело: {shortBody}; " +
                             $"решение: {busySolution}");
                throw new AiProviderBusyException(msg);
            }
            // 401 = ключ отклонила СТОРОНА
            // провайдера (протух/отозван/лишний символ) — в конфиге он есть,
            // иначе сюда не дошли бы (нет ключа = BusyException до запроса).
            // Честная причина вместо «сбой», цепь дальше переберёт остальных.
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.LogError($"[{Name}] сбой: HTTP 401: {shortBody}; " +
                              "решение: ключ отклонён на стороне провайдера (истёк срок/" +
                              "отозван/скопирован с лишним символом) — выпустить новый в " +
                              "кабинете провайдера и ввести в «Настройках»; следующие " +
                              "провайдеры цепи перебираются сами");
                throw new HttpRequestException($"{Name}: HTTP 401: {shortBody}");
            }

            // r22h: 403 — НЕ про ключ (401 был бы про ключ). 403 = «доступ к ЭТОЙ
            // модели аккаунту отказан»: модель только для agentic-платформ
            // (живой лог 2026-10-30: thinkingmachines/inkling-small:free —
            // «only available on agentic harnesses»), политика данных, региональный
            // запрет. Прежний текст зря обвинял ключ и звал выпускать новый —
            // BusyException честно отправляет перебор к следующей модели каталога.
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                var msg = "HTTP 403 — провайдер отказал в доступе к модели этому аккаунту/тарифу " +
                          "(только agentic-платформы, политика данных или региональный запрет); " +
                          "ключ ни при чём (401 был бы про ключ)";
                _log.LogWarn($"skip: {Name} — {msg}; тело: {shortBody}; решение: {busySolution}");
                throw new AiProviderBusyException(msg);
            }

            _log.LogError($"[{Name}] сбой: HTTP {(int)resp.StatusCode}: {shortBody}; решение: исключение наружу — цепь решает");
            throw new HttpRequestException($"{Name}: HTTP {(int)resp.StatusCode}: {shortBody}");
        }

        string content, reasoning;
        long pTok, cTok;
        var estimated = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var message = root.GetProperty("choices")[0].GetProperty("message");
            content = ReadTextContent(message);
            // reasoning-модели (DeepSeek-R1, гибридные Qwen) уносят ответ
            // в reasoning_content: usage при этом честный, а content пустой.
            reasoning = message.TryGetProperty("reasoning_content", out var rc)
                        && rc.ValueKind == JsonValueKind.String
                ? rc.GetString() ?? ""
                : "";
            if (root.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("prompt_tokens", out var pt) &&
                usage.TryGetProperty("completion_tokens", out var ct2))
            {
                pTok = pt.GetInt64();
                cTok = ct2.GetInt64();
            }
            else
            {
                // Сервер не отдал usage — оцениваем по символам (≈4 симв/токен), кричим об оценке.
                estimated = true;
                pTok = (request.SystemPrompt.Length + request.UserPrompt.Length) / 4;
                cTok = content.Length / 4;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogError($"[{Name}] неожиданный формат ответа: {ex.Message}; тело: {shortBody}; " +
                          "решение: исключение наружу — расход этого запроса регистрами не учтён");
            throw;
        }

        _reqLedger?.Add(1);      // учитываем факт запроса (ModelScope: 2000/сутки)
        _tokenLedger?.Add(pTok + cTok);
        sw.Stop();

        // Пустой content при потраченных токенах — НЕ результат, а сбой
        // ступени: модель унесла ответ в reasoning_content или шлюз оборвал
        // генерацию до текста. Сохранять пустоту в окно/PDF/историю нельзя —
        // это и есть глух «адаптировано, а резюме пустое» (2026-10-29).
        // Честно: LogError + BusyException — цепь перебирает следующих.
        if (string.IsNullOrWhiteSpace(content))
        {
            var where = reasoning.Length == 0
                ? "content пустой (ответ шлюза без текста)"
                : $"ответ ушёл в reasoning_content ({reasoning.Length} зн.), content пустой";
            _log.LogError($"[{Name}] пустой текст ответа: {where}; {pTok + cTok} ток " +
                          $"(вх {pTok} / вых {cTok}); тело: {shortBody}; " +
                          "решение: ступень пропускается — цепь перебирает следующих");
            throw new AiProviderBusyException($"{Name}: пустой content ({where})");
        }

        _log.LogSuccess($"[{Name}] OK: {pTok + cTok} ток (вх {pTok} / вых {cTok}{(estimated ? ", оценка" : "")}) " +
                        $"за {sw.Elapsed.TotalSeconds:F1}с" +
                        (_reqLedger is null ? "" : $"; запросов сегодня: {_reqLedger.UsedToday:N0}/{_reqLedger.Cap:N0}"));
        return new AiChatResult(content, model, new AiChatUsage(pTok, cTok), Name, sw.Elapsed);
    }

    // r22h: чат-модель ли? Урок 2026-10-30 — музыкальная lyria прошла в
    // «наилучшие» и отработала 402. Чат = архитектура каталога отдаёт ТЕКСТ:
    // output_modalities ровно ["text"] (у музыки там ["audio"], у
    // embeddings — ["embedding"]) либо строка modality «…->text» («text->audio»,
    // «text->embedding» мимо). Архитектуры нет вовсе — страховочный список
    // по имени (служебные семьи). Не чат-модель — не кандидат перебора.
    internal static bool IsChatArchitecture(JsonElement m, string id)
    {
        if (m.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object)
        {
            if (arch.TryGetProperty("output_modalities", out var outs) && outs.ValueKind == JsonValueKind.Array)
            {
                var hasText = false;
                foreach (var o in outs.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.String) continue;
                    if (o.GetString() == "text") hasText = true;
                    else return false; // audio/image/video — не текстовая модель
                }

                if (!hasText) return false;
            }
            else if (arch.TryGetProperty("modality", out var mod) && mod.ValueKind == JsonValueKind.String)
            {
                // «text->text» — чат; «text->audio», «text->image», «text->embedding» — нет.
                if (!mod.GetString()!.EndsWith("->text", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }

        foreach (var marker in NonChatCatalogMarkers)
            if (id.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Служебные семьи в имени модели — даже без architecture в каталоге.
    private static readonly string[] NonChatCatalogMarkers =
        { "whisper", "tts", "embed", "rerank", "lyria", "dall-e", "imagen", "veo", "sora", "flux", "music", "voice", "speech" };

    // content у OpenAI-совместимых шлюзов бывает строкой, а бывает массивом
    // частей [{type:"text", text:"..."}] (multi-part формат); reasoning
    // идёт отдельным полем reasoning_content и сюда не попадает. Читаем
    // оба формата, остальное (null/число/объект) — «текста нет».
    private static string ReadTextContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return "";
        }

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? "";
            case JsonValueKind.Array:
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(text.GetString());
                    }
                }

                return sb.ToString();
            default:
                return "";
        }
    }

    // FLOW: GET models -> каталог в протокол -> модель в каталоге?; падение пробы = WARN, не краш
    public async Task<bool> ProbeModelsAsync(CancellationToken ct = default)
    {
        try
        {
            _log.Log($"[{Name}] проба каталога: GET models ({_baseLabel})");
            // (: siliconflow — проба 401 при живом
            // ключе, а «Проверить всех» проходила): Bearer ставится и здесь —
            // /models у части провайдеров (siliconflow) требует ключ.
            using var req = new HttpRequestMessage(HttpMethod.Get, "models");
            if (!string.IsNullOrEmpty(_apiKey))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarn($"skip: проба каталога {Name} — HTTP {(int)resp.StatusCode}; " +
                             "решение: работаем с моделью по конфигу, сверить вручную на сайте провайдера");
                return false;
            }
            using var doc = JsonDocument.Parse(body);
            var ids = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                ids.Add(m.GetProperty("id").GetString() ?? "");
            var head = string.Join(", ", ids.Take(10));
            _log.LogSuccess($"[{Name}] каталог живой: {ids.Count} моделей; первые: {head}" +
                            (ids.Count > 10 ? $" (+{ids.Count - 10})" : ""));
            if (_freeCatalogActive)
            {
                // Агрегатор: фиксированной модели нет — достаточно наличия free-слотов;
                // конкретную выберет перебор при первом запросе (SendChatAsync).
                var freeCount = ids.Count(id => id.EndsWith(":free", StringComparison.OrdinalIgnoreCase));
                if (freeCount == 0)
                {
                    // не ошибка — ступень продолжает работать по модели
                    // конфига (проверка orcarouter прошла именно так).
                    _log.LogWarn($"skip: [{Name}] в каталоге нет моделей с суффиксом :free — " +
                                 $"free-перебор пуст, ступень работает по модели конфига ({_modelOverride ?? _model})");
                }

                return true;
            }
            if (!ids.Contains(_modelOverride ?? _model))
            {
                _log.LogError($"[{Name}] модели {_modelOverride ?? _model} нет в каталоге — обновите конфиг " +
                              "(env *_MODEL); иначе первый же chat/completions вернёт ошибку");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: проба каталога {Name} не удалась — причина: {ex.Message}; " +
                         "решение: работаем с моделью по конфигу, лимиты ведут локальные регистры");
            return false;
        }
    }

    private static string DefaultModelFor(string name) => name switch
    {
        "modelscope" => ModelScopeDefaultModel,
        "siliconflow" => SiliconFlowDefaultModel,
        "openrouter" => OpenRouterDefaultModel,
        "orcarouter" => OrcaRouterDefaultModel,
        "lm-studio" => "local-model",   // LM Studio подставляет загруженную модель
        _ => "local-model"
    };
}
