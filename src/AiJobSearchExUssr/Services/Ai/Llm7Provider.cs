using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// Llm7Provider — третий провайдер продуктовой цепи.
// База: https:// api.llm7.io/v1/ (OpenAI-совместимый).

// Лимиты free-токена (docs.llm7.io/limits, снято с доков):
// 1 req/сек, 60 req/мин, 250 req/час, 100 000 токенов/24ч (вход+выход).
// Дословно: «Free-token quotas ... may be reduced without notice» —
// осенью 2026 дневную квоту уже резали в 10 раз (1M → 100K, GitHub issue).
// Поэтому: капа консервативна, расход считает локальный регистр
// (AiBudgetLedger), 429/402 = фолбэк цепи, а не ошибка.

// Анонимный доступ: подтверждён живым тестом (chat/completions с
// ключом-заглушкой 'unused' отвечает, ~76 ток списался с анонимной квоты;
// itsfree.ai, AgentDeals) — но лимиты ниже, WARN обязателен.

// Эндпоинта остатка квоты в доках НЕТ (в отличие от OrcaRouter
// GET /api/free-package/public) → остаток = локальный регистр по факту
// списаний. Каталог моделей ротируется — на старте проба GET /v1/models
// (docs.llm7.io/guides/models-api, tier=turbo — доступно free-токену).

// Конфиг: AIJOB_LLM7_API_KEY (пусто → анонимно), AIJOB_LLM7_MODEL
// (дефолт gpt-oss:20b: живой — прежний дефолт
// DeepSeek-V4-Flash-0731 к моменту запроса из каталога РОТИРОВАЛСЯ,
// gpt-oss:20b turbo-тира отработал; каталог ротируется, проба на старте
// громко ловит ротации).
// ============================================================================

public sealed class Llm7Provider : IAiChatProvider
{
    public const string DefaultBaseUrl = "https://api.llm7.io/v1/";
    // Живой каталог turbo-тира снят GET /v1/models: codestral-latest,
    // gpt-oss:20b, minimax-m2.7, mistral-Nemo-Instruct-2407, nemotron-3-nano:30b
    // (DeepSeek-V4-Flash-0731 в момент живого прогона из каталога
    // исчез — прежний дефолт протух, гейт: тест DefaultModel_Is_Live_Turbo_Model).
    // Каталог РОТИРУЕТСЯ (gpt-oss-120b/gemma из списка itsfree.ai в каталоге
    // уже нет) — потому ProbeModelsAsync на старте и громкая ошибка при
    // отсутствии модели.
    public const string DefaultModel = "gpt-oss:20b";
    public const long DefaultDailyTokenCap = 100_000;
    public const string AnonymousKey = "unused"; // ключ-заглушка анонимного доступа

    public string Name => "llm7";
    // модель для карточки дашборда (морда вчерне); IAiChatProvider
    // сознательно не расширяли — сурфейс только у конкретных типов.
    public string Model => _model;

    // «Проверить всех»: фактический адрес для пробы GET {base}models.
    public string BaseLabel => _baseLabel;
    // Turbo-тир — модели средней величины: для писем достаточно, для резюме — фолбэк.
    public int QualityRank => 3;

    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _baseLabel;
    private readonly bool _ownsHttpClient;
    private readonly AiBudgetLedger _ledger;
    public bool IsAnonymous { get; }

