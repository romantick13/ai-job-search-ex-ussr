# architecture.md — каноническая архитектура ai-job-search-by
Статус: канонический документ | Дата: 2026-10-18 | Владелец: ThreeI
Основания: DECISIONS.md (RFC-001…007), реестр порталов 2026-10-17, реестр OrcaRouter 2026-10-17.
Приоритет документов: architecture.md → PLAN.md → README.md.

## 1. Каноническое решение
ai-job-search-by — публичный опенсорсный инструмент поиска IT-работы для
Беларуси: адаптация github.com/MadsLorentzen/ai-job-search (MIT) БЕЗ Claude Code
(официально недоступен в РБ) и БЕЗ зоопарка рантаймов. Харнес = собственный
фазовый пайплайн на C#; тексты промптов — свои (WP-A); upstream-ассеты
промптов не переносятся (решение владельца 2026-10-20).

Стек (RFC-001): .NET 10 (net10.0, Nullable enable, WinExe); Avalonia 12.1.1 +
Fluent, compiled bindings (x:DataType); CommunityToolkit.Mvvm 8.4.2;
SQLite + Microsoft.Data.Sqlite 10.0.10 + Dapper 2.1.79 (без EF);
Microsoft.Extensions.DependencyInjection 10.0.10; Markdig 1.3.2;
QuestPDF 2026.8.0; DocumentFormat.OpenXml 3.2.0; UglyToad.PdfPig (Apache-2.0,
версия пинится на WP-E); xunit 2.9.2 (тесты).

Железные запреты (не пересматривать без нового RFC): Entity Framework;
python.exe в runtime (python = только one-shot в tools/ с PYDOC-шапкой, UTF-8 без BOM);
параллельные LLM-запросы; cloud-only без локального фолбэка; ключи в репозитории;
TabControl (persistent-вкладки: Grid + IsVisible); ReactiveUI/Fody; LaTeX/Typst в v1;
внешний агент-харнес (opencode и подобные).

Правила кода с первого файла: граф-комментарии // FLOW / LIFECYCLE / STATE /
THREAD; каждая фаза = один коммит phase-N с обновлёнными доками в том же коммите;
код без обновлённой документации не коммитится.

## 2. Высокоуровневая схема
┌────────────────────────────────────────────────────────┐
│ Avalonia UI (persistent-вкладки Grid+IsVisible)         │
│  Search | Vacancies(Rank) | Apply | Tracker | Settings  │
│  Progress-панель (ETA) | DebugLog-окно                  │
└──────────────────────────┬─────────────────────────────┘
                           ▼
┌────────────────────────────────────────────────────────┐
│ Application Services                                    │
│  ConfigService (слои, снапшот, quarantine)              │
│  PortalRegistry + IPortalService (Api/Feed/Scrape/Gov)  │
│  ScrapeOrchestrator (порядок, снапшот, отчёт порталов)  │
│  DedupeHasher (композитный ключ, ДО нормализации)       │
│  RankService (fit-скоринг против профиля)               │
│  ApplyPipeline (фазы parse→eval→draft→reviewer→revise→  │
│    export→ATS; checkpoint; breaker; repair)             │
│  AtsChecker (PdfPig text-layer + детерминированные правила)│
│  CascadeAiService + IAiProvider (6 провайдеров)         │
│  LlmClient (одна попытка, типизированные ошибки)        │
│  LlmProfileService (me/public, Revision в снапшот)      │
│  EndpointGate (SemaphoreSlim(1)) | BionicPreflight      │
│  IFormatExporter (Pdf/Docx/Html/Markdown)               │
│  TrackerService (SQLite) | KeysCheckService             │
│  ProfileService/Onboarding (только data/, gitignore)    │
│  ProgressTracker (ETA) | LogService + FileLog           │
└──────────────────────────┬─────────────────────────────┘
                           ▼
┌────────────────────────────────────────────────────────┐
│ Storage                                                  │
│  data/applications.db (SQLite, идемпотентные миграции)   │
│  config/app.json (tracked, без секретов)                 │
│  config/local.json (gitignore: ключи, счётчики квот)     │
│  rate-limit.json (кулдауны 429, рядом с exe)             │
│  data/profile/, data/documents/ (личные, gitignore)      │
└──────────────────────────┬─────────────────────────────┘
                           ▼
