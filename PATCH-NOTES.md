# PATCH-NOTES — log-contract: контракт «никаких тихих игноров» (2026-10-21, фаза log-contract)

Третий кодовый пакет, по команде владельца: «никаких тихих игноров — всё,
абсолютно всё должно протоколироваться в лог на диске и параллельно
выводиться в окне приложения в онлайне, все действия; UI (дашборд с
окошками) потом».

Схема аварийных хендлеров и дискового протокола — по эталонному паттерну
лог-машины владельца (исходники переданы ранее; провенанс — в шапке
`Services/ILogService.cs`).

Проверено реально: .NET SDK 10.0.402, Release — 0 warning / 0 error;
тесты — 87/87 Passed (было 77/77; +10). Гигиена: UTF-8 без BOM, LF,
граф-комментарии на каждом файле.

## Что вошло

- **LOG-CONTRACT.md** (корень репо) — контракт: правила НИ-1…5, карточка
  интеграции, чек-лист аудита тишины, шаблоны catch/skip, раздел WP-U
  (окна дашборда = подписчики той же ленты Logs).
- **Services/ILogService.cs** — машина протокола: диск
  (`%APPDATA%\ai-job-search-by\logs\ai-job-search-by-YYYYMMDD.log`, ротация
  по дате, формат с миллисекундами) + живая лента `ObservableCollection`
  (кольцо 300, донорская семантика) с дублем в stdout до WP-U; LogWarn для
  осознанных skip-ов; сбой диска не молчит (stderr: 1-й и каждый 10-й) и не
  роняет (счётчик `DiskWriteFailures`); guard относительного профиля →
  фолбэк рядом с exe с анонсом в протокол.
- **Program.Main** — лог поднимается ПЕРВЫМ объектом; аварийные хендлеры
  (UnhandledException + UnobservedTaskException с SetObserved) до старта UI;
  boot-экземпляр уходит в DI без пересоздания.
- **CompositionRoot** — регистрация протокола первой в графе; тесты читают
  состав (гейт: адаптер обязан получить ILogService).
- **Портальный конвейер** — каждое действие в протокол (НИ-4):
  старт обхода/страница/разбор/квота (Retry-After, X-RateLimit, стоп-сигнал
  донора)/breaker/итог; отмена пользователем — LogWarn, ошибки — LogError с
  контекстом и решением перед re-throw (НИ-1); тихие пропуски битых
  элементов ленты (FeedPortalBase.ParseFeed) заменены на LogWarn-события
  через onSkip (НИ-2); юридический отказ rabota-by и канарейка hh-by
  логируются.
- **Тесты** — LogServiceTests (7: формат, маркеры, кольцо 300, диск, Clear,
  сбой диска, гонки 8×50) + CompositionRootLogContractTests (3) +
  SilentLog в TestSupport (немой протокол фикстур).

## Прежний пакет

Ниже — PATCH-NOTES предыдущего пакета (WP-P1), сохраняются как есть.

---

# PATCH-NOTES — WP-P1: rabota-by + hh-by, донор-режим «файлы вербатим» (2026-10-21, фаза wp-p1)

Второй кодовый пакет, по команде владельца «WP-P1». Объём — PLAN §7:
адаптеры rabota-by и hh-by (official_api), ОБА ВЫКЛЮЧЕНЫ В РЕЕСТРЕ ПО
УМОЛЧАНИЮ — включение отдельным решением владельца. Перед кодом — карточки
§2.11 (portals-catalog.md) + записи config/registry.json (правило RFC-003
§2.11: «код без заполненной карточки не коммитится»).

Параллельно закрыт процессный сдвиг: владелец передал ПОЛНЫЕ ИСХОДНИКИ
доноров (Atlas / MDC / TechDocTrans). Режим портирования сменён на «файлы
вербатим»: донорский код берётся файлами; «пишем заново» осталось только
там, где донора нет, — с явной пометкой «НАШЕ расширение» в шапке файла.
Сверка WP-P0 с реальными исходниками вскрыла и исправила отклонение в
DedupeHasher (см. ниже).

Проверено реально: .NET SDK 10.0.402, Release — 0 warning / 0 error;
тесты — 77/77 Passed (было 53/53; +24). Гигиена: UTF-8 без BOM, LF,
граф-комментарии // FLOW / LIFECYCLE / STATE / THREAD на каждом файле
(28/28 → 30/30 .cs).

## Донор-выравнивание WP-P0 по реальным исходникам (work/donors-src/)

- **DedupeHasher — вербатим Atlas**: оригинал
  `Atlas/Services/DedupeHasher.cs` отдаёт hex В ВЕРХНЕМ регистре
  (`Convert.ToHexString(SHA256.Create().ComputeHash(...))`). Прежний
  «порт 1:1» выдавал строчный (`ToHexStringLower`) — непреднамеренное
  отклонение, исправлено на строка-в-строку донора (отличие только в
  namespace). Тест-вектор переведён на `BA7816BF...` (верхний),
  `char.IsAsciiHexDigitUpper`. `ComputeComposite` — помечен «НАШЕ
  расширение» (в Atlas только Compute от URL).
- **ScrapePortalBase — донорская семантика квоты** (Atlas
  `GitHubHarvesterService.cs:89–94`, вербатим): `X-RateLimit-Remaining
  < 20` → «останавливаюсь до следующего раза» — стоп прогона ДО разбора
  страницы, собранное не возвращается; порог — константа
  `QuotaStopThreshold = 20` в PolitenessOptions. Джиттер ±30%,
  Retry-After в формате HTTP-даты, breaker — оставлены как РАСШИРЕНИЯ
  (спецификация RFC-003 §2.10, у донора их нет) с пометкой в шапке.
