using AiJobSearchBy.Controls;
using AiJobSearchBy.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiJobSearchBy.Views;

// модал «Страны…» — текстовый список
// разрешённых стран удалёнки. Чтение/запись — через MainViewModel
// (LoadAllowedCountriesText / SaveAllowedCountriesText, SettingsKV
// geo.allowed): окно не держит состояния и не знает про базу.
// «Вернуть все страны» — сброс к полному списку (эквивалент «весь мир»,
// ведь вычеркнутых нет); «Готово» — сохранить и закрыть. Статусная строка
// показывает, что именно сохранится (или «весь мир»).
public partial class GeoCountriesWindow : Window
{
    private readonly MainViewModel? _main;

    // Полная модель (DI) — текст и статус наполним при открытии.
    public GeoCountriesWindow(MainViewModel main)
        : this()
    {
        _main = main;
    }

    // Дизайнер: пустое окно без зависимостей.
    public GeoCountriesWindow()
    {
        InitializeComponent();
        // память размера + три точки в углу — как у всех изменяемых окошек.
        UiLayout.Attach(this, "GeoCountriesWindow");
        var restore = this.FindControl<Button>("RestoreAllButton");
        if (restore is not null)
        {
            restore.Click += OnRestoreAll;
        }

        var done = this.FindControl<Button>("DoneButton");
        if (done is not null)
        {
            done.Click += OnDone;
        }

        Opened += OnWindowOpened;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        var text = _main?.LoadAllowedCountriesText()
                   ?? string.Join(Environment.NewLine, Services.Resume.GeoMatcher.AllDisplays());
        var box = this.FindControl<TextBox>("CountriesText");
        if (box is not null)
        {
            box.Text = text;
        }

        UpdateStatus(text);
    }

    private void OnRestoreAll(object? sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, Services.Resume.GeoMatcher.AllDisplays());
        var box = this.FindControl<TextBox>("CountriesText");
        if (box is not null)
        {
            box.Text = text;
        }

        UpdateStatus(text);
    }

    private void OnDone(object? sender, RoutedEventArgs e)
    {
        var text = this.FindControl<TextBox>("CountriesText")?.Text ?? "";
        _main?.SaveAllowedCountriesText(text);
        Close();
    }

    // Статусная строка честно показывает, ЧТО сохранится (без тихой магии).
    private void UpdateStatus(string text)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Count();
        var total = Services.Resume.CountryTable.Entries.Count;
        var status = this.FindControl<TextBlock>("StatusLine");
        if (status is null)
        {
            return;
        }

        status.Text = lines switch
        {
            0 => "Пусто = дефолт (Беларусь + Россия) — остальное прячется.",
            var n when n >= total => $"Все {total} стран — весь мир (ничего не прячется).",
            var n => $"Разрешено {n} из {total}; вычеркнутые страны — прячутся (и у удалёнок, и по локации).",
        };
    }
}
