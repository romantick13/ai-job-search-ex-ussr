using AiJobSearchExUssr.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AiJobSearchExUssr;

// FLOW: сборка графа зависимостей — единственное место регистрации сервисов.
// Канон: интерфейсы на всё; по мере появления слоёв регистрации добавляются
// здесь и нигде больше.
// STATE: без состояния — чистая фабрика графа.
internal static class CompositionRoot
{
    // Регистрации отдельно от сборки провайдера — тесты читают состав графа.
    // bootLog: экземпляр, поднятый в Program.Main до контейнера (crash-хендлеры
    // вешаются раньше любой содержательной работы). null — только в тестах:
    // тогда лог регистрируется типом со своими дефолтами.
    public static IServiceCollection AddRegistrations(
        IServiceCollection services, ILogService? bootLog = null)
    {
        // Контракт «никаких тихих игноров»: протокол — первый сервис графа,
        // от него зависят все последующие (порталы пишут каждый шаг).
        if (bootLog is null)
        {
            services.AddSingleton<ILogService, LogService>();
        }
        else
        {
            services.AddSingleton(bootLog);
        }

        // local.json — секреты вне репо. Ленивая фабрика: файл читается при
        // первом resolve — до первого провайдера, логи [config] идут раньше
        // «провайдер готов».
        services.AddSingleton(sp =>
            ConfigService.LoadOrCreate(sp.GetRequiredService<ILogService>()));

        // Окно и его корневая модель, оба синглтоны. VM получает тот же
        // ILogService (живая лента протокола), всю цепь IAiChatProvider
        // (карточки статуса), реестр порталов, базу, адаптацию, PDF-экспорт
        // и индексатор PDF.
        services.AddSingleton(sp => new ViewModels.MainViewModel(
            sp.GetRequiredService<ILogService>(),
            sp.GetServices<Services.Ai.IAiChatProvider>(),
            sp.GetRequiredService<Portals.PortalRegistry>(),
            sp.GetRequiredService<Services.IApplicationStore>(),
            sp.GetRequiredService<Services.ResumeAdaptationService>(),
            sp.GetRequiredService<Services.IPdfExporter>(),
            sp.GetRequiredService<Services.PdfIndexerService>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<ViewModels.ResumeProfilesViewModel>(),
            // Чекбокс «авто-LMS» (MainViewModel.LmsAutoLoad -> CLI.AutoLoad)
            // и подъём lm-studio при «Проверить всех» (ProbeRowAsync).
            sp.GetRequiredService<Services.Ai.LmStudioCli>(),
            // Смежные запросы поиска (родственные специальности к запросу).
            sp.GetRequiredService<Services.Ai.RelatedQueriesService>()));
        services.AddSingleton<MainWindow>();

        // Портальный каркас: реестр наполняется адаптерами; KeysCheck читает
        // ключи через ILocalSecrets.
        // HttpClient — общий синглтон, таймаут 60 с. UA браузерного вида
        // обязателен: .NET по умолчанию User-Agent НЕ шлёт, и WAF-щиты
        // порталов режут такие запросы как ботов. Accept/Accept-Language —
        // щиты (Cloudflare и ко) считают запрос подозрительнее, если не
        // хватает стандартных браузерных заголовков; пара полей стоимости
        // не имеет.
        services.AddSingleton(_ =>
        {
            var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60),
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 ai-job-search-ex-ussr/0.9");
            http.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return http;
        });

        // Зеркала группы hh (Россия/Беларусь/Казахстан/Узбекистан): один
        // адаптер HhGroupPortal, у каждого зеркала свой хост и свой код
        // региона — регистрация фабрикой по карточкам каталога.
        foreach (var mirror in Portals.BuiltInPortals.HhMirrors)
        {
            services.AddSingleton<Portals.IPortalService>(sp =>
                new Portals.HhGroupPortal(
                    mirror,
                    sp.GetRequiredService<System.Net.Http.HttpClient>(),
                    sp.GetRequiredService<Services.ILogService>()));
        }

