using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiJobSearchBy.Services;

// ============================================================================
// WP-CONFIG (RFC-004 §2.1): local.json — секреты + машинно-специфичные
// настройки окружения, живёт вне репо (%APPDATA%\ai-job-search-by\local.json —
// канон путей патча 05), создаётся при первом старте из встроенного шаблона
// local.example.json (WP-U2: секция Ai.Embeddings — BaseUrl/Model локального
// эмбеддера; не секреты, но зависят от машины — им здесь самое место)
// (EmbeddedResource; паттерн UniD/YoutubeConfigService.ReadEmbeddedDefault —
// файл всегда в сборке, путь искать не нужно).
//
// FLOW: Program.Main -> CompositionRoot.AddRegistrations -> ленивая фабрика
//       ConfigService (первый resolve — до первого провайдера, логи [config]
//       идут раньше «провайдер готов») -> ResolveAiKey (лестница env ->
//       local.json) -> провайдеры; GetPortalKey -> KeysCheckService (шов
//       ILocalSecrets, комментарий в ILocalSecrets.cs).
//
// Лестница ключа (RFC-004 §3): local.json — база; env — опциональный
// override поверх (CI/серверные сценарии); пусто -> провайдер вне цепи со
// своей честной причиной (НИ-2). Источник и маска каждого найденного ключа
// — в протоколе при старте.
//
// Консистентность при старте (контракт «никаких тихих игноров»):
//   файла нет          -> создать из шаблона, LogSuccess с путём;
//   битый JSON         -> НЕ затирать: бэкап local.corrupt-<дата>.json рядом,
//                         свежий шаблон, LogError с путём бэкапа;
//   новых полей нет    -> докатка merge'ем из шаблона, значения сохраняются,
//                         LogSuccess со списком добавленного;
//   неизвестные поля   -> LogWarn (ловец опечаток: "APIKEY" vs "ApiKey");
//   не-строка в ApiKey -> LogWarn, значение игнорируется;
//   мусор в значении   -> LogWarn (кавычки/пробелы — урок 401-саги SiliconFlow
//                         2026-10-21); значение уходит как есть — маска в
//                         логе покажет кавычку, а не первый же 401.
//
// Приватность (RFC-004, PRIVACY.md): значения ключей НИКОГДА не попадают в
// лог — только Mask() (первые и последние 4 символа); сам файл — вне репо
// (.gitignore + RepoSecretsGuardTests, RFC-004 §2.5).
//
// THREAD: LoadOrCreate — одноразовый при старте (DI-синглтон, ленивая
//         фабрика CompositionRoot); ResolveAiKey/GetPortalKey читают
//         immutable-снапшот — потокобезопасно.
// STATE: _root — снапшот local.json на момент загрузки; мутации только
//        внутри LoadOrCreate (merge), после загрузки — только чтение.
// ============================================================================
public sealed class ConfigService : ILocalSecrets
{
    public const string ConfigFileName = "local.json";

    // Суффикс встроенного шаблона (EndsWith-поиск имени ресурса — UniD).
    private const string TemplateResourceSuffix = "local.example.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ILogService _log;
    private readonly JsonObject _root;

    public string FilePath { get; }
    public bool WasCreated { get; }

    // Поля, долившиеся merge'ем при этой загрузке (пусто — схема была полной).
    public IReadOnlyList<string> AddedFields { get; }

    private ConfigService(ILogService log, string filePath, JsonObject root,
        bool wasCreated, IReadOnlyList<string> addedFields)
    {
        _log = log;
        FilePath = filePath;
        _root = root;
        WasCreated = wasCreated;
        AddedFields = addedFields;
    }

