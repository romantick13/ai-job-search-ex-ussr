using Xunit;

namespace AiJobSearchExUssr.Tests;

// Гард проводки иконки — файл есть, это валидный мультиразмерный ico,
// csproj и окно с трей-иконой на него сослались. Не даёт «потерять» иконку
// при будущих правках шапки окна или csproj.
public class AppIconGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src/ai-job-search-ex-ussr.sln")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("корень репо не найден (src/ai-job-search-ex-ussr.sln)");
    }

    [Fact]
    public void AppIcon_Ico_Exists_Multisize()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "AiJobSearchExUssr", "Assets", "app.ico");
        Assert.True(File.Exists(path), "нет src/AiJobSearchExUssr/Assets/app.ico");
        var raw = File.ReadAllBytes(path);
        // ICO-магия 00 00 01 00
        Assert.True(raw.Length > 22 && raw[0] == 0 && raw[1] == 0 &&
                    raw[2] == 1 && raw[3] == 0, "app.ico не похож на ICO");
        var count = raw[4] | (raw[5] << 8);
        Assert.True(count >= 8, $"в app.ico записей {count}, ждём >= 8 (16..256)");
        var dims = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var e = raw[(6 + i * 16)..(6 + (i + 1) * 16)];
            dims.Add(e[0] == 0 ? 256 : e[0]);
        }
        Assert.Contains(16, dims);
        Assert.Contains(32, dims);
        Assert.Contains(256, dims);
    }

    [Fact]
    public void Csproj_Wires_ApplicationIcon_And_Assets()
    {
        var csproj = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "src", "AiJobSearchExUssr", "AiJobSearchExUssr.csproj"));
        // Рецепт: ApplicationIcon + Assets и на диск (Content), и в DLL
        Assert.Contains("<ApplicationIcon>Assets\\app.ico</ApplicationIcon>", csproj);
        Assert.Contains("<Content Include=\"Assets\\**\\*.*\">", csproj);
        Assert.Contains("PreserveNewest", csproj);
        Assert.Contains("<AvaloniaResource Include=\"Assets\\**\" />", csproj);
    }

    [Fact]
    public void App_Wires_Window_Icon_And_TrayIcon()
    {
        var root = FindRepoRoot();
        // окно: иконка таскбара/тайтлбара (avares — иконка в DLL)
        var window = File.ReadAllText(Path.Combine(root, "src", "AiJobSearchExUssr", "MainWindow.axaml"));
        Assert.Contains("Icon=\"/Assets/app.ico\"", window);
        // трей: код в App.axaml.cs, актив грузится стримом — WindowIcon(string)
        // открывает путь НА ДИСКЕ (File.OpenRead) и avares:// не понимает
        var appCs = File.ReadAllText(Path.Combine(root, "src", "AiJobSearchExUssr", "App.axaml.cs"));
        Assert.Contains("TrayIcon.SetIcons", appCs);
        Assert.Contains("avares://AiJobSearchExUssr/Assets/app.ico", appCs);
        Assert.Contains("AssetLoader.Open", appCs);
        Assert.Contains("ToolTipText", appCs);
    }
}
