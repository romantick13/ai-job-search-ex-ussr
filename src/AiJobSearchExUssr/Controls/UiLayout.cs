using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiJobSearchExUssr.Controls;

// =====================================================================
// UiLayout — переиспользуемый модуль «изменяемые окна с памятью»: один
// файл без зависимостей от домена приложения (переносится в любой
// Avalonia-проект как есть — один раз написали, везде юзаем).

// Умеет:
// 1) помнить размер и позицию окна (в нормальном состоянии; развёрнутое
// окно запомненную геометрию не перезаписывает);
// 2) помнить раскладку отслеживаемых Grid'ов — высоты строк и ширины
// колонок, кроме Auto: "420" = пиксели, "2*" = две доли, "*" = одна;
// 3) после перетаскивания GridSplitter возвращать соседним ячейкам
// доли (*) по текущим пикселям — картинка на глаз не меняется, зато
// при растяжении окна панели снова тянутся пропорционально;
// 4) хранить всё в layout.json в папке конфига (рядом с local.json):
// файла нет — копируется дефолт DefaultJson (автор правит его руками
// и перезапуском получает свои дефолтные окна); битый файл уходит в
// layout.json.broken, дальше работаем на дефолте.

// Использование (code-behind окна, после InitializeComponent):
// UiLayout.Attach(this, "MainWindow", RootGrid, WorkGrid);
// Сплиттеры ищутся среди ПРЯМЫХ детей отслеживаемого Grid'а.
// Сохранение: закрытие окна, перетаскивание сплиттера, изменение размера/
// позиции окна (с промедлением 400 мс, чтобы не писать файл на каждый тик).
// =====================================================================
public static class UiLayout
{
    // Имя приложения в профильном каталоге: %APPDATA%\<имя>\layout.json
    // (Linux: ~/.config/<имя>/layout.json). Для другого приложения
    // достаточно поменять эту строку.
    public static string AppFolderName { get; set; } = "ai-job-search-ex-ussr";


// Дефолтный конфиг: копируется в папку конфига при первом старте.
// Правится руками (автор приложения имеет право) — перезапуск примет.
// $schema — подсказка человеку, приложением не читается.
// Дефолт — ручная подгонка: 1800×938, живой протокол 138* против рабочих
// 421*, зоны 459*/1311*, правки 357*/110*. x/y = null — по центру:
// на 1920×1080 это ~59/48. На малых
// экранах размер клампится ClampToScreen. PdfOverlayWindow
// добавлен (580×640) — раньше запоминался только после первого юза.

    public const string DefaultJson = """
    {
      "$schema": "UiLayout v1: число = пиксели, N* = доля оставшегося места, x/y = null значит окно по центру. Правится руками, применяется при перезапуске.",
      "Windows": {
        "MainWindow": {
          "width": 1800, "height": 938, "x": null, "y": null,
          "grids": {
            "RootGrid": { "rows": { "4": "421*", "5": "6", "6": "138*" }, "cols": {} },
            "WorkGrid": { "rows": { "0": "357*", "1": "6", "2": "110*" }, "cols": {} },
            "ZoneGrid": { "rows": {}, "cols": { "0": "459*", "1": "6", "2": "1311*" } }
          }
        },
        "SettingsWindow": {
          "width": 560, "height": 973, "x": null, "y": null, "grids": {}
        },
        "ApplicationDetailsWindow": {
          "width": 640, "height": 620, "x": null, "y": null, "grids": {}
        },
        "PdfOverlayWindow": {
          "width": 580, "height": 640, "x": null, "y": null, "grids": {}
        }
      }
    }
    """;

    // ------------------------------ DTO ------------------------------
    internal sealed class LayoutFile
    {
        [JsonPropertyName("$schema")]
        public string? Schema { get; set; }

        public Dictionary<string, WinState> Windows { get; set; } = new();
    }

    internal sealed class WinState
    {
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public Dictionary<string, GridState> Grids { get; set; } = new();
    }

