using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// LmStudioCli — контакт со ступенью lm-studio через официальный CLI
// (lms) и парсеры ВСЕХ его ответов. В свежих версиях LM Studio фоновый
// демон поднимается командой `lms daemon up` и сам держит
// OpenAI-совместимый API на http:// localhost:1234/v1/; GUI-сервер
// (lms server start) НЕ используем — он конфликтует с демоном за файлы
// конфигурации (проверено на живых версиях).

// ЦЕПОЧКА ПОДЪЁМА (идемпотентна, вызывает декоратор LmStudioBootstrapping-
// Provider при первом доходе цепи до ступени):
// 0) lms найден на машине? нет -> ступень вне цепи, один громкий skip;
// 1) lms daemon up -> маркеры "already running (PID: N)" / "started
// (PID: N)" = успех; нераспознанный текст = WARN, правду скажет lms ps;
// 2) lms ps -> загруженные: строки ТОЛЬКО после шапки IDENTIFIER (иначе
// "No models are currently loaded." даёт фантомные модели No/To/lms);
// 3) целевая модель не загружена:
// - загружены ЧУЖИЕ -> событие Mismatch (модалка: «загружена X,
// а надо Y»): загрузить нужную / работать с X / пропустить;
// - пусто -> lms ls (секция LLM, до EMBEDDING) -> выбор кандидата;
// 4) lms load <id> --yes (таймаут 120с: у 8B грузится 12.9с,
// холодный старт больше) -> текст ответа — только источник
// идентификатора из кавычек; вердикт — ПОВТОРНЫЙ lms ps (состояние),
// финальный арбитр — отклик модели (шаг 5);
// 5) проверка ОТКЛИКА: POST {base}chat/completions «ping»
// (max_tokens=1) с таймаутом 30с — порт и модель живы оба.

// Выгрузку чужих моделей (lms unload) НЕ делаем: продукт не монополист за
// VRAM — чужие модели юзера не трогаем. Вместо молчаливой порчи — честная
// модалка выбора (см. выше).

// Выбор LM Studio как ступени: локальный рантайм без ключа и лимитов.
// r21: свап под эмбеддер вырезан вместе с векторизацией (lms unload
// теперь нигде не разрешён — чужие модели юзера не трогаются вовсе).
// При «Проверить всех» модель lm-studio
// грузится АВТОМАТОМ (чекбокс «авто-LMS» на морде, SettingsKV
// lms.autoload; AutoLoad), после загрузки — проверка ОТКЛИКА модели
// (chat-ping, 1 локальный токен) с таймаутом 30 с — загружать модель
// и проверять после загрузки отклик; 30 секунд обычно
// этого хватает.

// Гейт по тексту `lms load` ненадёжен: часть версий LM
// Studio на `lms load` печатает хвост «(6.10 GiB) ⏎ To use the model in
// the API/SDK, use the identifier "qwen/qwen3.5-9b".» БЕЗ «Model loaded
// successfully» — гейт по тексту браковал УСПЕШНУЮ загрузку (15,5с) и
// уходил в фолбэк, не спросив ни ps, ни отклик. Вердикт теперь:
// - lms load — болтовня TUI: из неё берём ТОЛЬКО идентификатор;
// - правда №1 — повторный lms ps (состояние);
// - правда №2 — отклик модели (chat-ping 30с), финальный арбитр;
// - «не удалась» — только если НИ ps, НИ идентификатор не подтвердили.

// Гигиена CLI-вывода: ANSI-эскейпы, \r, пустые строки и прогресс-спиннеры
// ("Loading...") снимаются; строки склеиваются через \n, НЕ через пробел —
// иначе таблица lms ps схлопывается в одну строку и парсер видит только
// шапку. stdout и stderr читаем ПАРАЛЛЕЛЬНО (классический дедлок двух
// пайпов иначе), по таймауту процесс убиваем.

// Конфиг: AIJOB_LMS_PATH — путь к lms (по умолчанию "lms", ищется в PATH).
// ============================================================================

// / <summary>Решение по чужой модели в VRAM (модалка ступени lm-studio).</summary>
public enum LmStudioMismatchDecision
{
    // / <summary>Загрузить требуемую модель (модалка по умолчанию, Esc/Enter).</summary>
    LoadTarget,
    // / <summary>Работать с уже загруженной чужой моделью (первой из lms ps).</summary>
    UseLoaded,
    // / <summary>Пропустить ступень — цепь уходит к следующему провайдеру.</summary>
    Skip
}