    // LIFECYCLE: синглтон DI; свой HttpClient (base api.llm7.io/v1/, таймаут 60с); регистр бюджета
    // персистится в state/llm7-budget.json (см. CompositionRoot)
    // STATE: AiBudgetLedger — сутки/потрачено (ротация в полночь); IsAnonymous — по AIJOB_LLM7_API_KEY
    // THREAD: AiBudgetLedger под _gate; HttpClient потокобезопасен; вызовы с любого потока
    public Llm7Provider(
        ILogService log,
        HttpClient? httpClient = null,
        string? apiKey = null,
        string? model = null,
        string? baseUrl = null,
        long dailyTokenCap = DefaultDailyTokenCap,
        string? budgetFilePath = null)
    {
        _log = log;
        _ownsHttpClient = httpClient is null;
        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl ?? DefaultBaseUrl), Timeout = TimeSpan.FromSeconds(60) };
            _baseLabel = _http.BaseAddress!.ToString();
        }
        else
        {
            _http = httpClient; // тесты/переиспользование: адрес уже задан снаружи
            _baseLabel = baseUrl ?? httpClient.BaseAddress?.ToString() ?? DefaultBaseUrl;
        }

        _model = model ?? Environment.GetEnvironmentVariable("AIJOB_LLM7_MODEL") ?? DefaultModel;
        var key = apiKey ?? Environment.GetEnvironmentVariable("AIJOB_LLM7_API_KEY");
        IsAnonymous = string.IsNullOrWhiteSpace(key);
        _apiKey = IsAnonymous ? AnonymousKey : key!.Trim();

        if (IsAnonymous)
        {
            // анонимный режим — осознанный режим с ограничениями, кричим.
            _log.LogWarn("skip: llm7 без токена — причина: AIJOB_LLM7_API_KEY не задан, " +
                         "работаем анонимно с заглушкой 'unused' (работает, но лимиты ниже); " +
                         "решение: бесплатный токен выдаёт dash.llm7.io");
        }

        _ledger = new AiBudgetLedger(dailyTokenCap, budgetFilePath, log);
        _log.Log($"[llm7] провайдер готов: base={_baseLabel}, model={_model}, " +
                 $"капа {dailyTokenCap:N0} ток/сутки (docs.llm7.io/limits 2026-10-21), " +
                 $"ключ: {(IsAnonymous ? "анонимно (заглушка)" : "токен")}, " +
                 $"регистр: {(budgetFilePath is null ? "в памяти процесса" : budgetFilePath)}");
    }

    public long? RemainingDailyTokens() => _ledger.Remaining;

    // FLOW: регистр исчерпан? -> фейл-фаст Busy (без HTTP); payload chat/completions -> 429/402 = Busy
    // (фолбэк цепи), прочие не-2xx = исключение наружу; usage -> регистр -> AiChatResult
    // STATE: расход списывается в _ledger под её _gate; мутируемого у провайдера больше нет
    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        if (_ledger.IsExhausted)
        {
            var msg = $"локальный регистр: дневной бюджет {_ledger.Cap:N0} токенов исчерпан " +
                      $"(потрачено {_ledger.UsedToday:N0}); лимит docs.llm7.io/limits от 2026-10-21";
            _log.LogWarn($"skip: llm7 — {msg}; решение: фолбэк к следующему провайдеру цепи");
            throw new AiProviderBusyException(msg);
        }

        var sw = Stopwatch.StartNew();
        _log.Log($"[llm7] запрос chat/completions: model={_model}; " +
                 $"промпт: система {request.SystemPrompt.Length} симв + пользователь {request.UserPrompt.Length} симв; " +
                 $"остаток дня по регистру: {_ledger.Remaining:N0} ток");

        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            max_tokens = request.MaxTokens,
            temperature = request.Temperature
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            // что делали / что поймали / что решили.
            _log.LogError($"[llm7] сеть недоступна: {ex.Message}; решение: исключение наружу — цепь решает");
            throw;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogError("[llm7] таймаут 60с; решение: исключение наружу — цепь решает");
            throw new TimeoutException("llm7: таймаут запроса chat/completions");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var shortBody = body.Length > 200 ? body[..200] + "…" : body;

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
            {
                var ra = resp.Headers.RetryAfter?.Delta is { } d
                    ? $" (Retry-After: {(int)d.TotalSeconds}с)" : "";
                var msg = $"HTTP {(int)resp.StatusCode}{ra} — лимит/бюджет исчерпан на стороне llm7";
                // 429/402 — не сбой, а сигнал фолбэка: осознанный skip с причиной .
                _log.LogWarn($"skip: llm7 отклонил запрос — {msg}; тело: {shortBody}; " +
                             "решение: фолбэк к следующему провайдеру цепи");
                throw new AiProviderBusyException(msg);
            }
            _log.LogError($"[llm7] сбой: HTTP {(int)resp.StatusCode}: {shortBody}; решение: исключение наружу — цепь решает");
            throw new HttpRequestException($"llm7: HTTP {(int)resp.StatusCode}: {shortBody}");
        }

        string content;
        long pTok, cTok;
        var estimated = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
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
            _log.LogError($"[llm7] неожиданный формат ответа: {ex.Message}; тело: {shortBody}; " +
                          "решение: исключение наружу — расход этого запроса регистром не учтён, сверить каталог моделей");
            throw;
        }

        _ledger.Add(pTok + cTok);
        sw.Stop();

        // Пустой content при потраченных токенах — сбой ступени, не
        // результат: сохранять пустоту в окно/PDF/историю нельзя. Честно:
        // LogError + BusyException — цепь перебирает следующих.
        if (string.IsNullOrWhiteSpace(content))
        {
            _log.LogError($"[llm7] пустой текст ответа: {pTok + cTok} ток (вх {pTok} / вых {cTok}); " +
                          $"тело: {shortBody}; решение: ступень пропускается — цепь перебирает следующих");
            throw new AiProviderBusyException("llm7: пустой content (ответ без текста)");
        }

        _log.LogSuccess($"[llm7] OK: {pTok + cTok} ток (вх {pTok} / вых {cTok}{(estimated ? ", оценка" : "")}) " +
                        $"за {sw.Elapsed.TotalSeconds:F1}с; остаток дня: {_ledger.Remaining:N0} ток");
        return new AiChatResult(content, _model, new AiChatUsage(pTok, cTok), Name, sw.Elapsed);
    }

    // / <summary>
    // / Проба каталога на старте: GET /v1/models. Каталог ротируется
    // / (awesome-free-llm-apis); если нашей модели нет — громкая ошибка в
    // / протокол. Падение пробы не роняет старт (вспомогательная проба).
    // / Возвращает true, если модель найдена в каталоге.
    // / </summary>
    // FLOW: старт цепи: GET models -> каталог в протокол (turbo-тир) -> модель в каталоге?;
    // падение пробы = WARN, не краш
    public async Task<bool> ProbeModelsAsync(CancellationToken ct = default)
    {
        try
        {
            _log.Log("[llm7] проба каталога: GET models");
            using var resp = await _http.GetAsync("models", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarn($"skip: проба каталога llm7 — HTTP {(int)resp.StatusCode}; " +
                             "решение: работаем с моделью по конфигу, сверить вручную на docs.llm7.io");
                return false;
            }
            using var doc = JsonDocument.Parse(body);
            var ids = new List<string>();
            var turbo = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString() ?? "";
                ids.Add(id);
                if (m.TryGetProperty("tier", out var t) && t.ValueEquals("turbo")) turbo.Add(id);
            }
            _log.LogSuccess($"[llm7] каталог живой: {ids.Count} моделей, turbo-тир (free): {string.Join(", ", turbo)}");
            if (!ids.Contains(_model))
            {
                _log.LogError($"[llm7] модели {_model} нет в каталоге — обновите конфиг (AIJOB_LLM7_MODEL); " +
                              "иначе первый же chat/completions вернёт ошибку");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: проба каталога llm7 не удалась — причина: {ex.Message}; " +
                         "решение: работаем с моделью по конфигу, лимиты ведёт локальный регистр");
            return false;
        }
    }
}