    internal sealed class GridState
    {
        public Dictionary<string, string> Rows { get; set; } = new();
        public Dictionary<string, string> Cols { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // --------------------------- регистрация -------------------------
    private sealed class Registration
    {
        public string Key = "";
        public List<Grid> Grids = new();
    }

    private static readonly ConditionalWeakTable<Window, Registration> Reg = new();
    private static readonly ConditionalWeakTable<Window, DispatcherTimer> Timers = new();

    // ------------------------------ API ------------------------------
    public static void Attach(Window window, string windowKey, params Grid[] grids)
    {
        if (Design.IsDesignMode) return;

        var file = Load();
        var state = EnsureWindow(file, windowKey, window);

        // Размер и (если в конфиге задан x/y) позиция — до показа окна,
        // чтобы не было прыжка после центрирования.
        if (state.Width is double w && w > 0)
            window.Width = Math.Max(window.MinWidth, w);
        if (state.Height is double h && h > 0)
            window.Height = Math.Max(window.MinHeight, h);
        // сохранённый
        // размер мог быть снят с большего монитора — клампим к рабочей области
        // текущего экрана. Повтор на Opened — экраны у платформы гарантированно
        // готовы после показа окна.
        ClampToScreen(window);
        window.Opened += (_, _) => ClampToScreen(window);
        if (state.X is double x && state.Y is double y)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        }

        var reg = new Registration { Key = windowKey };
        foreach (var grid in grids)
        {
            if (grid is null) continue;
            reg.Grids.Add(grid);

            if (!string.IsNullOrWhiteSpace(grid.Name))
                ApplyGrid(grid, state);

            foreach (var child in grid.Children)
            {
                if (child is not GridSplitter splitter) continue;
                splitter.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
                {
                    NormalizeNeighbors(grid, splitter);
                    ScheduleSave(window);
                });
            }
        }
        Reg.Remove(window);
        Reg.Add(window, reg);

        // Позицию дотягиваем по реальным экранам после показа (мониторы
        // меняются — окно не должно потеряться за краем).
        window.Opened += (_, _) => ClampPositionToScreens(window);

        window.SizeChanged += (_, _) => ScheduleSave(window);
        window.PositionChanged += (_, _) => ScheduleSave(window);
        window.Closing += (_, _) =>
        {
            SaveNow(window);
            if (Timers.TryGetValue(window, out var timer)) timer.Stop();
        };

    // Угловой грип окна УБРАН. Он решал не ту задачу:
    // окно и так меняет размер за край/рамку, а точки нужны на
    // РАЗДЕЛИТЕЛЯХ панелей — они теперь живут в разметке MainWindow.axaml
    // (три точки по центру каждого GridSplitter).
    }

    // ------------------------- применение ----------------------------
    private static void ApplyGrid(Grid grid, WinState state)
    {
        if (string.IsNullOrWhiteSpace(grid.Name)) return;
        if (!state.Grids.TryGetValue(grid.Name, out var gs)) return;

        if (gs.Rows is { Count: > 0 })
        {
            var defs = grid.RowDefinitions;
            foreach (var (key, value) in gs.Rows)
            {
                if (!int.TryParse(key, out var i) || i < 0 || i >= defs.Count) continue;
                if (defs[i].Height.IsAuto) continue; // Auto-строки не перекладываем
                var len = ParseLength(value);
                if (len is not null) defs[i].Height = len.Value;
            }
        }

        if (gs.Cols is { Count: > 0 })
        {
            var defs = grid.ColumnDefinitions;
            foreach (var (key, value) in gs.Cols)
            {
                if (!int.TryParse(key, out var i) || i < 0 || i >= defs.Count) continue;
                if (defs[i].Width.IsAuto) continue;
                var len = ParseLength(value);
                if (len is not null) defs[i].Width = len.Value;
            }
        }
    }

    private static void ClampPositionToScreens(Window window)
    {
        try
        {
            if (window.WindowState != WindowState.Normal) return;
            var screens = window.Screens;
            if (screens is null || screens.ScreenCount == 0) return;

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var screen in screens.All)
            {
                var b = screen.Bounds;
                minX = Math.Min(minX, b.X);
                minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.Right);
                maxY = Math.Max(maxY, b.Bottom);
            }

