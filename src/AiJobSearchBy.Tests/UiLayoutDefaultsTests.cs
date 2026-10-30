using AiJobSearchBy.Controls;
using Xunit;

namespace AiJobSearchBy.Tests;

// ============================================================================
// Дефолтный UiLayout.DefaultJson — ручная подгонка разметки:
// 1800×938; живой протокол 138* против рабочих 421*; зоны 459*/1311*;
// правки 357*/110%; модалки 560×973 / 640×620 / 580×640 (PdfOverlayWindow
// добавлен в дефолт). Тест держит дефолт от отката к старым значениям
// (1040, 4*/1*) и от битого JSON.
// ============================================================================
public sealed class UiLayoutDefaultsTests
{
    [Fact]
    public void DefaultJson_Parses_WithFourWindows()
    {
        var layout = UiLayout.Parse(UiLayout.DefaultJson);

        Assert.NotNull(layout);
        Assert.Equal(4, layout!.Windows.Count);
    }

    [Fact]
    public void MainWindow_DefaultMatchesOwnerTuning()
    {
        var main = UiLayout.Parse(UiLayout.DefaultJson)!.Windows["MainWindow"];

        Assert.Equal(1800.0, main.Width);
        Assert.Equal(938.0, main.Height);
        Assert.Null(main.X); // x/y = null — окно по центру (на 1920×1080 это ~59/48)
        Assert.Null(main.Y);

        var root = main.Grids["RootGrid"];
        Assert.Equal("421*", root.Rows["4"]);
        Assert.Equal("6", root.Rows["5"]);
        Assert.Equal("138*", root.Rows["6"]); // живой протокол

        var work = main.Grids["WorkGrid"];
        Assert.Equal("357*", work.Rows["0"]);
        Assert.Equal("6", work.Rows["1"]);
        Assert.Equal("110*", work.Rows["2"]);

        var zones = main.Grids["ZoneGrid"];
        Assert.Equal("459*", zones.Cols["0"]);
        Assert.Equal("6", zones.Cols["1"]);
        Assert.Equal("1311*", zones.Cols["2"]);
    }

    [Fact]
    public void Dialogs_DefaultSizesMatchOwnerTuning()
    {
        var layout = UiLayout.Parse(UiLayout.DefaultJson)!;

        var settings = layout.Windows["SettingsWindow"];
        Assert.Equal((560.0, 973.0), (settings.Width, settings.Height));

        var details = layout.Windows["ApplicationDetailsWindow"];
        Assert.Equal((640.0, 620.0), (details.Width, details.Height));

        var pdfOverlay = layout.Windows["PdfOverlayWindow"];
        Assert.Equal((580.0, 640.0), (pdfOverlay.Width, pdfOverlay.Height));
    }

    [Fact]
    public void Parse_BrokenJson_ReturnsNull()
    {
        Assert.Null(UiLayout.Parse("{ это не json"));
    }
}
