using AiJobSearchBy.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchBy;

// FLOW: сборка графа зависимостей — единственное место регистрации сервисов.
// Канон (RFC-001 §2): интерфейсы на всё; по мере появления слоёв (WP-P0+)
// регистрации добавляются здесь и нигде больше.
// STATE: без состояния — чистая фабрика графа.
internal static class CompositionRoot
{
    // Регистрации отдельно от сборки провайдера — тесты читают состав графа.
    // bootLog (LOG-CONTRACT.md): экземпляр, поднятый в Program.Main до контейнера
    // (crash-хендлеры вешаются раньше любой содержательной работы). null — только
    // в тестах: тогда лог регистрируется типом со своими дефолтами.
    public static IServiceCollection AddRegistrations(
        IServiceCollection services, ILogService? bootLog = null)
    {
        // Контракт «никаких тихих игноров»: протокол — первый сервис графа,
        // от него зависят все последующие (порталы пишут каждый шаг, НИ-4).
        if (bootLog is null)
        {
            services.AddSingleton<ILogService, LogService>();
        }
        else
        {
            services.AddSingleton(bootLog);
        }

        // WP-CONFIG (RFC-004 §2.1): local.json — секреты вне репо. Ленивая
        // фабрика: файл читается при первом resolve — до первого провайдера,
        // логи [config] идут раньше «провайдер готов».
        services.AddSingleton(sp =>
            ConfigService.LoadOrCreate(sp.GetRequiredService<ILogService>()));

        // Каркас WP-0: окно и его корневая модель, оба синглтоны.
        // WP-U0: VM получает тот же ILogService (живая лента дашборда) и всю
        // цепь IAiChatProvider (карточки статуса) — фабрика вместо голого типа.
        // WP-U1: + реестр порталов, база откликов, роутинг/адаптация,
        // PDF-экспорт, локальная векторизация (WP-U2), индексатор PDF.
        services.AddSingleton(sp => new ViewModels.MainViewModel(
            sp.GetRequiredService<ILogService>(),
            sp.GetServices<Services.Ai.IAiChatProvider>(),
            sp.GetRequiredService<Portals.PortalRegistry>(),
            sp.GetRequiredService<Services.IApplicationStore>(),
            sp.GetRequiredService<Services.ResumeAdaptationService>(),
            sp.GetRequiredService<Services.IPdfExporter>(),
            sp.GetRequiredService<Services.Ai.IEmbeddingService>(),
            sp.GetRequiredService<Services.PdfIndexerService>()));
        services.AddSingleton<MainWindow>();

        // WP-P0 (PLAN §7): портальный каркас. Реестр наполняется адаптерами;
        // KeysCheck читает ключи через ILocalSecrets.
        // HttpClient — общий синглтон, таймаут 60 с — донорский дефолт
        // (Atlas GitHubHarvesterService, вербатим-пин Task 51).
        services.AddSingleton(_ => new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
        });

        // WP-P1 (PLAN §7): РБ-порталы — оба DefaultEnabled=false (карточки
        // §2.11): hh-by ждёт возвращения анонимного поиска (перепроверка раз
        // в квартал), rabota-by — юридический стуб (API только работодателям).
        // В цепи дефолта их нет; включение — запись конфига Portals[] Enabled=true.
        services.AddSingleton<Portals.IPortalService, Portals.HhByPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.RabotaByPortal>();

        // WP-U1 (решение владельца 2026-10-22): шесть Core-адаптеров коробки
        // v1 (BuiltInPortals.Core, карточки §2.3) — реестр теперь 8 порталов,
        // дефолт-цепь 6 (все DefaultEnabled=true). URL и схемы ответов
        // подтверждены живым зондом 2026-10-22 (golden-фикстуры в тестах).
        services.AddSingleton<Portals.IPortalService, Portals.RemoteOkPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.RemotivePortal>();
        services.AddSingleton<Portals.IPortalService, Portals.JobicyPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.WwrPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.HimalayasPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.NodeskPortal>();

        services.AddSingleton<Portals.PortalRegistry>();
        // WP-CONFIG: шов ILocalSecrets (ILocalSecrets.cs) встал на реализацию —
        // KeysCheckService читает Portals.{id}.ApiKey из local.json.
        // NullLocalSecrets остаётся в коде для тестовых фикстур.
        services.AddSingleton<ILocalSecrets>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton<IKeysCheckService, KeysCheckService>();

