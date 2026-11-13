using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine;

/// <summary>
/// Central plugin lifecycle engine and extensibility platform of one runtime instance.
/// </summary>
/// <remarks>
/// <para>
/// The engine does not know how plugins are discovered or loaded; callers register an
/// <see cref="IPluginInstanceFactory"/> for each validated plugin. Each engine owns its own
/// event bus, service registry, extension registry, and ownership scopes; nothing is shared
/// between engine instances.
/// </para>
/// <para>
/// Teardown order for a plugin instance (stop, failed start, and disposal):
/// </para>
/// <list type="number">
/// <item>Close the instance's ownership scope to new resources and incoming invocations, and
/// cancel the tokens of its in-flight handlers.</item>
/// <item>Cancel the instance's <see cref="IPluginContext.StoppingToken"/>.</item>
/// <item>Drain in-flight handlers (bounded by <see cref="RuntimeLimits.DrainTimeout"/>).</item>
/// <item>Call <see cref="IPlugin.ShutdownAsync"/> (rollback shutdown after a failed start). The
/// plugin may still publish events and call services while shutting down.</item>
/// <item>Release every resource the instance still owns (subscriptions, services, extension
/// points with their contributions, contributions, tracked objects), dropping all handler
/// references.</item>
/// <item>Detach the instance and dispose its lifetime token source.</item>
/// <item>Release the factory, which requests unload of the plugin's load context.</item>
/// </list>
/// </remarks>
public sealed class PluginEngine : IAsyncDisposable
{
    private readonly RuntimeCore _core;
    private readonly IRuntimeLogger _logger;
    private readonly OwnershipScope _hostScope;
    private readonly RuntimeScopeFacade _host;
    private readonly object _registryLock = new();
    private readonly Dictionary<PluginId, PluginSlot> _slots = [];
    private readonly List<PluginSlot> _slotsInOrder = [];
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _shutdownStarted;

    /// <param name="logger">Runtime logger; defaults to a no-op logger.</param>
    /// <param name="limits">Platform limits; defaults to <see cref="RuntimeLimits"/>.</param>
    /// <param name="sharedTypePolicy">
    /// Decides which types may cross typed boundaries (typed services, streams). Defaults to
    /// <see cref="DefaultSharedTypePolicy"/>; the loader supplies a policy that knows its shared
    /// contract contexts.
    /// </param>
    public PluginEngine(IRuntimeLogger? logger = null, RuntimeLimits? limits = null, ISharedTypePolicy? sharedTypePolicy = null)
    {
        limits ??= new RuntimeLimits();
        limits.Validate();
        _logger = logger ?? NullRuntimeLogger.Instance;
        _core = new RuntimeCore(_logger, limits, sharedTypePolicy ?? DefaultSharedTypePolicy.Instance);
        _hostScope = _core.CreateHostScope();
        _host = new RuntimeScopeFacade(_core, _hostScope);
    }

    /// <summary>Unique identity of this runtime instance; part of every <see cref="ResourceOwner"/>.</summary>
    public Guid RuntimeId => _core.RuntimeId;

    public RuntimeLimits Limits => _core.Limits;

    internal SharedTypeAnalyzer SharedTypeAnalyzer => _core.SharedTypes.Analyzer;

    /// <summary>
    /// The host's scope: lets the hosting application publish and subscribe to events, provide
    /// and consume services, declare extension points, and contribute. Host resources are
    /// released when the engine is disposed, after all plugins have stopped.
    /// </summary>
    public IRuntimeScope Host => _host;

    /// <summary>
    /// Monotonic version of the observable state: plugin registrations and states, and every
    /// owned resource (subscriptions, services, extension points, contributions, tracked objects).
    /// Monitoring clients compare versions instead of registering callbacks.
    /// </summary>
    public long StateVersion => _core.StateVersion;

    /// <summary>
    /// Completes with the new version when <see cref="StateVersion"/> differs from
    /// <paramref name="knownVersion"/>. Changes are coalesced: one completion may cover several.
    /// </summary>
    public Task<long> WaitForStateChangeAsync(long knownVersion, CancellationToken cancellationToken = default) =>
        _core.WaitForStateChangeAsync(knownVersion, cancellationToken);

    /// <summary>True once disposal has begun. New registrations and starts are rejected.</summary>
    public bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;

