using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiJobSearchBy.ViewModels;

// FLOW: MainWindow -> MainViewModel (DataContext) -> поиск по порталам,
// адаптация резюме, отклики, история. Дочерние проекции:
// SiteToggleRow/VacancyRow/ApplicationRow.
// LIFECYCLE: синглтон DI (CompositionRoot); InitializeAsync зовёт MainWindow
// (fire-and-forget с ловлей — старт UI не ждёт базу).
// STATE: LogFeed — живая лента протокола; Vacancies/History — списки окон;
// ResumeText/AdaptedResumeText — редактируемые тексты резюме.
// MVVM CommunityToolkit: [ObservableProperty]/[RelayCommand].
// THREAD: свойства — только с UI-потока; сеть/база — Task.Run/await с
// ConfigureAwait(false) внутри сервисов; дельты в коллекции — через RunOnUi
// (диспетчер в тестах не крутится — тесты зовут синхронные Apply*-методы).
// Автоотправка откликов на порталах невозможна честно (личная сессия,
// капча, ToS) — кнопка «Отправить» сохраняет PDF и пишет отклик в историю.
// Клик по строке вакансии — только подсветка; скрепка — открыть в браузере;
// «Адаптировать» — цепочка ИИ-агрегаторов и PDF в папку откликов (язык
// адаптации — язык вакансии). Крестик — мягкое удаление: из базы вакансия
// не стирается, а переносится в таблицу удалённых и не выдаётся при новых
// поисках.
public partial class MainViewModel : ObservableObject
{
    private const string ResumeSettingKey = "resume.text";
    private const string SitesSettingKey = "sites.checked";
    // Формат выбора сайтов: v2 — JSON-объект {id: bool} по ВСЕМ порталам;
    // легаси «sites.checked» — массив ТОЛЬКО отмеченных id. Легаси читается
    // один раз: отмеченные сохраняются, у порталов, появившихся позже
    // легаси-записи, остаётся дефолт карточки; после миграции пишется v2.
    private const string SitesCheckedV2SettingKey = "sites.checked.v2";
    // Порталы, существовавшие в эпоху легаси-формата: их отсутствие в
    // легаси-массиве значит «выключено пользователем» (для появившихся
    // позже это не так — там действует DefaultEnabled).
    private static readonly string[] LegacySiteIds =
        ["remoteok", "remotive", "jobicy", "wwr", "himalayas", "nodesk", "hh-by"];
    private const string OutboxSettingKey = "outbox.path";
    // разрешённые страны удалёнки (JSON-массив display-имён; пусто = весь мир).
    private const string GeoAllowedSettingKey = "geo.allowed";
    // одноразовый крик о замене унаследованной папки откликов
    private bool _legacyOutboxWarned;
    // выключенные чекбоксами звенья цепи (SettingsKV)
    private const string ChainDisabledSettingKey = "chain.disabled";
    // Чекбокс «авто-LMS»: автозагрузка моделей LM Studio через lms — при
    // проверке звеньев lms с нужной моделью загружается сам (если есть на
    // компе и чекбокс установлен).
    private const string LmsAutoLoadSettingKey = "lms.autoload";

    // r21: векторизация вырезана — векторы ничего не потребляли
    // (функции «похожие отклики» не было), emb-шум в протоколе исчез;
    // столбцы VectorJson/VectorModel в базе остались пустыми.
    private readonly Services.Ai.LmStudioCli? _lmStudio;

    private readonly Portals.PortalRegistry? _registry;
    private readonly Services.IApplicationStore? _store;
    private readonly Services.ResumeAdaptationService? _adaptation;
    private readonly Services.IPdfExporter? _pdf;
    private readonly Services.PdfIndexerService? _pdfIndexer;
    private readonly Services.ILogService? _log;
    // Смежные запросы: ИИ называет 1-2 родственных запроса к поиску,
    // каждый проходит лёгким preview-проходом.
    private readonly Services.Ai.RelatedQueriesService? _related;
    // «крестики» юзера (DedupeHash) — удалённое из базы не возвращается
    // ни в окно, ни в базу, ни при следующих поисках. Грузится при старте.
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    // Панель профилей резюме + гео-фильтр поиска.
    private readonly ResumeProfilesViewModel? _resume;
    // свежие ключи/кастомные звенья для «Проверить всех» (env -> local.json)
    private readonly Services.ConfigService? _config;
    private CancellationTokenSource? _searchCts;
    private bool _restoringSites;
    private bool _sessionLogWarned;
    // Разрешённые страны удалёнки (каноны).
    // Дефолт (настройка не задана) — Беларусь + Россия: чужие города
    // прячутся и у удалёнок, и у офисных. «Вернуть все страны» в модалке
    // «Страны…» = весь мир (allowed = null).
    // _allowedInitialized отличает «ещё не загружали из базы» (дефолт)
    // от «весь мир осознанно» (null после полного списка).
    private IReadOnlySet<string>? _allowedCountries;
    private bool _allowedInitialized;

    [ObservableProperty]
    private string _greeting =
        "Поиск по порталам, ИИ-адаптация резюме под выбранную вакансию (язык — как у вакансии). " +
        "Клик по строке — только подсветка; скрепка — открыть на сайте; «Адаптировать» — цепочка ИИ и PDF в папку откликов.";

    [ObservableProperty]
    private string _searchQuery = "";

    // Гео-поля у строки поиска. Пусто = весь мир;
    // страна = офисные по стране; город = офисные по городу; удалёнка — всегда.
    [ObservableProperty]
    private string _searchCountry = "";

    [ObservableProperty]
    private string _searchCity = "";

    // Чекбокс «скрывать без гео»: по умолчанию выключен (лучше увидеть, чем пропустить).
    [ObservableProperty]
    private bool _hideUnknownGeo;

    [ObservableProperty]
    private string _searchStatus = "задай запрос и нажми «Искать»";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isAdapting;

    [ObservableProperty]
    private bool _isBusy;

    // Идёт «Проверить всех» — кнопка гасится на время прогона
    [ObservableProperty]
    private bool _isChecking;

    // Чекбокс «авто-LMS» (дефолт ВКЛ): чужая модель в VRAM — молча грузим
    // требуемую рядом, модалка не нужна; «Проверить всех» поднимает
    // lm-studio через lms сама. Персист — SettingsKV.
    [ObservableProperty]
    private bool _lmsAutoLoad = true;

    [ObservableProperty]
    private string _resumeText = "";

    [ObservableProperty]
    private string _adaptedResumeText = "";

    [ObservableProperty]
    private string _adaptedMeta = "";

    [ObservableProperty]
    private Models.JobPosting? _selectedVacancy;

    [ObservableProperty]
    private bool _hasVacancies;

    // Живая лента протокола (копия LogService.Logs с маршалингом на UI-поток)
    public ObservableCollection<string> LogFeed { get; } = new();

    // Карточки цепи ИИ-провайдеров (снимок на старте)
    public ObservableCollection<ProviderStatusRow> Providers { get; } = new();

    // Чекбоксы сайтов поиска (реестр порталов)
    public ObservableCollection<SiteToggleRow> Sites { get; } = new();

    // Строки перебора вакансий
    public ObservableCollection<VacancyRow> Vacancies { get; } = new();

    // Счётчик у окна «Вакансии»: «найдено XXX вакансий, XX дубля
    // исключено (оранжевым)». «Найдено» — живой размер окна (крестик
    // уменьшает), «дублей исключено» — сумма отсечек за сессию (дедуп в
    // обходе портала + дедуп окна + дубляжи при старте).
    [ObservableProperty]
    private int _duplicatesExcluded;

    partial void OnDuplicatesExcludedChanged(int value)
    {
        OnPropertyChanged(nameof(VacancyDupText));
        OnPropertyChanged(nameof(HasDuplicatesExcluded));
    }

    // «найдено 2 вакансии» рядом с заголовком окна; пусто — окно пустое.
    public string VacancyFoundText => Vacancies.Count == 0
        ? ""
        : $"найдено {VacanciesPlural(Vacancies.Count)}";

    // «Дублей исключено: N» — оранжевым рядом с «найдено».
    public string VacancyDupText => DuplicatesExcluded == 0
        ? ""
        : $"дублей исключено: {DuplicatesExcluded}";

    public bool HasDuplicatesExcluded => DuplicatesExcluded > 0;

    // «Удалённых вами скрыто: N» — сколько вакансий ЭТОГО поиска уже
    // помечено крестиком раньше: в окно они не возвращаются, счётчик
    // честно показывает, что фильтр сработал (вопрос владельца 2026-10-29:
    // «если найдены уже удалённые — там будет инфа?»). Копится за сессию,
    // как и «дублей исключено».
    [ObservableProperty]
    private int _dismissedHidden;

    partial void OnDismissedHiddenChanged(int value)
    {
        OnPropertyChanged(nameof(VacancyDismissedText));
        OnPropertyChanged(nameof(HasDismissedHidden));
    }

    public string VacancyDismissedText => DismissedHidden == 0
        ? ""
        : $"удалённых вами скрыто: {DismissedHidden}";

    public bool HasDismissedHidden => DismissedHidden > 0;

    // Русская грамматика числа: 1 вакансия / 3 вакансии / 5 вакансий /
    // 21 вакансия / 11 вакансий.
    internal static string VacanciesPlural(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        var word = mod10 == 1 && mod100 != 11 ? "вакансия"
            : mod10 is >= 2 and <= 4 && (mod100 is < 12 or > 14) ? "вакансии"
            : "вакансий";
        return $"{n} {word}";
    }

