using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ModularRuntime.Desktop.Services;

/// <summary>
/// Native window appearance through DWM: Windows 11 rounded corners, the 1 px window border color,
/// and the dark/light frame mode. Applied to the real top-level window (HWND), not drawn by WPF.
/// </summary>
/// <remarks>
/// <para>
/// Windows that use a custom <see cref="System.Windows.Shell.WindowChrome"/> are not rounded by DWM's
/// default policy; asking for <c>DWMWCP_ROUND</c> explicitly gives them the same corners, border,
/// and shadow as standard Windows 11 windows. DWM itself never rounds maximized or snapped-to-edge
/// windows, so nothing has to be undone when the window is maximized.
/// </para>
/// <para>
/// Fallback: before Windows 11 (build 22000) these attributes do not exist; the calls fail harmlessly
/// and the window keeps the system's square corners. No window region or per-pixel transparency is
/// used, so composition, snapping, and performance are unaffected.
/// </para>
/// </remarks>
internal static partial class WindowAppearance
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    /// <summary>Whether this Windows version supports DWM corner preferences (Windows 11 or later).</summary>
    public static bool SupportsRoundedCorners { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Applies corners, frame mode, and border color for the active palette (its <c>ThemeName</c>
    /// resource). Call from <c>SourceInitialized</c>, before the first frame, and after a theme change.
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var dark = Equals(window.TryFindResource("ThemeName"), nameof(AppTheme.Light)) ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        if (!SupportsRoundedCorners)
        {
            return;
        }

        var corner = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        if (window.TryFindResource("WindowBorderBrush") is SolidColorBrush border)
        {
            var colorRef = ToColorRef(border.Color);
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorRef, sizeof(int));
        }
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