    public PluginOperationResult Register(PluginDescriptor descriptor, IPluginInstanceFactory factory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(factory);

        if (IsShuttingDown)
        {
            return EngineShuttingDown(descriptor.Id, "register");
        }

        if (!PluginApiVersion.IsCompatible(descriptor.ApiVersion))
        {
            var message =
                $"Plugin '{descriptor.Id}' requires API version {descriptor.ApiVersion} but this runtime " +
                $"supports API version {PluginApiVersion.Supported}.";
            Log(LogLevel.Warning, message, descriptor.Id);
            return new PluginOperationResult(
                descriptor.Id,
                "register",
                PluginOperationStatus.Rejected,
                null,
                PluginErrorCodes.ApiVersionIncompatible,
                message);
        }

        PluginOperationResult result;
        lock (_registryLock)
        {
            if (IsShuttingDown)
            {
                return EngineShuttingDown(descriptor.Id, "register");
            }

            if (_slots.TryGetValue(descriptor.Id, out var existing))
            {
                var message = $"Plugin '{descriptor.Id}' is already registered.";
                Log(LogLevel.Warning, message, descriptor.Id);
                return new PluginOperationResult(
                    descriptor.Id,
                    "register",
                    PluginOperationStatus.Rejected,
                    existing.State,
                    PluginErrorCodes.DuplicatePluginId,
                    message);
            }

            var slot = new PluginSlot(descriptor, factory, _core.NotifyStateChanged);
            _slots.Add(descriptor.Id, slot);
            _slotsInOrder.Add(slot);
            result = new PluginOperationResult(
                descriptor.Id,
                "register",
                PluginOperationStatus.Succeeded,
                PluginState.Discovered,
                Message: $"Plugin '{descriptor.Id}' registered.");
        }

        _core.NotifyStateChanged();
        Log(LogLevel.Information, $"Plugin '{descriptor.Id}' registered.", descriptor.Id);
        return result;
    }

    /// <summary>
    /// Moves a discovered plugin to <see cref="PluginState.Validated"/> after loader validation.
    /// </summary>
    public PluginOperationResult MarkValidated(PluginId pluginId)
    {
        if (IsShuttingDown)
        {
            return EngineShuttingDown(pluginId, "validate");
        }

        if (!TryGetSlot(pluginId, out var slot))
        {
            return PluginNotFound(pluginId, "validate");
        }

        if (!slot.TryMarkValidated())
        {
            return InvalidState(pluginId, "validate", slot.State);
        }

        Log(LogLevel.Information, $"Plugin '{pluginId}' validated.", pluginId);
        return new PluginOperationResult(
            pluginId,
            "validate",
            PluginOperationStatus.Succeeded,
            PluginState.Validated,
            Message: $"Plugin '{pluginId}' validated.");
    }

    /// <summary>
    /// Removes a plugin that is not active (<see cref="PluginState.Discovered"/>,
    /// <see cref="PluginState.Validated"/>, <see cref="PluginState.Stopped"/>, or
    /// <see cref="PluginState.Failed"/>) and releases its factory, so the same ID can be registered
    /// again, for example after its installation was updated or removed.
    /// </summary>
    /// <remarks>
    /// Serialized with the plugin's other lifecycle operations through its gate. A start or stop
    /// that looked the plugin up before removal finds it removed after acquiring the gate and
    /// reports <c>plugin_not_found</c>. Unloading the plugin's load context remains cooperative:
    /// callers that must replace files wait for it separately.
    /// </remarks>
    public async Task<PluginOperationResult> UnregisterAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        if (IsShuttingDown)
        {
            return EngineShuttingDown(pluginId, "unregister");
        }

        if (!TryGetSlot(pluginId, out var slot))
        {
            return PluginNotFound(pluginId, "unregister");
        }

        if (slot.InstanceId is { } currentInstance && InvocationFrames.Contains(RuntimeId, currentInstance))
        {
            var message = $"Plugin '{pluginId}' cannot be unregistered from code running on its own behalf.";
            Log(LogLevel.Warning, message, pluginId);
            return new PluginOperationResult(
                pluginId,
                "unregister",
                PluginOperationStatus.Rejected,
                slot.State,
                PluginErrorCodes.ReentrantLifecycleOperation,
                message);
        }

        try
        {
            await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return CanceledBeforeStart(pluginId, "unregister", slot.State, exception);
        }