┌────────────────────────────────────────────────────────┐
│ External Systems                                         │
│  Порталы: official_api | feed | scrape (robots/polite)   │
│  AI: Bionic(1) OpenRouter(10) OrcaRouter(20)             │
│      OmniRoute(30) CherryStudio(40) GLM(50, breaker)     │
└────────────────────────────────────────────────────────┘

## 3. Контракты слоёв

### 3.1 Конфиг (RFC-004)
Слои: код-дефолты ← config/app.json ← config/local.json (gitignore).
Snapshot immutable на чтение из любого потока; UpdateAsync(updater) — RMW поверх
latest под SemaphoreSlim, только dirty-поля; Changed не стреляет на LoadAsync;
битый json = quarantine + fallback на дефолты (не крах). Ключеобразные значения
(sk-*, Bearer, app_key, client_secret) в app.json = ConfigValidationException до
записи. Снапшот прогона + ConfigSnapshotHash + ProfileRevision → в ScrapeRuns/
ApplyRuns для воспроизводимости.

### 3.2 AI-слой (RFC-002)
ILlmClient: CompleteAsync(request, options) — ОДНА
попытка, без ретраев; типизированные ошибки: LlmUnavailableException(RetryAfter,
StatusCode, BodySnapshot), LlmAuthenticationException, LlmEndpointMismatchException,
LlmRequestRejectedException, LlmResponseException; классификация: 408/429/5xx →
Unavailable, 400/413/422 → Reject, 401/403 → Auth, 404 → EndpointMismatch, прочие
4xx консервативно Reject; caller-cancel → OperationCanceled. Retry-цикл с backoff +
jitter — собственность ApplyPipeline/ScrapeOrchestrator; Retry-After сервера wins.

IAiProvider: Id, DisplayName, DefaultPriority, IsAvailableAsync,
CanHandle(estimatedTokens), CompleteAsync(system, user, ct) → AiAttemptResult.
CascadeAiService: цепь упорядочена по DefaultPriority (меньше = раньше); липкость
на уровне ПРОВАЙДЕРА (успешный первым в следующем чанке); исключение провайдера =
отказ, не крах; TextQuality.LooksLikeGarbage/LooksLikeThinking = провал и переход
дальше; липкость снимается с CoT-провайдера.

Провайдеры (приоритеты и факты — реестр 2026-10-17):
| Приоритет | Провайдер | Факты/лимиты |
|---|---|---|
| 1  | BionicProvider | localhost:1234; безлимитно, медленно; субчанки ≤15000 (иначе 400 Engine protocol failed); StripThinkingBlock; по умолчанию выкл — галочка в настройках (рев. 7) |
| 10 | OpenRouterProvider | 50 req/day; free-фильтр pricing.prompt=="0" && pricing.completion=="0" (СТРОКИ); openrouter/auto на free = всегда 402 (запрещён); скоринг семейств + кулдауны 429 персист (адаптив 5→60 мин); липкость по модели внутри; ModelBecamePaid → сброс липкости |
| 20 | OrcaRouterProvider | api.orcarouter.ai/v1, ключ sk-orca-; 50 req/day free (после $20 платежей воркспейса → 800/day); free-пул ротируется; нужен GitHub с историей; Lite (MIT) = self-host альтернатива |
| 30 | OmniRouteProvider | локальный роутер владельца; мало free-моделей; вкл если настроен |
| 40 | CherryStudioProvider | локальный; free: GLM 4.5, GLM 4.6; вкл если настроен |
| 50 | GlmProvider | Z.ai; ключ есть, баланса НЕТ (402/1113) → circuit breaker 10 мин держит вне цепи; не предлагать «просто GLM» |

LlmProfileService: профили "me" (bionic-first) и "public" (cloud-only, без
LocalProvider); ResolveActive: active → default → первый валидный;
Revision = SHA256(name+baseUrl) БЕЗ ключа → в снапшот прогона.
EndpointGate: SemaphoreSlim(1) — единственная точка HTTP к LLM-endpoint
(конкурентность убивает free-тарифы и VRAM).
BionicPreflight: ON-DEMAND (ПОЛИНГ ЗАПРЕЩЁН): lms ps = единственный источник
истины о резидентных (парсинг только после шапки IDENTIFIER, CleanCli от
ANSI/спиннеров); lms load --yes = синхронный барьер готовности; защита от
задвоения моделей в VRAM. Кнопка «Тест цепочки» = on-demand префлайт всех
провайдеров с таблицей вердиктов.

