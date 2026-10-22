using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiJobSearchBy.ViewModels;

// FLOW: MainWindow -> MainViewModel (DataContext) -> поиск по порталам,
// адаптация резюме, отправка откликов, история (WP-U1, решение владельца
// 2026-10-22). Дочерние проекции: SiteToggleRow/VacancyRow/ApplicationRow.
// LIFECYCLE: синглтон DI (CompositionRoot); InitializeAsync зовёт MainWindow
// (fire-and-forget с ловлей — старт UI не ждёт базу).
// STATE: LogFeed — живая лента протокола; Vacancies/History — списки окон;
// ResumeText/AdaptedResumeText — редактируемые тексты резюме.
// MVVM CommunityToolkit (RFC-001 §2): [ObservableProperty]/[RelayCommand].
// THREAD: свойства — только с UI-потока; сеть/база — Task.Run/await с
// ConfigureAwait(false) внутри сервисов; дельты в коллекции — через RunOnUi
// (диспетчер в тестах не крутится — тесты зовут синхронные Apply*-методы).
// ИТОГ WP-U1: автоотправка на порталах невозможна честно (личная сессия,
// капча, ToS) — кнопка «Отправить» сохраняет PDF (имя: работодатель_дата)
// в папку откликов, пишет отклик в базу и открывает вакансию в браузере.
public partial class MainViewModel : ObservableObject
{
    private const string ResumeSettingKey = "resume.text";
    private const string SitesSettingKey = "sites.checked";
    private const string OutboxSettingKey = "outbox.path";

    private readonly Portals.PortalRegistry? _registry;
    private readonly Services.IApplicationStore? _store;
    private readonly Services.ResumeAdaptationService? _adaptation;
    private readonly Services.IPdfExporter? _pdf;
    private readonly Services.Ai.IEmbeddingService? _embeddings;
    private readonly Services.PdfIndexerService? _pdfIndexer;
    private readonly Services.ILogService? _log;
    private CancellationTokenSource? _searchCts;
    private bool _restoringSites;

    [ObservableProperty]
    private string _greeting =
        "WP-U1: поиск по порталам, адаптация резюме под вакансию, отклики в базу. " +
        "Автоотправка честно невозможна (личная сессия/капча на сайтах) — «Отправить» " +
        "сохраняет PDF в папку откликов и открывает вакансию в браузере.";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _searchStatus = "задай запрос и нажми «Искать»";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isAdapting;

    [ObservableProperty]
    private bool _isBusy;

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

    // История откликов (работодатель + дата-время)
    public ObservableCollection<ApplicationRow> History { get; } = new();

    // Дизайнер и тесты: пустые коллекции, без зависимостей.
    // IsDesignerMode: параметрless-ctor используется только дизайнером XAML —
    // окно не запускает InitializeAsync (зависимостей-то нет).
    public MainViewModel()
    {
        IsDesignerMode = true;
    }

    // true — модель построена параметрless-ctor (дизайнер XAML).
    public bool IsDesignerMode { get; }

    // DI-ctor (CompositionRoot): протокол, цепь ИИ, реестр, база, сервисы WP-U1.
    // Роутинг не инжектится напрямую: стадия адаптации живёт внутри
    // ResumeAdaptationService (он и владеет TaskRoutingPolicy).
    public MainViewModel(
        Services.ILogService? log,
        IEnumerable<Services.Ai.IAiChatProvider>? providers,
        Portals.PortalRegistry? registry = null,
        Services.IApplicationStore? store = null,
        Services.ResumeAdaptationService? adaptation = null,
        Services.IPdfExporter? pdf = null,
        Services.Ai.IEmbeddingService? embeddings = null,
        Services.PdfIndexerService? pdfIndexer = null)
    {
        _log = log;
        _registry = registry;
        _store = store;
        _adaptation = adaptation;
        _pdf = pdf;
        _embeddings = embeddings;
        _pdfIndexer = pdfIndexer;

        if (log is not null)
        {
            // История — синхронно в конструкторе (UI-поток при старте окна)
            foreach (var line in log.Logs)
                LogFeed.Add(line);

            // Дальше — живая лента с маршалингом (фон-потоки -> UI)
            log.Logs.CollectionChanged += OnProtocolChanged;
        }

        foreach (var provider in providers ?? Enumerable.Empty<Services.Ai.IAiChatProvider>())
            Providers.Add(ProviderStatusRow.From(provider));
    }

