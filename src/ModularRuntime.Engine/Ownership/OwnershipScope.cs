using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;

namespace ModularRuntime.Engine.Ownership;

/// <summary>
/// Owns every resource created by one host or plugin instance and coordinates its shutdown.
/// </summary>
/// <remarks>
/// <para>
/// States: <c>Open</c> → <c>Closing</c> → <c>Closed</c>.
/// </para>
/// <list type="bullet">
/// <item><c>Open</c>: resources may be added; incoming invocations (handlers owned by this
/// scope) are admitted.</item>
/// <item><c>Closing</c> (<see cref="BeginClose"/>): no new resources and no new incoming
/// invocations. The scope token is canceled so in-flight handlers observe cancellation.
/// Outgoing calls made by the owner (publishing, invoking other services) are still allowed
/// so that <c>ShutdownAsync</c> can use the platform.</item>
/// <item><c>Closed</c> (<see cref="CloseAsync"/>): every remaining resource is released in
/// reverse registration order and the owner can no longer use the platform.</item>
/// </list>
/// <para>
/// Locking: a single private lock guards the state, resource list, and in-flight counter.
/// It is never held while calling owner code, releasing resources, or canceling the token,
/// and no other lock is ever acquired while it is held.
/// </para>
/// <para>
/// The scope token source is disposed exactly once: at close when no invocation is in flight,
/// otherwise by the last invocation to exit.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The token source is disposed exactly once by CloseAsync or by the last in-flight invocation, a Dispose method would race with in-flight handlers.")]
internal sealed class OwnershipScope
{
    private readonly object _sync = new();
    private readonly List<OwnedResource> _resources = [];
    private readonly CancellationTokenSource _cancellation = new();
    private ScopeState _state = ScopeState.Open;
    private int _inFlight;
    private TaskCompletionSource? _drained;
    private bool _disposeCancellationOnDrain;

    // Completes when the Cancel() started by BeginClose has returned (all callbacks have run).
    private Task? _cancellationCompleted;

    // The single close operation shared by every CloseAsync caller.
    private TaskCompletionSource<IReadOnlyList<ResourceReleaseFailure>>? _close;

    private readonly Action? _onChanged;

    /// <param name="onChanged">
    /// Invoked after resources are added or removed, outside the scope lock. Used by the engine
    /// to advance its state version; it must not call back into the scope.
    /// </param>
    public OwnershipScope(ResourceOwner owner, Guid ownerKey, Action? onChanged = null)
    {
        Owner = owner;
        OwnerKey = ownerKey;
        _onChanged = onChanged;
    }

    public ResourceOwner Owner { get; }