// / <summary>Данные модалки «в LM Studio загружена другая модель».</summary>
public sealed record LmStudioMismatchInfo(
    IReadOnlyList<string> LoadedIdentifiers,
    string TargetModel);

// FLOW: декоратор lm-studio -> EnsureReadyAsync -> LmStudioState (модель для
// chat/completions или null = ступень мимо, Busy наружу);
// STATE: без мутабельного состояния — каждый прогон честно переспрашивает CLI;
// THREAD: вызовы сериализует декоратор (свой семафор), класс пассивен
public sealed class LmStudioCli
{
    // / <summary>Приоритет подбора кандидата из lms ls: подстроки имени по убыванию
    // / качества для письма/адаптации (список: qwen3.5 —
    // / свежайшее поколение, далее VL-инструкты, llama, базовый qwen).</summary>
    public static readonly string[] DefaultModelPriority =
        { "qwen3.5", "qwen3-vl", "qwen2.5-vl", "llama", "qwen" };

    // / <summary>Не чат-модели (OCR/ASR) — в кандидаты не попадают даже из секции LLM.</summary>
    private static readonly string[] NonChatMarkers = { "whisper", "embed", "bge-", "nomic", "ocr", "rerank", "mineru" };

    public const int LoadTimeoutMs = 120_000;
    public const int QuickTimeoutMs = 15_000;
    public const int ProbeTimeoutMs = 5_000;
    public const int UnloadTimeoutMs = 60_000;
    // таймаут проверки отклика после загрузки — 30 с: обычно этого хватает.
    public const int ChatProbeTimeoutMs = 30_000;

    private readonly ILogService _log;
    private readonly string _lmsPath;
    private readonly string _httpBase;
    private readonly Func<HttpClient>? _httpClientFactory;
    // Тестовый шов: подмена запуска процесса (аргументы) -> вывод.
    // В бою всегда null — работает честный Process.Start; в тестах фикстуры
    // дословных ответов CLI подаются без реальных процессов.
    private readonly Func<string, string?>? _runner;

    // / <summary>
    // / Хендлер модалки «загружена чужая модель». Подписывает MainWindow
    // / (UI-поток через Dispatcher). null (тесты/headless) = осознанный
    // / дефолт LoadTarget: молча грузим требуемую, исход кричит в протокол.
    // / </summary>
    public Func<LmStudioMismatchInfo, Task<LmStudioMismatchDecision>>? MismatchHandler { get; set; }

    // / <summary>
    // / (чекбокс «авто-LMS» на морде, SettingsKV lms.autoload, дефолт
    // / ВКЛ — максимум автоматики): чужая модель в VRAM — молча грузим
    // / требуемую рядом (LoadTarget), модалку не показываем. false — решение
    // / за через модалку (поведение).
    // / </summary>
    public bool AutoLoad { get; set; }

    public string LmsPath => _lmsPath;

    public LmStudioCli(ILogService log, string? lmsPath = null, string? httpBase = null,
        Func<HttpClient>? httpClientFactory = null, Func<string, string?>? runner = null)
    {
        _log = log;
        _lmsPath = lmsPath
                   ?? Environment.GetEnvironmentVariable("AIJOB_LMS_PATH")
                   ?? "lms";
        _httpBase = httpBase ?? OpenAiCompatibleProvider.LmStudioBaseUrl;
        _httpClientFactory = httpClientFactory;
        _runner = runner;
    }


    // ПАРСЕРЫ (статические, чистые — покрыты тестами дословными выводами)


    // / <summary>Итог `lms daemon up`: AlreadyRunning | Started | Unknown.</summary>
    public static (LmStudioDaemonStatus Status, int? Pid) ParseDaemonUp(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return (LmStudioDaemonStatus.Unknown, null);
        var already = Regex.Match(output, @"already running \(PID:\s*(\d+)\)", RegexOptions.IgnoreCase);
        if (already.Success) return (LmStudioDaemonStatus.AlreadyRunning, int.Parse(already.Groups[1].Value));
        var started = Regex.Match(output, @"(?:started|running) \(PID:\s*(\d+)\)", RegexOptions.IgnoreCase);
        if (started.Success) return (LmStudioDaemonStatus.Started, int.Parse(started.Groups[1].Value));
        return (LmStudioDaemonStatus.Unknown, null);
    }

