using System.Reflection;
using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>Preferences, daemon information, data folders, and explicit daemon start/stop.</summary>
public sealed class SettingsViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settingsService;
    private readonly IThemeService _theme;
    private DesktopSettings _settings;
    private RuntimeStatusDto? _status;
    private string? _actionMessage;

    public SettingsViewModel(
        IRuntimeConnection connection,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        SettingsService settingsService,
        IThemeService theme)
        : base("Settings", "", connection, dispatcher)
    {
        _dialogs = dialogs;
        _settingsService = settingsService;
        _theme = theme;
        _settings = settingsService.Load();
        StartDaemonCommand = new RelayCommand(StartDaemon, () => connection.State is ConnectionState.Disconnected or ConnectionState.Error);
        StopDaemonCommand = new AsyncRelayCommand(StopDaemonAsync, () => connection.State == ConnectionState.Connected, e => ActionMessage = e.Message);
        OpenFolderCommand = new RelayCommand(p => _dialogs.OpenFolder(p as string ?? string.Empty), p => p is string { Length: > 0 });
        connection.StateChanged += (_, _) => dispatcher.Post(() =>
        {
            OnPropertyChanged(nameof(ConnectionState));
            OnPropertyChanged(nameof(ConnectionMessage));
            StartDaemonCommand.RaiseCanExecuteChanged();
            StopDaemonCommand.RaiseCanExecuteChanged();
        });
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (value == _settings.Theme)
            {
                return;
            }

            Save(_settings with { Theme = value });
            _theme.Apply(value);
            OnPropertyChanged();
        }
    }

    public bool StartDaemonOnLaunch
    {
        get => _settings.StartDaemonOnLaunch;
        set
        {
            if (value != _settings.StartDaemonOnLaunch)
            {
                Save(_settings with { StartDaemonOnLaunch = value });
                OnPropertyChanged();
            }
        }
    }

    public bool LogsAutoScroll
    {
        get => _settings.LogsAutoScroll;
        set
        {
            if (value != _settings.LogsAutoScroll)
            {
                Save(_settings with { LogsAutoScroll = value });
                OnPropertyChanged();
            }
        }
    }

    public RuntimeStatusDto? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ConnectionState ConnectionState => Connection.State;

    public string ConnectionMessage => Connection.StatusMessage;

    public string ProfileDirectory => Connection.Profile.ProfileDirectory;

    public string ConfigDirectory => Connection.Profile.ConfigDirectory;

    public string LogDirectory => Connection.Profile.LogDirectory;

    public string PluginDirectory => Status?.PluginDirectory ?? Connection.Profile.DefaultPluginDirectory;

    public string SettingsFile => _settingsService.FilePath;

    public string PipeName => Connection.Profile.PipeName;

    public string DesktopVersion { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public string ProtocolVersion { get; } = IpcProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public RelayCommand StartDaemonCommand { get; }

    public AsyncRelayCommand StopDaemonCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        Status = await client.GetStatusAsync(cancellationToken);
        OnPropertyChanged(nameof(PluginDirectory));
        return true;
    }

    protected override void Clear()
    {
        Status = null;
        OnPropertyChanged(nameof(PluginDirectory));
    }

    private void StartDaemon()
    {
        ActionMessage = "Starting the daemon…";
        Connection.RequestDaemonStart();
    }

    /// <summary>Explicit, confirmed shutdown of the daemon. Closing the window never does this.</summary>
    private async Task StopDaemonAsync()
    {
        var client = Connection.Client;
        if (client is null)
        {
            return;
        }

        var status = await client.GetStatusAsync();
        var message = status.RunningPluginCount > 0
            ? $"Stopping the daemon stops {status.RunningPluginCount} running plugin(s) and ends the ModularRuntime engine for this profile."
            : "Stop the ModularRuntime daemon for this profile?";
        if (!_dialogs.Confirm("Stop daemon", message, "Stop the daemon"))
        {
            return;
        }

        var response = await Connection.StopDaemonAsync();
        ActionMessage = $"Shutdown requested ({response.RunningPluginCount} plugin(s) were running). The daemon reports completion by closing the connection.";
    }

    private void Save(DesktopSettings settings)
    {
        _settings = settings;
        _settingsService.Save(settings);
    }
}