// ============================================================================
// AiBudgetLedger — локальный регистр дневного бюджета llm7.
// CLI живёт одним процессом на прогон: без файла остаток теряется между
// прогонами и бюджет «на бумаге» не совпадёт с серверным → персистим JSON
// рядом с дисковым протоколом (рекомендация: <профиль>/ai-job-search-ex-ussr/
// state/llm7-budget.json). Любой сбой файла кричит и не роняет процесс.
// Капа фиксируется на старте; внезапное урезание квоты сервером ловится
// обработкой 429 в провайдере (фолбэк цепи) — двойная страховка.
// ============================================================================
public sealed class AiBudgetLedger
{
    private readonly object _gate = new();
    private readonly ILogService _log;
    private readonly string? _filePath;
    private bool _persistWarned;

    public long Cap { get; }
    public DateTime Day { get; private set; }
    public long UsedToday { get; private set; }
    public long Remaining => Math.Max(0, Cap - UsedToday);
    public bool IsExhausted { get { lock (_gate) { RollDay(); return UsedToday >= Cap; } } }

    // STATE: сутки/потрачено, ротация в полночь (RollDay); капа фиксируется на старте
    // THREAD: всё состояние под _gate; Load/Save вызываются из-под того же замка
    public AiBudgetLedger(long dailyCap, string? filePath, ILogService log)
    {
        Cap = dailyCap;
        _filePath = filePath;
        _log = log;
        Day = DateTime.Now.Date;
        Load();
    }

    // FLOW: Add -> RollDay -> UsedToday += tokens -> Save (всё под _gate)
    public void Add(long tokens)
    {
        lock (_gate)
        {
            RollDay();
            UsedToday += tokens;
            Save();
        }
    }

    private void RollDay()
    {
        var today = DateTime.Now.Date;
        if (today != Day) { Day = today; UsedToday = 0; } // ротация суток леджера лимитов
    }

    private void Load()
    {
        if (_filePath is null) return;
        try
        {
            if (!File.Exists(_filePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
            var date = doc.RootElement.GetProperty("date").GetString();
            var used = doc.RootElement.GetProperty("usedTokens").GetInt64();
            if (date == DateTime.Now.Date.ToString("yyyy-MM-dd")) UsedToday = used;
        // Иначе файл вчерашний — начинаем сутки с нуля (RollDay дал бы то же).
        }
        catch (Exception ex)
        {
            WarnPersist($"чтение регистра не удалось — {ex.Message}; решение: начинаю сутки с нуля в памяти");
        }
    }

    private void Save()
    {
        if (_filePath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath,
                JsonSerializer.Serialize(new { date = DateTime.Now.Date.ToString("yyyy-MM-dd"), usedTokens = UsedToday }));
        }
        catch (Exception ex)
        {
            WarnPersist($"запись регистра не удалась — {ex.Message}; решение: считаю в памяти до конца процесса");
        }
    }

    private void WarnPersist(string message)
    {
        if (_persistWarned) return; // один крик на процесс — дальше счётчик молча не растёт
        _persistWarned = true;
        _log.LogWarn($"skip: регистр бюджета llm7 не персистентен — {message}");
    }
}