    // / <summary>Загруженные идентификаторы из `lms ps`: строки ТОЛЬКО после шапки
    // / IDENTIFIER — «No models are currently loaded.» фантомов не даёт.</summary>
    public static List<string> ParseLoadedIdentifiers(string output)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        bool seenHeader = false;
        foreach (var raw in output.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("IDENTIFIER", StringComparison.OrdinalIgnoreCase)) { seenHeader = true; continue; }
            if (!seenHeader) continue;

            var id = t.Split(' ', '\t')[0];
            if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
        }
        return result;
    }

    // / <summary>Кандидаты из `lms ls`: секция LLM (до EMBEDDING), шапки колонок мимо,
    // / суффикс «(N variant)» отпадает сам (первый токен строки), не-чат маркеры
    // / (whisper/ocr/embed/…) отфильтрованы. Порядок = порядок каталога.</summary>
    public static List<string> ParseLlmCandidates(string output)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        bool inLlm = false;
        foreach (var raw in output.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("LLM", StringComparison.OrdinalIgnoreCase)) { inLlm = true; continue; }
            if (t.StartsWith("EMBEDDING", StringComparison.OrdinalIgnoreCase)) { inLlm = false; continue; }
            if (!inLlm) continue;
            if (t.Contains("PARAMS") || t.Contains("ARCH") || t.Contains("DEVICE")) continue;

            var first = t.Split(' ', '\t')[0];
            if (first.Length == 0) continue;
            if (NonChatMarkers.Any(m => first.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(first);
        }
        return result;
    }

    // / <summary>Итог `lms load <id> --yes`: сигналы успеха + идентификатор из
    // / кавычек (его и подставляем в model= для chat/completions).
    // / текст — только ПОДСКАЗКА, не вердикт: у LM Studio печатает
    // / хвост «(6.10 GiB) ⏎ To use the model in the API/SDK, use the
    // / identifier "…» БЕЗ «Model loaded successfully».</summary>
    public static (bool Success, string? Identifier) ParseLoad(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return (false, null);
        var success =
            output.Contains("Model loaded successfully", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("use the identifier", StringComparison.OrdinalIgnoreCase);
        var id = Regex.Match(output, "identifier\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return (success, id.Success ? id.Groups[1].Value : null);
    }

    // / <summary>Точное совпадение алиаса — БЕЗ ловушки Contains («qwen» не матчит
    // / «qwen2.5-vl»): полное равенство или равенство последнего сегмента после «/».</summary>
    public static bool Matches(string loadedId, string model) =>
        string.Equals(loadedId, model, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(loadedId.Split('/').Last(), model.Split('/').Last(), StringComparison.OrdinalIgnoreCase);


    // ПРОЦЕСС И ГИГИЕНА ВЫВОДА


    // / <summary>Однократная проверка: есть ли lms на этой машине (абсолютный путь
    // / или поиск по PATH с суффиксами .exe/.cmd — как ставится на Windows).</summary>
    public bool LmsExists()
    {
        try
        {
            if (Path.IsPathRooted(_lmsPath)) return File.Exists(_lmsPath);

            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            foreach (var p in paths)
            {
                var dir = p.Trim();
                if (dir.Length == 0) continue;
                foreach (var suffix in new[] { "", ".exe", ".cmd" })
                    if (File.Exists(Path.Combine(dir, _lmsPath + suffix)))
                        return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // / <summary>Чистит CLI-вывод: ANSI, \r, пустые строки, спиннеры «Loading».
    // / Переносы СОХРАНЯЮТСЯ — таблицы ps/ls парсятся построчно.</summary>
    public static string CleanCli(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        s = Regex.Replace(s, "\x1b\\[[0-9;?]*[a-zA-Z]", ""); // ANSI-эскейпы
        s = s.Replace("\r", "");
        var lines = s.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.TrimStart().Length > 0)
            .Where(l => !l.TrimStart().StartsWith("Loading")) // прогресс-спиннер
            .Select(l => l.Trim());
        return string.Join("\n", lines);
    }

    // FLOW: Process.Start -> параллельное чтение stdout/stderr -> WaitForExit(таймаут)
    // -> Kill при зависании; stderr дописывается в конец (всё видно в протоколе)
    private string? RunLms(string arguments, int waitMs)
    {
        if (_runner is not null) return CleanCli(_runner(arguments) ?? "");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _lmsPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;

            var stdoutTask = p.StandardOutput.ReadToEndAsync(); // параллельно — иначе дедлок пайпов
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(waitMs))
            {
                try { p.Kill(); } catch { /* уже мёртв */ }
                _log.LogWarn($"skip: lm-studio — lms {arguments} не уложился в {waitMs / 1000}с; " +
                             "решение: ступень пропущена, цепь идёт дальше");
                return null;
            }

            var output = stdoutTask.Result;
            var error = stderrTask.Result;
            if (!string.IsNullOrWhiteSpace(error))
                output += (output.Length > 0 ? " " : "") + error.Trim();
            return CleanCli(output);
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: lm-studio — запуск '{_lmsPath} {arguments}' не удался: {ex.Message}; " +
                         "решение: проверьте установку LM Studio CLI или задайте AIJOB_LMS_PATH");
            return null;
        }
    }


    // ЦЕПОЧКА ПОДЪЁМА


    // / <param name="target">Требуемая модель (AIJOB_LMSTUDIO_MODEL либо «local-model» —
    // / тогда кандидат выбирается из каталога).</param>
    // / <returns>Модель для chat/completions или null — ступень мимо (фолбэк цепи).</returns>
    // FLOW: LmsExists -> daemon up -> ps -> [модалка чужой | ls-подбор] -> load
    // -> ps-перепроверка -> HTTP-проба порта демона; каждый исход — в протокол
    public async Task<string?> EnsureReadyAsync(string target, CancellationToken ct = default)
    {
        if (!LmsExists())
        {
            _log.LogWarn("skip: lm-studio — lms не найден на машине (PATH / AIJOB_LMS_PATH); " +
                         "решение: ступень вне цепи, установите LM Studio с CLI (lms) или задайте AIJOB_LMS_PATH");
            return null;
        }

        // 1. Демон (идемпотентно): already running | started — оба успех.
        var upOut = RunLms("daemon up", QuickTimeoutMs);
        var (status, pid) = ParseDaemonUp(upOut ?? "");
        switch (status)
        {
            case LmStudioDaemonStatus.AlreadyRunning:
                _log.Log($"[lm-studio] демон уже работает (PID: {pid}).");
                break;
            case LmStudioDaemonStatus.Started:
                _log.LogSuccess($"[lm-studio] демон поднят (PID: {pid}).");
                break;
            default:
                _log.LogWarn($"skip: lm-studio — ответ 'daemon up' не распознан: [{Truncate(upOut)}]; " +
                             "решение: продолжаю, правду скажет lms ps");
                break;
        }

        ct.ThrowIfCancellationRequested();

        // 2. Что загружено сейчас?
        var psOut = RunLms("ps", QuickTimeoutMs);
        var loaded = ParseLoadedIdentifiers(psOut ?? "");
        _log.Log($"[lm-studio] lms ps: {(loaded.Count == 0 ? "ничего не загружено" : string.Join(", ", loaded))}.");

        // 3а. Целевая уже стоит — сразу верифицируем отклик (chat-ping 30с).
        if (loaded.Any(id => Matches(id, target)))
            return await ProbeChatAsync(target, ct) ? target : null;

        // 3б. Чужие в VRAM — модалка выбора (без unload: VRAM не трогаем).
        if (loaded.Count > 0)
        {
            var decision = await DecideMismatchAsync(loaded, target);
            switch (decision)
            {
                case LmStudioMismatchDecision.UseLoaded:
                    var use = loaded[0];
                    _log.LogWarn($"skip-reason: lm-studio работает с загруженной '{use}' вместо '{target}' " +
                                 "— решение владельца в модалке; чужие модели НЕ выгружаем");
                    return await ProbeChatAsync(use, ct) ? use : null;
                case LmStudioMismatchDecision.Skip:
                    _log.LogWarn("skip: lm-studio — загрузка отменена в модалке; " +
                                 "решение: фолбэк к следующему провайдеру цепи");
                    return null;
            }
        }

        // 3в. Пусто (или пользователь выбрал «загрузить») — кандидата берём из каталога.
        var candidate = ResolveCandidate(target, psOut ?? "");
        if (candidate is null)
        {
            _log.LogError("[lm-studio] в lms ls нет чат-моделей (LLM-секция пуста или только OCR/ASR); " +
                          "решение: скачайте модель в LM Studio или задайте AIJOB_LMSTUDIO_MODEL");
            return null;
        }
        if (candidate != target)
            _log.Log($"[lm-studio] целевой '{target}' не задан/не найден — выбран кандидат '{candidate}' " +
                     "из каталога lms ls (приоритет качества).");

        // 4. Загрузка. текст lms load — болтовня TUI, вердикт по нему НЕ
        // выносим (гейт по хвосту «(6.10 GiB) ⏎ To use the model in the
        // API/SDK, use the identifier …» был признан провалом). Правда №1 —
        // lms ps, правда №2 — отклик модели (шаг 5).
        _log.Log($"[lm-studio] lms load {candidate} --yes (до {LoadTimeoutMs / 1000}с)...");
        var loadOut = RunLms($"load {candidate} --yes", LoadTimeoutMs);
        var (_, identifier) = ParseLoad(loadOut ?? "");
        var finalModel = identifier ?? candidate;

        var after = ParseLoadedIdentifiers(RunLms("ps", QuickTimeoutMs) ?? "");
        if (after.Any(id => Matches(id, finalModel)))
        {
            _log.LogSuccess($"[lm-studio] модель '{finalModel}' загружена (подтверждено lms ps).");
        }
        else if (identifier is not null)
        {
            // lms отчитался идентификатором, ps не показал (лад/другой формат) —
            // вердикт рано: пусть решает отклик модели (шаг 5).
            _log.LogWarn($"skip-note: lm-studio — lms ps не показывает '{finalModel}' " +
                         $"(загружено: [{string.Join(", ", after)}]); " +
                         "решение: верим только отклику — пробуем chat/completions");
        }
        else
        {
            _log.LogError($"[lm-studio] загрузка '{candidate}' не удалась: [{Truncate(loadOut)}]; " +
                          "решение: фолбэк к следующему провайдеру цепи");
            return null;
        }

        // 5. Модель ОТВЕЧАЕТ? (chat-ping «ping», max_tokens=1, 30с —
        // после загрузки проверять именно отклик.)
        return await ProbeChatAsync(finalModel, ct) ? finalModel : null;
    }


    // r21: свап-методы под эмбеддер (EnsureEmbedderLoadedAsync /
    // RestoreAfterEmbeddingAsync) вырезаны вместе с векторизацией.

    private async Task<LmStudioMismatchDecision> DecideMismatchAsync(
        IReadOnlyList<string> loaded, string target)
    {
        // чекбокс «авто-LMS» установлен — никакой модалки, грузим.
        if (AutoLoad)
        {
            _log.Log($"[lm-studio] чужие [{string.Join(", ", loaded)}], требуется '{target}' — " +
                     "авто-загрузка включена (чекбокс «авто-LMS»): грузим требуемую рядом, чужих не трогаем.");
            return LmStudioMismatchDecision.LoadTarget;
        }

        var handler = MismatchHandler;
        if (handler is null)
        {
            _log.LogWarn($"skip-reason: lm-studio — загружена чужая [{string.Join(", ", loaded)}], " +
                         $"требуется '{target}'; UI-модалки нет (тесты/фоновый режим) — " +
                         "решение по умолчанию: загрузить требуемую");
            return LmStudioMismatchDecision.LoadTarget;
        }

        try
        {
            var decision = await handler(new LmStudioMismatchInfo(loaded, target));
            _log.Log($"[lm-studio] решение по чужой модели: {decision}.");
            return decision;
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip-reason: lm-studio — модалка не сработала ({ex.Message}); " +
                         "решение по умолчанию: загрузить требуемую");
            return LmStudioMismatchDecision.LoadTarget;
        }
    }

    // / <summary>Кандидат из каталога: зафиксированная цель (не плейсхолдер) — она сама;
    // / иначе приоритет подстрок DefaultModelPriority по списку lms ls, затем первая LLM.</summary>
    public string? ResolveCandidate(string target, string psOutput)
    {
        // «local-model» — плейсхолдер-дефолт: реальную модель выбирает каталог.
        if (!string.IsNullOrWhiteSpace(target) &&
            !target.Equals("local-model", StringComparison.OrdinalIgnoreCase))
            return target;

        var candidates = ParseLlmCandidates(RunLms("ls", QuickTimeoutMs) ?? "");
        if (candidates.Count == 0) return null;

        foreach (var marker in DefaultModelPriority)
        {
            var hit = candidates.FirstOrDefault(c => c.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return candidates[0];
    }

    // / <summary>проверка ОТКЛИКА модели — POST {base}chat/completions
    // / («ping», max_tokens=1), таймаут 30 с (проверять после загрузки
    // / именно отклик). GET models подтверждает только
    // / порт, но не модель — она может ещё прогреваться. Токен локальный,
    // / чужие квоты не тратим.</summary>
    private async Task<bool> ProbeChatAsync(string model, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory is not null
                ? _httpClientFactory()
                : new HttpClient { Timeout = TimeSpan.FromMilliseconds(ChatProbeTimeoutMs) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ChatProbeTimeoutMs);

            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1,
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(
                new Uri(new Uri(_httpBase), "chat/completions"), content, timeout.Token);
            if (resp.IsSuccessStatusCode)
            {
                _log.LogSuccess($"[lm-studio] отклик модели '{model}' есть ({_httpBase}, таймаут " +
                                $"{ChatProbeTimeoutMs / 1000}с) — ступень готова.");
                return true;
            }
            _log.LogWarn($"skip: lm-studio — модель '{model}' не отвечает: HTTP {(int)resp.StatusCode} " +
                         $"от {_httpBase}; решение: фолбэк к следующему провайдеру цепи");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarn($"skip: lm-studio — модель '{model}' не ответила за {ChatProbeTimeoutMs / 1000}с " +
                         "(таймаут отклика — «обычно этого хватает», решение владельца 2026-10-27); " +
                         "решение: фолбэк к следующему провайдеру цепи");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarn($"skip: lm-studio — порт демона {_httpBase} не отвечает: {ex.Message}; " +
                         "решение: проверьте, что демон держит API на 1234, ступень пропущена");
            return false;
        }
    }

    private static string Truncate(string? s)
    {
        s = (s ?? "").Replace("\n", " ⏎ ");
        return s.Length <= 200 ? s : s[..200] + "…";
    }
}

// / <summary>Статус демона после `lms daemon up`.</summary>
public enum LmStudioDaemonStatus
{
    AlreadyRunning,
    Started,
    Unknown
}

// / <summary>
// / Декоратор ступени lm-studio: перед первым же чат-запросом честно
// / поднимает демона и модель через lms (LmStudioCli.EnsureReadyAsync),
// / после чего делегирует внутреннему OpenAiCompatibleProvider (порт 1234).
// / Результат бутстрапа кэшируется на процесс: повторные запросы идут
// / напрямую, пока внутренний провайдер отвечает.
// / </summary>
// FLOW: TaskRoutingPolicy -> CompleteAsync -> [первый раз: бутстрап -> OverrideModel]
// -> inner.CompleteAsync; бутстрап-исход null = AiProviderBusyException (фолбэк цепи)
// STATE: volatile bool _ready — кэш успеха; модель держит внутренний провайдер
// THREAD: бутстрап сериализован _gate; после _ready — сквозной проход
public sealed class LmStudioBootstrappingProvider : IAiChatProvider
{
    private readonly OpenAiCompatibleProvider _inner;
    private readonly LmStudioCli _cli;
    private readonly ILogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    public string Name => _inner.Name;
    public int QualityRank => _inner.QualityRank;
    public long? RemainingDailyTokens() => _inner.RemainingDailyTokens();

    // / <summary>Внутренний провайдер (карточка дашборда берёт модель/адрес отсюда).</summary>
    public OpenAiCompatibleProvider Inner => _inner;

    public LmStudioBootstrappingProvider(OpenAiCompatibleProvider inner, LmStudioCli cli, ILogService log)
    {
        _inner = inner;
        _cli = cli;
        _log = log;
    }

    // FLOW: готово? -> сквозной запрос; иначе _gate -> бутстрап -> OverrideModel -> inner
    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        if (!_ready)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (!_ready) // двойная проверка под семафором
                {
                    var model = await _cli.EnsureReadyAsync(_inner.Model, ct);
                    if (model is null)
                        throw new AiProviderBusyException(
                            "lm-studio недоступна: демон/модель не подняты (причины выше в протоколе)");
                    _inner.OverrideModel(model);
                    _ready = true;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        return await _inner.CompleteAsync(request, ct);
    }

    // / <summary>Сброс кэша готовности (следующий запрос повторит бутстрап) —
    // / для «Проверить всех» и тестов.</summary>
    public void ResetReady() => _ready = false;
}
