using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Desktop.Services;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>Shell: navigation, connection indicator, and shared connection actions.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IRuntimeConnection _connection;
    private PageViewModel _selectedPage;
    private ConnectionState _connectionState;
    private string _connectionMessage;

    public MainViewModel(
        IRuntimeConnection connection,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        SettingsService settings,
        IThemeService theme)
    {
        _connection = connection;
        Dashboard = new DashboardViewModel(connection, dispatcher);
        Operations = new PackageOperationsTracker(connection, dispatcher);
        Store = new StoreViewModel(connection, dispatcher, dialogs, Operations);
        Plugins = new PluginsViewModel(connection, dispatcher, dialogs, Operations);
        Events = new EventsViewModel(connection, dispatcher);
        Services = new ServicesViewModel(connection, dispatcher);
        Extensions = new ExtensionsViewModel(connection, dispatcher);
        Logs = new LogsViewModel(connection, dispatcher, dialogs, settings.Load().LogsAutoScroll);
        Settings = new SettingsViewModel(connection, dispatcher, dialogs, settings, theme);
        Pages = [Dashboard, Store, Plugins, Events, Services, Extensions, Logs, Settings];

        _selectedPage = Dashboard;
        Dashboard.IsActive = true;
        _connectionState = connection.State;
        _connectionMessage = connection.StatusMessage;

        RetryCommand = new RelayCommand(connection.RetryNow);
        StartDaemonCommand = new RelayCommand(connection.RequestDaemonStart, () => ConnectionState is ConnectionState.Disconnected or ConnectionState.Error);
        connection.StateChanged += (_, _) => dispatcher.Post(UpdateConnection);
    }

    public DashboardViewModel Dashboard { get; }

    /// <summary>Discover and install plugins (official registry, local ZIP, updates).</summary>
    public StoreViewModel Store { get; }

    /// <summary>Package operations shared by the Store and Plugins pages.</summary>
    public PackageOperationsTracker Operations { get; }

    /// <summary>Manage installed plugins (start, stop, restart, verify, remove).</summary>
    public PluginsViewModel Plugins { get; }

    public EventsViewModel Events { get; }

    public ServicesViewModel Services { get; }

    public ExtensionsViewModel Extensions { get; }

    public LogsViewModel Logs { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public RelayCommand RetryCommand { get; }

    public RelayCommand StartDaemonCommand { get; }

    public PageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedPage))
            {
                return;
            }

            _selectedPage.IsActive = false;
            SetProperty(ref _selectedPage, value);
            value.IsActive = true;
        }
    }

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set => SetProperty(ref _connectionState, value);
    }

    public string ConnectionMessage
    {
        get => _connectionMessage;
        private set => SetProperty(ref _connectionMessage, value);
    }

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    /// <summary>
    /// Called when the window closes: disconnects this client only. The daemon and its plugins keep
    /// running; nothing here sends a shutdown request.
    /// </summary>
    public async Task CloseAsync()
    {
        Logs.Detach();
        await _connection.DisposeAsync();
    }

    private void UpdateConnection()
    {
        ConnectionState = _connection.State;
        ConnectionMessage = _connection.StatusMessage;
        OnPropertyChanged(nameof(IsConnected));
        StartDaemonCommand.RaiseCanExecuteChanged();
    }
}