### 3.3 Порталы (RFC-003, реестр 2026-10-17)
IPortalService: Id, DisplayName, SourceClass (commercial|government|aggregator),
Access (official_api|feed|scrape), Auth (none|public_key|personal_key|oauth),
DefaultPriority, ApplySupported: bool, ApplyWarning: string?,
SearchAsync(query, ct) / DetailAsync(idOrUrl, ct) → нормализованный JobPosting.
Базовые классы: ApiPortalBase (JSON), FeedPortalBase (RSS/Atom),
ScrapePortalBase (HTML + polite-crawl: паузы между запросами,
рандомизация задержек, robots.txt, уважение X-RateLimit-Remaining/Retry-After,
инкрементальный since, лимит страниц за прогон; breaker после N подряд падений
парсинга + golden-фикстуры разметки в тестах), GovernmentPortalBase (строгий
polite-crawl + дисклеймер «сверять с первоисточником»).
PortalRegistry: порядок = массив конфига Portals[] (порядок = приоритет), чекбокс
Enabled; порталы вне массива дописываются в хвост по DefaultPriority. Снапшот
порядка фиксируется НА СТАРТЕ прогона; правки конфига посреди прогона не
перетасовывают живую цепочку.
ScrapeOrchestrator: последовательно по приоритету (параллельность запрещена);
MaxPortalsPerRun (0 = без лимита); per-portal таймаут 60 с дефолт + экспоненциальный
backoff на 429/503 + персист кулдауна в rate-limit.json; отчёт прогона по каждому
порталу: ok | no_key | refused | breaker | parse_fail | disabled (отказ портала ≠
крах прогона).
DedupeHasher: SHA256 от композитного ключа portal_id + external_id + canonical_url,
ДО нормализации текста (belmeta-агрегатор оборачивает вакансии своей обёрткой).
KeysCheckService (on-demand): auth=none → зелёный; ключевой → наличие в local.json
+ живой минимальный пинг (per_page=1); нет ключа/пинг мёртв = портал вне цепи С
ПРИЧИНОЙ в отчёте, не краш; старт без ключей = 12 enabled auth=none работают сразу.
Поиск ≠ подача: ApplySupported/ApplyWarning в манифесте; hh: ApplySupported=false,
ApplyWarning «ИИ-агент скрининга отсеивает ~100% кандидатур»; UI Apply требует
подтверждения на подачу в помеченные площадки.

Дефолтная цепочка (реестр 2026-10-17):
| Приоритет | id | access/auth | enabled |
|---|---|---|---|
| 10 | rabota_by | official_api / none (поиск) | вкл |
| 9  | hh | official_api / none (поиск); apply off | вкл |
| 8  | hirify, jobs_dev_by, belmeta, praca_by | scrape / none | вкл |
| 8  | remoteok, remotive, himalayas | official_api / none | вкл |
| 7  | jobicy, wwr(feed), nodesk(feed) | official_api|feed / none | вкл |
| 6  | hn_who_is_hiring (Algolia+парсер треда), adzuna (personal_key) | вкл |
| 5  | yc_work_at_a_startup | scrape / none | выкл |
| 4  | web3_jobs | official_api / personal_key | выкл |
| 3  | jooble (lifetime 500/ключ + счётчик «осталось N из 500» в local.json), careerjet, второй эшелон РБ (rdw/rab/joblab/rework/belrabota/rabota360) | выкл |
| 1  | linkedin (scrape/oauth, партнёрский API), indeed, glassdoor, monster, otta, dice | exclude |
| —  | wellfound, flexjobs, hiring_cafe, arc_dev, ux_remote_talent, kufar, vakantno/riv/trudbox/gorodrabot/mnogo-raboty/trud_com | exclude/парковка (portals-graveyard.md) |
| WP-P7 | gsz_gov_by, minsk_komtrud | government | выкл |
Гейт перед кодом каждого портал-WP: чек-лист из 5 пунктов (живость+свежесть;
robots/sitemap/api/rss/json → access; Cloudflare/капча/login-wall → antiBot;
дубли belmeta/hh → dedupePriority; юридическая допустимость → legalAccess
allowed|restricted|forbidden). Код без заполненной карточки не коммитится.

