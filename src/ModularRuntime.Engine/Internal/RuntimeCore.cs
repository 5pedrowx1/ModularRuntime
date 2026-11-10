using ModularRuntime.Contracts;
using ModularRuntime.Engine.Events;
using ModularRuntime.Engine.Extensions;
using ModularRuntime.Engine.Ownership;
using ModularRuntime.Engine.Services;

namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Per-runtime state shared by the lifecycle engine and the extensibility components.
/// There is no process-wide state: two engines never share subscriptions, services,
/// extension points, or identifiers.
/// </summary>
internal sealed class RuntimeCore
{
    private int _disposed;
    private long _stateVersion;
    private TaskCompletionSource _stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RuntimeCore(IRuntimeLogger logger, RuntimeLimits limits)
    {
        Logger = logger;
        Limits = limits;
        RuntimeId = Guid.NewGuid();
        Events = new EventBus(this);
        Services = new ServiceRegistry(this);
        Extensions = new ExtensionRegistry(this);
    }

    public Guid RuntimeId { get; }

    public IRuntimeLogger Logger { get; }

    public RuntimeLimits Limits { get; }

    public EventBus Events { get; }

    public ServiceRegistry Services { get; }

    public ExtensionRegistry Extensions { get; }

    /// <summary>Set after every scope has been closed during engine disposal.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void MarkDisposed() => Volatile.Write(ref _disposed, 1);

    public OwnershipScope CreateHostScope() =>
        new(new ResourceOwner(RuntimeId, ResourceOwnerKind.Host, null, null), Guid.NewGuid(), NotifyStateChanged);

    public OwnershipScope CreatePluginScope(PluginId pluginId, Guid instanceId) =>
        new(new ResourceOwner(RuntimeId, ResourceOwnerKind.Plugin, pluginId, instanceId), instanceId, NotifyStateChanged);

    /// <summary>Monotonic version of the observable runtime state.</summary>
    public long StateVersion => Interlocked.Read(ref _stateVersion);

    /// <summary>
    /// Advances <see cref="StateVersion"/> and wakes waiters. Never runs foreign code: waiters
    /// resume asynchronously, so this is safe to call from any thread after releasing a lock.
    /// </summary>
    public void NotifyStateChanged()
    {
        Interlocked.Increment(ref _stateVersion);
        var previous = Interlocked.Exchange(
            ref _stateChanged,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    /// <summary>Completes with the new version once <see cref="StateVersion"/> differs from <paramref name="knownVersion"/>.</summary>
    public async Task<long> WaitForStateChangeAsync(long knownVersion, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Read the signal before the version: a change made after the version read completes
            // the captured signal, so no change can be missed.
            var signal = Volatile.Read(ref _stateChanged);
            var current = StateVersion;
            if (current != knownVersion)
            {
                return current;
            }

            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public bool IsPayloadWithinLimit(JsonPayload payload) => payload.ByteCount <= Limits.MaxPayloadBytes;

    public string PayloadTooLargeMessage(JsonPayload payload) =>
        $"Payload of {payload.ByteCount} bytes exceeds the limit of {Limits.MaxPayloadBytes} bytes.";

    /// <summary>Throws when a registration cannot be accepted for <paramref name="scope"/>.</summary>
    public void EnsureCanRegister(OwnershipScope scope)
    {
        if (IsDisposed)
        {
            throw new PluginRuntimeException(PluginErrorCodes.EngineDisposed, "The runtime has been disposed.");
        }

        if (!scope.IsOpen)
        {
            throw ScopeClosed(scope);
        }
    }

    public static PluginRuntimeException ScopeClosed(OwnershipScope scope) =>
        new(
            PluginErrorCodes.ScopeClosed,
            $"Owner '{scope.Owner}' is shutting down or stopped; it cannot create new resources.");

    public void Log(LogLevel level, string category, string message, ResourceOwner? owner = null, PluginDiagnostic? diagnostic = null) =>
        SafeLogger.Log(
            Logger,
            new LogEntry(
                level,
                message,
                Category: category,
                PluginId: owner?.PluginId,
                InstanceId: owner?.InstanceId,
                Diagnostic: diagnostic));
}
