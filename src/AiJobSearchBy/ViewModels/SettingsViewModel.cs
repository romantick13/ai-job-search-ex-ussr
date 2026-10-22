using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiJobSearchBy.ViewModels;

// WP-U1: строка окна настроек — ключ провайдера + подпись-подсказка.
// ApiKey показывается как введён (локальное окно, RFC-004: публичные пути —
// только маски; сам файл вне репо).
public partial class SettingsRow : ObservableObject
{
    [ObservableProperty]
    private string _apiKey = "";

    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public required string Hint { get; init; }
}

// WP-U1: модальное окно настроек (решение владельца «шестерёнка -> настройки»):
// ключи ИИ-цепи + локальный эмбеддер (WP-U2), папка откликов. Запись —
// ConfigService.UpdateAiKey / UpdateEmbeddings (local.json, атомарно);
// применяются после перезапуска.
public partial class SettingsViewModel : ObservableObject
{
    private readonly Services.ConfigService? _config;
    private readonly Services.IApplicationStore? _store;
    private readonly Services.ILogService? _log;

    public ObservableCollection<SettingsRow> Rows { get; } = new();

    [ObservableProperty]
    private string _outboxPath = "";

    // WP-U2: локальный эмбеддер — не ключ, а адрес рантайма и имя модели
    // (пустая строка = «работает дефолт Ollama»).
    [ObservableProperty]
    private string _embeddingsBaseUrl = "";

    [ObservableProperty]
    private string _embeddingsModel = "";

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

        var embeddings = _config?.PeekEmbeddings() ?? ("", "");
        EmbeddingsBaseUrl = embeddings.Item1;
        EmbeddingsModel = embeddings.Item2;

        foreach (var row in Rows)
        {
            row.ApiKey = _config?.PeekAiKey(row.ProviderId) ?? "";
        }

        OutboxPath = _store?.GetSetting("outbox.path")
                     ?? Services.ApplicationStore.DefaultOutboxDir;
    }

    [RelayCommand]
    private void Save()
    {
        foreach (var row in Rows)
        {
            _config?.UpdateAiKey(row.ProviderId, row.ApiKey);
        }

        // WP-U2: эмбеддер — BaseUrl/Model в Ai.Embeddings (атомарно, как ключи).
        _config?.UpdateEmbeddings(EmbeddingsBaseUrl, EmbeddingsModel);

        var path = string.IsNullOrWhiteSpace(OutboxPath) ? Services.ApplicationStore.DefaultOutboxDir : OutboxPath.Trim();
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
}