### 3.4 Пайплайны вакансий
RankService: fit-скоринг вакансии против профиля (prompts/ru/04-job-evaluation +
критерии РБ: ТК РБ, BYN/USD, релокация); флаг UsePortalRatingInRank дефолт ВЫКЛ
(рейтинг портала = информационная колонка UI).
ApplyPipeline (фазы, паттерн TranslationPipeline): parse posting → eval fit →
draft CV (Markdown) → draft cover letter → reviewer (второй LLM-вызов + company
research, промпт из agents/gemini-research-expert.md как референс) → revise →
export → ATS-check → present. Checkpoint инкрементальный (resume после 429-шторма);
breaker на 3 подряд отказа; repair-цикл = смена промпта (владелец ретраев =
пайплайн). ОДИН apply-прогон за раз, строго последовательно.
AtsChecker: PdfPig читает число страниц и text-слой; детерминированные правила
(паттерн QAService): контакты литерально в text-слое; покрытие ключей вакансии
против профиля (keyword stuffing запрещён, дыры остаются видимыми); числа/даты
консистентны с профилем (жадные regex-классы заканчивать цифрой); TextQuality-
маркеры мусора/thinking. CV ≠ 2 стр или письмо ≠ 1 стр → детерминированные шаги
ужатия (кегль → интервалы → перенос блока), максимум 2 итерации; не влезло →
warning с фактом «N страниц», не молчаливый выход.

### 3.5 Экспорт (RFC-005)
IFormatExporter: Format, Extension, ExportAsync(markdown, outputPath,
ct). Реализации: PdfExporter (Markdig AST → QuestPDF), DocxExporter (OpenXml),
HtmlExporter (Markdig), MarkdownExporter. ExportStyles-константы; QuestPDF license
пишется идемпотентно (Community-лицензия — строка в README и NOTICE.md). Шаблоны
CV/письма = Markdown-шаблоны prompts/templates/ (структура секций и tailoring-
правила портированы чтением upstream cv/cover_letters как РЕФЕРЕНСА). Опциональные
OFL-шрифты (Lato/Raleway) — только с Google Fonts вместе с OFL.txt.

### 3.6 Трекер и онбординг
TrackerService: SQLite-статусы откликов (/outcome-аналог), follow-up-даты,
заметки (CSV-safe). Onboarding (/setup-аналог): пишет ТОЛЬКО в data/profile/ и
config/local.json; xUnit-гард белого списка путей записи (порт идеи upstream
test_onboarding_privacy); security-гард сканирует staged-файлы на ключеобразные
паттерны (порт идеи test_security_guards).

### 3.7 Прогресс и логи
ProgressTracker: Start/Report/Complete/Reset, ETA экспоненциальное
сглаживание, глобальная панель в MainWindow; опциональный IProgressTracker? в
сервисах. LogService (UI ObservableCollection) + FileLog (logs/app.log), при старте
подтягивает старые строки.

## 4. Схема БД V1 (SQLite, идемпотентные миграции EnsureColumn/PRAGMA table_info)
CREATE TABLE IF NOT EXISTS Vacancies (
  Id TEXT PRIMARY KEY, PortalId TEXT NOT NULL, ExternalId TEXT,
  CanonicalUrl TEXT NOT NULL, Title TEXT, Company TEXT, Location TEXT,
  SalaryText TEXT, DescriptionMd TEXT, SourceClass TEXT NOT NULL,
  AccessKind TEXT NOT NULL, Quality TEXT NOT NULL DEFAULT 'verified',
  DedupeHash TEXT NOT NULL UNIQUE, FetchedAt TEXT NOT NULL,
  Status TEXT NOT NULL DEFAULT 'new' );
CREATE TABLE IF NOT EXISTS Applications (
  Id TEXT PRIMARY KEY, VacancyId TEXT NOT NULL REFERENCES Vacancies(Id),
  Status TEXT NOT NULL DEFAULT 'draft', CvPath TEXT, LetterPath TEXT,
  SentAt TEXT, FollowUpAt TEXT, Notes TEXT, CreatedAt TEXT NOT NULL );
CREATE TABLE IF NOT EXISTS ScrapeRuns (
  Id TEXT PRIMARY KEY, StartedAt TEXT NOT NULL, FinishedAt TEXT,
  PortalOrderSnapshot TEXT NOT NULL, ConfigSnapshotHash TEXT NOT NULL,
  ProfileRevision TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'running' );
CREATE TABLE IF NOT EXISTS PortalRunReports (
  RunId TEXT NOT NULL REFERENCES ScrapeRuns(Id), PortalId TEXT NOT NULL,
  Status TEXT NOT NULL, ItemsFound INTEGER NOT NULL DEFAULT 0,
  Error TEXT, DurationMs INTEGER, PRIMARY KEY (RunId, PortalId) );
