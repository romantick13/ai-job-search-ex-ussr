using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiJobSearchExUssr.ViewModels;

// ============================================================================
// Панель «Моё резюме — профили».
// Одна строка списка = один профиль: загрузил файл -> одноразовый парс ->
// MD-канон в базе -> ключевые слова (два уровня: свои галочки из словаря
// ИЛИ «Прогнать извлечение» и принять правки).

// Словарь — файл keywords.json в конфиг-каталоге (рядом с local.json);
// юзер правит его один раз под себя и забывает (см. KeywordDictionary).

// Две старые строки «Моё резюме (база)» уходят: их место занимает активный
// профиль — адаптация берёт его CanonMd (MainViewModel.GetResumeText).

// FLOW: MainWindow (панель справа) -> команды этой VM -> ResumeStore/Parser/
// Dictionary/Extractor/MdToDocx -> протокол через ILogService.
// THREAD: сеть/база/файлы — Task.Run; коллекции — UI-поток (RunOnUi).
// ============================================================================

public partial class ResumeProfilesViewModel : ObservableObject
{
    private readonly Services.Resume.IResumeStore? _store;
    private readonly Services.Resume.IResumeParser? _parser;
    private readonly Services.Resume.IMdToDocx? _mdToDocx;
    private readonly Services.Resume.IKeywordDictionary? _dictionary;
    private readonly Services.ILogService? _log;

    [ObservableProperty]
    private ResumeProfileRow? _selectedProfile;

    [ObservableProperty]
    private ResumeProfileRow? _activeProfile;

    [ObservableProperty]
    private string _dictionaryFilter = "";

    [ObservableProperty]
    private bool _onlySelected;

    [ObservableProperty]
    private bool _hasProfiles;

    [ObservableProperty]
    private string _status = "загрузи PDF/DOCX/TXT/MD — парсим сами, вручную набирать не надо";

    [ObservableProperty]
    private bool _isBusy;

    // Слова из правок экстракции, ожидающие решения (плашки «+ в словарь»).
    [ObservableProperty]
    private string _extractStatus = "";

    // Есть ли правки после прогона (видимость блока «Принять отмеченные»).
    public bool HasSuggestions => Suggestions.Count > 0;

    // Шапка панели: активный профиль + счётчик ключевых (для плотной панели).
    [ObservableProperty]
    private string _activeHead = "активного профиля нет — загрузи резюме или назначь активное";

    private void UpdateActiveHead()
        => ActiveHead = ActiveProfile is null
            ? "активного профиля нет — загрузи резюме или назначь активное"
            : $"активное: «{ActiveProfile.Name}» · ключевых {ActiveProfile.Keywords.Count}";

    partial void OnActiveProfileChanged(ResumeProfileRow? value) => UpdateActiveHead();

    public ObservableCollection<ResumeProfileRow> Profiles { get; } = new();
    public ObservableCollection<KeywordItemRow> DictionaryItems { get; } = new();
    public ObservableCollection<ExtractedKeywordRow> Suggestions { get; } = new();

    public ResumeProfilesViewModel()
    {
    // Дизайнер/тесты: без зависимостей.
    }

    public ResumeProfilesViewModel(
        Services.Resume.IResumeStore store,
        Services.Resume.IResumeParser parser,
        Services.Resume.IMdToDocx mdToDocx,
        Services.Resume.IKeywordDictionary dictionary,
        Services.ILogService log)
    {
        _store = store;
        _parser = parser;
        _mdToDocx = mdToDocx;
        _dictionary = dictionary;
        _log = log;
    }

    public string DictionaryFilePath => _dictionary?.FilePath ?? "?";


    // Стартовая загрузка: словарь + профили. Зовёт MainWindow.Initialize.

