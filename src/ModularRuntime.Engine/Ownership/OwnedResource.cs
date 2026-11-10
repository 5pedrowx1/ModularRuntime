using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Ownership;

/// <summary>
/// Base class for every resource owned by an <see cref="OwnershipScope"/>.
/// </summary>
/// <remarks>
/// <para>
/// Release runs exactly once, as a single shared operation. The first caller (an explicit
/// <see cref="DisposeAsync"/>, the owner's scope closing, or the removal of an extension point)
/// starts it; every later or concurrent caller awaits the same operation and observes the same
/// outcome, including its exception. Nobody is told that a resource is released while its
/// release is still running.
/// </para>
/// <para>
/// The resource becomes inactive as soon as the release starts (<see cref="IsReleased"/>), so
/// no new invocation is admitted. It stays in its scope's resource list until the release has
/// completed, so a scope that closes concurrently waits for it before the engine releases the
/// factory and requests unload. Subclasses drop every reference to owner-supplied handlers or
/// objects in <see cref="ReleaseCoreAsync"/>.
/// </para>
/// </remarks>
internal abstract class OwnedResource : IPluginResource
{
    private TaskCompletionSource? _release;

    protected OwnedResource(OwnershipScope scope, ResourceKind kind, string name)
    {
        Scope = scope;
        Kind = kind;
        Name = name;
        ResourceId = Guid.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid ResourceId { get; }

    public ResourceKind Kind { get; }

    public string Name { get; }

    public ResourceOwner Owner => Scope.Owner;

    public DateTimeOffset CreatedAt { get; }

    /// <summary>True once release has started (it may still be running).</summary>
    public bool IsReleased => Volatile.Read(ref _release) is not null;

    /// <summary>
    /// Active while not released and while the owner is open. A resource whose owner is
    /// shutting down is inactive but has not been released yet.
    /// </summary>
    public bool IsActive => !IsReleased && Scope.IsOpen;

    internal OwnershipScope Scope { get; }

    public ValueTask DisposeAsync() => new(BeginRelease().Completion);

    /// <summary>Starts the release, or joins the one already started; completes when it has finished.</summary>
    internal Task ReleaseAsync() => BeginRelease().Completion;

    /// <summary>
    /// Starts the release or joins the one in progress. <see cref="ReleaseOperation.Started"/> tells
    /// the caller whether it started the release and is therefore responsible for reporting a failure.
    /// </summary>
    internal ReleaseOperation BeginRelease()
    {
        var candidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _release, candidate, null);
        if (existing is not null)
        {
            return new ReleaseOperation(existing.Task, Started: false);
        }

        _ = RunReleaseAsync(candidate);
        return new ReleaseOperation(candidate.Task, Started: true);
    }

    internal ResourceInfo ToInfo() => new(ResourceId, Kind, Name, Owner, CreatedAt);

    /// <summary>Unregisters the resource and drops owner-supplied references. Called once.</summary>
    protected abstract ValueTask ReleaseCoreAsync();

    private async Task RunReleaseAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await ReleaseCoreAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Every exception (including fatal ones) is handed to the awaiters; swallowing it
            // here would leave them waiting forever or hide the failure.
            failure = exception;
        }

        // Leave the scope only after the release has finished (successfully or not), so that a
        // concurrent scope close waits for it.
        Scope.Remove(this);
        if (failure is null)
        {
            completion.SetResult();
        }
        else
        {
            completion.SetException(failure);
        }
    }
}

/// <summary>The shared release operation of a resource, and whether this caller started it.</summary>
internal readonly record struct ReleaseOperation(Task Completion, bool Started);

/// <summary>A disposable object whose lifetime is tied to its owner via <see cref="IResourceScope.Track"/>.</summary>
internal sealed class TrackedResource : OwnedResource
{
    private IAsyncDisposable? _resource;

    public TrackedResource(OwnershipScope scope, string name, IAsyncDisposable resource)
        : base(scope, ResourceKind.Custom, name)
    {
        _resource = resource;
    }

    protected override ValueTask ReleaseCoreAsync()
    {
        var resource = Interlocked.Exchange(ref _resource, null);
        return resource?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