CREATE TABLE IF NOT EXISTS AiJobs (
  Id TEXT PRIMARY KEY, Phase TEXT NOT NULL, ProviderId TEXT NOT NULL,
  Model TEXT, PromptName TEXT NOT NULL, PromptVersion INTEGER NOT NULL,
  Status TEXT NOT NULL, RawResponse TEXT, ParseStatus TEXT NOT NULL DEFAULT 'ok',
  StartedAt TEXT, FinishedAt TEXT );
CREATE TABLE IF NOT EXISTS SettingsKV ( Key TEXT PRIMARY KEY, Value TEXT NOT NULL );
История AI-результатов не теряется (канон: связь результат ↔ промпт ↔
версия ↔ провайдер ↔ модель ↔ raw ↔ parse status).

## 5. Статусы сущностей
Vacancy: new | shortlisted | applied | duplicate | archived.
Application: draft | review | sent | reply | rejected | offer | withdrawn.
ScrapeRun: running | done | failed | cancelled.
PortalRunReport: ok | no_key | refused | breaker | parse_fail | disabled.
AiJob: queued | running | done | failed | cancelled | parse_failed.

## 6. Потоки и производительность
UI никогда не блокируется; обновления только через Dispatcher.UIThread; LLM/сеть —
async; экспортёры и тяжёлые операции — Task.Run; EndpointGate сериализует LLM;
порталы обходятся последовательно; CancellationToken во всех долгих операциях;
статусы idle/running/error/done видны в UI.

## 7. Обработка ошибок
Портал недоступен/капча/403 → refused + breaker, прогон живёт; нет ключа → no_key
с причиной; LLM Unavailable → каскад дальше; Auth → понятное сообщение + подсказка
keys-check; битый JSON от модели → raw сохранён + parse_failed; нет интернета →
понятный статус, данные не теряются; отмена пользователем = graceful interrupted
с телеметрией.

## 8. Безопасность и приватность
Ключи только в config/local.json (gitignore); личные данные только в data/
(gitignore); телеметрии нет; polite-crawl и robots.txt обязательны; уважать ToS
источников (legalAccess=forbidden → адаптер не пишем, graveyard); zip-дистрибутив
не содержит ключей и личных данных по построению.

## 9. Терминология (канон)
Portal — источник вакансий с контрактом IPortalService. JobPosting (Vacancy) —
нормализованная вакансия. ScrapeRun — прогон обхода порталов со снапшотом порядка.
Application — отклик на вакансию с артефактами CV/письма. Profile — данные
кандидата в data/profile/. Provider — AI-провайдер каскада. Cascade — упорядоченная
цепь провайдеров с липкостью. KeysCheck — on-demand диагностика ключей и пингов.
ATS-check — детерминированная проверка text-слоя и страниц. Reviewer — фаза
критики черновиков вторым LLM-вызовом. WP — work package плана (PLAN.md).

## 10. Что считается архитектурно важным (не ломать)
Послойный конфиг и quarantine битого json; снапшот порядка порталов и конфига на
старте прогона; дедуп ДО нормализации по композитному ключу; поиск ≠ подача;
preflight и watchdog on-demand (поллинг запрещён); LlmClient = одна попытка,
владелец ретраев = оркестратор; EndpointGate сериализация; каскад: отказ провайдера
≠ крах; экспорт без LaTeX; ключи и личные данные вне git; документация синхронна
коду; граф-комментарии и PYDOC с первого файла.

## 11. Ворк-пакеты (порядок канонический, детали в PLAN.md)
WP-P0 PortalRegistry + контракт + DedupeHasher + keys-check; WP-P1 rabota_by + hh;
WP-P2 hirify + jobs_dev_by + belmeta + praca_by; WP-P3 remoteok + remotive +
himalayas + jobicy; WP-P4 wwr + nodesk + hn; WP-P5 adzuna + jooble + careerjet +
web3_jobs + yc; WP-P6 второй эшелон РБ + пробник jobs_tut_by (alias_of hh?);
WP-P7 госсектор. WP-A AI-цепочка (6 провайдеров, профили, префлайт); WP-E экспорт +
ATS (пин PdfPig); WP-U UI-вкладки и Settings-порталы; WP-D доки/публичная сборка
(self-contained win-x64 zip, README, NOTICE.md).