        PluginErrorInfo? releaseError;
        try
        {
            if (IsShuttingDown)
            {
                return EngineShuttingDown(pluginId, "unregister");
            }

            if (slot.IsRemoved)
            {
                return PluginNotFound(pluginId, "unregister");
            }

            if (slot.State is not (PluginState.Discovered or PluginState.Validated or PluginState.Stopped or PluginState.Failed))
            {
                return InvalidState(pluginId, "unregister", slot.State);
            }

            lock (_registryLock)
            {
                _slots.Remove(pluginId);
                _slotsInOrder.Remove(slot);
            }

            slot.MarkRemoved();
            releaseError = await TryReleaseFactoryAsync(slot, "unregister").ConfigureAwait(false);
        }
        finally
        {
            slot.Gate.Release();
        }

        _core.NotifyStateChanged();
        Log(LogLevel.Information, $"Plugin '{pluginId}' unregistered.", pluginId);
        return new PluginOperationResult(
            pluginId,
            "unregister",
            PluginOperationStatus.Succeeded,
            null,
            releaseError?.Code,
            releaseError is null ? $"Plugin '{pluginId}' unregistered." : releaseError.Message,
            releaseError?.Diagnostic);
    }

    public async Task<PluginOperationResult> StartAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        if (IsShuttingDown)
        {
            return EngineShuttingDown(pluginId, "start");
        }

        if (!TryGetSlot(pluginId, out var slot))
        {
            return PluginNotFound(pluginId, "start");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CanceledBeforeStart(pluginId, "start", slot.State);
        }

        try
        {
            await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return CanceledBeforeStart(pluginId, "start", slot.State, exception);
        }

        try
        {
            if (IsShuttingDown)
            {
                return EngineShuttingDown(pluginId, "start");
            }

            if (slot.IsRemoved)
            {
                return PluginNotFound(pluginId, "start");
            }

            if (!slot.TryTransition(PluginState.Validated, PluginState.Loaded))
            {
                return InvalidState(pluginId, "start", slot.State);
            }

            Log(LogLevel.Information, $"Plugin '{pluginId}' is loading.", pluginId);

            IPlugin? instance = null;
            var attached = false;
            StartFailure failure;

            try
            {
                instance = await slot.Factory.CreateAsync(slot.Descriptor, cancellationToken)
                    .ConfigureAwait(false);

                if (instance is null)
                {
                    throw new InvalidOperationException("The plugin factory returned a null instance.");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        $"Starting plugin '{pluginId}' was canceled after the instance was created.",
                        cancellationToken);
                }

                var instanceId = Guid.NewGuid();
                var lifetime = new CancellationTokenSource();
                var scope = _core.CreatePluginScope(pluginId, instanceId);
                try
                {
                    slot.AttachInstance(instance, instanceId, lifetime, scope);
                }
                catch
                {
                    lifetime.Dispose();
                    throw;
                }

                attached = true;

                if (IsShuttingDown)
                {
                    // Disposal began after the shutdown check above. Disposal cancels the
                    // lifetimes of initializing instances, but this instance may have been
                    // attached after that pass. Cancel here so initialization observes it.
                    LogCancellationCallbackFailure(slot, pluginId);
                }

                var pluginLogger = new ScopedPluginLogger(_logger, pluginId, instanceId);
                var context = new EnginePluginContext(_core, scope, slot.Descriptor, instanceId, pluginLogger, lifetime.Token);

                using var initialization = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);

                await InvokeLifecycleAsync(
                        instanceId,
                        () => instance.InitializeAsync(context, initialization.Token))
                    .ConfigureAwait(false);

                slot.MarkRunning();
                Log(
                    LogLevel.Information,
                    $"Plugin '{pluginId}' started (instance {instanceId}).",
                    pluginId,
                    instanceId);

                return new PluginOperationResult(
                    pluginId,
                    "start",
                    PluginOperationStatus.Succeeded,
                    PluginState.Running,
                    Message: $"Plugin '{pluginId}' started.");
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                // Deliberately synchronous: an await inside this catch would hoist the exception
                // (possibly a plugin type that pins the plugin's load context) into this method's
                // state machine. Only the text snapshot survives the catch.
                failure = CaptureStartFailure(slot, attached, exception, cancellationToken);

                // Debug builds hoist every local into the state machine, which is cleared only
                // after callers' synchronous continuations have run. Drop the reference now.
                exception = null!;
            }

            instance = null;
            return await CompleteStartFailureAsync(slot, failure).ConfigureAwait(false);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task<PluginOperationResult> StopAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSlot(pluginId, out var slot))
        {
            return PluginNotFound(pluginId, "stop");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CanceledBeforeStart(pluginId, "stop", slot.State);
        }

        return await StopCoreAsync(slot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts all validated plugins in ascending <see cref="PluginDescriptor.StartOrder"/>, then
    /// registration order. A failure in one plugin does not prevent later plugins from starting.
    /// </summary>
    public async Task<IReadOnlyList<PluginOperationResult>> StartAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsShuttingDown)
        {
            return [];
        }

        var slots = GetSlotsInStartOrder();
        var results = new List<PluginOperationResult>(slots.Length);

        foreach (var slot in slots)
        {
            if (cancellationToken.IsCancellationRequested || IsShuttingDown)
            {
                break;
            }

            results.Add(await StartAsync(slot.Descriptor.Id, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Stops all running plugins in reverse start order. A failure in one plugin does not prevent
    /// the remaining plugins from being stopped.
    /// </summary>
    public async Task<IReadOnlyList<PluginOperationResult>> StopAllAsync(
        CancellationToken cancellationToken = default)
    {
        var slots = GetSlotsInStartOrder();
        var results = new List<PluginOperationResult>(slots.Length);

        for (var index = slots.Length - 1; index >= 0; index--)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            results.Add(
                await StopCoreAsync(slots[index], cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Returns a stable snapshot ordered by registration.</summary>
    public IReadOnlyList<PluginRuntimeInfo> GetPlugins()
    {
        var slots = GetSlotsSnapshot();
        var result = new PluginRuntimeInfo[slots.Length];
        for (var index = 0; index < slots.Length; index++)
        {
            result[index] = slots[index].GetInfo();
        }

        return result;
    }

    public PluginRuntimeInfo? GetPlugin(PluginId pluginId) =>
        TryGetSlot(pluginId, out var slot) ? slot.GetInfo() : null;

    /// <summary>
    /// Resources currently owned by the running (or initializing) instance of a plugin. Empty when
    /// the plugin is unknown or has no attached instance.
    /// </summary>
    public IReadOnlyList<ResourceInfo> GetResources(PluginId pluginId) =>
        TryGetSlot(pluginId, out var slot) && slot.Scope is { } scope ? scope.GetResources() : [];

    /// <summary>
    /// Stops all plugins, releases all factories and host resources, and disposes the platform.
    /// Idempotent; concurrent callers await the same shutdown.
    /// </summary>
    /// <exception cref="PluginRuntimeException">
    /// <c>reentrant_lifecycle_operation</c>: called from code running inside one of this runtime's
    /// handlers or plugin lifecycle methods, where waiting for shutdown would wait for the caller.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        if (InvocationFrames.ContainsRuntime(RuntimeId))
        {
            throw new PluginRuntimeException(
                PluginErrorCodes.ReentrantLifecycleOperation,
                "The runtime cannot be disposed from inside one of its own handlers or plugin lifecycle methods. " +
                "Signal the host to dispose it outside the callback.");
        }

        Task disposeTask;
        lock (_disposeLock)
        {
            disposeTask = _disposeTask ??= DisposeCoreAsync();
        }

        await disposeTask.ConfigureAwait(false);
    }

    private async Task<PluginOperationResult> StopCoreAsync(
        PluginSlot slot,
        CancellationToken cancellationToken,
        bool quietInvalidState = false)
    {
        var pluginId = slot.Descriptor.Id;

        // Checked before waiting for the gate: a stop requested from the plugin's own flow would
        // otherwise wait for itself (on the gate during initialization, or on the drain).
        if (slot.InstanceId is { } currentInstance &&
            slot.State is PluginState.Loaded or PluginState.Running &&
            InvocationFrames.Contains(RuntimeId, currentInstance))
        {
            var message =
                $"Plugin '{pluginId}' cannot be stopped from code running on its own behalf (one of its handlers, " +
                "lifecycle methods, or a callback chain they started). Request the stop outside that flow.";
            Log(LogLevel.Warning, message, pluginId);
            return new PluginOperationResult(
                pluginId,
                "stop",
                PluginOperationStatus.Rejected,
                slot.State,
                PluginErrorCodes.ReentrantLifecycleOperation,
                message);
        }

        try
        {
            await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return CanceledBeforeStart(pluginId, "stop", slot.State, exception);
        }

        try
        {
            if (slot.IsRemoved)
            {
                return PluginNotFound(pluginId, "stop");
            }

            if (!slot.TryTransition(PluginState.Running, PluginState.Stopping))
            {
                return InvalidState(pluginId, "stop", slot.State, log: !quietInvalidState);
            }

            Log(LogLevel.Information, $"Plugin '{pluginId}' is stopping.", pluginId);
            var instanceId = slot.InstanceId;

            var outcome = await TeardownInstanceAsync(slot, rollback: false, "stop", cancellationToken)
                .ConfigureAwait(false);

            if (outcome.ShutdownFailure is { } diagnostic)
            {
                var canceled = outcome.ShutdownCanceled;
                var code = canceled
                    ? PluginErrorCodes.OperationCanceled
                    : PluginErrorCodes.ShutdownFailed;
                var message = canceled
                    ? $"Shutdown of plugin '{pluginId}' was canceled."
                    : $"Plugin '{pluginId}' failed during shutdown: {diagnostic.Message}";
                var error = PluginErrorInfo.From(code, "stop", message, diagnostic);
                slot.MarkFailed(error);
                Log(canceled ? LogLevel.Warning : LogLevel.Error, message, pluginId, instanceId, diagnostic);

                return new PluginOperationResult(
                    pluginId,
                    "stop",
                    canceled ? PluginOperationStatus.Canceled : PluginOperationStatus.Failed,
                    slot.State,
                    code,
                    message,
                    diagnostic);
            }

            if (outcome.CleanupFailures.Count > 0)
            {
                var first = outcome.CleanupFailures[0];
                var message =
                    $"Plugin '{pluginId}' stopped, but {outcome.CleanupFailures.Count} owned resource(s) failed to release: " +
                    first.Diagnostic.Message;
                var error = PluginErrorInfo.From(PluginErrorCodes.ResourceCleanupFailed, "stop", message, first.Diagnostic);
                slot.MarkFailed(error);
                return new PluginOperationResult(
                    pluginId,
                    "stop",
                    PluginOperationStatus.Failed,
                    slot.State,
                    PluginErrorCodes.ResourceCleanupFailed,
                    message,
                    first.Diagnostic);
            }

            if (outcome.ReleaseError is { } releaseError)
            {
                slot.MarkFailed(releaseError);
                return new PluginOperationResult(
                    pluginId,
                    "stop",
                    PluginOperationStatus.Failed,
                    slot.State,
                    releaseError.Code,
                    releaseError.Message,
                    releaseError.Diagnostic);
            }

            slot.MarkStopped();
            Log(LogLevel.Information, $"Plugin '{pluginId}' stopped.", pluginId);

            return new PluginOperationResult(
                pluginId,
                "stop",
                PluginOperationStatus.Succeeded,
                PluginState.Stopped,
                Message: $"Plugin '{pluginId}' stopped.");
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// Shared teardown for stop and failed start. See the class remarks for the order.
    /// Must be called while holding the plugin's gate.
    /// </summary>
    private async Task<TeardownOutcome> TeardownInstanceAsync(
        PluginSlot slot,
        bool rollback,
        string operation,
        CancellationToken shutdownToken)
    {
        var pluginId = slot.Descriptor.Id;
        var scope = slot.Scope;
        var instanceId = slot.InstanceId;

        // 1. No new resources or incoming invocations; cancel in-flight handlers.
        if (scope?.BeginClose() is { } scopeCallbackFailure)
        {
            Log(
                LogLevel.Warning,
                $"One or more cancellation callbacks of in-flight handlers of plugin '{pluginId}' failed: {scopeCallbackFailure.Message}",
                pluginId,
                instanceId,
                scopeCallbackFailure);
        }

        // 2. Cooperative stop signal for the plugin's own background work.
        LogCancellationCallbackFailure(slot, pluginId);

        // 3. Drain in-flight handlers.
        if (scope is not null && !await scope.DrainAsync(_core.Limits.DrainTimeout).ConfigureAwait(false))
        {
            Log(
                LogLevel.Warning,
                $"[{PluginErrorCodes.DrainTimeout}] {scope.InFlightCount} handler(s) of plugin '{pluginId}' were still running " +
                $"{_core.Limits.DrainTimeout.TotalSeconds:0.#}s after cancellation. Teardown continues; the handlers keep " +
                "running in-process and may delay unload of the plugin's load context.",
                pluginId,
                instanceId);
        }

        // 4. Lifecycle shutdown.
        PluginDiagnostic? shutdownFailure = null;
        var shutdownCanceled = false;
        var instance = slot.GetInstance();
        if (instance is null)
        {
            if (!rollback)
            {
                shutdownFailure = PluginDiagnostic.FromException(
                    new InvalidOperationException($"Plugin '{pluginId}' was running without an attached instance."));
            }
        }
        else
        {
            try
            {
                await InvokeLifecycleAsync(instanceId ?? Guid.Empty, () => instance.ShutdownAsync(shutdownToken))
                    .ConfigureAwait(false);
                if (rollback)
                {
                    Log(LogLevel.Debug, $"Rollback shutdown completed for plugin '{pluginId}'.", pluginId);
                }
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                // Convert to text immediately; the exception may be a plugin type.
                shutdownCanceled = exception is OperationCanceledException && shutdownToken.IsCancellationRequested;
                shutdownFailure = PluginDiagnostic.FromException(exception);
                exception = null!;
                if (rollback)
                {
                    Log(
                        LogLevel.Warning,
                        $"Rollback shutdown failed for plugin '{pluginId}': {shutdownFailure.Message}",
                        pluginId,
                        diagnostic: shutdownFailure);
                }
            }
        }

        instance = null;

        // 5. Release everything the instance still owns.
        IReadOnlyList<ResourceReleaseFailure> cleanupFailures = [];
        if (scope is not null)
        {
            var ownedCount = scope.GetResources().Count;
            cleanupFailures = await scope.CloseAsync().ConfigureAwait(false);
            if (ownedCount > 0)
            {
                Log(
                    LogLevel.Information,
                    $"Released {ownedCount} resource(s) owned by plugin '{pluginId}'.",
                    pluginId,
                    instanceId);
            }

            foreach (var failure in cleanupFailures)
            {
                Log(
                    LogLevel.Warning,
                    $"[{PluginErrorCodes.ResourceCleanupFailed}] Releasing {failure.Resource?.Kind.ToString() ?? "a resource"} " +
                    $"'{failure.Resource?.Name}' of plugin '{pluginId}' failed: {failure.Diagnostic.Message}",
                    pluginId,
                    instanceId,
                    failure.Diagnostic);
            }
        }

        // 6. Drop runtime-held references to the instance and its lifetime.
        slot.DetachInstance();

        // Plugin code (ShutdownAsync, handlers, tracked disposables) may have completed
        // asynchronously, leaving this method running on top of plugin frames. Continue on a
        // fresh stack so that callers of stop/start never run on a stack holding plugin frames.
        await new ThreadPoolHop();

        // 7. Release the factory (requests unload of the load context).
        var releaseError = await TryReleaseFactoryAsync(slot, operation).ConfigureAwait(false);

        return new TeardownOutcome(
            rollback ? null : shutdownFailure,
            shutdownCanceled,
            cleanupFailures,
            releaseError);
    }

    /// <summary>Classifies a start failure and marks the plugin failed. Converts the exception to text.</summary>
    private StartFailure CaptureStartFailure(
        PluginSlot slot,
        bool attached,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var pluginId = slot.Descriptor.Id;
        var isCancellation = exception is OperationCanceledException;
        var callerCanceled = isCancellation && cancellationToken.IsCancellationRequested;
        var shutdownCanceled = isCancellation && !callerCanceled && IsShuttingDown;
        var phase = attached ? "initialization" : "loading";
        var code = callerCanceled || shutdownCanceled
            ? PluginErrorCodes.OperationCanceled
            : attached
                ? PluginErrorCodes.InitializationFailed
                : PluginErrorCodes.FactoryFailed;
        var diagnostic = PluginDiagnostic.FromException(exception);
        var message = callerCanceled
            ? $"Starting plugin '{pluginId}' was canceled during {phase}."
            : shutdownCanceled
                ? $"Starting plugin '{pluginId}' was canceled during {phase} because the engine is shutting down."
                : $"Plugin '{pluginId}' failed during {phase}: {diagnostic.Message}";

        slot.MarkFailed(PluginErrorInfo.From(code, "start", message, diagnostic));
        return new StartFailure(attached, callerCanceled || shutdownCanceled, code, message, diagnostic, slot.InstanceId);
    }

    private async Task<PluginOperationResult> CompleteStartFailureAsync(PluginSlot slot, StartFailure failure)
    {
        var pluginId = slot.Descriptor.Id;

        PluginErrorInfo? releaseError;
        if (failure.Attached)
        {
            // Rollback: drain handlers, ShutdownAsync, release every resource created during
            // initialization, detach, release the factory.
            var outcome = await TeardownInstanceAsync(slot, rollback: true, "start", CancellationToken.None)
                .ConfigureAwait(false);
            releaseError = outcome.ReleaseError;
        }
        else
        {
            // An instance may have been created (then canceled) on this stack; see ThreadPoolHop.
            await new ThreadPoolHop();
            releaseError = await TryReleaseFactoryAsync(slot, "start").ConfigureAwait(false);
        }

        if (releaseError is not null)
        {
            Log(
                LogLevel.Warning,
                $"The original start failure for plugin '{pluginId}' was: {failure.Diagnostic.Message}",
                pluginId);
        }

        Log(
            failure.Canceled ? LogLevel.Warning : LogLevel.Error,
            failure.Message,
            pluginId,
            failure.InstanceId,
            failure.Diagnostic);

        return new PluginOperationResult(
            pluginId,
            "start",
            failure.Canceled ? PluginOperationStatus.Canceled : PluginOperationStatus.Failed,
            slot.State,
            failure.Code,
            failure.Message,
            failure.Diagnostic);
    }

    /// <summary>Runs plugin lifecycle code inside an invocation frame of the instance.</summary>
    private async Task InvokeLifecycleAsync(Guid instanceId, Func<Task> lifecycleCall)
    {
        InvocationFrames.Enter(RuntimeId, instanceId);
        await lifecycleCall().ConfigureAwait(false);
    }

    private void LogCancellationCallbackFailure(PluginSlot slot, PluginId pluginId)
    {
        var cancellationError = slot.CancelLifetime();
        if (cancellationError is null)
        {
            return;
        }

        Log(
            LogLevel.Warning,
            cancellationError.Message,
            pluginId,
            slot.InstanceId,
            cancellationError.Diagnostic);
    }

    private async Task<PluginErrorInfo?> TryReleaseFactoryAsync(PluginSlot slot, string operation)
    {
        try
        {
            await slot.ReleaseFactoryAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
        {
            var diagnostic = PluginDiagnostic.FromException(exception);
            var message =
                $"Releasing resources for plugin '{slot.Descriptor.Id}' failed during {operation}: " +
                diagnostic.Message;
            var error = PluginErrorInfo.From(
                PluginErrorCodes.ReleaseFailed,
                operation,
                message,
                diagnostic);
            Log(
                LogLevel.Error,
                message,
                slot.Descriptor.Id,
                diagnostic: error.Diagnostic);
            return error;
        }
    }

    private async Task DisposeCoreAsync()
    {
        PluginSlot[] slots;
        lock (_registryLock)
        {
            Volatile.Write(ref _shutdownStarted, 1);
        }

        slots = GetSlotsInStartOrder();
        Log(LogLevel.Information, "Plugin engine shutdown started.");

        // Instances that are still initializing hold the per-plugin gate. Cancel their
        // lifetimes (linked into the initialization token) so cooperative plugins abort
        // initialization instead of delaying shutdown. Running instances are left alone
        // here and are cancelled by the ordered stop below.
        foreach (var slot in slots)
        {
            var cancellationError = slot.CancelLifetimeIfInitializing();
            if (cancellationError is not null)
            {
                Log(
                    LogLevel.Warning,
                    cancellationError.Message,
                    slot.Descriptor.Id,
                    slot.InstanceId,
                    cancellationError.Diagnostic);
            }
        }

        for (var index = slots.Length - 1; index >= 0; index--)
        {
            PluginOperationResult result;
            try
            {
                result = await StopCoreAsync(
                        slots[index],
                        CancellationToken.None,
                        quietInvalidState: true)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                // Defensive: an unexpected engine failure for one plugin must not prevent
                // the remaining plugins from being stopped and released.
                var diagnostic = PluginDiagnostic.FromException(exception);
                Log(
                    LogLevel.Error,
                    $"Engine shutdown failed unexpectedly for plugin '{slots[index].Descriptor.Id}': " +
                    diagnostic.Message,
                    slots[index].Descriptor.Id,
                    diagnostic: diagnostic);
                continue;
            }

            if (!result.IsSuccess &&
                result.ErrorCode != PluginErrorCodes.InvalidState)
            {
                Log(
                    LogLevel.Warning,
                    $"Engine shutdown could not stop plugin '{result.PluginId}': {result.Message}",
                    result.PluginId);
            }
        }

        foreach (var slot in slots)
        {
            await TryReleaseFactoryAsync(slot, "dispose").ConfigureAwait(false);
        }

        // Host resources are released last so that host subscribers and host-owned extension
        // points stay usable while plugins shut down.
        await CloseHostScopeAsync().ConfigureAwait(false);
        _core.MarkDisposed();

        Log(LogLevel.Information, "Plugin engine shutdown completed.");
    }

    private async Task CloseHostScopeAsync()
    {
        if (_hostScope.BeginClose() is { } callbackFailure)
        {
            Log(LogLevel.Warning, $"A cancellation callback of an in-flight host handler failed: {callbackFailure.Message}", diagnostic: callbackFailure);
        }

        if (!await _hostScope.DrainAsync(_core.Limits.DrainTimeout).ConfigureAwait(false))
        {
            Log(LogLevel.Warning, $"[{PluginErrorCodes.DrainTimeout}] Host handlers were still running after cancellation; shutdown continues.");
        }

        foreach (var failure in await _hostScope.CloseAsync().ConfigureAwait(false))
        {
            Log(
                LogLevel.Warning,
                $"[{PluginErrorCodes.ResourceCleanupFailed}] Releasing host resource '{failure.Resource?.Name}' failed: {failure.Diagnostic.Message}",
                diagnostic: failure.Diagnostic);
        }
    }

    private bool TryGetSlot(PluginId pluginId, out PluginSlot slot)
    {
        lock (_registryLock)
        {
            return _slots.TryGetValue(pluginId, out slot!);
        }
    }

    private PluginSlot[] GetSlotsSnapshot()
    {
        lock (_registryLock)
        {
            return [.. _slotsInOrder];
        }
    }

    /// <summary>Ascending start order; ties keep registration order (stable sort).</summary>
    private PluginSlot[] GetSlotsInStartOrder() =>
        [.. GetSlotsSnapshot().OrderBy(slot => slot.Descriptor.StartOrder)];

    private PluginOperationResult PluginNotFound(PluginId pluginId, string operation)
    {
        var message = $"Plugin '{pluginId}' is not registered.";
        Log(LogLevel.Warning, message, pluginId);
        return new PluginOperationResult(
            pluginId,
            operation,
            PluginOperationStatus.Rejected,
            null,
            PluginErrorCodes.PluginNotFound,
            message);
    }

    private PluginOperationResult InvalidState(
        PluginId pluginId,
        string operation,
        PluginState state,
        bool log = true)
    {
        var message = $"Plugin '{pluginId}' cannot perform '{operation}' from state '{state}'.";
        if (log)
        {
            Log(LogLevel.Warning, message, pluginId);
        }

        return new PluginOperationResult(
            pluginId,
            operation,
            PluginOperationStatus.Rejected,
            state,
            PluginErrorCodes.InvalidState,
            message);
    }

    private PluginOperationResult CanceledBeforeStart(
        PluginId pluginId,
        string operation,
        PluginState state,
        Exception? exception = null)
    {
        var message = $"Operation '{operation}' for plugin '{pluginId}' was canceled before it ran.";
        Log(LogLevel.Debug, message, pluginId);
        return new PluginOperationResult(
            pluginId,
            operation,
            PluginOperationStatus.Canceled,
            state,
            PluginErrorCodes.OperationCanceled,
            message,
            PluginDiagnostic.FromExceptionOrNull(exception));
    }

    private PluginOperationResult EngineShuttingDown(PluginId pluginId, string operation)
    {
        var message = $"The engine is shutting down; operation '{operation}' was rejected.";
        Log(LogLevel.Warning, message, pluginId);
        return new PluginOperationResult(
            pluginId,
            operation,
            PluginOperationStatus.Rejected,
            GetPlugin(pluginId)?.State,
            PluginErrorCodes.EngineDisposed,
            message);
    }

    private void Log(
        LogLevel level,
        string message,
        PluginId? pluginId = null,
        Guid? instanceId = null,
        PluginDiagnostic? diagnostic = null) =>
        SafeLogger.Log(
            _logger,
            new LogEntry(
                level,
                message,
                Category: "Engine",
                PluginId: pluginId,
                InstanceId: instanceId,
                Diagnostic: diagnostic));

    private sealed record StartFailure(
        bool Attached,
        bool Canceled,
        string Code,
        string Message,
        PluginDiagnostic Diagnostic,
        Guid? InstanceId);

    private sealed record TeardownOutcome(
        PluginDiagnostic? ShutdownFailure,
        bool ShutdownCanceled,
        IReadOnlyList<ResourceReleaseFailure> CleanupFailures,
        PluginErrorInfo? ReleaseError);
}
