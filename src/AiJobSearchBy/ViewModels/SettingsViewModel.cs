using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiJobSearchBy.ViewModels;

// строка окна настроек — ключ провайдера + подпись-подсказка.
// кастомное звено (IsCustom) — редактируемые имя/адрес/модель + ключ;
// встроенные провайдеры — только ключ (адрес/модель — в их классах).
// ApiKey показывается как введён (локальное окно, публичные пути —
// только маски; сам файл вне репо).
public partial class SettingsRow : ObservableObject
{
    [ObservableProperty]
    private string _apiKey = "";

    // поля кастомного звена (видны только при IsCustom)
    [ObservableProperty]
    private string _customName = "";

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    private string _customModel = "";

    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public required string Hint { get; init; }
    public bool IsCustom { get; init; }
}

// модальное окно настроек:
// ключи ИИ-цепи, папка откликов. Запись —
// ConfigService.UpdateAiKey (local.json, атомарно);
// применяются после перезапуска. r21: секция эмбеддера вырезана вместе с векторизацией.
public partial class SettingsViewModel : ObservableObject
{
    private readonly Services.ConfigService? _config;
    private readonly Services.IApplicationStore? _store;
    private readonly Services.ILogService? _log;

    public ObservableCollection<SettingsRow> Rows { get; } = new();

    [ObservableProperty]
    private string _outboxPath = "";

    public string ConfigPath { get; }

    // Подписку ставит SettingsWindow: закрыть после сохранения/«Готово».
    public event Action? CloseRequested;

    // Дизайнер: пустая модель без зависимостей.
    public SettingsViewModel()
    {
        ConfigPath = "(дизайнер)";
    }

    public SettingsViewModel(Services.ConfigService config, Services.IApplicationStore store,
        Services.ILogService log)
    {
        _config = config;
        _store = store;
        _log = log;
        ConfigPath = config.FilePath;
        Load();
    }

    private void Load()
    {
        Rows.Clear();
        Rows.Add(new SettingsRow
        {
            ProviderId = "Llm7",
            DisplayName = "LLM7",
            Hint = "free 100K токенов/сутки; без ключа работает анонимно (лимиты ниже)",
        });
        Rows.Add(new SettingsRow
        {
            ProviderId = "ModelScope",
            DisplayName = "ModelScope",
            Hint = "2000 запросов/сутки на аккаунт, ≤200 на модель",
        });
        Rows.Add(new SettingsRow
        {
            ProviderId = "SiliconFlow",
            DisplayName = "SiliconFlow",
            Hint = "free-модели; без баланса ключ не встаёт в цепь (402)",
        });
        // Решение: OpenRouter и OrcaRouter вернулись в
        // дефолтную цепь — главные поставщики «100-лимитного» free-промта.
        Rows.Add(new SettingsRow
        {
            ProviderId = "OpenRouter",
            DisplayName = "OpenRouter",
            Hint = "free 50 запр/сутки; депозит $10 — 1000/сутки; из РБ работает без VPN (проверено 2026-10-20)",
        });
        Rows.Add(new SettingsRow
        {
            ProviderId = "OrcaRouter",
            DisplayName = "OrcaRouter",
            Hint = "free-ступени 50 → 800 запр/сутки (после $20 платежей); ключ sk-orca-",
        });

        // кастомные звенья из local.json (Chain.Custom) — редактируемые
        // строки: имя, адрес /v1, модель, ключ
        foreach (var link in _config?.PeekCustomLinks() ?? Enumerable.Empty<Services.ChainLinkData>())
        {
            Rows.Add(new SettingsRow
            {
                ProviderId = link.Id,
                DisplayName = link.Name,
                CustomName = link.Name,
                BaseUrl = link.BaseUrl,
                CustomModel = link.Model,
                ApiKey = link.ApiKey,
                Hint = "кастомное звено цепи (Chain.Custom в local.json): адрес /v1, модель, ключ",
                IsCustom = true,
            });
        }

        foreach (var row in Rows)
        {
            row.ApiKey = _config?.PeekAiKey(row.ProviderId) ?? "";
        }

        // показываем фактическую папку (унаследованное «Документы»
        // уже считается пустым — резолвер вернёт resume\ рядом с exe).
        OutboxPath = Services.ApplicationStore.ResolveOutbox(_store?.GetSetting("outbox.path"));
    }