    // Счётчик «найдено» живёт вместе с окном — крестик уменьшает.
    private void OnVacancyRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HasVacancies = Vacancies.Count > 0;
        OnPropertyChanged(nameof(VacancyFoundText));
    }

    // История откликов (работодатель + дата-время)
    public ObservableCollection<ApplicationRow> History { get; } = new();

    // Панель «Моё резюме — профили»: активный профиль даёт текст
    // для адаптации и ключевые для поиска/процентов.
    public ResumeProfilesViewModel Resume { get; }

    // Дизайнер и тесты: пустые коллекции, без зависимостей.
    // IsDesignerMode: параметрless-ctor используется только дизайнером XAML —
    // окно не запускает InitializeAsync (зависимостей-то нет).
    public MainViewModel()
    {
        IsDesignerMode = true;
        Resume = new ResumeProfilesViewModel();
        Vacancies.CollectionChanged += OnVacancyRowsChanged;
    }

    // true — модель построена параметрless-ctor (дизайнер XAML).
    public bool IsDesignerMode { get; }

    // Конфиг создан из шаблона при первом старте: модальное окно первого
    // запуска (MainWindow.OnOpened) сообщает об этом и зовёт настраивать ключи.
    public bool ConfigCreatedFromTemplate => _config?.WasCreated == true;

    // Путь конфига для текста того же окна (где искать local.json).
    public string ConfigPathDisplay => _config?.FilePath ?? "?";

    // DI-ctor (CompositionRoot): протокол, цепь ИИ, реестр, база, сервисы.
    // Роутинг не инжектится напрямую: стадия адаптации живёт внутри
    // ResumeAdaptationService (он и владеет TaskRoutingPolicy).
    public MainViewModel(
        Services.ILogService? log,
        IEnumerable<Services.Ai.IAiChatProvider>? providers,
        Portals.PortalRegistry? registry = null,
        Services.IApplicationStore? store = null,
        Services.ResumeAdaptationService? adaptation = null,
        Services.IPdfExporter? pdf = null,
        Services.PdfIndexerService? pdfIndexer = null,
        Services.ConfigService? config = null,
        ResumeProfilesViewModel? resumeProfiles = null,
        Services.Ai.LmStudioCli? lmStudio = null,
        Services.Ai.RelatedQueriesService? related = null)
    {
        _log = log;
        _config = config;
        _registry = registry;
        _store = store;
        _adaptation = adaptation;
        _pdf = pdf;
        _pdfIndexer = pdfIndexer;
        _resume = resumeProfiles;
        _lmStudio = lmStudio;
        _related = related;
        Resume = resumeProfiles ?? new ResumeProfilesViewModel();

        // Счётчик «найдено X вакансий» у окна «Вакансии» — живёт
        // вместе с коллекцией (крестик уменьшает без ручных пересчётов).
        Vacancies.CollectionChanged += OnVacancyRowsChanged;

        // чекбокс «авто-LMS» — SettingsKV lms.autoload (дефолт ВКЛ).
        // Порядок важен: _store/_lmStudio уже присвоены — OnLmsAutoLoadChanged
        // сработает и тут же синхронизирует CLI и базу тем же значением.
        LmsAutoLoad = _store?.GetSetting(LmsAutoLoadSettingKey) != "0";

        if (log is not null)
        {
            // История — синхронно в конструкторе (UI-поток при старте окна)
            foreach (var line in log.Logs)
                LogFeed.Add(line);

            // Дальше — живая лента с маршалингом (фон-потоки -> UI)
            log.Logs.CollectionChanged += OnProtocolChanged;
        }

        foreach (var provider in providers ?? Enumerable.Empty<Services.Ai.IAiChatProvider>())
        {
            var row = ProviderStatusRow.From(provider);
            row.Toggled += (_, _) => PersistChainDisabled();
            Providers.Add(row);
        }

        // Карточки кастомных звеньев из local.json (Chain.Custom) —
        // «добавил в настройках — карточка на экране».
        AppendCustomLinkRows();

        // Лампы карточек загораются сразу после стартовой пробы каталогов
        // (Program.cs) — зелёная/красная, без ручной кнопки «Проверить
        // всех». Пробы стартуют раньше окна: и уже прошедшие исходы
        // (снимок), и живые события ведут к лампам.
        Services.StartupProbeHub.Finished += OnStartupProbeFinished;
        foreach (var (name, ok) in Services.StartupProbeHub.Snapshot())
            OnStartupProbeFinished(name, ok);
    }

    // Исход стартовой пробы -> лампа карточки звена. Весь доступ к
    // строкам — через RunOnUi (пробы приходят из фонового потока).
    private void OnStartupProbeFinished(string name, bool ok)
    {
        RunOnUi(() =>
        {
            var row = Providers.FirstOrDefault(p => p.LinkId == name);
            if (row is null) return; // звено без карточки (случиться не должно) — красить некуда
            row.SetState(ok ? ChainCheckState.Ok : ChainCheckState.Failed);
        });
    }


    // Стартовая загрузка: сайты, резюме, история, фоновая переиндексация PDF.
    // Вызывается MainWindow один раз; тесты — напрямую (без диспетчера).

    public async Task InitializeAsync()
    {
        LoadSites();
        await RestorePersistedStateAsync().ConfigureAwait(false);
        ApplyHistory(await LoadHistoryAsync().ConfigureAwait(false));

        // Вакансии переживают перезапуск: база их уже хранит — возвращаем
        // в окно при старте (свежие первыми). Гео-фильтр — по текущим
        // настройкам (RestorePersistedState уже их загрузил); крестики
        // юзера — тоже из базы.
        if (_store is not null)
        {
            try
            {
                _dismissed.UnionWith(await Task.Run(() => _store.DismissedHashes())
                    .ConfigureAwait(false));
                var saved = await Task.Run(() => _store.RecentVacancies())
                    .ConfigureAwait(false);
                // Громкая диагностика старта — сразу видно ПУТЬ базы и
                // сколько в ней строк. Если строки пропадают, протокол
                // отвечает: база пуста (не писалось/чужой файл) или строки
                // не дошли до окна (фильтры/крестики) — и почему.
                _log?.Log($"[база] путь: {_store.DbPath}; вакансий в базе {saved.Count}, "
                          + $"крестиков {_dismissed.Count}");
                ApplySavedVacancies(saved);
            }
            catch (Exception ex)
            {
                // старт не должен падать из-за чтения вакансий.
                _log?.LogError($"[db] сохранённые вакансии не прочитаны: {ex.Message}; "
                               + "решение: окно стартует пустым, поиск работает как обычно");
            }

            // «Подбор хвостов» (r20): процессы, оставшиеся 'running' после
            // обрыва (интернет/питание/закрыли окно), закрываются при старте —
            // каждая закрытая строка попадает в протокол (тихих исходов нет).
            try
            {
                var tails = await Task.Run(() => _store.SweepStaleProcesses())
                    .ConfigureAwait(false);
                foreach (var tail in tails)
                {
                    var started = tail.StartedAt.ToString("dd.MM HH:mm");
                    if (string.Equals(tail.Kind, "adapt", StringComparison.Ordinal))
                    {
                        _log?.Log($"[хвост] адаптация «{tail.Detail}» от {started} не завершилась " +
                                  "(обрыв во время ответа модели) — в историю и PDF ничего не " +
                                  "записано; решение: выбрать вакансию и нажать «Адаптировать» заново");
                    }
                    else
                    {
                        _log?.Log($"[хвост] поиск «{tail.Detail}» от {started} прерван обрывом — " +
                                  "всё собранное к моменту обрыва осталось в базе; решение: " +
                                  "повторный поиск дополнит без дублей");
                    }
                }
                if (tails.Count > 0)
                {
                    _log?.LogSuccess($"[хвост] незавершённых процессов закрыто: {tails.Count} — " +
                                     "журнал процессов чист");
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarn($"skip: журнал процессов не проверен — {ex.Message}; " +
                              "решение: поиск и адаптация работают как обычно");
            }
        }
        else
        {
            _log?.Log("[база] в этой сборке базы нет — окно живёт только до закрытия");
        }

        // Панель профилей (словарь + профили) стартует вместе с окном.
        if (_resume is not null)
        {
            await _resume.InitializeAsync().ConfigureAwait(false);
        }

        if (_pdfIndexer is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _pdfIndexer.IndexNewPdfsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // фоновая переиндексация не роняет приложение.
                    _log?.LogError($"[pdf] фоновая переиндексация упала: {ex.Message}; " +
                                   "решение: перезапуск по кнопке «⟳ PDF» в окне истории");
                }
            });
        }
    }

    // Сайты из реестра: все порталы, дефолт чекбокса = DefaultEnabled карточки.
    // Синхронно — тесты зовут напрямую и читают Sites без диспетчера.
    internal void LoadSites()
    {
        if (_registry is null)
        {
            return;
        }

        _restoringSites = true;
        Sites.Clear();
        foreach (var portal in _registry.Portals)
        {
            // подсказка чекбокса — почему портал выключен/какой доступ.
            var hint = portal.Descriptor.ApplyWarning
                ?? portal.Descriptor.Access switch
                {
                    Portals.PortalAccessKind.OfficialApi => "официальное API, ключ не нужен",
                    Portals.PortalAccessKind.Feed => "RSS-лента, ключ не нужен",
                    _ => null,
                };
            var row = new SiteToggleRow
            {
                PortalId = portal.Descriptor.Id,
                DisplayName = portal.Descriptor.DisplayName,
                DefaultEnabled = portal.Descriptor.DefaultEnabled,
                IsChecked = portal.Descriptor.DefaultEnabled,
                Hint = hint,
            };
            row.Toggled += (_, _) => PersistSitesChecked();
            Sites.Add(row);
        }
        _restoringSites = false;
    }

    private async Task RestorePersistedStateAsync()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var savedSitesV2 = await Task.Run(() => _store.GetSetting(SitesCheckedV2SettingKey))
                .ConfigureAwait(false);
            if (savedSitesV2 is not null)
            {
                // v2: полный снапшот {id: bool} — применяем ровно как сохранено.
                var states = JsonSerializer.Deserialize<Dictionary<string, bool>>(savedSitesV2)
                             ?? new Dictionary<string, bool>();
                foreach (var site in Sites)
                {
                    if (states.TryGetValue(site.PortalId, out var on))
                    {
                        site.IsChecked = on;
                    }
                }
            }
            else
            {
                var savedSites = await Task.Run(() => _store.GetSetting(SitesSettingKey))
                    .ConfigureAwait(false);
                if (savedSites is not null)
                {
                    // Легаси-формат: массив только отмеченных. Отмеченные
                    // сохраняем; у порталов, которых не было в эпоху легаси
                    // (новые зеркала hh), остаётся дефолт карточки.
                    var checkedIds = JsonSerializer.Deserialize<string[]>(savedSites)
                                     ?? Array.Empty<string>();
                    foreach (var site in Sites)
                    {
                        site.IsChecked = checkedIds.Contains(site.PortalId)
                            || (!LegacySiteIds.Contains(site.PortalId) && site.DefaultEnabled);
                    }

                    // Разовая миграция: сразу фиксируем v2-снапшот.
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            PersistSitesCheckedSnapshot();
                            _log?.Log("[настройки] выбор сайтов переведён на полный формат — " +
                                      "добавленные зеркала hh включены по умолчанию");
                        }
                        catch (Exception ex)
                        {
                            _log?.LogError($"[db] миграция выбора сайтов не удалась: {ex.Message}; " +
                                           "решение: выбор сохранится при следующем переключении");
                        }
                    });
                }
            }

            var resume = await Task.Run(() => _store.GetSetting(ResumeSettingKey))
                .ConfigureAwait(false);
            if (resume is not null)
            {
                ResumeText = resume;
            }

            // выключенные чекбоксами звенья цепи (включены по умолчанию)
            var disabled = await Task.Run(() => _store.GetSetting(ChainDisabledSettingKey))
                .ConfigureAwait(false);
            if (disabled is not null)
            {
                var disabledIds = JsonSerializer.Deserialize<string[]>(disabled)
                                  ?? Array.Empty<string>();
                foreach (var row in Providers)
                {
                    row.InChain = !disabledIds.Contains(row.LinkId);
                }
            }

            // список разрешённых стран удалёнки (пусто/нет — весь мир).
            var allowed = await Task.Run(() => _store.GetSetting(GeoAllowedSettingKey))
                .ConfigureAwait(false);
            ApplyAllowedCountries(allowed);
        }
        catch (Exception ex)
        {
            // восстановление настроек не роняет старт.
            _log?.LogError($"[db] восстановление настроек не удалось: {ex.Message}; " +
                           "решение: продолжаю с дефолтами, записи в базе не тронуты");
        }
    }

    private void PersistSitesChecked()
    {
        if (_store is null || _restoringSites)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PersistSitesCheckedSnapshot();
            }
            catch (Exception ex)
            {
                _log?.LogError($"[db] выбор сайтов не сохранён: {ex.Message}; " +
                               "решение: выбор восстановится при следующем переключении");
            }
        });
    }

    // Снапшот выбора сайтов: v2 — полное {id: bool} по всем порталам;
    // легаси-массив держим свежим ради отката на старую версию.
    private void PersistSitesCheckedSnapshot()
    {
        if (_store is null)
        {
            return;
        }

        var states = Sites.ToDictionary(s => s.PortalId, s => s.IsChecked);
        _store.SetSetting(SitesCheckedV2SettingKey, JsonSerializer.Serialize(states));
        var checkedIds = Sites.Where(s => s.IsChecked).Select(s => s.PortalId).ToArray();
        _store.SetSetting(SitesSettingKey, JsonSerializer.Serialize(checkedIds));
    }


    // ------------------------------------------------------------------
    // Список разрешённых стран удалёнки. Настройка не задана/пуста ->
    // ДЕФОЛТ: Беларусь + Россия (неБеларусь/неРоссия прячу — и у удалёнок,
    // и у офисных, и у «гео не указано»); «Вернуть все страны» в модалке =
    // весь мир (null — ничего не прячем).
    // ------------------------------------------------------------------

    internal void ApplyAllowedCountries(string? json)
    {
        _allowedInitialized = true;
        if (string.IsNullOrWhiteSpace(json))
        {
            _allowedCountries = Services.Resume.GeoMatcher.DefaultAllowedCountries();
            _log?.Log("[гео] список стран не задан — дефолт: Беларусь + Россия " +
                      "(неБеларусь/неРоссия прячу, удалёнка — внутри этих стран); " +
                      "«Вернуть все страны» = весь мир");
            return;
        }

        try
        {
            var displays = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            var canons = displays
                .Select(Services.Resume.GeoMatcher.CanonOfDisplay)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Cast<string>()
                .ToHashSet();
            _allowedCountries = canons;
            _log?.Log(canons.Count == 0
                ? "[гео] список стран: пусто — весь мир (все члены ООН)"
                : $"[гео] список стран: {canons.Count} из {Services.Resume.CountryTable.Entries.Count}; " +
                  "вычеркнутые — прячутся (и у удалёнок, и по локации)");
        }
        catch (Exception ex)
        {
            _allowedCountries = Services.Resume.GeoMatcher.DefaultAllowedCountries();
            _log?.LogWarn($"skip: список стран не прочитан — {ex.Message}; " +
                          "решение: дефолт Беларусь + Россия");
        }
    }

    // Текст модала настроек: сохранённый список; нет настройки — ДЕФОЛТ
    // (Беларусь + Россия), полный список юзер вернёт кнопкой «Вернуть все
    // страны» (= весь мир).
    internal string LoadAllowedCountriesText()
    {
        var json = _store?.GetSetting(GeoAllowedSettingKey);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var displays = JsonSerializer.Deserialize<string[]>(json);
                if (displays is { Length: > 0 })
                {
                    return string.Join(Environment.NewLine, displays);
                }
            }
            catch
            {
            // битый JSON — фолбэк на дефолт (ApplyAllowedCountries уже
            // крикнула WARN при старте — здесь молча не остаёмся)
            }
        }

        return string.Join(Environment.NewLine,
            Services.Resume.GeoMatcher.DefaultAllowedCountries()
                .Select(Services.Resume.GeoMatcher.DisplayOf)
                .OrderBy(d => d, StringComparer.Ordinal));
    }

    internal void SaveAllowedCountriesText(string text)
    {
        // Сохранение = осознанный выбор юзера: дальше дефолт не применяется
        // (полный список = осознанный «весь мир», null не перетирается).
        _allowedInitialized = true;
        var displays = (text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();
        var total = Services.Resume.CountryTable.Entries.Count;
        try
        {
            if (displays.Count == 0)
            {
                // Пусто = ДЕФОЛТ (Беларусь + Россия) — настройку снимаем.
                _allowedCountries = Services.Resume.GeoMatcher.DefaultAllowedCountries();
                _ = Task.Run(() => _store?.SetSetting(GeoAllowedSettingKey, ""));
                _log?.Log("[гео] список стран: пусто — дефолт (Беларусь + Россия)");
            }
            else if (displays.Count >= total)
            {
                // Полный список = весь мир: храним список ЦЕЛИКОМ (не пусто —
                // пусто после перезапуска вернуло бы дефолт Беларусь + Россия).
                _allowedCountries = null;
                var fullJson = JsonSerializer.Serialize(displays);
                _ = Task.Run(() => _store?.SetSetting(GeoAllowedSettingKey, fullJson));
                _log?.Log($"[гео] список стран: весь мир ({total} записей — все члены ООН)");
            }
            else
            {
                var json = JsonSerializer.Serialize(displays);
                ApplyAllowedCountries(json);
                _ = Task.Run(() => _store?.SetSetting(GeoAllowedSettingKey, json));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarn($"skip: список стран не сохранён — {ex.Message}; " +
                          "решение: действует прежний список");
        }
    }

    private async Task<IReadOnlyList<Services.ApplicationRecord>> LoadHistoryAsync()
    {
        if (_store is null)
        {
            return Array.Empty<Services.ApplicationRecord>();
        }
        try
        {
            return await Task.Run(() => _store.RecentApplications(50)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[db] история откликов не прочитана: {ex.Message}; " +
                           "решение: окно истории пустое, база не тронута");
            return Array.Empty<Services.ApplicationRecord>();
        }
    }


    // Поиск: последовательный обход отмеченных порталов (architecture §6);
    // отказ одного портала ≠ крах прогона (§7) — ловим и продолжаем.
    // Строки добавляются по мере ответов («бегут») и пишутся в Vacancies.

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SearchAsync(CancellationToken ct)
    {
        if (_registry is null || IsSearching)
        {
            return;
        }

        var query = SearchQuery.Trim();
        var checkedIds = Sites.Where(s => s.IsChecked).Select(s => s.PortalId).ToHashSet();
        // Чекбоксы и есть включение: отмеченные порталы попадают в цепь
        // прогона, неотмеченные — нет. hh.by и rabota.by — одно зеркало
        // (hh.by 302 -> rabota.by), поэтому одна карточка
        // «HH.by + rabota.by (Беларусь)».
        var configured = Sites
            .Select(s => new Portals.PortalRegistry.PortalOrderEntry(s.PortalId, s.IsChecked))
            .ToList();
        var chain = _registry.ResolveChain(configured);
        if (chain.Count == 0)
        {
            SearchStatus = "не отмечен ни один сайт";
            _log?.LogWarn("skip: поиск не запущен — причина: нет отмеченных сайтов; " +
                          "решение: отметить сайты чекбоксами над полем запроса");
            return;
        }

        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _searchCts.Token;
        IsSearching = true;
        // Окно не чистится между поисками: строки накапливаются, дедуп —
        // по каноническому URL в окне и по базе. Убрать лишнее — крестик.
        SearchStatus = $"ищу «{query}» по {chain.Count} сайтам…";
        _log?.Log($"[search] старт: «{query}», сайты: {string.Join(", ", chain.Select(p => p.Descriptor.Id))}"
                  + GeoFilterLog());

        // Журнал процессов (r20): прогон получает скользящий Id — пишется в
        // базу на старте и закрывается на финише (done/interrupted/failed).
        // Обрыв приложения посреди прогона оставит строку 'running' — её при
        // следующем старте закроет «подбор хвостов» (SweepStaleProcesses).
        var processId = _store?.StartProcess("search", $"«{query}» по {chain.Count} сайтам");
        var processStatus = "done";
        try
        {
            await RunSearchChainAsync(query, chain, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Сбой вне портальных попыток (окно/база) не должен оставлять
            // статус «ищу…» висеть вечно: фиксируем честно, собранное остаётся.
            processStatus = "failed";
            _log?.LogError($"[search] прогон прерван сбоем — {ex.GetType().Name}: {ex.Message}; " +
                           "решение: собранное к этому моменту осталось в окне и в базе");
        }
        finally
        {
            if (processId is { } pid)
            {
                _store?.FinishProcess(pid,
                    token.IsCancellationRequested ? "interrupted" : processStatus);
            }
            SearchStatus = token.IsCancellationRequested
                ? "поиск остановлен пользователем"
                : $"готово: {Vacancies.Count} вакансий в окне";
            IsSearching = false;
        }
    }

    // Тело прогона: обход цепи порталов + смежные запросы (r20 — вынесено
    // из SearchAsync один в один, логика не менялась; вокруг тела появился
    // try/catch/finally журнала процессов).
    private async Task RunSearchChainAsync(
        string query,
        IReadOnlyList<Portals.IPortalService> chain,
        CancellationToken token)
    {
        foreach (var portal in chain)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }
            try
            {
                var postings = await portal.SearchAsync(string.IsNullOrWhiteSpace(query) ? "*" : query, token)
                    .ConfigureAwait(false);
                if (portal is Portals.RemoteOkPortal remoteOk)
                {
                    postings = remoteOk.ApplyQuery(postings, query);
                }

                // Пустой запрос на API-порталах, где он обязателен, заменён «*»:
                // каталог целиком. Строки в окно — по мере ответа портала.
                // Строка запроса идёт в окно вместе со строками — по ней
                // ApplyVacancies отсекает нерелевантное («гардеробщик» -> саппорт).
                // Счётчики фильтров — в итоговую строку, а не только построчно.
                var count = postings.Count;
                // Похожие вакансии («Вам подойдут эти вакансии») не собираются:
                // конвейер принимает только выдачу.
                var applied = default((int Added, int GeoHidden, int Irrelevant));
                await RunOnUiAsync(() =>
                {
                    applied = ApplyVacancies(postings, query);
                    // дубли, отсечённые в обходе портала (перепосты
                    // «название+работодатель»), — в тот же счётчик окна.
                    if (portal is Portals.BrowserEmulationPortalBase browser)
                    {
                        DuplicatesExcluded += browser.LastRunDuplicatesExcluded;
                    }
                }).ConfigureAwait(false);
                var inserted = await Task.Run(() => _store?.UpsertVacancies(postings) ?? 0, token)
                    .ConfigureAwait(false);
                var filtered = applied.GeoHidden > 0 || applied.Irrelevant > 0
                    ? $" (гео скрыл {applied.GeoHidden}, нерелевантных {applied.Irrelevant})"
                    : "";
                _log?.Log($"[search] {portal.Descriptor.Id}: в окно {applied.Added} из {count}{filtered}, " +
                          $"в базу новеньких {inserted}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Отказ одного портала не валит прогон — причина уже в
                // протоколе адаптера; фиксируем продолжение прогона.
                _log?.LogWarn($"skip: {portal.Descriptor.Id} пропущен в этом прогоне — " +
                              $"{ex.GetType().Name}; решение: двигаюсь к следующему порталу");
            }
        }


        // Смежные запросы: hh на странице вакансии сам подбирает родственные
        // специальности — наш поиск делает то же самое: ИИ-цепь называет 1-2
        // смежных запроса, каждый проходит ЛЁГКИЙ preview-проход (первая
        // страница выдачи, без вкладок). Кэш в базе: повторный поиск ИИ не
        // платит.

        if (!token.IsCancellationRequested)
        {
            IReadOnlyList<string> related = _related is null
                ? Array.Empty<string>()
                : await _related.ExpandAsync(query, token).ConfigureAwait(false);
            foreach (var relatedQuery in related)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                foreach (var portal in chain.OfType<Portals.BrowserEmulationPortalBase>())
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var preview = await portal.SearchPreviewAsync(relatedQuery, token)
                            .ConfigureAwait(false);
                        var relatedApplied = default((int Added, int GeoHidden, int Irrelevant));
                        await RunOnUiAsync(() =>
                            relatedApplied = ApplyVacancies(preview, relatedQuery)).ConfigureAwait(false);
                        var relatedInserted = await Task.Run(
                            () => _store?.UpsertVacancies(preview) ?? 0, token).ConfigureAwait(false);
                        var relatedFiltered = relatedApplied.GeoHidden > 0 || relatedApplied.Irrelevant > 0
                            ? $" (гео скрыл {relatedApplied.GeoHidden}, нерелевантных {relatedApplied.Irrelevant})"
                            : "";
                        _log?.Log($"[search] {portal.Descriptor.Id}: смежный запрос «{relatedQuery}» — " +
                                  $"в окно {relatedApplied.Added} из {preview.Count}{relatedFiltered}, " +
                                  $"в базу новеньких {relatedInserted}");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break; // Отмена в смежном проходе: собранное уже в окне/базе
                    }
                    catch (Exception ex)
                    {
                        // Смежный проход — надстройка: сбой не валит основной результат.
                        _log?.LogWarn($"skip: смежный запрос «{relatedQuery}» на {portal.Descriptor.Id} " +
                                      $"{ex.GetType().Name}; решение: основной результат не тронут");
                    }
                }
            }
        }
    }

    // Синхронное применение (тесты зовут напрямую; UI — через RunOnUi).
    // каждая строка получает бейдж режима (удалёнка/офис/?), вердикт
    // гео-фильтра и % совпадения ключевых активного профиля; явно
    // несовпавшие (офис в другом городе) в окно не идут — причина каждой
    // скрытой — в протоколе (ни один тихий фильтр не разрешён).
    // relevanceQuery — строка поиска; вакансии, где ни одного слова
    // запроса нет в заголовке (или меньше порога для списка ключевых),
    // скрываются с записью в протокол — «гардеробщик» больше не тянет
    // саппорт-вакансии каталогов, игнорирующих query.
    // возвращает счётчики (добавлено в окно / скрыто гео / скрыто
    // релевант-гейтом) — итоговая строка [search] больше не считает
    // скрытые «в окне».
    // Окно не чистится между поисками — дубли окна пропускаются
    // по каноническому URL; «крестики» юзера не возвращаются вообще;
    // похожие (IsSimilar, hh сам подобрал) релевант-гейт не проходят.
    internal (int Added, int GeoHidden, int Irrelevant) ApplyVacancies(
        IReadOnlyList<Models.JobPosting> postings,
        string? relevanceQuery = null)
    {
        var filter = new Services.Resume.GeoFilter(SearchCountry, SearchCity);
        // до первой загрузки настройки из базы действует ДЕФОЛТ
        // (Беларусь + Россия). Флаг отличает «ещё не загружали» от осознанного
        // «весь мир» (null после «Вернуть все страны») — перетирать его нельзя.
        if (!_allowedInitialized)
        {
            _allowedCountries = Services.Resume.GeoMatcher.DefaultAllowedCountries();
            _allowedInitialized = true;
        }

        var allowed = _allowedCountries;
        var keywords = _resume?.ActiveProfile?.Keywords ?? (IReadOnlyList<string>)Array.Empty<string>();
        var added = 0;
        var hiddenGeo = 0;
        var hiddenIrrelevant = 0;
        var duplicates = 0;
        var dismissedHits = 0;
        // ключи «название+работодатель» уже болтающихся в окне строк —
        // новый поиск не доливает перепосты к существующим.
        var titleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Vacancies)
        {
            var existingKey = Services.DedupeHasher.TitleKey(row.Posting.Title, row.Posting.Company);
            if (existingKey.Length > 0)
            {
                titleKeys.Add(existingKey);
            }
        }

        foreach (var posting in postings)
        {
            // крестик юзера — закон: помеченное не возвращается.
            if (_dismissed.Contains(posting.DedupeHash))
            {
                dismissedHits++;
                continue;
            }

            // дедуп окна — та же вакансия (канонический URL) не
            // дублируется между поисками; «болтается» одна строка.
            if (Vacancies.Any(v => string.Equals(
                    v.Posting.CanonicalUrl, posting.CanonicalUrl, StringComparison.Ordinal)))
            {
                duplicates++;
                continue;
            }

            var remote = Services.Resume.GeoMatcher.DetectRemote(posting);
            var verdict = Services.Resume.GeoMatcher.Match(posting, filter, remote, HideUnknownGeo,
                allowed);
            if (!verdict.Pass)
            {
                hiddenGeo++;
                _log?.Log($"[гео] скрыл «{posting.Title ?? posting.CanonicalUrl}» — {verdict.Note}");
                continue;
            }

            // похожие (hh сам подобрал на странице вакансии) релевант
            // строки запроса не проходят намеренно — иначе «Системный
            // администратор» при запросе «SAP администратор» прятался бы.
            // Гео-фильтр общий — чужой город прячет и похожих.
            if (!posting.IsSimilar && !Services.Resume.RelevanceFilter.Pass(posting, relevanceQuery))
            {
                hiddenIrrelevant++;
                _log?.Log($"[поиск] скрыл «{posting.Title ?? posting.CanonicalUrl}» ({posting.PortalId}) — "
                          + $"нерелевантно: {Services.Resume.RelevanceFilter.Reason(relevanceQuery)}");
                continue;
            }

            // Одна строка на название+работодателя. Перепосты hh
            // под другими id и филиалы «Компания. Отдел» окно не забивают
            // (свои порталы уже отсеял — это добегают чужие поиски/похожие).
            // После гео/релевант-гейтов: спрятанная фильтром строка ключ
            // не занимает — первым приходит та, что реально прошла.
            var titleKey = Services.DedupeHasher.TitleKey(posting.Title, posting.Company);
            if (titleKey.Length > 0 && !titleKeys.Add(titleKey))
            {
                duplicates++;
                _log?.Log($"[search] дубль «{posting.Title}» "
                          + $"({posting.Company ?? "работодатель не указан"}) — {posting.CanonicalUrl}: "
                          + "одинаковое название и работодатель уже в окне");
                continue;
            }

            Vacancies.Add(new VacancyRow
            {
                Posting = posting,
                Remote = remote,
                PassGeo = true,
                UnknownGeo = verdict.UnknownGeo,
                GeoNote = verdict.Note,
                MatchPercent = MatchPercent(keywords, posting),
            });
            added++;
        }

        // Тихих фильтров не бывает: пропуски дедупа/крестиков — в протокол.
        if (duplicates > 0)
        {
            _log?.Log($"[search] дублей в окне пропущено: {duplicates} "
                      + "(канонический URL либо название+работодатель уже в окне)");
        }

        // отсечённые дубли — в счётчик у окна «Вакансии»
        // («найдено X вакансий, дублей исключено Y» — оранжевым).
        DuplicatesExcluded += duplicates;
        // встречи с «крестиками» — в свой счётчик («удалённых вами скрыто N»):
        // молчаливая отсечка видна только в протоколе — этого мало.
        DismissedHidden += dismissedHits;
        if (dismissedHits > 0)
        {
            _log?.Log($"[search] с крестиком пропущено: {dismissedHits} (в таблице удалённых — по решению юзера)");
        }
        if (hiddenIrrelevant > 0)
        {
            _log?.Log($"[поиск] нерелевантных скрыто: {hiddenIrrelevant} (строка запроса — фильтр по заголовку/тексту)");
        }

        HasVacancies = Vacancies.Count > 0;
        return (added, hiddenGeo, hiddenIrrelevant);
    }


    // Возврат сохранённых вакансий в окно при старте (вакансии переживают
    // перезапуск). База отдаёт свежие первыми; релевант-гейт строки запроса
    // НЕ применяется (у каждой строки свой запрос из прошлого), гео — по
    // текущим настройкам. Синхронно — тесты зовут напрямую.

    internal (int Shown, int GeoHidden) ApplySavedVacancies(
        IReadOnlyList<Models.JobPosting> saved)
    {
        var filter = new Services.Resume.GeoFilter(SearchCountry, SearchCity);
        if (!_allowedInitialized)
        {
            _allowedCountries = Services.Resume.GeoMatcher.DefaultAllowedCountries();
            _allowedInitialized = true;
        }

        var allowed = _allowedCountries;
        var keywords = _resume?.ActiveProfile?.Keywords ?? (IReadOnlyList<string>)Array.Empty<string>();
        var shown = 0;
        var hiddenGeo = 0;
        var hiddenDuplicates = 0;
        // при старте дедуп «название+работодатель» тоже работает —
        // перепосты, легшие в базу раньше, вторую строку в окне не занимают
        // (свежие первыми — выживает самый новый перепост).
        var titleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var posting in saved)
        {
            if (_dismissed.Contains(posting.DedupeHash)
                || Vacancies.Any(v => string.Equals(
                    v.Posting.CanonicalUrl, posting.CanonicalUrl, StringComparison.Ordinal)))
            {
                continue;
            }

            var titleKey = Services.DedupeHasher.TitleKey(posting.Title, posting.Company);
            if (titleKey.Length > 0 && titleKeys.Contains(titleKey))
            {
                hiddenDuplicates++;
                continue;
            }

            if (titleKey.Length > 0)
            {
                titleKeys.Add(titleKey);
            }

            var remote = Services.Resume.GeoMatcher.DetectRemote(posting);
            var verdict = Services.Resume.GeoMatcher.Match(posting, filter, remote, HideUnknownGeo,
                allowed);
            if (!verdict.Pass)
            {
                hiddenGeo++;
                continue; // при старте молчим: причины уже были в протоколе при поиске
            }

            Vacancies.Add(new VacancyRow
            {
                Posting = posting,
                Remote = remote,
                PassGeo = true,
                UnknownGeo = verdict.UnknownGeo,
                GeoNote = verdict.Note,
                MatchPercent = MatchPercent(keywords, posting),
            });
            shown++;
        }

        HasVacancies = Vacancies.Count > 0;
        // дубляжи при старте — тоже в счётчик окна.
        DuplicatesExcluded += hiddenDuplicates;
        _log?.Log(saved.Count == 0
            ? "[база] сохранённых вакансий нет — окно пустое, первый поиск всё запомнит"
            : $"[база] в окно {shown} из {saved.Count} сохранённых вакансий" +
              (hiddenDuplicates > 0 ? $" (дублей название+работодатель {hiddenDuplicates})" : "") +
              (hiddenGeo > 0 ? $" (гео скрыл {hiddenGeo} — список стран уже другой)" : "") +
              "; решение: строки переживают перезапуск, убрать лишние — крестик справа");
        return (shown, hiddenGeo);
    }


    // Крестик: строка исчезает с экрана, база ПЕРЕНОСИТ вакансию в таблицу
    // удалённых (полный снапшот — данные не теряются), при новых поисках
    // она не возвращается ни в окно, ни в базу.

    [RelayCommand]
    private void DeleteVacancy(VacancyRow? row)
    {
        if (row is null)
        {
            return;
        }

        Vacancies.Remove(row);
        HasVacancies = Vacancies.Count > 0;
        _dismissed.Add(row.Posting.DedupeHash);
        if (_store is null)
        {
            _log?.Log($"[крестик] «{row.Title}» убрана из окна (базы в этой сборке нет)");
            return;
        }

        try
        {
            _store.DeleteVacancy(row.Posting.DedupeHash);
            _log?.LogSuccess($"[крестик] «{row.Title}» убрана с экрана — перемещена в таблицу " +
                             "удалённых вакансий (данные в базе сохранены); при новых поисках " +
                             "возвращаться не будет");
        }
        catch (Exception ex)
        {
            // строка из окна уже убрана; база — при следующем крестике.
            _log?.LogError($"[крестик] перенос в таблицу удалённых не удался — {ex.GetType().Name}: {ex.Message}; " +
                           "решение: строка из окна убрана, в удалённые уйдёт при следующей попытке");
        }
    }

    // % совпадения: доля ключевых профиля, найденных в тексте вакансии
    // (нормализация: регистр/ё — GeoMatcher). Ключевых нет — null (не показываем).
    private static int? MatchPercent(IReadOnlyList<string> keywords, Models.JobPosting posting)
    {
        if (keywords.Count == 0)
        {
            return null;
        }

        var hay = Services.Resume.GeoMatcher.Normalize(
            $"{posting.Title} {posting.Company} {posting.DescriptionMd}");
        var matched = keywords.Count(k =>
        {
            var n = Services.Resume.GeoMatcher.Normalize(k);
            return n.Length > 0 && hay.Contains(n, StringComparison.Ordinal);
        });
        return (int)Math.Round(100.0 * matched / keywords.Count);
    }

    // Строка гео-фильтра для [search]-записи протокола (видно, ЧТО фильтровалось).
    private string GeoFilterLog()
    {
        var country = string.IsNullOrWhiteSpace(SearchCountry) ? null : SearchCountry.Trim();
        var city = string.IsNullOrWhiteSpace(SearchCity) ? null : SearchCity.Trim();
        return country is null && city is null
            ? ""
            : $"; гео: {country ?? "—"}/{city ?? "—"} (удалёнка — в разрешённых странах)";
    }


    // кнопка «Из резюме» — ключевые активного профиля в строку поиска.

    [RelayCommand]
    private void UseResumeKeywords()
    {
        var query = _resume?.ActiveKeywordsQuery() ?? "";
        if (query.Length == 0)
        {
            _log?.LogWarn("skip: «Из резюме» пусто — причина: нет активного профиля или ключевых; " +
                          "решение: загрузить резюме в панели профилей и отметить слова");
            SearchStatus = "нет ключевых у активного профиля";
            return;
        }

        SearchQuery = query;
        _log?.Log($"[search] подставил ключевые активного профиля ({query.Split(',').Length} слов)");
    }


    // Кнопка-ссылка в строке карточки + двойной клик — CanonicalUrl
    // в браузере (каждая вакансия ведёт точно на свой сайт).

    [RelayCommand]
    private void OpenVacancy(VacancyRow? row)
    {
        if (row is null)
        {
            return;
        }

        var url = row.Posting.CanonicalUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            _log?.LogWarn($"skip: у «{row.Title}» нет ссылки — источник не дал canonical url");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _log?.Log($"[отклик] открыл вакансию в браузере: {url}");
        }
        catch (Exception ex)
        {
            // браузера нет/запрещён — не роняем окно, причина в протоколе.
            _log?.LogWarn($"skip: не смог открыть браузер — {ex.GetType().Name}; решение: скопируй ссылку из окна отклика");
        }
    }



    // кнопка «Папка лога» — открывает каталог протокола в проводнике.
    // Файл: logs/ai-job-search-by-ГГГГММДД.log (баннер старта печатает путь);
    // под Windows это %APPDATA%\ai-job-search-by\logs\.

    [RelayCommand]
    private void OpenLogFolder()
    {
        var dir = _log?.LogDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _log?.LogWarn($"skip: папка протокола не найдена — {dir ?? "(службы лога нет)"}; " +
                          "путь напечатан первой строкой в баннере старта");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            _log?.Log($"[протокол] открыл папку лога: {dir}");
        }
        catch (Exception ex)
        {
            // проводника нет/запрещён — не роняем окно, причина в протоколе
            _log?.LogWarn($"skip: не смог открыть папку лога — {ex.GetType().Name}; " +
                          $"открой вручную: {dir}");
        }
    }


    // Клик по строке ТОЛЬКО выбирает — окно держит выделение (MainWindow
    // больше не сбрасывает SelectedIndex), а работать с выбранной — скрепка
    // (сайт) и кнопка «Адаптировать» (цепочка ИИ). Никаких сетей/баз/
    // адаптаций по клику.

    [RelayCommand]
    private void SelectVacancy(VacancyRow? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedVacancy = row.Posting;
        AdaptedResumeText = "";
        AdaptedMeta = "";
        _log?.Log($"[выбор] «{row.Title}» — работаем с этой; адаптация — кнопкой " +
                  "«Адаптировать», открыть на сайте — скрепка в строке");
    }

    // текст для адаптации — MD-канон активного профиля; профилей нет —
    // фолбэк на старое поле (SettingsKV resume.text) для совместимости.
    private string GetResumeText()
        => _resume?.ActiveProfile?.CanonMd is { Length: > 0 } canon ? canon : ResumeText;


    // Кнопка «Адаптировать» — ЕДИНСТВЕННОЕ место запуска цепочки
    // агрегаторов (по клику на строку цепь не трогается). Делает:
    // ИИ-адаптация резюме активного профиля под выбранную вакансию (язык —
    // как у вакансии: EN -> EN, RU -> RU), текст в окно, PDF в папку
    // откликов, строка в истории. Браузер НЕ открываем: открыть вакансию —
    // скрепка в строке.

    [RelayCommand]
    private async Task AdaptVacancyAsync()
    {
        if (IsAdapting)
        {
            return;
        }

        if (SelectedVacancy is null)
        {
            _log?.LogWarn("skip: адаптация не запущена — причина: вакансия не выбрана; " +
                          "решение: кликнуть строку в окне «Вакансии» (подсветится) и нажать «Адаптировать»");
            return;
        }

        var posting = SelectedVacancy;
        var baseResume = GetResumeText();
        if (string.IsNullOrWhiteSpace(baseResume))
        {
            AdaptedResumeText = "Сначала загрузи резюме в панели «Моё резюме» (PDF/DOCX/TXT/MD) — модель адаптирует его под выбранную вакансию.";
            _log?.LogWarn("skip: адаптация не запущена — причина: резюме пустое (нет активного профиля); " +
                          "решение: загрузить файл в панели «Моё резюме — профили»");
            return;
        }

        IsAdapting = true;
        IsBusy = true;

        // Журнал процессов (r20): адаптация получает скользящий Id на старте;
        // финал — done (запись в «Истории откликов» создана) или failed
        // (цепь не ответила / пустой текст / сбой). Обрыв приложения финала
        // не пишет вовсе — хвост закроет SweepStaleProcesses при следующем
        // старте; в историю попадает только полный успех (см. ниже).
        // Процесс открыт ДО похода за полным текстом — обрыв на этом шаге
        // тоже хвост, и он будет подобран.
        var processId = _store?.StartProcess("adapt", $"{posting.Company ?? "(работодатель не указан)"} — {posting.Title}");
        var processStatus = "failed";

        // r20: полный текст вакансии — со страницы вакансии. У hh-порталов
        // карточка листинга несёт только «шапку» (опыт/выплаты/удалёнка) —
        // модели не хватает описания обязанностей (вопрос владельца 2026-10-29,
        // подтверждён кодом). Достаём страницу вакансии через DetailAsync,
        // кэшируем в базу (UpdateVacancyDetail + флаг DetailFetched) —
        // повторная адаптация той же вакансии в сеть не ходит. Не досталось
        // (щит/сеть) — адаптируем по карточке листинга, честно в протоколе.
        posting = await EnrichVacancyWithDetailAsync(posting).ConfigureAwait(true);

        var language = Services.ResumeAdaptationService.DetectLanguage(posting);
        var employer = string.IsNullOrWhiteSpace(posting.Company)
            ? "(работодатель не указан)"
            : posting.Company!;
        AdaptedResumeText = language == Services.VacancyLanguage.English
            ? "адаптирую под вакансию (на английском)…"
            : "адаптирую под вакансию…";

        try
        {
            var result = await _adaptation!.AdaptAsync(baseResume, posting).ConfigureAwait(false);
            if (result is null)
            {
                AdaptedResumeText = "цепь ИИ недоступна — причины в протоколе; " +
                                    "перезапусти адаптацию позже или введи ключ провайдера";
                return;
            }

            // Пустой текст — не результат: PDF и запись истории НЕ создаём
            // (пустая строка в истории и пустое окно = глух, воспроизведён
            // 2026-10-29: «адаптировано 15.4 с», а резюме пустое — модель
            // унесла ответ в reasoning_content). В окно — причина, дальше
            // повторная адаптация берёт следующего провайдера цепи.
            if (string.IsNullOrWhiteSpace(result.Content))
            {
                AdaptedResumeText = "модель вернула пустой текст — в историю и PDF ничего " +
                                    "не сохранено; причины в протоколе; решение: нажать " +
                                    "«Адаптировать» ещё раз — цепь возьмёт следующего провайдера";
                _log?.LogError("[adapt] модель вернула пустой текст — PDF не собран, запись в " +
                               "историю не создана; решение: повторить адаптацию (фолбэк цепи)");
                return;
            }

            AdaptedResumeText = result.Content;
            AdaptedMeta = $"адаптировано " +
                          $"({(language == Services.VacancyLanguage.English ? "на английском" : "на русском")}): " +
                          $"{result.ProviderName} · {result.Model} · " +
                          $"{result.Usage.TotalTokens} токенов · {result.Duration.TotalSeconds:0.#} с";

            // Адаптированное резюме сохраняется PDF-файлом в папку откликов.
            var outDir = ResolveOutboxDir();
            var createdAt = DateTimeOffset.Now;
            var pdfPath = await Task.Run(() =>
                _pdf!.SaveResumePdfAsync(employer, posting.Title, AdaptedResumeText, createdAt, outDir))
                .ConfigureAwait(false);

            var vacancyId = await Task.Run(() => _store!.EnsureVacancy(posting)).ConfigureAwait(false);

            var record = new Services.ApplicationRecord
            {
                Id = Guid.NewGuid(),
                VacancyId = vacancyId,
                Employer = employer,
                VacancyTitle = posting.Title,
                VacancyUrl = posting.CanonicalUrl,
                Status = "adapted",
                CvPath = pdfPath,
                SentAt = createdAt,
                CreatedAt = createdAt,
                ResumeText = AdaptedResumeText,
                VectorJson = null, // r21: векторизация вырезана — столбец остался пустым
                VectorModel = null,
            };
            await Task.Run(() => _store!.SaveApplication(record)).ConfigureAwait(false);
            // Точка полного успеха: запись в истории создана — процесс «done».
            processStatus = "done";
            RunOnUi(() => History.Insert(0, new ApplicationRow { Record = record }));

            _log?.LogSuccess($"адаптация готова, PDF сохранён: {Path.GetFileName(pdfPath)}; " +
                             "решение: файл в папке откликов — прикрепи его на сайте " +
                             "(открыть вакансию — скрепка в строке)");
        }
        catch (Exception ex)
        {
            // полный контекст сбоя — в протокол; частично сделанное
            // (PDF/база) не откатываем — файл ценен сам по себе.
            _log?.LogError($"адаптация не удалась — {ex.GetType().Name}: {ex.Message}; " +
                           "решение: проверить протокол выше (ИИ-цепь/PDF/база), исправить и повторить");
        }
        finally
        {
            if (processId is { } pid)
            {
                _store?.FinishProcess(pid, processStatus);
            }
            IsAdapting = false;
            IsBusy = false;
        }
    }

    // r20: полный текст вакансии со страницы вакансии — в промпт адаптации.
    // Достаём ТОЛЬКО когда: (а) портал умеет детали (hh-группа: SupportsDetail,
    // ленты/API отдают описание прямо в ответе поиска — им ходить не нужно)
    // и (б) текст ещё не в кэше базы (флаг DetailFetched). Итог кэшируется
    // (UpdateVacancyDetail). Любой сбой — не повод срывать адаптацию:
    // в протоколе причина, адаптируем по карточке листинга как раньше.
    // THREAD: вызов на UI-потоке (до цепочки ИИ); сеть — в Task.Run.
    private async Task<Models.JobPosting> EnrichVacancyWithDetailAsync(
        Models.JobPosting posting)
    {
        if (posting.DetailFetched)
        {
            _log?.Log($"[adapt] полный текст вакансии уже в кэше базы — в сеть не хожу " +
                      $"({posting.DescriptionMd?.Length ?? 0} зн. описания)");
            return posting;
        }

        var portal = _registry?.Portals
            .FirstOrDefault(p => p.Descriptor.Id == posting.PortalId);
        if (portal is null || !portal.SupportsDetail)
        {
            // Ленты/API: описание приходит в ответе поиска — доставлять нечего.
            return posting;
        }

        AdaptedResumeText = "достаю полный текст вакансии со страницы сайта…";
        _log?.Log($"[adapt] полный текст вакансии: открываю страницу " +
                  $"{posting.CanonicalUrl} (портал {posting.PortalId})");
        try
        {
            var detail = await Task.Run(
                () => portal.DetailAsync(posting.CanonicalUrl)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(detail.DescriptionMd))
            {
                _log?.LogWarn("[adapt] страница вакансии открылась, но описания в ней нет — " +
                              "адаптирую по карточке листинга; решение: повторить адаптацию позже");
                return posting;
            }

            var merged = posting with
            {
                DescriptionMd = detail.DescriptionMd,
                SalaryText = posting.SalaryText ?? detail.SalaryText,
            };
            var cached = await Task.Run(
                () => _store!.UpdateVacancyDetail(posting.DedupeHash, detail.DescriptionMd!))
                .ConfigureAwait(false);
            _log?.LogSuccess($"[adapt] полный текст вакансии получен: " +
                             $"{detail.DescriptionMd.Length} зн. — идёт в промпт модели; " +
                             (cached
                                 ? "закэширован в базе — повторная адаптация этой вакансии в сеть не пойдёт"
                                 : "в базе вакансии уже нет (крестик) — кэш не пишется"));
            return merged;
        }
        catch (Exception ex)
        {
            _log?.LogWarn($"[adapt] полный текст вакансии не достался — адаптирую по карточке " +
                          $"листинга ({ex.GetType().Name}: {ex.Message}); решение: повторить " +
                          "адаптацию — попытка достать текст будет снова");
            return posting;
        }
    }

    private string ResolveOutboxDir()
    {
        // общий резолвер — пусто или унаследованное «Документы\…» →
        // новый дефолт <exe>\resume.
        var configured = _store?.GetSetting(OutboxSettingKey);
        var resolved = Services.ApplicationStore.ResolveOutbox(configured);
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(resolved, configured!.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !_legacyOutboxWarned)
        {
            _legacyOutboxWarned = true;
            _log?.Log($"[отклики] старая папка {configured.Trim()} из настроек заменена дефолтом {resolved} " +
                      "(решение 2026-10-26: отклики — рядом с exe в resume\\; файлы из Документов переносит мигратор при старте)");
        }
        return resolved;
    }

    [RelayCommand]
    private void OpenOutbox()
    {
        var dir = ResolveOutboxDir();
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log?.LogError($"папка откликов не открылась: {dir} — {ex.Message}; " +
                           "решение: открыть папку вручную в проводнике");
        }
    }


    // Выгрузка видимых строк «Вакансий (перебор)» в CSV
    // (Excel-совместимый: «;», UTF-8 с BOM — VacancyCsvExport). Файл —
    // exports\vacancies_ГГГГ-ММ-ДД_ЧЧмм.csv рядом с exe; путь — в протокол
    // и в статусную строку (никаких тихих файлов).

    // Для тестов: куда класть файл (в проде — exports рядом с exe).
    internal string? ExportDirOverride { private get; set; }

    [RelayCommand]
    private async Task ExportVacanciesAsync()
    {
        var rows = Vacancies.ToList();
        if (rows.Count == 0)
        {
            _log?.LogWarn("skip: экспорт не выполнен — причина: список вакансий пуст; " +
                          "решение: запустить поиск — строки пойдут в окно «Вакансии (перебор)»");
            return;
        }

        var dir = ExportDirOverride ?? Path.Combine(AppContext.BaseDirectory, "exports");
        var filePath = Path.Combine(dir, VacancyCsvExport.FileName(DateTimeOffset.Now));
        IsBusy = true;
        try
        {
            var csv = VacancyCsvExport.Build(rows);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, csv, System.Text.Encoding.UTF8);
            }).ConfigureAwait(false);
            _log?.LogSuccess($"[экспорт] выгрузил {rows.Count} вакансий: {filePath}; " +
                             "решение: файл открывается в Excel (разделитель «;», UTF-8)");
            SearchStatus = $"экспорт: {rows.Count} вакансий → {filePath}";
        }
        catch (Exception ex)
        {
            // экспорт не роняет окно — причина и путь в протоколе.
            _log?.LogError($"экспорт не удался — {ex.GetType().Name}: {ex.Message}; " +
                           $"решение: проверить права на {dir}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveResumeAsync()
    {
        if (_store is null)
        {
            return;
        }
        try
        {
            await Task.Run(() => _store.SetSetting(ResumeSettingKey, ResumeText)).ConfigureAwait(false);
            _log?.LogSuccess($"резюме сохранено в базу ({ResumeText.Length} зн.)");
        }
        catch (Exception ex)
        {
            _log?.LogError($"резюме не сохранено: {ex.Message}; решение: повторить сохранение");
        }
    }

    [RelayCommand]
    private async Task ReindexPdfAsync()
    {
        if (_pdfIndexer is null)
        {
            return;
        }
        try
        {
            await Task.Run(() => _pdfIndexer.IndexNewPdfsAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // кнопка не роняет UI.
            _log?.LogError($"переиндексация PDF упала: {ex.Message}; решение: повторить позже");
        }
    }

    // Окно настроек: MainWindow подписывается и открывает модальное окно.
    public event Action? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();


    // «Проверить всех»: живая проба
    // всех ВКЛЮЧЁННЫХ звеньев цепи. Ноль сожжённых токенов: GET {base}models
    // (каталог), а не chat/completions. Ключи берутся СВЕЖИМИ (env ->
    // local.json / Chain.Custom): подкрутил настройки — проверяй без
    // перезапуска. Звенья, уже ок в ЭТОЙ СЕССИИ, не перепроверяются
    // (экономия бесплатных квот), неудачные — каждый раз. Каждый исход —
    // в лампу карточки, протокол (с причиной) и сессионный лог на диске.

    [RelayCommand]
    private async Task CheckAllAsync()
    {
        if (IsChecking)
        {
            return;
        }

        var links = Providers.Where(p => p.InChain).ToList();
        if (links.Count == 0)
        {
            _log?.LogWarn("skip: «Проверить всех» не запущен — причина: нет включённых звеньев; " +
                          "решение: включить чекбоксы на карточках цепи");
            return;
        }

        IsChecking = true;
        try
        {
            var toCheck = links.Where(p => p.State != ChainCheckState.Ok).ToList();
            var skipped = links.Count - toCheck.Count;
            _log?.Log($"[check] старт: в цепи {links.Count}, к проверке {toCheck.Count}" +
                      (skipped > 0
                          ? $", пропущено (уже ок в этой сессии): {skipped} — не жжём токены"
                          : ""));
            AppendSessionLog(
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | запуск «Проверить всех»: " +
                $"в цепи {links.Count}, к проверке {toCheck.Count}, пропущено (уже ок): {skipped}");

            var okCount = 0;
            var failCount = 0;
            foreach (var row in toCheck)
            {
                var outcome = await ProbeRowAsync(row).ConfigureAwait(false);

                var verdict = outcome.Ok ? ChainCheckState.Ok : ChainCheckState.Failed;
                RunOnUi(() =>
                {
                    row.SetState(verdict);
                    row.KeyNote = outcome.Reason;
                });

                var line = $"{row.Name} / {row.Model} — " +
                           $"{(outcome.Ok ? "ОК" : "НЕУДАЧА")} ({outcome.Duration.TotalSeconds:0.#} с) — {outcome.Reason}";
                if (outcome.Ok)
                {
                    okCount++;
                    _log?.LogSuccess($"[check] {line}");
                }
                else
                {
                    failCount++;
                    _log?.LogError($"[check] {line}");
                }
                AppendSessionLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {line}");
            }

            var path = SessionChecksPath;
            _log?.Log($"[check] готово: ок {okCount}, неудач {failCount}" +
                      (path is null ? "" : $"; сессионный лог: {path}"));
            AppendSessionLog($"=== итог: ок {okCount}, неудач {failCount}");
        }
        finally
        {
            IsChecking = false;
        }
    }

    // Ступень lm-studio при «Проверить всех» — не голый GET models, а
    // честный подъём: lms daemon up -> ps -> загрузить модель, если не
    // стоит (чекбокс «авто-LMS»), -> проверка ОТКЛИКА chat-ping'ом
    // (1 локальный токен, таймаут 30 с). Остальные звенья — ChainProbe
    // (ноль сожжённых токенов).
    private async Task<Services.Ai.ChainProbeOutcome> ProbeRowAsync(ProviderStatusRow row)
    {
        if (row.LinkId != "lm-studio" || _lmStudio is null)
        {
            var key = FreshApiKeyFor(row);
            // Открытие модели: 5 попыток с
            // паузой 2 с, каждая попытка строкой в протокол и сессионный лог
            // («попытка 1/5 …»). Любая удачная = звено ОК; все 5 мимо — честный
            // вердикт «не прошло за 5 попыток» в итоговой строке [check].
            if (row.LinkId == "openrouter")
            {
                return await Services.Ai.ChainProbe.RunAsync(
                    row.BaseUrl, row.RequiresKey, key,
                    attempts: 5, pauseBetween: TimeSpan.FromSeconds(2),
                    onAttempt: msg =>
                    {
                        _log?.Log($"[check] openrouter: {msg}");
                        AppendSessionLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | openrouter: {msg}");
                    }).ConfigureAwait(false);
            }
            return await Services.Ai.ChainProbe
                .RunAsync(row.BaseUrl, row.RequiresKey, key)
                .ConfigureAwait(false);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var model = await _lmStudio.EnsureReadyAsync(row.Model).ConfigureAwait(false);
        sw.Stop();
        return model is null
            ? new Services.Ai.ChainProbeOutcome(false,
                "lms не смог поднять модель (причины выше в протоколе)", sw.Elapsed)
            : new Services.Ai.ChainProbeOutcome(true,
                $"lms: модель '{model}' загружена, отклик есть ({sw.Elapsed.TotalSeconds:0.#} с)", sw.Elapsed);
    }

    // Свежий ключ звена для проверки: встроенные облачные — лестница
    // env -> local.json (ConfigService); llm7 — ключ опционален (без него
    // анонимно); кастомные — ключ из Chain.Custom. null — пробы без ключа.
    private string? FreshApiKeyFor(ProviderStatusRow row)
    {
        if (row.LinkId == "llm7")
        {
            return _config?.ResolveAiKey("Llm7", "AIJOB_LLM7_API_KEY").Value;
        }

        var (providerId, envVar) = row.LinkId switch
        {
            "modelscope" => ("ModelScope", "AIJOB_MODELSCOPE_API_KEY"),
            "siliconflow" => ("SiliconFlow", "AIJOB_SILICONFLOW_API_KEY"),
            "openrouter" => ("OpenRouter", "AIJOB_OPENROUTER_API_KEY"),
            "orcarouter" => ("OrcaRouter", "AIJOB_ORCAROUTER_API_KEY"),
            _ => (null, ""),
        };
        if (providerId is not null)
        {
            return _config?.ResolveAiKey(providerId, envVar).Value;
        }

        return string.IsNullOrWhiteSpace(row.ApiKey) ? null : row.ApiKey;
    }

    // Сессионный лог проверок — в logs\ рабочего каталога (все
    // протоколы в одном месте — подкаталог logs\ рядом с local.json).
    // Путь объявляется в протоколе по итогам прогона.
    private string? SessionChecksPath =>
        _config is null || _config.FilePath == "?"
            ? null
            : Path.Combine(Path.GetDirectoryName(_config.FilePath)!, "logs", "session-checks.log");

    private void AppendSessionLog(string line)
    {
        var path = SessionChecksPath;
        if (path is null)
        {
            if (!_sessionLogWarned)
            {
                _sessionLogWarned = true;
                _log?.LogWarn("skip: сессионный лог проверок не пишется — причина: local.json " +
                              "не загружен (сбой при старте); решение: перезапустить приложение");
            }
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(path, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[check] сессионный лог не записан: {ex.Message}; " +
                           $"решение: проверить права на {path}");
        }
    }

    // Карточки кастомных звеньев (local.json Chain.Custom). Новое звено —
    // чекбокс включён по умолчанию; лампа серая до проверки.
    internal void AppendCustomLinkRows()
    {
        foreach (var link in _config?.PeekCustomLinks() ?? Enumerable.Empty<Services.ChainLinkData>())
        {
            if (Providers.Any(p => p.LinkId == link.Id))
            {
                continue; // уже на доске
            }

            var row = new ProviderStatusRow
            {
                LinkId = link.Id,
                Name = link.Name,
                Model = string.IsNullOrWhiteSpace(link.Model) ? "—" : link.Model,
                BaseUrl = link.BaseUrl,
                RequiresKey = link.RequiresKey,
                ApiKey = link.ApiKey,
                KeyPresent = !string.IsNullOrWhiteSpace(link.ApiKey),
                IsCustom = true,
                InChain = true,
                KeyNote = string.IsNullOrWhiteSpace(link.BaseUrl)
                    ? "адрес не задан — впишите в настройках"
                    : link.BaseUrl,
            };
            row.Toggled += (_, _) => PersistChainDisabled();
            Providers.Add(row);
        }
    }

    // Применение настроек: пересобрать кастомные карточки (встроенные не
    // трогаем — их ключи применяются при перезапуске, см. WARN UpdateAiKey).
    internal void ReloadCustomLinks()
    {
        if (_config is null)
        {
            return;
        }

        foreach (var row in Providers.Where(p => p.IsCustom).ToList())
        {
            Providers.Remove(row);
        }
        AppendCustomLinkRows();
    }

    // Чекбоксы цепи персистятся в SettingsKV (как выбор сайтов): отключённые
    // звенья не проверяются и не участвуют в цепи после перезапуска.
    private void PersistChainDisabled()
    {
        if (_store is null)
        {
            return;
        }

        var disabledIds = Providers.Where(p => !p.InChain).Select(p => p.LinkId).ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                _store.SetSetting(ChainDisabledSettingKey, JsonSerializer.Serialize(disabledIds));
            }
            catch (Exception ex)
            {
                _log?.LogError($"[chain] чекбоксы цепи не сохранены: {ex.Message}; " +
                               "решение: состояние восстановится при следующем переключении");
            }
        });
    }

    // Синхронное применение истории (тесты; UI — через RunOnUi/Insert в Send).
    internal void ApplyHistory(IReadOnlyList<Services.ApplicationRecord> records)
    {
        History.Clear();
        foreach (var record in records)
        {
            History.Add(new ApplicationRow { Record = record });
        }
    }

    // переключение «авто-LMS» — сразу в CLI (модалку показывать или
    // грузить молча) и в базу (SettingsKV), чтобы пережило перезапуск.
    partial void OnLmsAutoLoadChanged(bool value)
    {
        if (_lmStudio is not null)
        {
            _lmStudio.AutoLoad = value;
        }
        _ = Task.Run(() =>
        {
            try
            {
                _store?.SetSetting(LmsAutoLoadSettingKey, value ? "1" : "0");
            }
            catch (Exception ex)
            {
                _log?.LogWarn($"skip: {LmsAutoLoadSettingKey} не сохранён: {ex.Message}; " +
                              "решение: чекбокс «авто-LMS» не переживёт перезапуск");
            }
        });
    }

    // r21: галка «векторизация» и её обработчик вырезаны вместе с векторизацией
    // (решение владельца: векторы ничего не потребляли — «похожие отклики»
    // не существовало; emb-шум в протоколе на каждый отклик исчез).

    // Выход из окна: резюме сохраняем лучшими усилиями (Loss на диске недопустим).
    public async Task ShutdownAsync()
    {
        try
        {
            if (_store is not null)
            {
                await Task.Run(() => _store.SetSetting(ResumeSettingKey, ResumeText)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"резюме не сохранено при выходе: {ex.Message}; " +
                           "решение: повторно нажать «Сохранить резюме» при следующем старте");
        }
    }

    private static void RunOnUi(Action action)
    {
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess())
        {
            action();
        }
        else
        {
            ui.Post(action);
        }
    }

    // await-вариант — поисковый цикл ждёт фактического применения
    // фильтров (счётчики «в окно/скрыто» к моменту строки [search] готовы).
    // TCS вместо InvokeAsync: DispatcherOperation не Task — обёртка не зависит
    // от версии Avalonia.
    private static Task RunOnUiAsync(Action action)
    {
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess())
        {
            action(); // на UI-потоке (тесты) — синхронно
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    // THREAD: вызывается и с фоновых потоков (probe в Task.Run) — мутации
    // LogFeed только через Dispatcher.UIThread (см. комментарий класса)
    private void OnProtocolChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        void Apply()
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems is null) return;
                    foreach (var line in e.NewItems.Cast<string>()) LogFeed.Add(line);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    // Кольцо LogService: RemoveAt(0) — «Защита от утечки памяти»
                    if (e.OldStartingIndex < LogFeed.Count) LogFeed.RemoveAt(e.OldStartingIndex);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    LogFeed.Clear();
                    break;
            }
        }

        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess()) Apply();
        else ui.Post(Apply);
    }
}