    /// <summary>Key used by <see cref="InvocationFrames"/>: the instance ID, or a host-specific GUID.</summary>
    public Guid OwnerKey { get; }

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _state == ScopeState.Open;
            }
        }
    }

    /// <summary>True until <see cref="CloseAsync"/>; outgoing calls are allowed while closing.</summary>
    public bool CanCallOut
    {
        get
        {
            lock (_sync)
            {
                return _state != ScopeState.Closed;
            }
        }
    }

    public int InFlightCount
    {
        get
        {
            lock (_sync)
            {
                return _inFlight;
            }
        }
    }

    public bool TryAdd(OwnedResource resource)
    {
        lock (_sync)
        {
            if (_state != ScopeState.Open)
            {
                return false;
            }

            _resources.Add(resource);
        }

        _onChanged?.Invoke();
        return true;
    }

    public void Remove(OwnedResource resource)
    {
        bool removed;
        lock (_sync)
        {
            removed = _resources.Remove(resource);
        }

        if (removed)
        {
            _onChanged?.Invoke();
        }
    }

    public IReadOnlyList<ResourceInfo> GetResources()
    {
        lock (_sync)
        {
            return _resources.Select(resource => resource.ToInfo()).ToArray();
        }
    }

    /// <summary>
    /// Admits one incoming invocation of a handler owned by this scope. Returns
    /// <see langword="false"/> once the scope is closing. Every successful call must be paired
    /// with <see cref="ExitInvocation"/>.
    /// </summary>
    public bool TryEnterInvocation(out CancellationToken scopeToken)
    {
        lock (_sync)
        {
            if (_state != ScopeState.Open)
            {
                scopeToken = default;
                return false;
            }

            _inFlight++;
            scopeToken = _cancellation.Token;
            return true;
        }
    }

    public void ExitInvocation()
    {
        TaskCompletionSource? drained = null;
        var disposeCancellation = false;
        lock (_sync)
        {
            _inFlight--;
            if (_inFlight == 0)
            {
                drained = _drained;
                if (_disposeCancellationOnDrain)
                {
                    _disposeCancellationOnDrain = false;
                    disposeCancellation = true;
                }
            }
        }

        drained?.TrySetResult();
        if (disposeCancellation)
        {
            _cancellation.Dispose();
        }
    }

    /// <summary>
    /// Stops admitting resources and incoming invocations and cancels in-flight handlers.
    /// Cancellation callbacks run outside the lock, on the calling thread. Returns a diagnostic if
    /// a callback threw. Idempotent: only the first call cancels; later calls return at once (use
    /// <see cref="CloseAsync"/> to wait for callbacks started by another thread).
    /// </summary>
    public PluginDiagnostic? BeginClose()
    {
        TaskCompletionSource cancellationCompleted;
        lock (_sync)
        {
            if (_state != ScopeState.Open)
            {
                return null;
            }

            _state = ScopeState.Closing;
            cancellationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancellationCompleted = cancellationCompleted.Task;
        }

        try
        {
            // The token source cannot be disposed before this returns: disposal happens only in
            // the Closed state, which CloseAsync enters after awaiting _cancellationCompleted.
            _cancellation.Cancel();
            return null;
        }
        catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
        {
            return PluginDiagnostic.FromException(exception);
        }
        finally
        {
            cancellationCompleted.SetResult();
        }
    }

    /// <summary>Completes when no incoming invocation is in flight.</summary>
    public Task WaitForDrainAsync()
    {
        lock (_sync)
        {
            if (_inFlight == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    /// <summary>
    /// Waits for in-flight invocations to finish, bounded by <paramref name="timeout"/>.
    /// Returns <see langword="false"/> on timeout. Handlers that ignore cancellation cannot be
    /// interrupted in-process; they keep running after the timeout.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var drain = WaitForDrainAsync();
        if (drain.IsCompleted || timeout == Timeout.InfiniteTimeSpan)
        {
            await drain.ConfigureAwait(false);
            return true;
        }

        try
        {
            await drain.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Closes the scope and releases every remaining resource in reverse registration order.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>Begins closing (cancels in-flight handlers) if nobody has yet, and waits until the
    /// cancellation callbacks have returned, even when another thread started them.</item>
    /// <item>Enters <c>Closed</c> and takes the remaining resources, including resources whose
    /// release another caller has already started.</item>
    /// <item>Releases or joins the release of each resource and waits for all of them.</item>
    /// <item>Disposes the scope token source when no invocation is in flight (otherwise the
    /// last invocation to exit disposes it).</item>
    /// </list>
    /// Release failures are collected and do not stop the remaining releases. A failure is reported
    /// here only for releases this close started; a release started explicitly by the owner reports
    /// its failure to that caller. Concurrent and later calls share the same operation and result.
    /// </remarks>
    public Task<IReadOnlyList<ResourceReleaseFailure>> CloseAsync()
    {
        TaskCompletionSource<IReadOnlyList<ResourceReleaseFailure>> close;
        lock (_sync)
        {
            if (_close is not null)
            {
                return _close.Task;
            }

            close = _close = new TaskCompletionSource<IReadOnlyList<ResourceReleaseFailure>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = RunCloseAsync(close);
        return close.Task;
    }

    private async Task RunCloseAsync(TaskCompletionSource<IReadOnlyList<ResourceReleaseFailure>> close)
    {
        try
        {
            close.SetResult(await CloseCoreAsync().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            close.SetException(exception);
        }
    }

    private async Task<IReadOnlyList<ResourceReleaseFailure>> CloseCoreAsync()
    {
        var failures = new List<ResourceReleaseFailure>();
        if (BeginClose() is { } cancellationFailure)
        {
            failures.Add(new ResourceReleaseFailure(null, cancellationFailure));
        }

        Task cancellationCompleted;
        lock (_sync)
        {
            cancellationCompleted = _cancellationCompleted!;
        }

        // Callbacks may still be running on the thread that called BeginClose first.
        await cancellationCompleted.ConfigureAwait(false);

        OwnedResource[] snapshot;
        bool disposeCancellationNow;
        lock (_sync)
        {
            _state = ScopeState.Closed;
            snapshot = [.. _resources];
            _resources.Clear();

            disposeCancellationNow = _inFlight == 0;
            _disposeCancellationOnDrain = !disposeCancellationNow;
        }

        if (snapshot.Length > 0)
        {
            _onChanged?.Invoke();
        }

        // Release (or join the release of) each resource sequentially, newest first, so that a
        // resource is disposed only after the resources created after it.
        for (var index = snapshot.Length - 1; index >= 0; index--)
        {
            var operation = snapshot[index].BeginRelease();
            try
            {
                await operation.Completion.ConfigureAwait(false);
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                if (operation.Started)
                {
                    failures.Add(new ResourceReleaseFailure(snapshot[index].ToInfo(), PluginDiagnostic.FromException(exception)));
                }

                exception = null!;
            }

            // Do not keep a faulted task (which may carry a plugin exception) in this state machine.
            operation = default;
        }

        if (disposeCancellationNow)
        {
            _cancellation.Dispose();
        }

        return failures;
    }

    private enum ScopeState
    {
        Open,
        Closing,
        Closed,
    }
}

/// <summary>A resource whose release threw, or (with no resource) a failing cancellation callback.</summary>
internal sealed record ResourceReleaseFailure(ResourceInfo? Resource, PluginDiagnostic Diagnostic);
