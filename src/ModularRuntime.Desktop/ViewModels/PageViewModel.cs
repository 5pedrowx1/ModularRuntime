using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>What a page currently shows.</summary>
public enum PageState
{
    Loading,
    Empty,
    Ready,
    Error,
    Disconnected,
}

/// <summary>
/// Base class of navigable pages: tracks the connection, refreshes from the daemon when it becomes
/// active, when the connection is (re)established, and when the daemon reports a state change.
/// </summary>
/// <remarks>
/// Refreshes are coalesced: a change notification during a refresh schedules exactly one more.
/// While disconnected, data is cleared so stale information is never shown as current.
/// All members run on the UI thread; background events are marshaled through <see cref="IUiDispatcher"/>.
/// </remarks>
public abstract class PageViewModel : ObservableObject
{
    private PageState _state = PageState.Disconnected;
    private string? _errorMessage;
    private bool _isActive;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _hasLoaded;

    protected PageViewModel(string title, string glyph, IRuntimeConnection connection, IUiDispatcher dispatcher)
    {
        Title = title;
        Glyph = glyph;
        Connection = connection;
        Dispatcher = dispatcher;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => connection.State == ConnectionState.Connected);
        connection.StateChanged += (_, _) => dispatcher.Post(OnConnectionStateChanged);
        connection.RuntimeStateChanged += (_, _) => dispatcher.Post(OnRuntimeStateChanged);
    }

    public string Title { get; }

    /// <summary>Segoe Fluent Icons / MDL2 glyph for the navigation item.</summary>
    public string Glyph { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public PageState State
    {
        get => _state;
        protected set => SetProperty(ref _state, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value)
            {
                RequestRefresh();
            }
        }
    }

    /// <summary>Whether the page refreshes on daemon state changes even while not visible.</summary>
    protected virtual bool RefreshWhenInactive => false;

    protected IRuntimeConnection Connection { get; }

    protected IUiDispatcher Dispatcher { get; }

    /// <summary>Requests a refresh; coalesces with one in progress.</summary>
    public void RequestRefresh()
    {
        if (Connection.State != ConnectionState.Connected)
        {
            return;
        }

        if (_refreshing)
        {
            _refreshPending = true;
            return;
        }

        RefreshCommand.Execute(null);
    }

    public async Task RefreshAsync()
    {
        var client = Connection.Client;
        if (client is null)
        {
            ShowDisconnected();
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                if (!_hasLoaded)
                {
                    State = PageState.Loading;
                }

                try
                {
                    var hasData = await LoadAsync(client, CancellationToken.None);
                    _hasLoaded = true;
                    ErrorMessage = null;
                    State = hasData ? PageState.Ready : PageState.Empty;
                }
                catch (IpcConnectionLostException)
                {
                    ShowDisconnected();
                    return;
                }
                catch (IpcClientException exception)
                {
                    ErrorMessage = $"[{exception.Code}] {exception.Message}";
                    State = PageState.Error;
                }
            }
            while (_refreshPending && Connection.Client is not null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Loads the page from the daemon. Returns whether there is anything to show.</summary>
    protected abstract Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken);

    /// <summary>Clears data that must not be shown while disconnected.</summary>
    protected abstract void Clear();

    protected virtual void OnConnected()
    {
    }

    protected virtual void OnDisconnected()
    {
    }

    private void OnConnectionStateChanged()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        if (Connection.State == ConnectionState.Connected)
        {
            OnConnected();
            if (IsActive || RefreshWhenInactive)
            {
                RequestRefresh();
            }
        }
        else if (Connection.State is ConnectionState.Disconnected or ConnectionState.Error or ConnectionState.Connecting or ConnectionState.Starting)
        {
            ShowDisconnected();
        }
    }

    private void OnRuntimeStateChanged()
    {
        if (IsActive || RefreshWhenInactive)
        {
            RequestRefresh();
        }
    }

    private void ShowDisconnected()
    {
        if (State == PageState.Disconnected && !_hasLoaded)
        {
            return;
        }

        _hasLoaded = false;
        Clear();
        OnDisconnected();
        State = PageState.Disconnected;
    }
}
