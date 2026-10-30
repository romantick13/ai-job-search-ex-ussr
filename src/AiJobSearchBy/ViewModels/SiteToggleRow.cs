using CommunityToolkit.Mvvm.ComponentModel;

namespace AiJobSearchBy.ViewModels;

// строка чекбокса сайта поиска (реестр порталов -> UI).
// STATE: IsChecked — единственное мутируемое поле; событие Toggled — для
// персистенции выбора в SettingsKV (sites.checked, JSON-массив id).
// Hint — подсказка чекбокса (ApplyWarning карточки для
// выключенных по умолчанию / тип доступа для Core) — отвечает на вопрос
// «куда включить» прямо на экране.
// THREAD: свойства — только с UI-потока (Avalonia).
public partial class SiteToggleRow : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public required string PortalId { get; init; }
    public required string DisplayName { get; init; }
    public bool DefaultEnabled { get; init; }
    public string? Hint { get; init; }

    // Сигнал персистенции; подписка ставится после восстановления выбора.
    public event EventHandler? Toggled;

    partial void OnIsCheckedChanged(bool value) => Toggled?.Invoke(this, EventArgs.Empty);
}