    // ------------------------------------------------------------------
    // FLOW: первый resolve из DI -> файл/шаблон/merge -> снапшот готов.
    // baseDirOverride — только тесты; продакшн-путь резолвится как в
    // CompositionRoot.StatePath (guard абсолютного пути К1, немой фолбэк
    // рядом с exe — НИ-2).
    // ------------------------------------------------------------------
    public static ConfigService LoadOrCreate(ILogService log, string? baseDirOverride = null)
    {
        try
        {
            var dir = ResolveDir(log, baseDirOverride);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ConfigFileName);
            var template = ParseTemplate(log);

            if (!File.Exists(path))
            {
                WriteAtomic(path, template.ToJsonString(Indented));
                log.LogSuccess($"[config] {path} создан из шаблона — вставь ключи в секции " +
                               "Ai.*.ApiKey (файл вне репо, в git не попадёт); env работает как override поверх файла");
                return new ConfigService(log, path, template, wasCreated: true, addedFields: Array.Empty<string>());
            }

            JsonObject? user;
            try
            {
                user = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (Exception ex)
            {
                RebuildCorrupt(log, path, dir, template, ex);
                return new ConfigService(log, path, template, wasCreated: false, addedFields: Array.Empty<string>());
            }
            if (user is null)
            {
                RebuildCorrupt(log, path, dir, template, new Exception("корень JSON — не объект"));
                return new ConfigService(log, path, template, wasCreated: false, addedFields: Array.Empty<string>());
            }

            var added = MergeMissing(user, template, prefix: "");
            var unknown = UnknownFields(user, template, prefix: "");

            if (added.Count > 0)
            {
                WriteAtomic(path, user.ToJsonString(Indented));
                log.LogSuccess($"[config] схема дополнена новыми полями: {string.Join(", ", added)} — " +
                               "существующие значения сохранены");
            }
            if (unknown.Count > 0)
            {
                var head = string.Join(", ", unknown.Take(5)) +
                           (unknown.Count > 5 ? $" и ещё {unknown.Count - 5}" : "");
                log.LogWarn($"[config] неизвестные поля в {ConfigFileName}: {head} — опечатка?; " +
                            "решение: сверить имена с local.example.json (канон: Ai.{Provider}.ApiKey, Portals.{portal}.ApiKey)");
            }
            CheckVersion(user, template, log);

            return new ConfigService(log, path, user, wasCreated: false, addedFields: added);
        }
        catch (Exception ex)
        {
            // НИ-2: конфиг не должен валить старт; провайдеры честно встанут
            // вне цепи со своими причинами (WARN при их старте).
            log.LogError($"[config] сбой загрузки {ConfigFileName}: {ex.Message}; " +
                         "решение: старт без секретов — ключи можно ввести позже (env или файл)");
            return new ConfigService(log, "?", new JsonObject(), wasCreated: false, addedFields: Array.Empty<string>());
        }
    }

    // ------------------------------------------------------------------
    // Лестница: env (непустой, мусор — WARN, значение как есть) ->
    // local.json -> null (провайдер сам крикнет свою причину, НИ-2).
    // Возвращаем (значение, источник) — источник нужен составителю лога.
    // ------------------------------------------------------------------
    public (string? Value, string Source) ResolveAiKey(string providerId, string envVarName)
    {
        var env = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            ResolveLog($"Ai.{providerId} ({envVarName}, env)", env);
            return (env, "env");
        }

        var file = ReadSecret(_root["Ai"] as JsonObject, providerId);
        if (!string.IsNullOrWhiteSpace(file))
        {
            ResolveLog($"Ai.{providerId}.ApiKey (local.json)", file!);
            return (file, "local.json");
        }

