using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Services;

/// <summary>Posts work to the UI thread. Background events (IPC reader, timers) never touch view models directly.</summary>
public interface IUiDispatcher
{
    void Post(Action action);

    /// <summary>Runs <paramref name="tick"/> on the UI thread at the interval until disposed.</summary>
    IDisposable StartTimer(TimeSpan interval, Action tick);
}

/// <summary>Creates daemon connections (abstracted for tests).</summary>
public interface IRuntimeClientFactory
{
    Task<IRuntimeClient> ConnectAsync(RuntimeProfile profile, string clientName, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Starts the daemon process (abstracted for tests).</summary>
public interface IDaemonController
{
    Task<DaemonStartOutcome> EnsureRunningAsync(RuntimeProfile profile, CancellationToken cancellationToken);
}

/// <summary>User interaction services (abstracted for tests).</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, string confirmText);

    /// <summary>Lets the user pick a local plugin package (ZIP); <see langword="null"/> when canceled.</summary>
    string? PickPackageFile();

    /// <summary>Shows what the daemon found in an uploaded package and asks whether to install it.</summary>
    PackageInstallDecision ShowPackagePreview(PackagePreviewDto preview);

    void CopyToClipboard(string text);

    void OpenFolder(string path);
}

/// <param name="Install">The user chose to install.</param>
/// <param name="AcceptedUnverified">The user acknowledged that the package has no official signature.</param>
/// <param name="StopIfRunning">The user agreed to stop the running version it replaces.</param>
public sealed record PackageInstallDecision(bool Install, bool AcceptedUnverified, bool StopIfRunning)
{
    public static PackageInstallDecision Cancel { get; } = new(false, false, false);
}

/// <summary>Connection state shown by the connection indicator.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Starting,
    Connected,
    Stopping,
    Error,
}

/// <summary>
/// The desktop application's single connection to its profile's daemon.
/// </summary>
/// <remarks>
/// Events are raised on background threads; subscribers marshal to the UI with <see cref="IUiDispatcher"/>.
/// Disposing the connection disconnects the client only: it never stops plugins or the daemon.
/// </remarks>
public interface IRuntimeConnection : IAsyncDisposable
{
    RuntimeProfile Profile { get; }

    ConnectionState State { get; }

    string StatusMessage { get; }

    /// <summary>The connected client, or <see langword="null"/> while not connected.</summary>
    IRuntimeClient? Client { get; }

    event EventHandler? StateChanged;

    /// <summary>The daemon's runtime state version advanced; snapshots should be refreshed.</summary>
    event EventHandler<long>? RuntimeStateChanged;

    event EventHandler<LogBatchNotification>? LogBatchReceived;

    /// <summary>A package operation (install, update, removal) changed stage or progress.</summary>
    event EventHandler<PackageOperationDto>? PackageOperationChanged;

    /// <summary>Starts the connection supervisor. With <paramref name="autoStartDaemon"/>, starts the daemon if none is running at first attempt.</summary>
    void Start(bool autoStartDaemon);

    /// <summary>Retries immediately instead of waiting for the backoff delay.</summary>
    void RetryNow();

    /// <summary>Explicitly starts the daemon if it is not running, then connects.</summary>
    void RequestDaemonStart();

    /// <summary>Explicitly asks the daemon to shut down (stops all plugins). Never called implicitly.</summary>
    Task<ShutdownResponse> StopDaemonAsync(CancellationToken cancellationToken = default);
}

internal sealed class RuntimeClientFactory : IRuntimeClientFactory
{
    public async Task<IRuntimeClient> ConnectAsync(RuntimeProfile profile, string clientName, TimeSpan timeout, CancellationToken cancellationToken) =>
        await RuntimeClient.ConnectAsync(profile, clientName, timeout, cancellationToken).ConfigureAwait(false);
}

internal sealed class DaemonController : IDaemonController
{
    public Task<DaemonStartOutcome> EnsureRunningAsync(RuntimeProfile profile, CancellationToken cancellationToken) =>
        DaemonLauncher.EnsureRunningAsync(profile, cancellationToken: cancellationToken);
}
