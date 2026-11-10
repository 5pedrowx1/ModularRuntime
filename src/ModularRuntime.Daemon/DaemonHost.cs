using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ModularRuntime.Contracts;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;

namespace ModularRuntime.Daemon;

/// <summary>
/// The daemon process core: the only owner of the <see cref="PluginRuntime"/> for its profile.
/// </summary>
/// <remarks>
/// <para>
/// Startup: discover plugins (optionally start them), start the IPC server, start the state notifier.
/// Shutdown (explicit request or process termination): enter <see cref="DaemonState.Stopping"/> so
/// administrative operations are rejected, notify clients, dispose the runtime (stops plugins,
/// releases resources and factories, requests unload) bounded by
/// <see cref="DaemonOptions.ShutdownTimeout"/>, then stop the IPC server and flush logs.
/// </para>
/// <para>
/// Clients never own the runtime: a client disconnecting, crashing, or closing has no effect on
/// plugins. Only <see cref="RequestShutdown"/> ends the daemon.
/// </para>
/// </remarks>
internal sealed class DaemonHost : IAsyncDisposable
{
    private readonly TaskCompletionSource<string> _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<Guid, StateSubscriber> _stateSubscribers = new();
    private Task? _notifier;
    private int _state = (int)DaemonState.Starting;
    private int _stopStarted;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public DaemonHost(DaemonOptions options, LogStore logs)
    {
        Options = options;
        Logs = logs;
        TrustStore = LoadTrustStore(options, logs);
        Store = new PackageStore(options.PluginDirectory, options.PackagesDirectory, TrustStore);
        Revocations = new RevocationGate(Store, new PackageLoadVerifier(Store));
        Runtime = new PluginRuntime(new PluginRuntimeOptions
        {
            PluginDirectory = options.PluginDirectory,
            Logger = logs,
            LoadVerifier = Revocations,
        });
        Server = new IpcServer(
            new IpcServerOptions(options.Profile.PipeName) { MaxMessageBytes = options.MaxMessageBytes },
            new DaemonRequestHandler(this));
        Packages = new PackageManager(
            this,
            Store,
            new RegistryClient(
                new RegistryOptions { Location = options.RegistryLocation },
                TrustStore,
                new DefaultRegistryTransport(_http),
                Path.Combine(options.PackagesDirectory, "registry")));
    }

    public TrustStore TrustStore { get; }

    public PackageStore Store { get; }

    /// <summary>Load gate: revocations from the signed catalog, then the installation's integrity.</summary>
    public RevocationGate Revocations { get; }

    public PackageManager Packages { get; }

    public static string Version { get; } =
        typeof(DaemonHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(DaemonHost).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public DaemonOptions Options { get; }

    public LogStore Logs { get; }

    public PluginRuntime Runtime { get; }

    public IpcServer Server { get; }

    public Guid InstanceId { get; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public TimeSpan Uptime => _uptime.Elapsed;

    public DaemonState State => (DaemonState)Volatile.Read(ref _state);

    public bool IsStopping => State == DaemonState.Stopping;

    /// <summary>Completes with the reason when shutdown has been requested.</summary>
    public Task<string> ShutdownRequested => _shutdownRequested.Task;

    /// <summary>
    /// Discovers plugins and starts serving clients.
    /// </summary>
    /// <exception cref="IOException">The pipe is already served by another process.</exception>
    public async Task StartAsync()
    {
        Logs.Write(LogLevel.Information, "Daemon", $"ModularRuntime daemon {Version} starting (pid {Environment.ProcessId}, profile '{Options.Profile.ProfileDirectory}').");
        Directory.CreateDirectory(Options.PluginDirectory);

        // Complete or roll back package operations interrupted by a crash before anything is discovered.
        foreach (var message in Store.Recover())
        {
            Logs.Write(LogLevel.Warning, "Packages", message);
        }

        Packages.LoadCachedRegistry();
        var discovery = await Runtime.DiscoverAsync(_stopping.Token).ConfigureAwait(false);
        Logs.Write(
            LogLevel.Information,
            "Daemon",
            $"Discovered {discovery.Discovery.Plugins.Count} plugin(s) in '{Options.PluginDirectory}'; registered {discovery.RegisteredCount}.");

        if (Options.AutoStartPlugins)
        {
            // Local packages without an official signature are never started automatically.
            foreach (var plugin in Runtime.Plugins.OrderBy(p => p.Descriptor.StartOrder))
            {
                if (Packages.GetTrust(plugin.Descriptor.Id) == TrustStateDto.LocalUnverified)
                {
                    Logs.Write(LogLevel.Information, "Packages", $"'{plugin.Descriptor.Id}' is a local package without an official signature; it is not started automatically.");
                    continue;
                }

                var result = await Runtime.StartAsync(plugin.Descriptor.Id, _stopping.Token).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    Packages.RecordLoadRejection(plugin.Descriptor.Id, result.Message ?? "The start failed.");
                }
            }
        }

        Server.Start();
        _notifier = Task.Run(NotifyStateChangesAsync);
        Volatile.Write(ref _state, (int)DaemonState.Running);
        WriteRuntimeInfo();
        Logs.Write(LogLevel.Information, "Daemon", $"Daemon ready on pipe '{Options.Profile.PipeName}'.");
    }

    /// <summary>Requests a graceful shutdown. Idempotent; returns immediately.</summary>
    public void RequestShutdown(string reason) => _shutdownRequested.TrySetResult(reason);

    /// <summary>Blocks until <see cref="StopAsync"/> has finished (used by process-exit handlers).</summary>
    public bool WaitForStopped(TimeSpan timeout) => _stopped.Wait(timeout);

    /// <summary>Stops the runtime and the server. Returns the process exit code.</summary>
    public async Task<int> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            await Task.Run(() => _stopped.Wait()).ConfigureAwait(false);
            return DaemonExitCodes.Success;
        }