        // Шесть международных адаптеров (BuiltInPortals.Core): URL и схемы
        // ответов подтверждены живым зондом, golden-фикстуры в тестах.
        services.AddSingleton<Portals.IPortalService, Portals.RemoteOkPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.RemotivePortal>();
        services.AddSingleton<Portals.IPortalService, Portals.JobicyPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.WwrPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.HimalayasPortal>();
        services.AddSingleton<Portals.IPortalService, Portals.NodeskPortal>();

        services.AddSingleton<Portals.PortalRegistry>();
        // Шов ILocalSecrets (ILocalSecrets.cs) встал на реализацию —
        // KeysCheckService читает Portals.{id}.ApiKey из local.json.
        // NullLocalSecrets остаётся в коде для тестовых фикстур.
        services.AddSingleton<ILocalSecrets>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton<IKeysCheckService, KeysCheckService>();

        // База откликов (SQLite по architecture.md §4) — личные данные вне
        // репо: <профиль>\ai-job-search-ex-ussr\data\ai-job-search-ex-ussr.db
        // (r22; старую папку/файл переименовывает AppPaths при старте).
        // Таблицы Vacancies/Applications/SettingsKV — канон §4; PdfDocuments —
        // текстовый индекс PDF-папки (столбцы векторов остались пустыми после
        // вырезания векторизации, r21); расширения Applications — EnsureColumn.
        services.AddSingleton<Services.IApplicationStore>(sp =>
            new Services.ApplicationStore(
                AppPaths.DbFile,
                sp.GetRequiredService<Services.ILogService>()));

        // r21: векторизация вырезана (решение владельца — векторы ничего не
        // потребляли, «похожие отклики» не существовало; столбцы VectorJson/
        // VectorModel в базе оставлены пустыми для совместимости старых баз).

        // PDF отклика (QuestPDF 2026.8.0 — пин THIRD-PARTY-NOTICES §2,
        // Community-лицензия идемпотентно) и индексатор PDF-папки (PdfPig).
        services.AddSingleton<Services.IPdfExporter, Services.PdfExportService>();
        // Заплатка чужого PDF (PdfPig-координаты + PDFsharp overlay).
        services.AddSingleton<Services.IPdfOverlay, Services.PdfOverlayService>();
        services.AddSingleton(sp => new Services.PdfIndexerService(
            sp.GetRequiredService<Services.IApplicationStore>(),
            sp.GetRequiredService<Services.ILogService>(),
            // Общий резолвер — пусто/унаследованное «Документы» → дефолт
            // <exe>\resume; кастомный путь юзера живёт как есть.
            () => Services.ApplicationStore.ResolveOutbox(
                sp.GetRequiredService<Services.IApplicationStore>().GetSetting("outbox.path"))));

        // Точечная правка DOCX: temp-копия, атомарная подмена, форматирование
        // не трогаем.
        services.AddSingleton<Services.IDocxTextEditor, Services.DocxTextEditService>();

        // Адаптация резюме (стадия ResumeAdaptation через TaskRoutingPolicy).
        services.AddSingleton<Services.ResumeAdaptationService>();


        // Профили резюме. База — рядом с конфигами:
        // <профиль>\ai-job-search-ex-ussr\resume\, файлы профилей — там же.
        // Словарь ключевых — файл keywords.json в каталоге local.json: юзер
        // правит один раз под себя, обновления приложения его не перетирают.

        services.AddSingleton<Services.Resume.IResumeStore>(sp =>
            new Services.Resume.ResumeStore(
                ResumeDataPath(sp, "resume.db"),
                sp.GetRequiredService<Services.ILogService>()));
        services.AddSingleton<Services.Resume.IResumeParser, Services.Resume.ResumeParser>();
        services.AddSingleton<Services.Resume.IMdToDocx, Services.Resume.MdToDocx>();
        services.AddSingleton<Services.Resume.IKeywordDictionary>(sp =>
        {
            var log = sp.GetRequiredService<Services.ILogService>();
            var config = sp.GetRequiredService<ConfigService>();
            // Рядом с local.json; конфиг не резолвнулся — фолбэк state\
            // (кричим в протоколе).
            var dir = config.FilePath is { Length: > 3 } path && System.IO.File.Exists(path)
                ? System.IO.Path.GetDirectoryName(path)!
                : System.IO.Path.GetDirectoryName(StatePath(sp, "x"))!;
            if (config.FilePath is { Length: > 3 } && !System.IO.File.Exists(config.FilePath))
            {
                log.LogWarn("skip: local.json не найден — keywords.json ляжет в state\\; " +
                            "решение: запустить приложение, конфиг создаётся из шаблона");
            }

            return new Services.Resume.KeywordDictionary(
                System.IO.Path.Combine(dir, "keywords.json"), log);
        });
        services.AddSingleton<ViewModels.ResumeProfilesViewModel>(sp =>
            new ViewModels.ResumeProfilesViewModel(
                sp.GetRequiredService<Services.Resume.IResumeStore>(),
                sp.GetRequiredService<Services.Resume.IResumeParser>(),
                sp.GetRequiredService<Services.Resume.IMdToDocx>(),
                sp.GetRequiredService<Services.Resume.IKeywordDictionary>(),
                sp.GetRequiredService<Services.ILogService>()));