    public async Task InitializeAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                _dictionary?.Load();
            }).ConfigureAwait(false);
            RunOnUi(ReloadDictionaryRows);
            await ReloadProfilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // старт не роняем; панель остаётся пустой, причина — в протоколе.
            _log?.LogError($"[резюме] стартовая загрузка не удалась: {ex.Message}; " +
                           "решение: перезапустить приложение — база и словарь не тронуты");
        }
    }


    // Профили

    public async Task ReloadProfilesAsync()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var profiles = await Task.Run(() => _store.ListProfiles()).ConfigureAwait(false);
            RunOnUi(() =>
            {
                Profiles.Clear();
                ResumeProfileRow? active = null;
                foreach (var p in profiles)
                {
                    var keywords = _store.GetKeywords(p.Id)
                        .Where(k => k.Accepted)
                        .Select(k => k.Value)
                        .ToList();
                    var row = new ResumeProfileRow
                    {
                        Id = p.Id,
                        Name = p.Name,
                        CanonMd = p.CanonMd,
                        SourcePath = p.SourcePath,
                        DocxPath = p.DocxPath,
                        UpdatedAt = p.UpdatedAt,
                        Keywords = keywords,
                        IsActive = p.IsActive,
                    };
                    if (p.IsActive)
                    {
                        active = row;
                    }

                    Profiles.Add(row);
                }

                ActiveProfile = active;
                HasProfiles = Profiles.Count > 0;
                SyncDictionaryChecks();
            });
        }
        catch (Exception ex)
        {
            _log?.LogError($"[резюме] список профилей не прочитан: {ex.Message}; " +
                           "решение: проверить базу resume/resume.db (файл на месте?)");
        }
    }

    // Загрузка файла: диалог выбирает MainWindow (FileDialogService-шов),
    // сюда приходит путь. Весь конвейер: парс -> база -> файлы -> экстракт.
    [RelayCommand]
    private async Task AddResumeAsync(string? filePath)
    {
        if (_store is null || _parser is null || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (!File.Exists(filePath))
        {
            _log?.LogWarn($"skip: файл не найден — причина: {filePath}; " +
                          "решение: выбрать файл заново");
            return;
        }

        IsBusy = true;
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".doc")
            {
                // Решение «сохрани как DOCX... мы не няньки».
                _log?.LogError("[резюме] старый .doc (97-2003) не парсим — пересохрани как DOCX " +
                               "(Word/LibreOffice -> «Сохранить как» -> DOCX) и загрузи снова; " +
                               "решение: тот же файл в DOCX");
                Status = "старый .doc не парсим — пересохрани как DOCX";
                return;
            }

            var result = await Task.Run(() => _parser.Parse(filePath)).ConfigureAwait(false);
            if (result.IsEmptyText)
            {
                _log?.LogError($"[резюме] в {Path.GetFileName(filePath)} нет текстового слоя " +
                               "(похоже, скан) — распознать не смог; " +
                               "решение: загрузить версию с текстом (PDF из Word, а не скан)");
                Status = "скан без текста — нужен файл с текстовым слоем";
                return;
            }

            var name = Path.GetFileNameWithoutExtension(filePath);
            var added = await Task.Run(() => _store.AddProfile(
                name, filePath, result.SourceHash, result.CanonicalMd, docxPath: null))
                .ConfigureAwait(false);

            // Копия исходника рядом с базой (профиль самодостаточен).
            await Task.Run(() =>
            {
                var dir = Services.Resume.ResumeStore.FilesDir(_store.DbPath, added.Id);
                Directory.CreateDirectory(dir);
                var copy = Path.Combine(dir, Path.GetFileName(filePath));
                File.Copy(filePath, copy, overwrite: true);
            }).ConfigureAwait(false);

            // Первичный набор ключевых: словарные вхождения -> accepted сразу.
            await SeedKeywordsAsync(added.Id, result.CanonicalMd).ConfigureAwait(false);

            // Первый профиль становится активным сразу — из коробки работает
            // поиск с ключевыми; смена активного — кнопкой на карточке.
            // Решение по БД, не по UI-коллекции: RunOnUi(Post) догоняет позже.
            if (_store.ListProfiles().All(p => !p.IsActive))
            {
                await Task.Run(() => _store.SetActive(added.Id)).ConfigureAwait(false);
            }

            await ReloadProfilesAsync().ConfigureAwait(false);

            // Выбор строки — ТОЛЬКО на UI-потоке: Profiles правит RunOnUi-блок
            // ReloadProfilesAsync (Dispatcher.Post), а фоновый FirstOrDefault
            // по той же ObservableCollection гонит с ним классическую гонку
            // «Collection was modified; enumeration operation may not execute»
            // (воспроизведено у владельца r22a: профиль №2, файл DOCX ни при чём).
            RunOnUi(() =>
            {
                var row = Profiles.FirstOrDefault(p => p.Id == added.Id);
                if (row is not null)
                {
                    SelectedProfile = row;
                }
            });

            Status = $"«{name}» распарсен: MD-канон готов";
            _log?.LogSuccess($"[резюме] загрузил «{name}»: {result.CanonicalMd.Length} зн., " +
                             "MD-канон в базе; ключевые — вкладка словаря");
        }
        catch (NotSupportedException nse)
        {
            _log?.LogError($"[резюме] {nse.Message}");
            Status = nse.Message;
        }
        catch (Exception ex)
        {
            // Сбой на любом шаге конвейера (парс/база/файлы/UI) не роняет окно.
            // Текст честный: падение ПОСЛЕ AddProfile — это уже не парс (у
            // владельца r22a «парс упал» при сохранённом профиле — гонка
            // Reload-блока, файл был ни при чём). Первая строка стека — в
            // протокол: без неё дистанционно не отличить битый файл от бага
            // конвейера.
            var place = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "стека нет";
            _log?.LogError($"[резюме] загрузка «{Path.GetFileName(filePath)}» упала — " +
                           $"{ex.GetType().Name}: {ex.Message}; место: {place}; " +
                           "решение: повторить; если падает снова — файл не виноват, пришли протокол");
            Status = "загрузка не удалась — детали в протоколе";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Первичный набор: словарные вхождения становятся «принятыми» сразу —
    // из коробки поиск уже работает; правки юзер докрутит галочками.
    private async Task SeedKeywordsAsync(int profileId, string canonMd)
    {
        if (_dictionary is null || _store is null)
        {
            return;
        }

        await Task.Run(() =>
        {
            var found = Services.Resume.KeywordExtractor.Extract(canonMd, _dictionary.Flat());
            var items = found
                .Where(k => k.InDictionary)
                .Select(k => new Services.Resume.ResumeKeywordRecord
                {
                    ProfileId = profileId,
                    Kind = "extracted",
                    Group = k.Category,
                    Value = k.Value,
                    Norm = Services.Resume.GeoMatcher.Normalize(k.Value),
                    Accepted = true,
                })
                .ToList();
            if (items.Count > 0)
            {
                _store.ReplaceKeywords(profileId, items);
            }
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SetActiveAsync(ResumeProfileRow? row)
    {
        if (_store is null || row is null)
        {
            return;
        }

        await Task.Run(() => _store.SetActive(row.Id)).ConfigureAwait(false);
        await ReloadProfilesAsync().ConfigureAwait(false);
        _log?.Log($"[резюме] активный профиль: «{row.Name}» — адаптация и поиск по его ключевым");
    }

    // Красный крестик у строки профиля: удаляет профиль из базы вместе с
    // файлами и ключевыми (каскад), без диалога — по решению владельца
    // «просто красный крестик». Если удалили активный — говорим прямо:
    // активного больше нет, назначь другой или загрузи заново.
    [RelayCommand]
    private async Task DeleteProfileAsync(ResumeProfileRow? row)
    {
        if (_store is null || row is null)
        {
            return;
        }

        var wasActive = row.IsActive;
        try
        {
            await Task.Run(() => _store.DeleteProfile(row.Id)).ConfigureAwait(false);
            _log?.Log(wasActive
                ? $"[резюме] профиль «{row.Name}» удалён — был активным, активного больше нет " +
                  "(назначь другой или загрузи заново)"
                : $"[резюме] профиль «{row.Name}» удалён (ключевые и файлы ушли с ним)");
            await ReloadProfilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[резюме] профиль «{row.Name}» не удалён — {ex.GetType().Name}: " +
                           $"{ex.Message}; решение: повторить; если файл занят (открыт в Word) — " +
                           "закрой и повтори");
            Status = "удалить не удалось — детали в протоколе";
        }
    }

    [RelayCommand]
    private async Task ResaveDocxAsync(ResumeProfileRow? row)
    {
        if (_store is null || _mdToDocx is null || row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var outPath = await Task.Run(() =>
            {
                var dir = Services.Resume.ResumeStore.FilesDir(_store.DbPath, row.Id);
                var path = Path.Combine(dir, "resume.docx");
                _mdToDocx.Convert(row.CanonMd, path);
                _store.UpdateDocxPath(row.Id, path);
                return path;
            }).ConfigureAwait(false);

            await ReloadProfilesAsync().ConfigureAwait(false);
            _log?.LogSuccess($"[резюме] DOCX-копия пересохранена: {outPath} — " +
                             "приложение само, юзеру конвертировать не надо");
            Status = "DOCX-копия готова";
        }
        catch (Exception ex)
        {
            _log?.LogError($"[резюме] DOCX-копия не сохранена: {ex.Message}; " +
                           "решение: проверить диск/права и повторить");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSource(ResumeProfileRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            if (File.Exists(row.SourcePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.SourcePath)
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(row.SourcePath) ?? ".";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[резюме] исходник не открылся: {ex.Message}; " +
                           "решение: найти файл вручную по пути в протоколе");
        }
    }


    // Словарь: скролл-список с чекбоксами.

    public void ReloadDictionaryRows()
    {
        if (_dictionary is null)
        {
            return;
        }

        var filter = Services.Resume.GeoMatcher.Normalize(DictionaryFilter);
        var selected = new HashSet<string>(
            ActiveProfile?.Keywords.Select(Services.Resume.GeoMatcher.Normalize)
            ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        DictionaryItems.Clear();
        foreach (var (value, category) in _dictionary.Flat())
        {
            var norm = Services.Resume.GeoMatcher.Normalize(value);
            if (filter.Length > 0 && !norm.Contains(filter, StringComparison.Ordinal))
            {
                continue;
            }

            if (OnlySelected && !selected.Contains(norm))
            {
                continue;
            }

            DictionaryItems.Add(new KeywordItemRow
            {
                Value = value,
                Category = category,
                IsChecked = selected.Contains(norm),
            });
        }
    }

    partial void OnDictionaryFilterChanged(string value) => ReloadDictionaryRows();
    partial void OnOnlySelectedChanged(bool value) => ReloadDictionaryRows();

    // Галочка в словаре = мгновенная синхронизация набора активного профиля
    // с состоянием чекбокса (Avalonia: Command после смены IsChecked — читаем
    // актуальное значение, а не флипаем).
    [RelayCommand]
    private async Task ToggleKeywordAsync(KeywordItemRow? item)
    {
        if (_store is null || item is null)
        {
            return;
        }

        if (ActiveProfile is null)
        {
            item.IsChecked = !item.IsChecked; // вернуть как было
            Status = "нет активного профиля — сначала загрузи резюме или назначь активное";
            return;
        }

        var wantChecked = item.IsChecked;
        var profileId = ActiveProfile.Id;
        await Task.Run(() =>
        {
            if (wantChecked)
            {
                _store!.AddKeyword(profileId, new Services.Resume.ResumeKeywordRecord
                {
                    ProfileId = profileId,
                    Kind = "my",
                    Group = item.Category,
                    Value = item.Value,
                    Norm = Services.Resume.GeoMatcher.Normalize(item.Value),
                    Accepted = true,
                });
            }
            else
            {
                var current = _store!.GetKeywords(profileId)
                    .Where(k => Services.Resume.GeoMatcher.Normalize(k.Value)
                                != Services.Resume.GeoMatcher.Normalize(item.Value));
                _store.ReplaceKeywords(profileId, current);
            }
        }).ConfigureAwait(false);
        await ReloadProfilesAsync().ConfigureAwait(false);
    }

    // «＋ Добавить в словарь»: слово -> файл keywords.json (атомарно, навсегда).
    [RelayCommand]
    private async Task AddToDictionaryAsync(string? rawValue)
    {
        if (_dictionary is null || string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var value = rawValue.Trim();
        var category = "Словарь пользователя";
        var added = await Task.Run(() => _dictionary.AddItem(category, value)).ConfigureAwait(false);
        if (added)
        {
            _log?.LogSuccess($"[словарь] «{value}» добавлен в категорию «{category}» -> {DictionaryFilePath}");
        }
        else
        {
            _log?.Log($"[словарь] «{value}» уже в словаре — дубль не пишу");
        }

        // DictionaryItems правит только UI-поток (RunOnUi) — фон тут после
        // ConfigureAwait(false).
        RunOnUi(ReloadDictionaryRows);
    }

    [RelayCommand]
    private void OpenDictionaryFile()
    {
        if (_dictionary is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_dictionary.FilePath))
            {
                _dictionary.Load(); // создаёт дефолт
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_dictionary.FilePath)
            {
                UseShellExecute = true,
            });
            _log?.Log($"[словарь] открыл файл: {_dictionary.FilePath} — правь под себя и жми «⟳ Перечитать»");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[словарь] файл не открылся: {ex.Message}; " +
                           $"решение: открыть в редакторе вручную {_dictionary.FilePath}");
        }
    }

    [RelayCommand]
    private async Task ReloadDictionaryAsync()
    {
        if (_dictionary is null)
        {
            return;
        }

        await Task.Run(() => _dictionary.Load()).ConfigureAwait(false);
        RunOnUi(ReloadDictionaryRows);
        Status = $"словарь перечитан: {_dictionary.TotalItems} позиций";
    }


    // «Прогнать извлечение»: правки-галочки (— второй
    // уровень ключевых слов: либо свой набор, либо прогнать и посмотреть).

    [RelayCommand]
    private async Task RunExtractionAsync()
    {
        if (_store is null || _dictionary is null || ActiveProfile is null)
        {
            Status = "нет активного профиля — прогонять некуда";
            return;
        }

        var found = await Task.Run(() => Services.Resume.KeywordExtractor.Extract(
            ActiveProfile.CanonMd, _dictionary.Flat())).ConfigureAwait(false);

        // Симптом: по кнопке "прогнать извлечение" — каша на
        // картинке: в ленте правок смешивались новые слова и «уже в наборе ✓»
        // — читать невозможно. Теперь в списке ТОЛЬКО новые (галками отмечены),
        // «уже в наборе» — счётчиком в статусе. Нормы принятых считаем один раз
        // до UI-блока: и для фильтра списка, и для строки протокола ниже.
        var acceptedNorms = new HashSet<string>(
            ActiveProfile.Keywords.Select(Services.Resume.GeoMatcher.Normalize),
            StringComparer.Ordinal);

        RunOnUi(() =>
        {
            Suggestions.Clear();
            foreach (var f in found.Where(f => !acceptedNorms.Contains(
                         Services.Resume.GeoMatcher.Normalize(f.Value))))
            {
                Suggestions.Add(new ExtractedKeywordRow
                {
                    Value = f.Value,
                    Category = f.Category,
                    InDictionary = f.InDictionary,
                });
            }

            OnPropertyChanged(nameof(HasSuggestions));
            var already = found.Count - Suggestions.Count;
            ExtractStatus = Suggestions.Count == 0
                ? "нового нет — всё уже в наборе"
                : already > 0
                    ? $"новых {Suggestions.Count} (ещё {already} уже в наборе) — отметь и прими"
                    : $"новых {Suggestions.Count} — отметь и прими";
        });

        // Счётчики — из found на фоне: RunOnUi с фона — Post, коллекция на
        // момент лога ещё пуста (учтено в раунде ).
        _log?.Log($"[резюме] прогнал извлечение по «{ActiveProfile.Name}»: " +
                  $"в словаре {found.Count(f => f.InDictionary)}, кандидатов всего {found.Count}, " +
                  $"новых {found.Count(f => !acceptedNorms.Contains(Services.Resume.GeoMatcher.Normalize(f.Value)))}");
    }

    // Принять отмеченные правки: чекбоксы Suggestions -> набор профиля;
    // слова вне словаря дописываем и в keywords.json (пометка «+ в словарь»).
    [RelayCommand]
    private async Task AcceptSuggestionsAsync()
    {
        if (_store is null || ActiveProfile is null)
        {
            return;
        }

        var picked = Suggestions.Where(s => s.IsChecked && !s.AlreadyAccepted).ToList();
        if (picked.Count == 0)
        {
            Status = "отмеченных правок нет";
            return;
        }

        await Task.Run(() =>
        {
            var current = _store!.GetKeywords(ActiveProfile.Id).ToList();
            foreach (var p in picked)
            {
                current.Add(new Services.Resume.ResumeKeywordRecord
                {
                    ProfileId = ActiveProfile.Id,
                    Kind = "extracted",
                    Group = p.Category,
                    Value = p.Value,
                    Norm = Services.Resume.GeoMatcher.Normalize(p.Value),
                    Accepted = true,
                });
            }

            _store.ReplaceKeywords(ActiveProfile.Id, current);

            if (_dictionary is not null)
            {
                foreach (var p in picked.Where(p => p.AddToDictionary))
                {
                    _dictionary.AddItem("Словарь пользователя", p.Value);
                }
            }
        }).ConfigureAwait(false);

        if (_dictionary is not null)
        {
            await Task.Run(() => _dictionary.Load()).ConfigureAwait(false);
        }

        RunOnUi(() => Suggestions.Clear());
        OnPropertyChanged(nameof(HasSuggestions));
        await ReloadProfilesAsync().ConfigureAwait(false);
        // Словарь — тоже UI-коллекция: перебор/правка только на UI-потоке
        // (та же гонка, что была в AddResumeAsync).
        RunOnUi(ReloadDictionaryRows);
        _log?.LogSuccess($"[резюме] принял правки: {picked.Count} слов в наборе «{ActiveProfile.Name}»");
        Status = $"правки приняты: +{picked.Count}";
    }

    // Сохранение правленного MD-канона (модалка «MD-канон»).
    [RelayCommand]
    private async Task SaveCanonAsync(string? canonMd)
    {
        if (_store is null || ActiveProfile is null || canonMd is null)
        {
            return;
        }

        await Task.Run(() => _store.UpdateCanon(ActiveProfile.Id, canonMd)).ConfigureAwait(false);
        await ReloadProfilesAsync().ConfigureAwait(false);
        _log?.LogSuccess($"[резюме] канон «{ActiveProfile.Name}» сохранён ({canonMd.Length} зн.)");
    }

    // Подставить ключевые активного профиля в строку поиска (кнопка «Из резюме»).
    public string ActiveKeywordsQuery()
        => ActiveProfile is null ? "" : string.Join(", ", ActiveProfile.Keywords);

    private void SyncDictionaryChecks()
    {
        var selected = new HashSet<string>(
            ActiveProfile?.Keywords.Select(Services.Resume.GeoMatcher.Normalize)
            ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        foreach (var row in DictionaryItems)
        {
            row.IsChecked = selected.Contains(Services.Resume.GeoMatcher.Normalize(row.Value));
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
}

// Строка правок после «Прогнать извлечение».
public sealed class ExtractedKeywordRow : ObservableObject
{
    private bool _isChecked = true;
    private bool _addToDictionary = true;

    public required string Value { get; init; }
    public required string Category { get; init; }
    public required bool InDictionary { get; init; }
    public bool AlreadyAccepted { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public bool AddToDictionary
    {
        get => _addToDictionary;
        set => SetProperty(ref _addToDictionary, value);
    }

    public string StatusDisplay => AlreadyAccepted ? "уже в наборе ✓" : InDictionary ? "из словаря" : "новое";
}
