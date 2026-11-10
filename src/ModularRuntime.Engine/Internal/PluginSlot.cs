using ModularRuntime.Contracts;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Internal;

internal sealed class PluginSlot
{
    private readonly object _sync = new();
    private PluginState _state = PluginState.Discovered;
    private IPlugin? _instance;
    private CancellationTokenSource? _lifetime;
    private OwnershipScope? _scope;
    private Guid? _instanceId;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _stoppedAt;
    private PluginErrorInfo? _lastError;
    private int _factoryReleaseStarted;
    private int _removed;
    private int _lifetimeCancelCount;
    private bool _lifetimeReleaseRequested;
    private readonly Action? _onChanged;

    /// <param name="onChanged">
    /// Invoked after every observable state change, outside the slot lock. Used by the engine to
    /// advance its state version; it must not call back into the slot.
    /// </param>
    public PluginSlot(PluginDescriptor descriptor, IPluginInstanceFactory factory, Action? onChanged = null)
    {
        Descriptor = descriptor;
        Factory = factory;
        _onChanged = onChanged;
    }

    public PluginDescriptor Descriptor { get; }

    public IPluginInstanceFactory Factory { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// True once the slot was unregistered. Set while holding <see cref="Gate"/>; lifecycle
    /// operations that captured the slot before removal check it after acquiring the gate.
    /// </summary>
    public bool IsRemoved => Volatile.Read(ref _removed) != 0;

    public void MarkRemoved() => Volatile.Write(ref _removed, 1);

    public PluginState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    private bool TryMarkValidatedCore()
    {
        lock (_sync)
        {
            if (_state != PluginState.Discovered ||
                !PluginLifecycle.CanTransition(_state, PluginState.Validated))
            {
                return false;
            }

            _state = PluginState.Validated;
            return true;
        }
    }

    private bool TryTransitionCore(PluginState expected, PluginState next)
    {
        lock (_sync)
        {
            if (_state != expected || !PluginLifecycle.CanTransition(_state, next))
            {
                return false;
            }

            _state = next;
            return true;
        }
    }

    public void AttachInstance(
        IPlugin instance,
        Guid instanceId,
        CancellationTokenSource lifetime,
        OwnershipScope scope)
    {
        AttachInstanceCore(instance, instanceId, lifetime, scope);
        _onChanged?.Invoke();
    }

    private void AttachInstanceCore(
        IPlugin instance,
        Guid instanceId,
        CancellationTokenSource lifetime,
        OwnershipScope scope)
    {
        lock (_sync)
        {
            if (_state != PluginState.Loaded)
            {
                throw new InvalidOperationException(
                    $"Cannot attach an instance to plugin '{Descriptor.Id}' while it is in state '{_state}'.");
            }

            if (_instance is not null)
            {
                throw new InvalidOperationException(
                    $"Plugin '{Descriptor.Id}' already has an attached instance.");
            }

            _instance = instance;
            _instanceId = instanceId;
            _lifetime = lifetime;
            _scope = scope;
        }
    }

    /// <summary>Ownership scope of the attached instance, or <see langword="null"/>.</summary>
    public OwnershipScope? Scope
    {
        get
        {
            lock (_sync)
            {
                return _scope;
            }
        }
    }

    /// <summary>Instance ID of the current or last instance.</summary>
    public Guid? InstanceId
    {
        get
        {
            lock (_sync)
            {
                return _instanceId;
            }
        }
    }

    public IPlugin? GetInstance()
    {
        lock (_sync)
        {
            return _instance;
        }
    }

    /// <summary>
    /// Cancels the attached lifetime without invoking cancellation callbacks while the
    /// slot state lock is held.
    /// </summary>
    /// <returns>
    /// A textual diagnostic when a cancellation callback threw a recoverable exception;
    /// otherwise <see langword="null"/>. Cleanup of the lifetime always completes.
    /// </returns>
    public PluginErrorInfo? CancelLifetime()
    {
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            lifetime = _lifetime;
            if (lifetime is null)
            {
                return null;
            }

            // Keep the source alive until every concurrent Cancel call has completed.
            // DetachInstance defers Dispose while this count is non-zero.
            _lifetimeCancelCount++;
        }

        Exception? callbackFailure = null;
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The lifetime was released by a concurrent cleanup before it could be
            // signaled. This is an expected cleanup race, not a plugin failure.
        }
        catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
        {
            // CancellationTokenSource.Cancel invokes registered callbacks synchronously
            // and aggregates their exceptions. Report the failure, but do not let it
            // prevent shutdown or lifetime disposal.
            callbackFailure = exception;
        }
        finally
        {
            var disposeLifetime = false;
            lock (_sync)
            {
                _lifetimeCancelCount--;
                if (_lifetimeReleaseRequested && _lifetimeCancelCount == 0)
                {
                    _lifetimeReleaseRequested = false;
                    disposeLifetime = true;
                }
            }

            if (disposeLifetime)
            {
                lifetime.Dispose();
            }
        }

        if (callbackFailure is null)
        {
            return null;
        }