        // Модель настроек (модальное окно шестерёнки).
        services.AddSingleton<ViewModels.SettingsViewModel>();


        // ИИ-цепь. Провайдеры: llm7 (free 100K ток/сутки), modelscope
        // (2000 запр/сутки на аккаунт, ≤200/модель), siliconflow (free-модели,
        // RPM 1000+/TPM 40K+), openrouter (free 50 запр/сутки; депозит $10 —
        // 1000), orcarouter (free-ступени 50 → 800), lm-studio (локальный,
        // без ключа, безлимит). Без ключа провайдер вне цепи, но КРИЧИТ
        // причину и путь: ввёл ключ — встал в цепь сам.
        // Ключи — лестница env -> local.json; источник и маска каждого ключа
        // — в протоколе ([config] строки). Стадия match ИИ НЕ тратит —
        // LocalMatchScorer.

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
                apiKey: key,
                qualityRank: 2, // сильные Qwen-инструкты — на резюме первыми
                dailyRequestCap: Services.Ai.OpenAiCompatibleProvider.ModelScopeDailyRequestCap,
                maxConcurrent: 1, // «single-request concurrency» — онбординг ModelScope
                requireApiKey: true,
                disableThinking: true, // гибридные Qwen думают по умолчанию -> 504 Tengine
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
                apiKey: key,
                qualityRank: 4,
                dailyRequestCap: null, // суточные RPD free-моделей не публикуются — [cost] соберёт фактически
                requireApiKey: true,
                disableThinking: true, // Qwen3-8B тоже гибридный — thinking глушим
                signupHint: "siliconflow.com");
        });
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
        {
            // Ступень lm-studio — декоратор над OpenAiCompatibleProvider
            // (порт 1234). Декоратор при первом доходе цепи поднимает демона
            // и модель через официальный CLI (lms): daemon up -> ps -> (ls ->
            // выбор) -> load -> ps-перепроверка -> отклик-проба (chat-ping,
            // 30с). GUI-сервер НЕ используем (конфликтует с демоном за
            // конфиги). Чужие модели НЕ выгружаем — модалка выбора, а при
            // чекбоксе «авто-LMS» — молча.
            var log = sp.GetRequiredService<Services.ILogService>();
            var cli = sp.GetRequiredService<Services.Ai.LmStudioCli>();
            var inner = new Services.Ai.OpenAiCompatibleProvider(
                log,
                name: "lm-studio",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.LmStudioBaseUrl,
                modelEnvVar: "AIJOB_LMSTUDIO_MODEL",
                qualityRank: 7,
                requireApiKey: false);
            return new Services.Ai.LmStudioBootstrappingProvider(inner, cli, log);
        });
        services.AddSingleton<Services.Ai.LmStudioCli>();
        // OpenRouter и OrcaRouter — главные поставщики free-промта (ключи в
        // настройках); локальная ollama в дефолт не входит (карточка краснела
        // бы без причины).
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
        {
            var log = sp.GetRequiredService<Services.ILogService>();
            var (key, _) = sp.GetRequiredService<ConfigService>()
                .ResolveAiKey("OpenRouter", "AIJOB_OPENROUTER_API_KEY");
            return new Services.Ai.OpenAiCompatibleProvider(
                log,
                name: "openrouter",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.OpenRouterBaseUrl,
                modelEnvVar: "AIJOB_OPENROUTER_MODEL",
                apiKeyEnvVar: "AIJOB_OPENROUTER_API_KEY",
                apiKey: key,
                qualityRank: 5, // сильные free-модели — после облачных базовых
                dailyRequestCap: null, // free 50/сутки, депозит $10 → 1000 — не хардкодим, 429 решит
                requireApiKey: true,
                signupHint: "openrouter.ai",
                // Агрегатор: список бесплатных -> наилучшая -> отказ = следующая
                // -> удачная («липучесть») в файл.
                freeCatalog: true,
                stickyFilePath: StatePath(sp, "openrouter-sticky.json"));
        });
        services.AddSingleton<Services.Ai.IAiChatProvider>(sp =>
        {
            var log = sp.GetRequiredService<Services.ILogService>();
            var (key, _) = sp.GetRequiredService<ConfigService>()
                .ResolveAiKey("OrcaRouter", "AIJOB_ORCAROUTER_API_KEY");
            return new Services.Ai.OpenAiCompatibleProvider(
                log,
                name: "orcarouter",
                baseUrl: Services.Ai.OpenAiCompatibleProvider.OrcaRouterBaseUrl,
                modelEnvVar: "AIJOB_ORCAROUTER_MODEL",
                apiKeyEnvVar: "AIJOB_ORCAROUTER_API_KEY",
                apiKey: key,
                qualityRank: 6,
                dailyRequestCap: null, // ступени 50→800/сутки знает сам провайдер (429 = фолбэк)
                requireApiKey: true,
                signupHint: "orcarouter.ai",
                // Free-каталог — фишка OpenRouter, у orcarouter его нет:
                // ступень работает по модели конфига (deepseek/deepseek-v4.1-
                // flash-free / AIJOB_ORCAROUTER_MODEL) без перебора бесплатных
                // и без липучести.
                freeCatalog: false);
        });
        services.AddSingleton<Services.Ai.TaskRoutingPolicy>(sp =>
            new Services.Ai.TaskRoutingPolicy(
                sp.GetRequiredService<Services.ILogService>(),
                sp.GetServices<Services.Ai.IAiChatProvider>()
                    // Кастомные звенья из local.json (Chain.Custom) —
                    // полноправный хвост фолбэка после встроенных.
                    .Concat(sp.GetRequiredService<ConfigService>()
                        .BuildCustomProviders(sp.GetRequiredService<Services.ILogService>())),
                coverLetterOrder: new[] { "llm7", "modelscope", "siliconflow", "openrouter", "orcarouter", "lm-studio" })); // письма — от дешёвых к дорогим

        // Смежные запросы поиска: ИИ называет 1-2 родственных запроса, кэш в
        // SettingsKV — повторные поиски того же слова ИИ не платят.
        services.AddSingleton<Services.Ai.RelatedQueriesService>();

        return services;
    }

    // Личные данные — вне репо: <AppPaths.Root>/<подкаталог>/файл.
    // Корень — AppPaths.Root: %USERPROFILE% (C:\Users\<user>), НЕ %APPDATA%:
    // профиль виден глазами в проводнике, и конфиг с ключами не теряется при
    // перекомпиляции. Guard абсолютного пути и фолбэк рядом с exe — внутри
    // AppPaths (не молчит: заметка уходит в протокол при старте).
    private static string RootedPath(IServiceProvider sp, string subDir, string fileName)
    {
        return System.IO.Path.Combine(AppPaths.Root, subDir, fileName);
    }

    // Регистры бюджета/квот — <профиль>/ai-job-search-ex-ussr/state/.
    private static string StatePath(IServiceProvider sp, string fileName)
        => RootedPath(sp, "state", fileName);

    // База профилей резюме — рядом с конфигами:
    // <профиль>\ai-job-search-ex-ussr\resume\resume.db + files\<профиль>\.
    private static string ResumeDataPath(IServiceProvider sp, string fileName)
        => RootedPath(sp, "resume", fileName);

    public static ServiceProvider Build(ILogService? bootLog = null)
        => AddRegistrations(new ServiceCollection(), bootLog).BuildServiceProvider();
}