            var p = window.Position;
            var nx = Math.Clamp(p.X, (int)minX - 8,
                Math.Max((int)minX - 8, (int)(maxX - window.Width) + 8));
            var ny = Math.Clamp(p.Y, (int)minY - 8,
                Math.Max((int)minY - 8, (int)(maxY - window.Height) + 8));
            if (nx != p.X || ny != p.Y)
                window.Position = new PixelPoint(nx, ny);
        }
        catch
        {
        // Экраны не резолвятся — позицию не трогаем.
        }
    }

    // сохранённая в
    // layout.json высота/ширина могли быть сняты с большего монитора — клампим
    // размер к рабочей области экрана, где окно находится (фолбэк — первичный).
    // WorkingArea в физических пикселях — приводим к DIP через Scaling; запас
    // 48 DIP по высоте — рамка и заголовок окна. Вызывается дважды: до показа
    // (если экраны уже резолвятся) и на Opened (гарантированно). Если экраны
    // не резолвятся вовсе — размер не трогаем: дефолт 938 на малых экранах
    // подожмётся этим же клампом при следующей попытке (тот же фолбэк-принцип,
    // что у ClampPositionToScreens).
    private static void ClampToScreen(Window window)
    {
        try
        {
            if (window.WindowState != WindowState.Normal) return;
            var screens = window.Screens;
            if (screens is null || screens.ScreenCount == 0) return;

            var screen = screens.ScreenFromWindow(window) ?? screens.Primary;
            if (screen is null) return;

            var maxW = screen.WorkingArea.Width / screen.Scaling - 16;
            var maxH = screen.WorkingArea.Height / screen.Scaling - 48;
            if (maxW >= window.MinWidth && window.Width > maxW)
                window.Width = maxW;
            if (maxH >= window.MinHeight && window.Height > maxH)
                window.Height = maxH;
        }
        catch
        {
        // Экраны не резолвятся — размер не трогаем (дефолт влезает).
        }
    }

    // --------------------------- сохранение --------------------------
    private static void ScheduleSave(Window window)
    {
        if (Design.IsDesignMode) return;
        if (!Timers.TryGetValue(window, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SaveNow(window);
            };
            Timers.Add(window, timer);
        }
        timer.Stop();
        timer.Start();
    }

    public static void SaveNow(Window window)
    {
        if (!Reg.TryGetValue(window, out var reg)) return;
        try
        {
            var file = Load();
            var state = EnsureWindow(file, reg.Key, window);

            if (window.WindowState == WindowState.Normal)
            {
                if (!double.IsNaN(window.Width))
                    state.Width = Math.Round(window.Width, 1);
                if (!double.IsNaN(window.Height))
                    state.Height = Math.Round(window.Height, 1);
                state.X = window.Position.X;
                state.Y = window.Position.Y;
            }
            // Развёрнутое окно геометрию не перезаписывает: в конфиге
            // остаётся последняя нормальная (вернётся после распахивания).

            state.Grids.Clear();
            foreach (var grid in reg.Grids)
            {
                if (string.IsNullOrWhiteSpace(grid.Name)) continue;
                state.Grids[grid.Name] = CaptureGrid(grid);
            }

            Save(file);
        }
        catch
        {
        // Раскладка не должна ронять приложение — молча живём без неё.
        }
    }

    private static GridState CaptureGrid(Grid grid)
    {
        var gs = new GridState();

        var rows = grid.RowDefinitions;
        for (var i = 0; i < rows.Count; i++)
            if (!rows[i].Height.IsAuto)
                gs.Rows[i.ToString()] = SerializeLength(rows[i].Height);

        var cols = grid.ColumnDefinitions;
        for (var i = 0; i < cols.Count; i++)
            if (!cols[i].Width.IsAuto)
                gs.Cols[i.ToString()] = SerializeLength(cols[i].Width);

        return gs;
    }

    private static string SerializeLength(GridLength length)
    {
        var v = Math.Round(length.Value, 1).ToString("0.#", CultureInfo.InvariantCulture);
        if (length.IsStar) return length.Value == 1d ? "*" : v + "*";
        return v; // пиксели
    }

    // --------------- сплиттер: пиксели обратно в доли ----------------
    // GridSplitter после перетаскивания оставляет соседние ячейки в
    // пикселях — раскладка «застывает». Возвращаем соседям доли (*) с
    // весами по текущим размерам: на глаз ничего не меняется, зато при
    // растяжении окна панели снова тянутся пропорционально.
    private static void NormalizeNeighbors(Grid grid, GridSplitter splitter)
    {
        if (splitter.ResizeBehavior != GridResizeBehavior.PreviousAndNext) return;

        var direction = splitter.ResizeDirection;
        if (direction == GridResizeDirection.Auto)
        {
            direction = grid.RowDefinitions.Count >= grid.ColumnDefinitions.Count
                ? GridResizeDirection.Rows
                : GridResizeDirection.Columns;
        }

        if (direction == GridResizeDirection.Rows)
        {
            var i = Grid.GetRow(splitter);
            ToStarRow(grid.RowDefinitions, i - 1);
            ToStarRow(grid.RowDefinitions, i + 1);
        }
        else
        {
            var i = Grid.GetColumn(splitter);
            ToStarColumn(grid.ColumnDefinitions, i - 1);
            ToStarColumn(grid.ColumnDefinitions, i + 1);
        }
    }

    private static void ToStarRow(IList<RowDefinition> defs, int index)
    {
        if (index < 0 || index >= defs.Count) return;
        var def = defs[index];
        if (def.Height.IsStar || def.Height.IsAuto || def.ActualHeight <= 0.5) return;
        def.Height = new GridLength(Math.Round(def.ActualHeight, 1), GridUnitType.Star);
    }

    private static void ToStarColumn(IList<ColumnDefinition> defs, int index)
    {
        if (index < 0 || index >= defs.Count) return;
        var def = defs[index];
        if (def.Width.IsStar || def.Width.IsAuto || def.ActualWidth <= 0.5) return;
        def.Width = new GridLength(Math.Round(def.ActualWidth, 1), GridUnitType.Star);
    }

    // ----------------------------- парсинг ---------------------------
    private static GridLength? ParseLength(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return null;
        if (s.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return GridLength.Auto;
        if (s.EndsWith("*"))
        {
            var num = s[..^1].Trim();
            var value = num.Length == 0 ? 1d : ParseDouble(num);
            if (value is null || value < 0) return null;
            return new GridLength(value.Value, GridUnitType.Star);
        }
        var px = ParseDouble(s);
        return px is null || px < 0 ? null : new GridLength(px.Value, GridUnitType.Pixel);
    }

    private static double? ParseDouble(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    // ------------------------------ файл -----------------------------
    private static string ConfigPath()
    {
        // Тот же корень, что у local.json: %USERPROFILE%\<AppFolderName>

        // если профильный каталог не резолвится — рядом с exe.
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rooted = !string.IsNullOrEmpty(profileDir) && System.IO.Path.IsPathRooted(profileDir);
        var root = rooted ? profileDir : AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, AppFolderName, "layout.json");
    }

    private static LayoutFile Load()
    {
        var path = ConfigPath();
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultJson); // файла нет — копируется дефолтный
                return Parse(DefaultJson) ?? new LayoutFile();
            }
            return Parse(File.ReadAllText(path)) ?? BackupAndDefaults(path);
        }
        catch
        {
            return Parse(DefaultJson) ?? new LayoutFile();
        }
    }

    private static LayoutFile BackupAndDefaults(string path)
    {
        try
        {
            File.Copy(path, path + ".broken", overwrite: true);
            File.WriteAllText(path, DefaultJson);
        }
        catch
        {
        // Не смогли и забекапить — просто работаем на дефолте.
        }
        return Parse(DefaultJson) ?? new LayoutFile();
    }

    internal static LayoutFile? Parse(string json) // internal: тест дефолта
    {
        try
        {
            return JsonSerializer.Deserialize<LayoutFile>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static void Save(LayoutFile file)
    {
        var path = ConfigPath();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, path, overwrite: true); // атомарно
    }

    private static WinState EnsureWindow(LayoutFile file, string key, Window window)
    {
        if (!file.Windows.TryGetValue(key, out var state))
        {
            state = new WinState
            {
                Width = double.IsNaN(window.Width) ? null : window.Width,
                Height = double.IsNaN(window.Height) ? null : window.Height,
                X = null, // новый раздел — окно по центру
                Y = null,
            };
            file.Windows[key] = state;
        }
        return state;
    }
}
