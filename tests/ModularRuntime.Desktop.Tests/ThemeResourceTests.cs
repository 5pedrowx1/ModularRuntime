using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ModularRuntime.Desktop.Tests;

/// <summary>
/// Static checks of the theme resources: a key missing from one palette would leave that part of the
/// UI in the other theme's colors (or unresolved) after switching.
/// </summary>
public sealed partial class ThemeResourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string DesktopSource { get; } = LocateDesktopSource();

    private static string LocateDesktopSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ModularRuntime.Desktop");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/ModularRuntime.Desktop not found.");
    }

    private static Dictionary<string, string> PaletteKeys(string theme)
    {
        var document = XDocument.Load(Path.Combine(DesktopSource, "Themes", $"Colors.{theme}.xaml"));
        return document.Root!.Elements()
            .Where(e => e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => e.Attribute(Xaml + "Key")!.Value, e => e.Value);
    }

    [Fact]
    public void DarkAndLightPalettes_DefineExactlyTheSameKeys()
    {
        var dark = PaletteKeys("Dark").Keys.Order().ToList();
        var light = PaletteKeys("Light").Keys.Order().ToList();

        Assert.Equal(dark, light);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void EachPalette_IsMarkedWithItsThemeName(string theme)
    {
        // ThemeService locates the merged palette by this key.
        Assert.Equal(theme, PaletteKeys(theme)["ThemeName"]);
    }

    [Fact]
    public void EveryDynamicResourceReference_ResolvesToAPaletteOrControlsKey()
    {
        var defined = PaletteKeys("Dark").Keys.ToHashSet(StringComparer.Ordinal);
        var controls = XDocument.Load(Path.Combine(DesktopSource, "Themes", "Controls.xaml"));
        defined.UnionWith(controls.Descendants().Select(e => e.Attribute(Xaml + "Key")?.Value).OfType<string>());

        var missing = Directory.EnumerateFiles(DesktopSource, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => DynamicResourcePattern().Matches(File.ReadAllText(path)).Select(m => (File: Path.GetFileName(path), Key: m.Groups[1].Value)))
            .Where(reference => !defined.Contains(reference.Key))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void ViewsAndStyles_ContainNoHardcodedColors()
    {
        // Colors live only in the two palettes; everything else references them.
        var offenders = Directory.EnumerateFiles(DesktopSource, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("Colors.", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => HexColorPattern().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\{DynamicResource\s+([A-Za-z0-9_.]+)\s*\}")]
    private static partial Regex DynamicResourcePattern();

    [GeneratedRegex(@"=""#[0-9A-Fa-f]{3,8}""")]
    private static partial Regex HexColorPattern();
}
