using System.IO;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Services;

/// <summary>
/// Supervises the connection to the daemon: connect, optionally start the daemon once, subscribe to
/// state changes, detect loss, and reconnect with bounded exponential backoff (1 s … 10 s).
/// </summary>
/// <remarks>
/// <para>
/// The supervisor is one background task owned by this object and ended by <see cref="DisposeAsync"/>.
/// It never blocks the UI thread. After the daemon was stopped explicitly from this application,
/// the supervisor waits for an explicit retry or start instead of polling.
/// </para>
/// <para>
/// The daemon is started automatically only at the first attempt and only if requested; a daemon
/// that crashes later is not restarted silently — the UI shows Disconnected and offers to start it.
/// </para>
/// </remarks>
public sealed class RuntimeConnection : IRuntimeConnection
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly IRuntimeClientFactory _factory;
    private readonly IDaemonController _daemon;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _disposing = new();
    private TaskCompletionSource _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _supervisor = Task.CompletedTask;
    private IRuntimeClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string _statusMessage = "Not connected.";
    private bool _startRequested;
    private bool _stopRequested;
    private bool _stoppedByUser;
    private int _started;

    public RuntimeConnection(RuntimeProfile profile, IRuntimeClientFactory factory, IDaemonController daemon)
    {
        Profile = profile;
        _factory = factory;
        _daemon = daemon;
    }

    public RuntimeProfile Profile { get; }

    public ConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public string StatusMessage
    {
        get
        {
            lock (_sync)
            {
                return _statusMessage;
            }
        }
    }

    public IRuntimeClient? Client => Volatile.Read(ref _client);

    /// <summary>The supervisor task (for tests and orderly disposal).</summary>
    internal Task Supervisor => _supervisor;

    public event EventHandler? StateChanged;

    public event EventHandler<long>? RuntimeStateChanged;

    public event EventHandler<LogBatchNotification>? LogBatchReceived;

    public event EventHandler<PackageOperationDto>? PackageOperationChanged;

    public void Start(bool autoStartDaemon)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _supervisor = Task.Run(() => SuperviseAsync(autoStartDaemon, _disposing.Token));
    }

    public void RetryNow()
    {
        lock (_sync)
        {
            _stoppedByUser = false;
            _retry.TrySetResult();
        }
    }

    public void RequestDaemonStart()
    {
        lock (_sync)
        {
            _startRequested = true;
            _stoppedByUser = false;
            _retry.TrySetResult();
        }
    }

    public async Task<ShutdownResponse> StopDaemonAsync(CancellationToken cancellationToken = default)
    {
        var client = Client ?? throw new InvalidOperationException("Not connected to the daemon.");
        lock (_sync)
        {
            _stopRequested = true;
        }

        SetState(ConnectionState.Stopping, "Stopping the daemon…");
        try
        {
            return await client.ShutdownAsync("requested from ModularRuntime Desktop", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                _stopRequested = false;
            }

            SetState(ConnectionState.Connected, "Connected.");
            throw;
        }
    }

    /// <summary>Disconnects this client. Never stops plugins or the daemon.</summary>
    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync().ConfigureAwait(false);
        try
        {
            await _supervisor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _disposing.Dispose();
    }

    private async Task SuperviseAsync(bool autoStartDaemon, CancellationToken cancellationToken)
    {
        var allowAutoStart = autoStartDaemon;
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting, "Connecting to the daemon…");
            IRuntimeClient client;
            try
            {
                client = await _factory.ConnectAsync(Profile, "desktop", ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsConnectFailure(exception))
            {
                bool startNow;
                lock (_sync)
                {
                    startNow = allowAutoStart || _startRequested;
                    allowAutoStart = false;
                    _startRequested = false;
                }

                if (startNow && await TryStartDaemonAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (State != ConnectionState.Error)
                {
                    SetState(ConnectionState.Disconnected, exception is IpcClientException { Code: IpcErrorCodes.ProtocolVersionMismatch } protocol
                        ? $"Incompatible daemon: {protocol.Message}"
                        : "The daemon is not running.");
                }

                await WaitForRetryAsync(Backoff(failures++), cancellationToken).ConfigureAwait(false);
                continue;
            }

            failures = 0;
            if (!await RunConnectedAsync(client, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Serves one connection until it is lost. Returns false when disposing.</summary>
    private async Task<bool> RunConnectedAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        client.NotificationReceived += OnNotification;
        Volatile.Write(ref _client, client);
        try
        {
            await client.SubscribeStateAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _stoppedByUser = false;
            }

            SetState(ConnectionState.Connected, $"Connected to daemon {client.Welcome.DaemonVersion}.");
            await client.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IpcClientException or IOException or ObjectDisposedException)
        {
            // The connection broke during setup; handled as a loss below.
        }
        finally
        {
            client.NotificationReceived -= OnNotification;
            Interlocked.CompareExchange(ref _client, null, client);
            await client.DisposeAsync().ConfigureAwait(false);
        }

        bool stopped;
        lock (_sync)
        {
            stopped = _stopRequested;
            _stopRequested = false;
            _stoppedByUser = stopped;
        }

        SetState(ConnectionState.Disconnected, stopped ? "The daemon was stopped." : "Lost the connection to the daemon. Reconnecting…");
        if (!stopped)
        {
            await WaitForRetryAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WaitForRetryAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        return !cancellationToken.IsCancellationRequested;
    }

    private async Task<bool> TryStartDaemonAsync(CancellationToken cancellationToken)
    {
        SetState(ConnectionState.Starting, "Starting the daemon…");
        try
        {
            await _daemon.EnsureRunningAsync(Profile, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetState(ConnectionState.Error, $"The daemon could not be started: {exception.Message}");
            return false;
        }
    }

    private async Task WaitForRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task retry;
        lock (_sync)
        {
            if (_retry.Task.IsCompleted)
            {
                _retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            retry = _retry.Task;
            if (_stoppedByUser)
            {
                delay = Timeout.InfiniteTimeSpan;
            }
        }

        try
        {
            await Task.WhenAny(retry, Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnNotification(object? sender, IpcNotificationEventArgs notification)
    {
        switch (notification.Name)
        {
            case IpcNotifications.StateChanged when notification.Read<StateChangedNotification>() is { } changed:
                RuntimeStateChanged?.Invoke(this, changed.StateVersion);
                break;
            case IpcNotifications.LogEntries when notification.Read<LogBatchNotification>() is { } batch:
                LogBatchReceived?.Invoke(this, batch);
                break;
            case IpcNotifications.PackageOperation when notification.Read<PackageOperationDto>() is { } operation:
                PackageOperationChanged?.Invoke(this, operation);
                break;
            case IpcNotifications.DaemonStopping:
                SetState(ConnectionState.Stopping, "The daemon is shutting down…");
                break;
        }
    }

    private void SetState(ConnectionState state, string message)
    {
        lock (_sync)
        {
            if (_state == state && _statusMessage == message)
            {
                return;
            }

            _state = state;
            _statusMessage = message;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, Math.Min(failures, 10)), MaxBackoff.TotalMilliseconds));

    private static bool IsConnectFailure(Exception exception) =>
        exception is TimeoutException or IOException or IpcClientException or UnauthorizedAccessException or OperationCanceledException;
}
