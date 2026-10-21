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

        // Каркас WP-0: окно и его корневая модель, оба синглтоны.
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<MainWindow>();

        // WP-P0 (PLAN §7): портальный каркас. Реестр наполняется адаптерами;
        // KeysCheck читает ключи через ILocalSecrets (реализация из ConfigService
        // придёт с RFC-004), пинги — с адаптерами.
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

        services.AddSingleton<Portals.PortalRegistry>();
        services.AddSingleton<ILocalSecrets, NullLocalSecrets>();
        services.AddSingleton<IKeysCheckService, KeysCheckService>();

        // WP-A-LLM7 + WP-A-OPENAI-COMPAT (решения владельца 2026-10-21;
        // LLM7-INTEGRATION.md §7, ai-providers-card-2026-10-21.md §6):
        // ИИ-цепь. Провайдеры: llm7 (free 100K ток/сутки), modelscope
        // (2000 запр/сутки на аккаунт, ≤200/модель), siliconflow (free-модели,
        // RPM 1000+/TPM 40K+), ollama-local и lm-studio (локальные, без ключа,
        // безлимит — для владельцев GPU). Без ключа провайдер вне цепи, но
        // КРИЧИТ причину и путь (НИ-2): ввёл ключ — встал в цепь сам.
        // Стадия match ИИ НЕ тратит — LocalMatchScorer (критика Мистраля).
        services.AddSingleton<Services.Ai.LocalMatchScorer>();
        services.AddSingleton<Services.Ai.ResponseCostCounter>();
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
        {
            var log = sp.GetRequiredService<Services.ILogService>();
            var budgetPath = StatePath(sp, "llm7-budget.json");
            return new Services.Ai.Llm7Provider(log, budgetFilePath: budgetPath);
        });
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
            new Services.Ai.OpenAiCompatibleProvider(
                sp.GetRequiredService<Services.ILogService>(),
                name: "modelscope",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.ModelScopeBaseUrl,
                modelEnvVar: "AIJOB_MODELSCOPE_MODEL",
                apiKeyEnvVar: "AIJOB_MODELSCOPE_API_KEY",
                qualityRank: 2, // сильные Qwen-инструкты — на резюме первыми
                dailyRequestCap: Services.Ai.OpenAiCompatibleProvider.ModelScopeDailyRequestCap,
                maxConcurrent: 1, // «single-request concurrency» — онбординг ModelScope
                requireApiKey: true,
                requestLedgerPath: StatePath(sp, "modelscope-requests.json"),
                signupHint: "modelscope.ai"));
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
            new Services.Ai.OpenAiCompatibleProvider(
                sp.GetRequiredService<Services.ILogService>(),
                name: "siliconflow",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.SiliconFlowBaseUrl,
                modelEnvVar: "AIJOB_SILICONFLOW_MODEL",
                apiKeyEnvVar: "AIJOB_SILICONFLOW_API_KEY",
                qualityRank: 4,
                dailyRequestCap: null, // суточные RPD free-моделей не публикуются — [cost] соберёт фактически
                requireApiKey: true,
                signupHint: "siliconflow.com"));
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

    public static ServiceProvider Build(ILogService? bootLog = null)
        => AddRegistrations(new ServiceCollection(), bootLog).BuildServiceProvider();
}
