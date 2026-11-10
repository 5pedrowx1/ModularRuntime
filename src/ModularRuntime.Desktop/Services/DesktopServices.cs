using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace ModularRuntime.Desktop.Services;

/// <summary>UI-thread dispatcher backed by the WPF <see cref="Dispatcher"/>.</summary>
internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick(), dispatcher);
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

internal sealed class DialogService : IDialogService
{
    /// <summary>Themed confirmation (follows Light/Dark); the system MessageBox cannot be themed.</summary>
    public bool Confirm(string title, string message, string confirmText) =>
        Views.MessageDialog.Confirm(Application.Current.MainWindow, title, message, confirmText);

    public string? PickPackageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Install plugin from ZIP",
            Filter = "Plugin packages (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public PackageInstallDecision ShowPackagePreview(Ipc.Contracts.PackagePreviewDto preview) =>
        Views.PackagePreviewDialog.Show(Application.Current.MainWindow, preview);

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is temporarily locked by another application; copying is best effort.
        }
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>Visual preferences of the desktop app (not daemon configuration).</summary>
public sealed record DesktopSettings
{
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Start the daemon when the app opens and none is running.</summary>
    public bool StartDaemonOnLaunch { get; init; } = true;

    public bool LogsAutoScroll { get; init; } = true;
}

/// <summary>Stores <see cref="DesktopSettings"/> in <c>&lt;profile&gt;\desktop\settings.json</c>, separate from <c>config\daemon.json</c>.</summary>
public sealed class SettingsService(string directory)
{
    // Enums are written as names ("Light"); numbers written by v0.3.0 are still accepted.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath => Path.Combine(directory, "settings.json");

    public DesktopSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), Options) ?? new DesktopSettings()
                : new DesktopSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DesktopSettings();
        }
    }

    public void Save(DesktopSettings settings)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences are best effort; the app keeps working with in-memory values.
        }
    }
}

/// <summary>Swaps the color dictionary at runtime; every brush is referenced with DynamicResource.</summary>
public interface IThemeService
{
    void Apply(AppTheme theme);
}

/// <remarks>
/// <para>
/// The palette dictionary is located by its <c>ThemeName</c> key, not by its <c>Source</c> URI: the
/// URI's text depends on how the dictionary was declared (App.xaml uses a relative path, this class
/// an absolute pack URI). Matching on the URI text was the v0.3.0 Light-mode bug: the lookup never
/// matched the dictionary declared in App.xaml, so the new palette was inserted <i>before</i> it and
/// the Dark palette, merged later, kept winning.
/// </para>
/// <para>
/// Exactly one palette stays merged, at the position of the previous one (before Controls.xaml, so
/// control styles resolve palette brushes through DynamicResource). Open windows also get their
/// native frame (DWM dark mode and border color) updated.
/// </para>
/// </remarks>
internal sealed class ThemeService : IThemeService
{
    private const string ThemeNameKey = "ThemeName";

    public void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palettes = dictionaries.Where(d => d.Contains(ThemeNameKey)).ToList();
        if (palettes.Count == 1 && Equals(palettes[0][ThemeNameKey], theme.ToString()))
        {
            UpdateWindows();
            return;
        }

        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ModularRuntime.Desktop;component/Themes/Colors.{theme}.xaml", UriKind.Absolute),
        };
        var index = palettes.Count > 0 ? dictionaries.IndexOf(palettes[0]) : 0;
        foreach (var palette in palettes)
        {
            dictionaries.Remove(palette);
        }

        dictionaries.Insert(index, replacement);
        UpdateWindows();
    }

    private static void UpdateWindows()
    {
        foreach (Window window in Application.Current.Windows)
        {
            WindowAppearance.Apply(window);
        }
    }
}
