using CommunityToolkit.Mvvm.ComponentModel;

namespace AiJobSearchBy.ViewModels;

// WP-U1: строка чекбокса сайта поиска (реестр порталов -> UI).
// STATE: IsChecked — единственное мутируемое поле; событие Toggled — для
// персистенции выбора в SettingsKV (sites.checked, JSON-массив id).
// THREAD: свойства — только с UI-потока (Avalonia).
public partial class SiteToggleRow : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public required string PortalId { get; init; }
    public required string DisplayName { get; init; }
    public bool DefaultEnabled { get; init; }

    // Сигнал персистенции; подписка ставится после восстановления выбора.
    public event EventHandler? Toggled;

    partial void OnIsCheckedChanged(bool value) => Toggled?.Invoke(this, EventArgs.Empty);
}