        var exitCode = DaemonExitCodes.Success;
        try
        {
            var reason = _shutdownRequested.Task.IsCompleted ? _shutdownRequested.Task.Result : "stop";
            Volatile.Write(ref _state, (int)DaemonState.Stopping);
            Logs.Write(LogLevel.Information, "Daemon", $"Daemon shutdown started ({reason}).");
            Server.Broadcast(IpcNotifications.DaemonStopping, new DaemonStoppingNotification(reason));

            // Package operations end (or are rolled back at next start) before the runtime goes away.
            await Packages.DisposeAsync().ConfigureAwait(false);

            var dispose = Runtime.DisposeAsync().AsTask();
            if (await Task.WhenAny(dispose, Task.Delay(Options.ShutdownTimeout)).ConfigureAwait(false) != dispose)
            {
                exitCode = DaemonExitCodes.ShutdownTimedOut;
                Logs.Write(
                    LogLevel.Critical,
                    "Daemon",
                    $"Runtime shutdown did not complete within {Options.ShutdownTimeout.TotalSeconds:0}s (a plugin is ignoring cancellation). " +
                    "The daemon exits anyway; code that is still running is terminated with the process.");
            }
            else
            {
                await dispose.ConfigureAwait(false);
                Logs.Write(LogLevel.Information, "Daemon", "Runtime stopped; plugins stopped and released.");
            }

            await _stopping.CancelAsync().ConfigureAwait(false);
            if (_notifier is not null)
            {
                await _notifier.ConfigureAwait(false);
            }

            await Server.StopAsync().ConfigureAwait(false);
            DeleteRuntimeInfo();
            Logs.Write(LogLevel.Information, "Daemon", $"Daemon stopped (exit code {exitCode}).");
            return exitCode;
        }
        finally
        {
            _stopped.Set();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _http.Dispose();
    }

    private static TrustStore LoadTrustStore(DaemonOptions options, LogStore logs)
    {
        var trust = TrustStore.LoadOfficial();
        if (options.TestTrustStorePath is { } path)
        {
            var test = TrustStore.Parse(File.ReadAllBytes(path), allowTestKeys: true);
            if (test.Keys.Any(k => !k.IsTestKey))
            {
                throw new InvalidOperationException("Every key in a --test-trust-store file must be marked \"test\": true.");
            }

            trust = trust.Merge(test);
            logs.Write(
                LogLevel.Warning,
                "Packages",
                $"TEST TRUST STORE LOADED from '{path}' ({test.Keys.Count} test key(s)). Packages signed with them are shown as official (test key). Never use this in production.");
        }

        return trust;
    }

    internal StateSubscribeResponse SubscribeState(IpcConnection connection)
    {
        var subscriber = new StateSubscriber(connection) { LastSentVersion = Runtime.StateVersion };
        _stateSubscribers[subscriber.Id] = subscriber;
        return new StateSubscribeResponse(subscriber.Id, subscriber.LastSentVersion);
    }

    internal bool UnsubscribeState(IpcConnection connection, Guid subscriptionId) =>
        _stateSubscribers.TryGetValue(subscriptionId, out var subscriber) &&
        ReferenceEquals(subscriber.Connection, connection) &&
        _stateSubscribers.TryRemove(subscriptionId, out _);

    internal void RemoveStateSubscribers(IpcConnection connection)
    {
        foreach (var (id, subscriber) in _stateSubscribers)
        {
            if (ReferenceEquals(subscriber.Connection, connection))
            {
                _stateSubscribers.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Pushes <c>state.changed</c> to subscribed clients when the runtime state version advances.
    /// Changes are coalesced (100 ms); a client whose queue was full is retried every second until
    /// it has received the latest version, so no client stays silently stale.
    /// </summary>
    private async Task NotifyStateChangesAsync()
    {
        var token = _stopping.Token;
        var known = Runtime.StateVersion;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var behind = _stateSubscribers.Values.Any(s => s.LastSentVersion < known);
                var wait = Runtime.WaitForStateChangeAsync(known, token);
                if (behind)
                {
                    await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(1), token)).ConfigureAwait(false);
                }
                else
                {
                    await wait.ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                known = Runtime.StateVersion;
                foreach (var subscriber in _stateSubscribers.Values)
                {
                    if (subscriber.LastSentVersion < known &&
                        subscriber.Connection.TrySendNotification(IpcNotifications.StateChanged, new StateChangedNotification(known)))
                    {
                        subscriber.LastSentVersion = known;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private string RuntimeInfoPath => Path.Combine(Options.Profile.RuntimeDirectory, "daemon.json");

    private void WriteRuntimeInfo()
    {
        try
        {
            Directory.CreateDirectory(Options.Profile.RuntimeDirectory);
            File.WriteAllText(RuntimeInfoPath, JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId,
                instanceId = InstanceId,
                runtimeId = Runtime.RuntimeId,
                version = Version,
                startedAt = StartedAt,
                pipeName = Options.Profile.PipeName,
            }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Logs.Write(LogLevel.Warning, "Daemon", $"Could not write runtime information: {exception.Message}");
        }
    }

    private void DeleteRuntimeInfo()
    {
        try
        {
            File.Delete(RuntimeInfoPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed class StateSubscriber(IpcConnection connection)
    {
        public Guid Id { get; } = Guid.NewGuid();

        public IpcConnection Connection { get; } = connection;

        public long LastSentVersion { get; set; }
    }
}