        // Convert to text immediately; the callback exception may be a plugin type.
        var diagnostic = PluginDiagnostic.FromException(callbackFailure);
        return PluginErrorInfo.From(
            PluginErrorCodes.CancellationCallbackFailed,
            "cancel",
            $"One or more cancellation callbacks for plugin '{Descriptor.Id}' failed: " +
            diagnostic.Message,
            diagnostic);
    }

    /// <summary>
    /// Cancels the lifetime only while an instance is being initialized
    /// (<see cref="PluginState.Loaded"/>). Used by engine disposal so that a plugin that
    /// observes its initialization token does not block shutdown indefinitely.
    /// Running instances are not affected; they are cancelled by the ordered stop.
    /// </summary>
    public PluginErrorInfo? CancelLifetimeIfInitializing()
    {
        lock (_sync)
        {
            if (_state != PluginState.Loaded || _lifetime is null)
            {
                return null;
            }
        }

        // The state may change between the check and the cancel. Cancelling a lifetime
        // that has just moved to Running is harmless during disposal: the ordered stop
        // that follows cancels it anyway before ShutdownAsync.
        return CancelLifetime();
    }

    private IPlugin? DetachInstanceCore()
    {
        CancellationTokenSource? lifetime;
        IPlugin? instance;
        var disposeLifetime = false;
        lock (_sync)
        {
            instance = _instance;
            lifetime = _lifetime;
            _instance = null;
            _lifetime = null;
            _scope = null;

            if (lifetime is not null)
            {
                if (_lifetimeCancelCount == 0)
                {
                    disposeLifetime = true;
                }
                else
                {
                    // A cancellation callback is still running. The last Cancel call
                    // disposes the source after all callbacks have returned.
                    _lifetimeReleaseRequested = true;
                }
            }
        }

        if (disposeLifetime)
        {
            lifetime!.Dispose();
        }

        return instance;
    }

    private void MarkRunningCore()
    {
        lock (_sync)
        {
            if (_state != PluginState.Loaded ||
                !PluginLifecycle.CanTransition(_state, PluginState.Running))
            {
                throw new InvalidOperationException(
                    $"Cannot mark plugin '{Descriptor.Id}' as running from state '{_state}'.");
            }

            _state = PluginState.Running;
            _startedAt = DateTimeOffset.UtcNow;
            _lastError = null;
        }
    }

    private void MarkStoppedCore()
    {
        lock (_sync)
        {
            if (_state != PluginState.Stopping ||
                !PluginLifecycle.CanTransition(_state, PluginState.Stopped))
            {
                throw new InvalidOperationException(
                    $"Cannot mark plugin '{Descriptor.Id}' as stopped from state '{_state}'.");
            }

            _state = PluginState.Stopped;
            _stoppedAt = DateTimeOffset.UtcNow;
            _lastError = null;
        }
    }

    private void MarkFailedCore(PluginErrorInfo error)
    {
        lock (_sync)
        {
            if (_state == PluginState.Failed)
            {
                _lastError = error;
                return;
            }

            if (!PluginLifecycle.CanTransition(_state, PluginState.Failed))
            {
                throw new InvalidOperationException(
                    $"Cannot mark plugin '{Descriptor.Id}' as failed from state '{_state}'.");
            }

            _state = PluginState.Failed;
            _lastError = error;
            _stoppedAt = DateTimeOffset.UtcNow;
        }
    }

    public PluginRuntimeInfo GetInfo()
    {
        lock (_sync)
        {
            return new PluginRuntimeInfo(
                Descriptor,
                _state,
                _instanceId,
                _startedAt,
                _stoppedAt,
                _lastError);
        }
    }

    public async ValueTask<bool> ReleaseFactoryAsync()
    {
        if (Interlocked.CompareExchange(ref _factoryReleaseStarted, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            await Factory.ReleaseAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Allow a later cleanup pass (for example engine disposal) to retry a
            // release that failed. The factory implementation remains responsible
            // for being idempotent if it partially completed.
            Volatile.Write(ref _factoryReleaseStarted, 0);
            throw;
        }
    }

    // Public mutators: change state under the lock, then notify outside it.

    public bool TryMarkValidated()
    {
        var changed = TryMarkValidatedCore();
        if (changed)
        {
            _onChanged?.Invoke();
        }

        return changed;
    }

    public bool TryTransition(PluginState expected, PluginState next)
    {
        var changed = TryTransitionCore(expected, next);
        if (changed)
        {
            _onChanged?.Invoke();
        }

        return changed;
    }

    public IPlugin? DetachInstance()
    {
        var instance = DetachInstanceCore();
        _onChanged?.Invoke();
        return instance;
    }

    public void MarkRunning()
    {
        MarkRunningCore();
        _onChanged?.Invoke();
    }

    public void MarkStopped()
    {
        MarkStoppedCore();
        _onChanged?.Invoke();
    }

    public void MarkFailed(PluginErrorInfo error)
    {
        MarkFailedCore(error);
        _onChanged?.Invoke();
    }
}