    // ------------------------------------------------------------------
    // Стартовая загрузка: сайты, резюме, история, фоновая переиндексация PDF.
    // Вызывается MainWindow один раз; тесты — напрямую (без диспетчера).
    // ------------------------------------------------------------------
    public async Task InitializeAsync()
    {
        LoadSites();
        await RestorePersistedStateAsync().ConfigureAwait(false);
        ApplyHistory(await LoadHistoryAsync().ConfigureAwait(false));

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
                    // НИ-1: фоновая переиндексация не роняет приложение.
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
            var row = new SiteToggleRow
            {
                PortalId = portal.Descriptor.Id,
                DisplayName = portal.Descriptor.DisplayName,
                DefaultEnabled = portal.Descriptor.DefaultEnabled,
                IsChecked = portal.Descriptor.DefaultEnabled,
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
            var savedSites = await Task.Run(() => _store.GetSetting(SitesSettingKey))
                .ConfigureAwait(false);
            if (savedSites is not null)
            {
                var checkedIds = JsonSerializer.Deserialize<string[]>(savedSites)
                                 ?? Array.Empty<string>();
                foreach (var site in Sites)
                {
                    site.IsChecked = checkedIds.Contains(site.PortalId);
                }
            }

            var resume = await Task.Run(() => _store.GetSetting(ResumeSettingKey))
                .ConfigureAwait(false);
            if (resume is not null)
            {
                ResumeText = resume;
            }
        }
        catch (Exception ex)
        {
            // НИ-1: восстановление настроек не роняет старт.
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

        var checkedIds = Sites.Where(s => s.IsChecked).Select(s => s.PortalId).ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                _store.SetSetting(SitesSettingKey, JsonSerializer.Serialize(checkedIds));
            }
            catch (Exception ex)
            {
                _log?.LogError($"[db] выбор сайтов не сохранён: {ex.Message}; " +
                               "решение: выбор восстановится при следующем переключении");
            }
        });
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

    // ------------------------------------------------------------------
    // Поиск: последовательный обход отмеченных порталов (architecture §6);
    // отказ одного портала ≠ крах прогона (§7) — ловим и продолжаем.
    // Строки добавляются по мере ответов («бегут») и пишутся в Vacancies.
    // ------------------------------------------------------------------
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SearchAsync(CancellationToken ct)
    {
        if (_registry is null || IsSearching)
        {
            return;
        }

        var query = SearchQuery.Trim();
        var checkedIds = Sites.Where(s => s.IsChecked).Select(s => s.PortalId).ToHashSet();
        var chain = _registry.ResolveChain()
            .Where(p => checkedIds.Contains(p.Descriptor.Id))
            .ToList();
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
        RunOnUi(() =>
        {
            Vacancies.Clear();
            HasVacancies = false;
        });
        SearchStatus = $"ищу «{query}» по {chain.Count} сайтам…";
        _log?.Log($"[search] старт: «{query}», сайты: {string.Join(", ", chain.Select(p => p.Descriptor.Id))}");

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
                RunOnUi(() => ApplyVacancies(postings));
                var count = postings.Count;
                var inserted = await Task.Run(() => _store?.UpsertVacancies(postings) ?? 0, token)
                    .ConfigureAwait(false);
                _log?.Log($"[search] {portal.Descriptor.Id}: в окно {count}, в базу новеньких {inserted}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // §7: отказ одного портала не валит прогон — причина уже в
                // протоколе адаптера; фиксируем продолжение прогона.
                _log?.LogWarn($"skip: {portal.Descriptor.Id} пропущен в этом прогоне — " +
                              $"{ex.GetType().Name}; решение: двигаюсь к следующему порталу");
            }
        }

        SearchStatus = token.IsCancellationRequested
            ? "поиск остановлен пользователем"
            : $"готово: {Vacancies.Count} вакансий в окне";
        IsSearching = false;
    }

    // Синхронное применение (тесты зовут напрямую; UI — через RunOnUi).
    internal void ApplyVacancies(IReadOnlyList<Models.JobPosting> postings)
    {
        foreach (var posting in postings)
        {
            Vacancies.Add(new VacancyRow { Posting = posting });
        }
        HasVacancies = Vacancies.Count > 0;
    }

    // ------------------------------------------------------------------
    // Клик по вакансии: адаптация резюме (стадия ResumeAdaptation).
    // ------------------------------------------------------------------
    [RelayCommand]
    private async Task SelectVacancyAsync(VacancyRow? row)
    {
        if (row is null || IsAdapting)
        {
            return;
        }

        SelectedVacancy = row.Posting;
        AdaptedMeta = "";
        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            AdaptedResumeText = "Сначала заполни «Моё резюме» слева — модель адаптирует его под выбранную вакансию.";
            _log?.LogWarn("skip: адаптация не запущена — причина: резюме пустое; " +
                          "решение: заполнить окно «Моё резюме» и нажать «Сохранить резюме»");
            return;
        }

        IsAdapting = true;
        IsBusy = true;
        AdaptedResumeText = "адаптирую под вакансию…";
        try
        {
            var result = await _adaptation!.AdaptAsync(ResumeText, row.Posting).ConfigureAwait(false);
            if (result is null)
            {
                AdaptedResumeText = "цепь ИИ недоступна — причины в протоколе; " +
                                    "перезапусти адаптацию позже или введи ключ провайдера";
            }
            else
            {
                AdaptedResumeText = result.Content;
                AdaptedMeta = $"адаптировано: {result.ProviderName} · {result.Model} · " +
                              $"{result.Usage.TotalTokens} токенов · {result.Duration.TotalSeconds:0.#} с";
            }
        }
        finally
        {
            IsAdapting = false;
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------
    // Отправка: PDF (работодатель_дата) -> база -> браузер (решение владельца).
    // ------------------------------------------------------------------
    [RelayCommand]
    private async Task SendAsync()
    {
        if (SelectedVacancy is null)
        {
            _log?.LogWarn("skip: отправка не выполнена — причина: вакансия не выбрана; " +
                          "решение: кликнуть строку в окне «Вакансии»");
            return;
        }
        if (string.IsNullOrWhiteSpace(AdaptedResumeText) || AdaptedResumeText.StartsWith("адаптирую", StringComparison.Ordinal)
            || AdaptedResumeText.StartsWith("цепь ИИ недоступна", StringComparison.Ordinal)
            || AdaptedResumeText.StartsWith("Сначала заполни", StringComparison.Ordinal))
        {
            _log?.LogWarn("skip: отправка не выполнена — причина: адаптированное резюме пустое; " +
                          "решение: выбрать вакансию и дождаться адаптации");
            return;
        }

        var posting = SelectedVacancy;
        var employer = string.IsNullOrWhiteSpace(posting.Company)
            ? "(работодатель не указан)"
            : posting.Company!;
        IsBusy = true;
        try
        {
            var outDir = ResolveOutboxDir();
            var createdAt = DateTimeOffset.Now;
            var pdfPath = await Task.Run(() =>
                _pdf!.SaveResumePdfAsync(employer, posting.Title, AdaptedResumeText, createdAt, outDir))
                .ConfigureAwait(false);

            var vacancyId = await Task.Run(() => _store!.EnsureVacancy(posting)).ConfigureAwait(false);

            string? vectorJson = null;
            string? vectorModel = null;
            if (_embeddings is { Enabled: true })
            {
                var vector = await _embeddings.EmbedAsync(AdaptedResumeText).ConfigureAwait(false);
                if (vector is not null)
                {
                    vectorJson = Services.Ai.VectorJson.To(vector);
                    vectorModel = _embeddings.Model;
                }
            }

            var record = new Services.ApplicationRecord
            {
                Id = Guid.NewGuid(),
                VacancyId = vacancyId,
                Employer = employer,
                VacancyTitle = posting.Title,
                VacancyUrl = posting.CanonicalUrl,
                Status = "sent",
                CvPath = pdfPath,
                SentAt = createdAt,
                CreatedAt = createdAt,
                ResumeText = AdaptedResumeText,
                VectorJson = vectorJson,
                VectorModel = vectorModel,
            };
            await Task.Run(() => _store!.SaveApplication(record)).ConfigureAwait(false);
            RunOnUi(() => History.Insert(0, new ApplicationRow { Record = record }));

            OpenInBrowser(posting.CanonicalUrl);
            _log?.LogSuccess($"отклик сохранён: {Path.GetFileName(pdfPath)}; " +
                             "решение: PDF уже в папке откликов — на сайте выбери его и отправь");
        }
        catch (Exception ex)
        {
            // НИ-1: полный контекст сбоя — в протокол; частично сделанное
            // (PDF/база) не откатываем — файл отклика ценен сам по себе.
            _log?.LogError($"отправка не удалась — {ex.GetType().Name}: {ex.Message}; " +
                           "решение: проверить протокол выше (PDF/база/сеть), исправить и повторить");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string ResolveOutboxDir()
    {
        var configured = _store?.GetSetting(OutboxSettingKey);
        return string.IsNullOrWhiteSpace(configured)
            ? Services.ApplicationStore.DefaultOutboxDir
            : configured!;
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // НИ-1: браузер не открылся — ссылка остаётся в базе и в протоколе.
            _log?.LogError($"браузер не открылся для {url}: {ex.Message}; " +
                           "решение: открыть ссылку вручную (она в базе и в строке вакансии)");
        }
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
            // НИ-1: кнопка не роняет UI.
            _log?.LogError($"переиндексация PDF упала: {ex.Message}; решение: повторить позже");
        }
    }

    // Окно настроек: MainWindow подписывается и открывает модальное окно.
    public event Action? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    // Синхронное применение истории (тесты; UI — через RunOnUi/Insert в Send).
    internal void ApplyHistory(IReadOnlyList<Services.ApplicationRecord> records)
    {
        History.Clear();
        foreach (var record in records)
        {
            History.Add(new ApplicationRow { Record = record });
        }
    }

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
                    // Кольцо LogService: RemoveAt(0) — «Защита от утечки памяти» (донор)
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
