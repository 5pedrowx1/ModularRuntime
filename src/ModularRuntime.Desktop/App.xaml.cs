using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Desktop.ViewModels;
using ModularRuntime.Ipc;

namespace ModularRuntime.Desktop;

/// <summary>
/// Desktop client. It owns only its IPC connection and its preferences; the daemon and the plugins
/// are separate and keep running when this application closes or crashes.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A UI failure must never take anything else down: report it and keep the app alive.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var (profileDirectory, noAutoStart) = ParseArguments(e.Args);
        RuntimeProfile profile;
        try
        {
            profile = RuntimeProfile.Resolve(profileDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show($"Invalid profile directory: {exception.Message}", "ModularRuntime", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var settingsService = new SettingsService(Path.Combine(profile.ProfileDirectory, "desktop"));
        var settings = settingsService.Load();
        var theme = new ThemeService();
        theme.Apply(settings.Theme);

        var dispatcher = new WpfUiDispatcher(Dispatcher);
        var connection = new RuntimeConnection(profile, new RuntimeClientFactory(), new DaemonController());
        var viewModel = new MainViewModel(connection, dispatcher, new DialogService(), settingsService, theme);

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        connection.Start(autoStartDaemon: settings.StartDaemonOnLaunch && !noAutoStart);
    }

    private static (string? Profile, bool NoAutoStart) ParseArguments(string[] args)
    {
        string? profile = null;
        var noAutoStart = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--profile", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                profile = args[++index];
            }
            else if (args[index].Equals("--no-autostart", StringComparison.OrdinalIgnoreCase))
            {
                noAutoStart = true;
            }
        }

        return (profile, noAutoStart);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Views.MessageDialog.Show(
            MainWindow,
            "Unexpected error",
            $"An unexpected error occurred in the desktop app:\n\n{e.Exception.Message}\n\nThe daemon and its plugins are not affected.");
    }
}