        // WP-U1: база откликов (SQLite по architecture.md §4) — личные данные
        // вне репо: %APPDATA%\ai-job-search-by\data\ai-job-search-by.db.
        // Таблицы Vacancies/Applications/SettingsKV — канон §4; PdfDocuments —
        // векторные ссылки на PDF; расширения Applications — EnsureColumn.
        services.AddSingleton<Services.IApplicationStore>(sp =>
            new Services.ApplicationStore(
                DataPath(sp, "ai-job-search-by.db"),
                sp.GetRequiredService<Services.ILogService>()));

        // WP-U2 (решение владельца 2026-10-22): ЛОКАЛЬНЫЙ эмбеддер вместо Jina
        // (NC-лицензия весов + экспортный риск из РБ, проверка 2026-10-22).
        // Qwen3-Embedding-0.6B (Apache-2.0) через OpenAI-совместимый
        // /v1/embeddings: Ollama (дефолт), LM Studio, llama-server — один GGUF.
        // Ключа нет: живость решает самопробник (LocalEmbeddingService).
        // Лестница настроек: env AIJOB_EMBEDDINGS_BASE_URL/MODEL -> local.json
        // Ai.Embeddings -> дефолт Ollama (RFC-004 §3).
        services.AddSingleton<Services.Ai.IEmbeddingService>(sp =>
        {
            var (baseUrl, model) = sp.GetRequiredService<ConfigService>()
                .ResolveEmbeddings("AIJOB_EMBEDDINGS_BASE_URL", "AIJOB_EMBEDDINGS_MODEL");
            return new Services.Ai.LocalEmbeddingService(
                sp.GetRequiredService<System.Net.Http.HttpClient>(),
                sp.GetRequiredService<Services.ILogService>(),
                baseUrl, model);
        });

        // WP-U1: PDF отклика (QuestPDF 2026.8.0 — пин THIRD-PARTY-NOTICES §2,
        // Community-лицензия идемпотентно) и индексатор PDF-папки (PdfPig).
        services.AddSingleton<Services.IPdfExporter, Services.PdfExportService>();
        services.AddSingleton(sp => new Services.PdfIndexerService(
            sp.GetRequiredService<Services.IApplicationStore>(),
            sp.GetRequiredService<Services.Ai.IEmbeddingService>(),
            sp.GetRequiredService<Services.ILogService>(),
            () => sp.GetRequiredService<Services.IApplicationStore>()
                    .GetSetting("outbox.path") ?? Services.ApplicationStore.DefaultOutboxDir));

        // WP-U1: адаптация резюме (стадия ResumeAdaptation через TaskRoutingPolicy).
        services.AddSingleton<Services.ResumeAdaptationService>();

        // WP-U1: модель настроек (модальное окно шестерёнки).
        services.AddSingleton<ViewModels.SettingsViewModel>();

