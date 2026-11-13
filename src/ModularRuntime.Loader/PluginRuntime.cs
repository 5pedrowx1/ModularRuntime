using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader.Internal;

namespace ModularRuntime.Loader;

/// <summary>
/// Public composition of the shared loader and engine. Both the standalone host and embedded
/// applications use this class instead of reimplementing lifecycle or loading logic.
/// </summary>
/// <remarks>
/// Instances are independent: each runtime has its own engine, event bus, service registry, and
/// extension registry. Discovery should normally complete before plugins are started;
/// lifecycle operations are serialized per plugin by <see cref="PluginEngine"/>.
/// </remarks>
public sealed class PluginRuntime : IAsyncDisposable
{
    private readonly PluginRuntimeOptions _options;
    private readonly PluginLoader _loader;
    private readonly PluginEngine _engine;
    private readonly object _registrationsLock = new();
    private readonly Dictionary<PluginId, Registration> _registrations = [];

    private sealed record Registration(PluginCandidate Candidate, AssemblyPluginInstanceFactory Factory);

    public PluginRuntime(PluginRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.PluginDirectory))
        {
            throw new ArgumentException(
                "PluginRuntimeOptions.PluginDirectory must not be empty.",
                nameof(options));
        }

        _options = options;
        _loader = new PluginLoader(options.Logger);
        _engine = new PluginEngine(options.Logger, options.Limits, _loader.CreateSharedTypePolicy());
    }

    public string PluginDirectory => _options.PluginDirectory;

    /// <summary>Unique identity of this runtime instance.</summary>
    public Guid RuntimeId => _engine.RuntimeId;

    /// <summary>
    /// The host's scope on the extensibility platform: events, services, extension points, and
    /// owned resources of the application embedding this runtime.
    /// </summary>
    public IRuntimeScope Host => _engine.Host;

    public IReadOnlyList<PluginRuntimeInfo> Plugins => _engine.GetPlugins();

    /// <summary>Monotonic version of the observable runtime state; see <see cref="PluginEngine.StateVersion"/>.</summary>
    public long StateVersion => _engine.StateVersion;

    /// <summary>Completes when <see cref="StateVersion"/> differs from <paramref name="knownVersion"/>.</summary>
    public Task<long> WaitForStateChangeAsync(long knownVersion, CancellationToken cancellationToken = default) =>
        _engine.WaitForStateChangeAsync(knownVersion, cancellationToken);

    /// <summary>Resources owned by the current instance of a plugin.</summary>
    public IReadOnlyList<ResourceInfo> GetResources(PluginId pluginId) => _engine.GetResources(pluginId);

    /// <summary>Contract assemblies currently loaded into shared contexts, with the plugins holding them.</summary>
    public IReadOnlyList<SharedContractInfo> GetSharedContracts() => _loader.GetSharedContracts();

    /// <summary>
    /// Discovers, validates, registers, and marks plugins as validated. Registration failures
    /// are returned instead of thrown; discovery failures are reported by the loader.
    /// </summary>
    public async Task<PluginRuntimeDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var discovery = await _loader.DiscoverAsync(_options.PluginDirectory, cancellationToken)
            .ConfigureAwait(false);

        var registrationFailures = new List<PluginOperationResult>();
        var registeredCount = 0;

        foreach (var candidate in discovery.Plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await RegisterCandidateAsync(candidate).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                registeredCount++;
            }
            else
            {
                registrationFailures.Add(result);
            }
        }

        return new PluginRuntimeDiscoveryResult(discovery, registrationFailures, registeredCount);
    }

    /// <summary>
    /// Reads, validates, registers, and marks as validated one installation directory (for example
    /// a plugin that was just installed). Nothing is loaded until the plugin is started.
    /// </summary>
    public async Task<PluginRuntimeDiscoveryResult> RegisterInstallationAsync(
        string installationDirectory,
        CancellationToken cancellationToken = default)
    {
        var discovery = await _loader.ReadInstallationAsync(installationDirectory, cancellationToken).ConfigureAwait(false);
        var failures = new List<PluginOperationResult>();
        var registered = 0;
        foreach (var candidate in discovery.Plugins)
        {
            var result = await RegisterCandidateAsync(candidate).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                registered++;
            }
            else
            {
                failures.Add(result);
            }
        }

        return new PluginRuntimeDiscoveryResult(discovery, failures, registered);
    }

    /// <summary>
    /// Removes an inactive plugin (validated, stopped, or failed) from the runtime so its
    /// installation can be replaced or deleted; see <see cref="PluginEngine.UnregisterAsync"/>.
    /// </summary>
    public async Task<PluginOperationResult> UnregisterAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        var result = await _engine.UnregisterAsync(pluginId, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            lock (_registrationsLock)
            {
                // Every retired load context is tracked (weakly) until it is observed collected;
                // a plugin restarted several times can have several.
                if (GetFactoryReference(pluginId) is { } reference)
                {
                    _retiredLoadContexts.Track(pluginId, reference);
                }

                _registrations.Remove(pluginId);
            }
        }

        return result;
    }

    /// <summary>
    /// Waits until every retired load context of a plugin (from unregistration and from previous
    /// runs of a restarted plugin) has been collected, without forcing garbage collections.
    /// See <see cref="WaitForUnloadAsync(PluginId, TimeSpan, PluginUnloadWait, CancellationToken)"/>.
    /// </summary>
    public Task<bool> WaitForUnloadAsync(PluginId pluginId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitForUnloadAsync(pluginId, timeout, PluginUnloadWait.Observe, cancellationToken);

    /// <summary>
    /// Waits until every retired load context of a plugin has been collected. Returns
    /// <see langword="false"/> when one is still alive at the timeout (the plugin left references
    /// behind; its files may stay locked). On timeout or cancellation the live contexts remain
    /// tracked, so the wait can be repeated. Only weak references are kept.
    /// </summary>
    public Task<bool> WaitForUnloadAsync(PluginId pluginId, TimeSpan timeout, PluginUnloadWait mode, CancellationToken cancellationToken = default) =>
        _retiredLoadContexts.WaitAsync(pluginId, timeout, mode, _options.TimeProvider, cancellationToken);

    /// <summary>Number of retired load contexts of the plugin that have not been collected yet.</summary>
    public int GetPendingUnloadCount(PluginId pluginId) => _retiredLoadContexts.GetPendingCount(pluginId);

    /// <summary>The validated installation currently registered for a plugin, if any.</summary>
    public PluginCandidate? GetInstallation(PluginId pluginId)
    {
        lock (_registrationsLock)
        {
            return _registrations.TryGetValue(pluginId, out var registration) ? registration.Candidate : null;
        }
    }

    private readonly Internal.RetiredLoadContextTracker _retiredLoadContexts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PluginId, SemaphoreSlim> _renewalGates = new();

    private WeakReference? GetFactoryReference(PluginId pluginId) =>
        _registrations.TryGetValue(pluginId, out var registration) ? registration.Factory.CreateLoadContextWeakReference() : null;

    private async Task<PluginOperationResult> RegisterCandidateAsync(PluginCandidate candidate)
    {
        var factory = _loader.CreateInstanceFactory(candidate, _options.LoadVerifier);
        var registration = _engine.Register(candidate.Descriptor, factory);
        if (!registration.IsSuccess)
        {
            await TryReleaseFactoryAsync(factory, candidate.Descriptor.Id).ConfigureAwait(false);
            return registration;
        }

        lock (_registrationsLock)
        {
            _registrations[candidate.Descriptor.Id] = new Registration(candidate, factory);
        }

        var validation = _engine.MarkValidated(candidate.Descriptor.Id);
        if (!validation.IsSuccess)
        {
            // Registered but not usable: take it out again so the ID can be registered later.
            await _engine.UnregisterAsync(candidate.Descriptor.Id).ConfigureAwait(false);
            lock (_registrationsLock)
            {
                _registrations.Remove(candidate.Descriptor.Id);
            }

            return validation;
        }

        return validation;
    }

    public PluginRuntimeInfo? GetPlugin(PluginId pluginId) => _engine.GetPlugin(pluginId);

    /// <summary>
    /// Starts a plugin. A plugin that is <see cref="PluginState.Stopped"/> or
    /// <see cref="PluginState.Failed"/> is started again with a <b>new</b> registration: its
    /// installation is read and validated again, the old registration (whose factory and load
    /// context are released) is removed, and a new factory creates a new load context, so nothing
    /// of the previous instance is reused. The load verifier runs again before the new load.
    /// </summary>
    /// <remarks>
    /// Stopped and Failed stay terminal for a registration in <see cref="PluginEngine"/>; renewal is
    /// serialized per plugin, so concurrent starts produce at most one new instance. If the
    /// installation can no longer be read, the stopped registration is kept and the reason returned.
    /// </remarks>
    public async Task<PluginOperationResult> StartAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        // A stopped or failed plugin is renewed (unregistered, then registered again) under its gate.
        // While a renewal runs, the plugin is briefly unregistered (or not yet validated): a concurrent
        // start that sees that must wait for the renewal instead of asking the engine, which would
        // answer plugin_not_found for a plugin that exists. Only an ID with no renewal gate is
        // genuinely unknown (no gate is created for arbitrary IDs).
        var state = _engine.GetPlugin(pluginId)?.State;
        SemaphoreSlim? gate;
        if (state is PluginState.Stopped or PluginState.Failed or PluginState.Discovered)
        {
            gate = _renewalGates.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        }
        else if (state is null && _renewalGates.TryGetValue(pluginId, out var renewing))
        {
            gate = renewing;
        }
        else
        {
            return await _engine.StartAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-checked under the gate: another caller may already have renewed and started it.
            if (_engine.GetPlugin(pluginId)?.State is PluginState.Stopped or PluginState.Failed)
            {
                var renewal = await RenewRegistrationAsync(pluginId, cancellationToken).ConfigureAwait(false);
                if (!renewal.IsSuccess)
                {
                    return renewal;
                }
            }

            return await _engine.StartAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops a running plugin and starts it again with a new instance and load context
    /// (see <see cref="StartAsync"/>). A plugin that is not running is just started.
    /// </summary>
    public async Task<PluginOperationResult> RestartAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        var info = _engine.GetPlugin(pluginId);
        if (info is null)
        {
            return (await _engine.StartAsync(pluginId, cancellationToken).ConfigureAwait(false)) with { Operation = "restart" };
        }

        if (info.State == PluginState.Running)
        {
            var stop = await _engine.StopAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (!stop.IsSuccess && _engine.GetPlugin(pluginId)?.State is not (PluginState.Stopped or PluginState.Failed))
            {
                return stop with { Operation = "restart" };
            }
        }

        return (await StartAsync(pluginId, cancellationToken).ConfigureAwait(false)) with { Operation = "restart" };
    }

    private async Task<PluginOperationResult> RenewRegistrationAsync(PluginId pluginId, CancellationToken cancellationToken)
    {
        var state = _engine.GetPlugin(pluginId)?.State;
        var previous = GetInstallation(pluginId);
        if (previous is null)
        {
            return new PluginOperationResult(
                pluginId,
                "start",
                PluginOperationStatus.Rejected,
                state,
                PluginErrorCodes.InvalidState,
                $"Plugin '{pluginId}' is {state} and was not registered from an installation directory, so it cannot be started again.");
        }

        // Read and validate the installation again before touching the current registration.
        var read = await _loader.ReadInstallationAsync(previous.PluginDirectory, cancellationToken).ConfigureAwait(false);
        if (read.Plugins.Count != 1 || read.Plugins[0].Descriptor.Id != pluginId)
        {
            var reason = read.Errors.Count > 0 ? read.Errors[0].Message : $"'{previous.PluginDirectory}' no longer contains plugin '{pluginId}'.";
            SafeLogger.Log(_options.Logger, new LogEntry(LogLevel.Warning, $"Plugin '{pluginId}' cannot be started again: {reason}", Category: "Runtime", PluginId: pluginId));
            return new PluginOperationResult(
                pluginId,
                "start",
                PluginOperationStatus.Failed,
                state,
                PluginErrorCodes.FactoryFailed,
                $"Plugin '{pluginId}' cannot be started again because its installation is no longer valid: {reason}");
        }

        var unregister = await UnregisterAsync(pluginId, cancellationToken).ConfigureAwait(false);
        if (!unregister.IsSuccess)
        {
            return unregister with { Operation = "start" };
        }

        var registration = await RegisterCandidateAsync(read.Plugins[0]).ConfigureAwait(false);
        if (registration.IsSuccess)
        {
            SafeLogger.Log(
                _options.Logger,
                new LogEntry(LogLevel.Information, $"Plugin '{pluginId}' registered again for a new start (new instance and load context).", Category: "Runtime", PluginId: pluginId));
        }

        return registration with { Operation = "start" };
    }

    public Task<PluginOperationResult> StopAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default) =>
        _engine.StopAsync(pluginId, cancellationToken);

    /// <summary>
    /// Starts every validated or stopped plugin in ascending start order (registration order for
    /// ties). Stopped plugins get a new instance (see <see cref="StartAsync"/>); failed plugins are
    /// only retried by an explicit <see cref="StartAsync"/>. A failure does not stop the others.
    /// </summary>
    public async Task<IReadOnlyList<PluginOperationResult>> StartAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<PluginOperationResult>();
        foreach (var plugin in _engine.GetPlugins().OrderBy(p => p.Descriptor.StartOrder))
        {
            results.Add(plugin.State == PluginState.Stopped
                ? await StartAsync(plugin.Descriptor.Id, cancellationToken).ConfigureAwait(false)
                : await _engine.StartAsync(plugin.Descriptor.Id, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public Task<IReadOnlyList<PluginOperationResult>> StopAllAsync(
        CancellationToken cancellationToken = default) =>
        _engine.StopAllAsync(cancellationToken);

    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    private async Task TryReleaseFactoryAsync(
        AssemblyPluginInstanceFactory factory,
        PluginId pluginId)
    {
        try
        {
            await factory.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!LoaderExceptionClassifier.IsFatal(exception))
        {
            var diagnostic = PluginDiagnostic.FromException(exception);
            SafeLogger.Log(
                _options.Logger,
                new LogEntry(
                    LogLevel.Warning,
                    $"Releasing the loader factory for plugin '{pluginId}' failed: {diagnostic.Message}",
                    Category: "Runtime",
                    PluginId: pluginId,
                    Diagnostic: diagnostic));
        }
    }
}