        return (null, "нет");
    }

    // ILocalSecrets: ключ портала по Id — секция Portals.{portalId}.ApiKey
    // (RFC-004 §2.1). Найден — маска в лог; нет — null (keys-check скажет
    // no_key с причиной, тихих игноров нет и тут: отсутствие секции —
    // легитимное состояние «ключ не введён»).
    public string? GetPortalKey(string portalId)
    {
        var key = ReadSecret(_root["Portals"] as JsonObject, portalId);
        if (!string.IsNullOrWhiteSpace(key))
            _log.Log($"[config] ключ Portals.{portalId} (local.json), маска {Mask(key)}");
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    // ------------------------------------------------------------------
    // WP-U1: окно настроек читает и пишет ключи Ai.{providerId}.ApiKey.
    // PeekAiKey — сырое значение ТОЛЬКО в локальное окно настроек (в лог не
    // пишется: маскирование публичных путей соблюдено, PrivACy RFC-004).
    // UpdateAiKey — атомарная запись (temp+move) с сохранением ВСЕХ остальных
    // полей файла (правка одного узла JsonObject, не пересоздание файла);
    // каждый шаг — событие протокола (НИ-4), значение — только маской.
    // ------------------------------------------------------------------
    public string? PeekAiKey(string providerId)
    {
        return ReadSecret(_root["Ai"] as JsonObject, providerId);
    }

    public void UpdateAiKey(string providerId, string? value)
    {
        if (FilePath == "?")
        {
            // НИ-2: загрузка сорвалась ранее — писать некуда, молчание запрещено.
            _log.LogError($"[config] ключ Ai.{providerId}.ApiKey не сохранён — причина: " +
                          "local.json не загружен (сбой загрузки при старте); " +
                          "решение: поправьте local.json вручную и перезапустите приложение");
            return;
        }

        var clean = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

        var ai = _root["Ai"] as JsonObject ?? new JsonObject();
        _root["Ai"] = ai;
        var section = ai[providerId] as JsonObject;
        if (section is null)
        {
            section = new JsonObject { ["ApiKey"] = clean };
            ai[providerId] = section;
        }
        else
        {
            section["ApiKey"] = clean;
        }

        try
        {
            WriteAtomic(FilePath, _root.ToJsonString(Indented));
        }
        catch (Exception ex)
        {
            _log.LogError($"[config] ключ Ai.{providerId}.ApiKey не записан: {ex.Message}; " +
                          "решение: проверьте права на файл и повторите");
            return;
        }

        _log.Log(clean.Length == 0
            ? $"[config] ключ Ai.{providerId}.ApiKey очищен из окна настроек ({FilePath})"
            : $"[config] ключ Ai.{providerId}.ApiKey сохранён из окна настроек, маска {Mask(clean)} ({FilePath})");
        _log.LogWarn("skip: ключи применяются при старте — провайдеры уже собраны; " +
                     "решение: перезапустите приложение, чтобы цепь перечитала ключи");
    }

    // ------------------------------------------------------------------
    // WP-U2: локальный эмбеддер — ключа нет, решают BaseUrl/Model. Лестница
    // та же (RFC-004 §3): env -> local.json Ai.Embeddings -> дефолт Ollama
    // (LocalEmbeddingService.Default*). Не секреты — в протокол идут без
    // маски, но ровно один раз при старте (составитель графа).
    // ------------------------------------------------------------------
    public (string BaseUrl, string Model) ResolveEmbeddings(string envBaseUrlVar, string envModelVar)
    {
        var baseUrl = Environment.GetEnvironmentVariable(envBaseUrlVar);
        var source = "env";
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = ReadLeaf(_root["Ai"] as JsonObject, "Embeddings", "BaseUrl");
            source = "local.json";
        }
        var model = Environment.GetEnvironmentVariable(envModelVar);
        var modelSource = "env";
        if (string.IsNullOrWhiteSpace(model))
        {
            model = ReadLeaf(_root["Ai"] as JsonObject, "Embeddings", "Model");
            modelSource = "local.json";
        }

        baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? Services.Ai.LocalEmbeddingService.DefaultBaseUrl
            : baseUrl.Trim();
        model = string.IsNullOrWhiteSpace(model)
            ? Services.Ai.LocalEmbeddingService.DefaultModel
            : model.Trim();
        if (source == "env" && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envBaseUrlVar)))
        {
            source = "дефолт";
        }
        if (modelSource == "env" && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envModelVar)))
        {
            modelSource = source;
        }

        _log.Log($"[config] эмбеддер: base={baseUrl}, model={model} ({(source == modelSource ? source : source + "/" + modelSource)})");
        return (baseUrl, model);
    }

    // Сырые значения для окна настроек (пустые строки — «в файле не задано,
    // работает дефолт»; в UI подставится дефолт при сохранении).
    public (string BaseUrl, string Model) PeekEmbeddings()
        => (ReadLeaf(_root["Ai"] as JsonObject, "Embeddings", "BaseUrl") ?? "",
            ReadLeaf(_root["Ai"] as JsonObject, "Embeddings", "Model") ?? "");

    // Атомарная запись Ai.Embeddings (temp+move) с сохранением всех остальных
    // полей; пустая строка = «вернуть дефолт» (лестница упадёт в Default*).
    public void UpdateEmbeddings(string baseUrl, string model)
    {
        if (FilePath == "?")
        {
            _log.LogError($"[config] настройки эмбеддера не сохранены — причина: " +
                          "local.json не загружен (сбой загрузки при старте); " +
                          "решение: поправьте local.json вручную и перезапустите приложение");
            return;
        }

        var ai = _root["Ai"] as JsonObject ?? new JsonObject();
        _root["Ai"] = ai;
        var section = ai["Embeddings"] as JsonObject;
        if (section is null)
        {
            section = new JsonObject();
            ai["Embeddings"] = section;
        }
        section["BaseUrl"] = string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.Trim();
        section["Model"] = string.IsNullOrWhiteSpace(model) ? "" : model.Trim();

        try
        {
            WriteAtomic(FilePath, _root.ToJsonString(Indented));
        }
        catch (Exception ex)
        {
            _log.LogError($"[config] настройки эмбеддера не записаны: {ex.Message}; " +
                          "решение: проверьте права на файл и повторите");
            return;
        }

        _log.Log($"[config] эмбеддер сохранён из окна настроек: base={section["BaseUrl"]}, " +
                 $"model={section["Model"]} ({FilePath})");
        _log.LogWarn("skip: настройки эмбеддера применяются при старте — сервис уже собран; " +
                     "решение: перезапустите приложение");
    }

    private void ResolveLog(string what, string value)
    {
        if (IsDirty(value))
        {
            _log.LogWarn($"[config] ключ {what}: кавычки или пробелы по краям/внутри значения — " +
                         $"похоже на ошибку вставки; маска: {Mask(value)}; решение: поправь значение " +
                         $"(env: set VAR=значение без кавычек; навсегда: setx VAR \"значение\"; файл: {FilePath})");
        }
        else
        {
            _log.Log($"[config] ключ {what}: маска {Mask(value)}");
        }
    }

    // Строка-значение секции: section[key].ApiKey. Не строка — WARN и null.
    private string? ReadSecret(JsonObject? section, string key)
        => ReadLeaf(section, key, "ApiKey");

    // Строка-лист секции: section[key].{leaf}. Не строка — WARN и null.
    private string? ReadLeaf(JsonObject? section, string key, string leaf)
    {
        if (section is null || !section.TryGetPropertyValue(key, out var node) || node is not JsonObject obj)
            return null;
        if (obj.TryGetPropertyValue(leaf, out var v))
        {
            if (v is JsonValue val && val.TryGetValue<string>(out var s))
                return s;
            _log.LogWarn($"[config] поле {key}.{leaf} не строка — игнорирую; " +
                         "решение: значение — строка в кавычках");
        }
        return null;
    }

    // Путь: override (тесты) -> AIJOB_CONFIG_DIR (изоляция тестов контейнера)
    // -> %APPDATA%\ai-job-search-by -> фолбэк рядом с exe (кричим).
    private static string ResolveDir(ILogService log, string? baseDirOverride)
    {
        var dir = baseDirOverride ?? Environment.GetEnvironmentVariable("AIJOB_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
            return dir;

        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
        if (!rooted)
        {
            log.LogWarn("[config] профильный каталог не резолвится — local.json ляжет " +
                        "рядом с exe (фолбэк AppContext.BaseDirectory)");
        }
        return Path.Combine(rooted ? profileDir : AppContext.BaseDirectory, "ai-job-search-by");
    }

    // Битый файл: копия с таймштампом, свежий шаблон, крик с путём бэкапа.
    // Значения юзера не теряются — бэкап лежит рядом.
    private static void RebuildCorrupt(ILogService log, string path, string dir, JsonObject template, Exception reason)
    {
        var backup = Path.Combine(dir, $"local.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            File.Copy(path, backup, overwrite: true);
        }
        catch (Exception copyEx)
        {
            log.LogError($"[config] бэкап битого {ConfigFileName} не удался: {copyEx.Message} — " +
                         "решение: файл не тронут, исправь его руками (RFC-004: секреты не пересоздаются молча)");
            return;
        }
        WriteAtomic(path, template.ToJsonString(Indented));
        log.LogError($"[config] {ConfigFileName} не читается: {reason.Message} — файл НЕ затёрт, " +
                     $"бэкап: {backup}; создан свежий из шаблона; решение: перенеси ключи из бэкапа");
    }

    // Докатка недостающих полей из шаблона (рекурсивно). Существующие
    // значения не трогаем НИКОГДА — это и есть «не перезаписывать ключи».
    private static List<string> MergeMissing(JsonObject user, JsonObject template, string prefix)
    {
        var added = new List<string>();
        foreach (var (name, tNode) in template)
        {
            var exists = user.TryGetPropertyValue(name, out var uNode) && uNode is not null;
            if (!exists)
            {
                user[name] = tNode?.DeepClone();
                added.Add(prefix + name);
                continue;
            }
            // Пустые объекты в шаблоне (Portals) — открытые секции: вглубь
            // не идём, юзерские ключи порталов легитимны.
            if (tNode is JsonObject tObj && tObj.Count > 0 && uNode is JsonObject uObj)
                added.AddRange(MergeMissing(uObj, tObj, prefix + name + "."));
        }
        return added;
    }

    // Ловец опечаток: листья юзера, которых нет в шаблоне. Portals.* —
    // открытая секция, там любые Id легитимны — не шумим.
    private static List<string> UnknownFields(JsonObject user, JsonObject template, string prefix)
    {
        var unknown = new List<string>();
        foreach (var (name, uNode) in user)
        {
            if (!template.TryGetPropertyValue(name, out var tNode) || tNode is null)
            {
                if (prefix == "Portals.")
                    continue;
                unknown.Add(prefix + name);
                continue;
            }
            if (uNode is JsonObject uObj && tNode is JsonObject tObj && tObj.Count > 0)
                unknown.AddRange(UnknownFields(uObj, tObj, prefix + name + "."));
        }
        return unknown;
    }

    private static void CheckVersion(JsonObject user, JsonObject template, ILogService log)
    {
        if (user["version"] is not JsonValue uVal || !uVal.TryGetValue<int>(out var userVersion)) return;
        if (template["version"] is not JsonValue tVal || !tVal.TryGetValue<int>(out var templateVersion)) return;
        if (userVersion < templateVersion)
        {
            log.Log("[config] схема local.json старее шаблона — недостающие поля долились merge'ем");
        }
        else if (userVersion > templateVersion)
        {
            log.LogWarn($"[config] local.json версии {userVersion} — файл из более новой версии программы; " +
                        "решение: обнови приложение");
        }
    }

    // RFC-004 §2.1: атомарная запись — temp + move.
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static JsonObject ParseTemplate(ILogService log)
    {
        var asm = typeof(ConfigService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(TemplateResourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            // НИ-2: шаблона нет — кричим; файл создастся минимальным, ключи
            // всё равно подхватятся из env.
            log.LogError($"[config] встроенный шаблон *{TemplateResourceSuffix} не найден в сборке — " +
                         "решение: проверь csproj (EmbeddedResource local.example.json) и пересобери");
            return new JsonObject
            {
                ["version"] = 1,
                ["Ai"] = new JsonObject(),
                ["Portals"] = new JsonObject(),
            };
        }
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject
            ?? throw new InvalidOperationException("шаблон local.example.json — корень не JsonObject");
    }

    // Мусор во вставке: крайние пробелы и кавычки где угодно (кавычки из
    // `set VAR="..."` в cmd становятся частью значения — урок 2026-10-21).
    private static bool IsDirty(string value)
        => value != value.Trim() || value.Contains('"') || value.Contains('\'');

    // Маска: первые и последние 4 символа через «…» (≥12), иначе короче.
    // Управляющие символы экранируем, кавычки НЕ прячем — они и есть улика.
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(пусто)";
        var v = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return v.Length >= 12 ? $"{v[..4]}…{v[^4..]}"
             : v.Length >= 6 ? $"{v[..2]}…{v[^2..]}"
             : "***";
    }
}