- **Ретраи 403/429 НЕ в адаптеры**: донорский `SendWithRetryAsync` пойдёт
  в оркестратор прогонов — по зафиксированному решению владельца «одна
  попытка в адаптере, ретраи/backoff/кулдауны — оркестратор». Туда же —
  кулдауны MDC `OpenRouterService.cs` (адаптивные 5→10→20→40→60 мин,
  персист файла, атомарная запись) и HttpClient таймаут 60 с (уже
  применён, см. ниже).
- Стиль комментариев FLOW/LIFECYCLE/STATE/THREAD подтверждён по MDC
  исходникам (OpenRouterService) — стандарт владельца из первоисточника.

## Что внутри WP-P1

- **Карточки §2.11**: portals-catalog.md — hh_by и rabota_by (поля:
  access/antiBot/дубли/tos_status/lastTermsCheck 2026-10-19/healthCheck
  контракт/дефолт цепи).
- **registry.json** (schema v3): rabota-by, hh-by —
  `adapter_implemented: true`, `default_enabled: false`, updated 2026-10-21;
  счётчик README «в цепочке по умолчанию: 6» сохранён генератором
  (gen_readme_counters.py RC=0, 6/6).
- **PortalDescriptor.DefaultEnabled** — выключатель карточки (зеркало
  default_enabled реестра); запись конфига Portals[] сильнее: Enabled=true
  включает и «выключенный по умолчанию» портал. PortalRegistry: хвост цепи
  набирается только из DefaultEnabled=true.
- **HhByPortal** (`Portals/HhByPortal.cs`, ApiPortalBase): поиск по
  документированной схеме API группы hh — items[] с
  id/name/area/salary/employer/alternate_url; salary → «от X до Y CUR» /
  «от X CUR» / «X CUR» / null; смена схемы (нет items[]/alternate_url) →
  ParseFailedException; дедуп = ComputeComposite(hh-by, id, alternate_url).
  healthCheck-контракт: канарейка GET /vacancies?per_page=1 — 200 → Ok
  («пересмотреть карточку»), 403 → Degraded («закрыт платформой с 04.2026,
  перепроверка раз в квартал»), исключение → Degraded с причиной (не краш).
  DetailAsync — NotSupportedException до включения портала.
- **RabotaByPortal** (`Portals/RabotaByPortal.cs`) — юридический стуб:
  соглашение dev.rabota.by п.1.5 («Пользователем Сервиса могут быть только
  организации-работодатели», снято дословно 2026-10-19) — соискательского
  пути нет. Search/Detail = NotSupportedException с вердиктом, health =
  Degraded с той же причиной; никаких тихих пустых ответов.
- **BuiltInPortals.Deferred** — манифесты двух РБ-порталов рядом с Core (6);
  тест держит: Core = 6, Deferred = [hh-by, rabota-by], объединение 8 без
  пересечений, приоритеты 5, ApplyWarning заполнен («поиск ≠ подача», §2.7).
- **CompositionRoot**: HttpClient — общий синглтон, таймаут 60 с (донорский
  дефолт Atlas GitHubHarvester); оба адаптера — синглтоны IPortalService,
  реестр DI видит 2 портала, цепь дефолта пуста.
- **Идентификаторы** — реестровый kebab-case: `hh-by`, `rabota-by`
  (registry.json = источник правды).

## Тесты (+24, итог 77/77)

HhByPortalTests (11): дескриптор по карточке, парсинг golden-фикстуры
(маппинг полей + дедуп-хэш), варианты salary, ParseFailed при смене схемы
и при item без alternate_url, канарейка 403/200/исключение, реестр: вне
цепи дефолта, конфиг-оверрайд включает. RabotaByPortalTests (5): дескриптор,
Search/Detail бросают вердикт (п.1.5), health Degraded, вне цепи дефолта.
PortalRegistryDefaultEnabledTests (4): выключенный не в хвосте; конфиг
Enabled=true включает; Enabled=false исключает; Portals-листинг видит
выключенных. BuiltInPortalsDeferredTests (3): состав, все выключены,
нет пересечений с Core. CompositionRootWpp1Tests (2): реестр 2 портала +
пустая цепь; HttpClient синглтон с таймаутом 60 с.

## Порядок применения

1. Поверх WP-0 и WP-P0 (или поверх зеркала с ними же).
2. Проверка целостности: `Expand-Archive` → `dotnet build -c Release` →
   `dotnet test -c Release`.
3. Ожидание: Release 0 warning / 0 error; тесты 77/77 Passed;
   генератор README — 6/6.

## Атрибуция (donor-map, обновлено 2026-10-21)

- Atlas `Services/DedupeHasher.cs` — вербатим Compute (верхний hex).
- Atlas `Services/GitHubHarvesterService.cs` — семантика квоты
  Remaining<20 (строки 89–94) + таймаут HttpClient 60 с.
- MDC `Services/OpenRouterService.cs` — стиль граф-комментариев;
  кулдауны 429 — целевой вербатим для оркестратора (следующий WP).
- Композитный дедуп, DefaultEnabled, адаптеры РБ-порталов — наших доноров
  нет, написаны по канону (архитектура §3.3/§4, RFC-003) с пометкой
  «НАШЕ расширение».