    [RelayCommand]
    private void Save()
    {
        foreach (var row in Rows.Where(r => !r.IsCustom))
        {
            _config?.UpdateAiKey(row.ProviderId, row.ApiKey);
        }

        // кастомные звенья — единым узлом Chain.Custom (local.json,
        // атомарно); карточки на главном экране обновятся сразу (ReloadCustomLinks)
        var custom = Rows.Where(r => r.IsCustom).Select(r => new Services.ChainLinkData(
            r.ProviderId,
            string.IsNullOrWhiteSpace(r.CustomName) ? r.DisplayName : r.CustomName.Trim(),
            r.BaseUrl.Trim(),
            r.CustomModel.Trim(),
            r.ApiKey.Trim())).ToList();
        if (custom.Count > 0)
        {
            _config?.UpdateChain(custom);
        }

        // r21: секция эмбеддера вырезана вместе с векторизацией.

        // резолвер один — пусто/унаследованное «Документы» → <exe>\resume
        var path = Services.ApplicationStore.ResolveOutbox(OutboxPath);
        try
        {
            _store?.SetSetting("outbox.path", path);
            _log?.Log($"[config] папка откликов: {path}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[config] папка откликов не сохранена: {ex.Message}; " +
                           "решение: проверьте путь и повторите");
        }
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Done() => CloseRequested?.Invoke();


    // расширение цепи прямо из окна —
    // «Добавить провайдера» (пустое кастомное звено) и «Добавить модель»
    // (звено с подставленными адресом/ключом последней строки — вписать
    // только id модели). Звеньев может стать много — секция ключей
    // листается (скролл), окно остаётся прежнего размера.

    [RelayCommand]
    private void AddProvider()
    {
        Rows.Add(new SettingsRow
        {
            ProviderId = NewLinkId(),
            DisplayName = "новое звено",
            CustomName = "",
            Hint = "кастомный провайдер (OpenAI-совместимый /v1): имя, адрес, модель, ключ; VPN/прокси — на вашей совести",
            IsCustom = true,
        });
    }

    [RelayCommand]
    private void AddModel()
    {
        var last = Rows.LastOrDefault();
        var prefillName = last is null ? "" : last.IsCustom ? last.CustomName : last.DisplayName;
        var prefillBase = last is null
            ? ""
            : last.IsCustom ? last.BaseUrl : BuiltInBaseUrl(last.ProviderId);
        Rows.Add(new SettingsRow
        {
            ProviderId = NewLinkId(),
            DisplayName = string.IsNullOrWhiteSpace(prefillName) ? "новая модель" : prefillName,
            CustomName = prefillName,
            BaseUrl = prefillBase,
            CustomModel = "",
            ApiKey = last?.ApiKey ?? "",
            Hint = "ещё одна модель того же провайдера: адрес и ключ подставлены — впишите id модели",
            IsCustom = true,
        });
    }

    [RelayCommand]
    private void RemoveRow(SettingsRow row) => Rows.Remove(row);

    // Адрес встроенного провайдера — для подстановки в «Добавить модель»
    private static string BuiltInBaseUrl(string providerId) => providerId switch
    {
        "Llm7" => Services.Ai.Llm7Provider.DefaultBaseUrl,
        "ModelScope" => Services.Ai.OpenAiCompatibleProvider.ModelScopeBaseUrl,
        "SiliconFlow" => Services.Ai.OpenAiCompatibleProvider.SiliconFlowBaseUrl,
        "OpenRouter" => Services.Ai.OpenAiCompatibleProvider.OpenRouterBaseUrl,
        "OrcaRouter" => Services.Ai.OpenAiCompatibleProvider.OrcaRouterBaseUrl,
        _ => "",
    };

    private static string NewLinkId() => "custom-" + Guid.NewGuid().ToString("N")[..8];
}