        // WP-A-LLM7 + WP-A-OPENAI-COMPAT (решения владельца 2026-10-21;
        // LLM7-INTEGRATION.md §7, ai-providers-card-2026-10-21.md §6):
        // ИИ-цепь. Провайдеры: llm7 (free 100K ток/сутки), modelscope
        // (2000 запр/сутки на аккаунт, ≤200/модель), siliconflow (free-модели,
        // RPM 1000+/TPM 40K+), ollama-local и lm-studio (локальные, без ключа,
        // безлимит — для владельцев GPU). Без ключа провайдер вне цепи, но
        // КРИЧИТ причину и путь (НИ-2): ввёл ключ — встал в цепь сам.
        // WP-CONFIG: ключи — лестница env -> local.json (RFC-004 §2.1);
        // источник и маска каждого ключа — в протоколе ([config] строки).
        // Стадия match ИИ НЕ тратит — LocalMatchScorer (критика Мистраля).
        services.AddSingleton<Services.Ai.LocalMatchScorer>();
        services.AddSingleton<Services.Ai.ResponseCostCounter>();
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
        {
            var log = sp.GetRequiredService<Services.ILogService>();
            var (key, _) = sp.GetRequiredService<ConfigService>()
                .ResolveAiKey("Llm7", "AIJOB_LLM7_API_KEY"); // лестница: env -> local.json
            var budgetPath = StatePath(sp, "llm7-budget.json");
            return new Services.Ai.Llm7Provider(log, budgetFilePath: budgetPath, apiKey: key);
        });
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
        {
            var log = sp.GetRequiredService<Services.ILogService>();
            var (key, _) = sp.GetRequiredService<ConfigService>()
                .ResolveAiKey("ModelScope", "AIJOB_MODELSCOPE_API_KEY");
            return new Services.Ai.OpenAiCompatibleProvider(
                log,
                name: "modelscope",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.ModelScopeBaseUrl,
                modelEnvVar: "AIJOB_MODELSCOPE_MODEL",
                apiKeyEnvVar: "AIJOB_MODELSCOPE_API_KEY",
                apiKey: key, // лестница: env -> local.json (WP-CONFIG)
                qualityRank: 2, // сильные Qwen-инструкты — на резюме первыми
                dailyRequestCap: Services.Ai.OpenAiCompatibleProvider.ModelScopeDailyRequestCap,
                maxConcurrent: 1, // «single-request concurrency» — онбординг ModelScope
                requireApiKey: true,
                disableThinking: true, // WP-A-LIVE: гибридные Qwen думают по умолчанию -> 504 Tengine
                requestLedgerPath: StatePath(sp, "modelscope-requests.json"),
                signupHint: "modelscope.ai");
        });
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
        {
            var log = sp.GetRequiredService<Services.ILogService>();
            var (key, _) = sp.GetRequiredService<ConfigService>()
                .ResolveAiKey("SiliconFlow", "AIJOB_SILICONFLOW_API_KEY");
            return new Services.Ai.OpenAiCompatibleProvider(
                log,
                name: "siliconflow",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.SiliconFlowBaseUrl,
                modelEnvVar: "AIJOB_SILICONFLOW_MODEL",
                apiKeyEnvVar: "AIJOB_SILICONFLOW_API_KEY",
                apiKey: key, // лестница: env -> local.json (WP-CONFIG)
                qualityRank: 4,
                dailyRequestCap: null, // суточные RPD free-моделей не публикуются — [cost] соберёт фактически
                requireApiKey: true,
                disableThinking: true, // WP-A-LIVE: Qwen3-8B тоже гибридный — thinking глушим
                signupHint: "siliconflow.com");
        });
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
            new Services.Ai.OpenAiCompatibleProvider(
                sp.GetRequiredService<Services.ILogService>(),
                name: "ollama-local",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.OllamaLocalBaseUrl,
                modelEnvVar: "AIJOB_OLLAMA_MODEL",
                qualityRank: 5, // локальные — резерв: медленно, зато безлимит и круглосуточно
                requireApiKey: false));
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
            new Services.Ai.OpenAiCompatibleProvider(
                sp.GetRequiredService<Services.ILogService>(),
                name: "lm-studio",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.LmStudioBaseUrl,
                modelEnvVar: "AIJOB_LMSTUDIO_MODEL",
                qualityRank: 6,
                requireApiKey: false));
        services.AddSingleton<Services.Ai.TaskRoutingPolicy>(sp =>
            new Services.Ai.TaskRoutingPolicy(
                sp.GetRequiredService<Services.ILogService>(),
                sp.GetServices<Services.Ai.IAiChatProvider>(),
                coverLetterOrder: new[] { "llm7", "modelscope", "siliconflow", "ollama-local", "lm-studio" })); // письма — от дешёвых к дорогим

        return services;
    }

    // Личные данные (регистры бюджета) — вне репо: <профиль>/ai-job-search-by/state/.
    // Guard абсолютного пути — как в LogService (К1); фолбэк рядом с exe не молчит (НИ-2).
    private static string StatePath(IServiceProvider sp, string fileName)
    {
        var log = sp.GetRequiredService<Services.ILogService>();
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
        if (!rooted)
        {
            log.LogWarn($"skip: профильный каталог не резолвится — {fileName} " +
                        "ляжет рядом с exe (фолбэк AppContext.BaseDirectory)");
        }
        var root = rooted ? profileDir : AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "ai-job-search-by", "state", fileName);
    }

    // WP-U1: личные данные откликов — <профиль>/ai-job-search-by/data/ (канон:
    // personal data только вне репо, architecture.md §8). Guard и фолбэк —
    // те же, что у StatePath (К1/НИ-2).
    private static string DataPath(IServiceProvider sp, string fileName)
    {
        var log = sp.GetRequiredService<Services.ILogService>();
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rooted = !string.IsNullOrWhiteSpace(profileDir) && Path.IsPathRooted(profileDir);
        if (!rooted)
        {
            log.LogWarn($"skip: профильный каталог не резолвится — {fileName} " +
                        "ляжет рядом с exe (фолбэк AppContext.BaseDirectory)");
        }
        var root = rooted ? profileDir : AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "ai-job-search-by", "data", fileName);
    }

    public static ServiceProvider Build(ILogService? bootLog = null)
        => AddRegistrations(new ServiceCollection(), bootLog).BuildServiceProvider();
}
