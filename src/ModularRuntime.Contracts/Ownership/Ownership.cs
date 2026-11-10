namespace ModularRuntime.Contracts;

/// <summary>Kind of party that owns runtime resources.</summary>
public enum ResourceOwnerKind
{
    /// <summary>The application hosting the runtime (standalone host or embedding application).</summary>
    Host = 0,

    /// <summary>One started instance of a plugin.</summary>
    Plugin = 1,
}

/// <summary>
/// Unambiguous identity of a resource owner.
/// </summary>
/// <remarks>
/// <see cref="RuntimeId"/> identifies the runtime instance, so identities never collide
/// between independent runtimes in one process. Plugin owners are identified by instance:
/// a plugin registration may only ever have one instance, but resources belong to the
/// instance, not to the permanent plugin ID.
/// </remarks>
public sealed record ResourceOwner(
    Guid RuntimeId,
    ResourceOwnerKind Kind,
    PluginId? PluginId,
    Guid? InstanceId)
{
    public bool IsHost => Kind == ResourceOwnerKind.Host;

    public override string ToString() =>
        Kind == ResourceOwnerKind.Host
            ? "host"
            : $"{PluginId} (instance {InstanceId?.ToString("N")[..8]})";
}

/// <summary>Kind of a resource tracked by an ownership scope.</summary>
public enum ResourceKind
{
    EventSubscription = 0,
    Service = 1,
    ExtensionPoint = 2,
    ExtensionContribution = 3,

    /// <summary>A disposable object registered with <see cref="IResourceScope.Track"/>.</summary>
    Custom = 4,
}

/// <summary>Immutable snapshot describing one owned resource.</summary>
public sealed record ResourceInfo(
    Guid ResourceId,
    ResourceKind Kind,
    string Name,
    ResourceOwner Owner,
    DateTimeOffset CreatedAt);

/// <summary>
/// A resource owned by a host or plugin instance: an event subscription, a service
/// registration, an extension point, an extension contribution, or a tracked object.
/// </summary>
/// <remarks>
/// Disposing the resource removes it immediately and is idempotent. Resources that are not
/// disposed explicitly are released automatically when their owner stops, fails, or the
/// runtime is disposed. After release, <see cref="IsActive"/> is <see langword="false"/> and
/// the runtime holds no reference to the handler or object supplied by the owner.
/// </remarks>
public interface IPluginResource : IAsyncDisposable
{
    Guid ResourceId { get; }

    ResourceKind Kind { get; }

    string Name { get; }

    ResourceOwner Owner { get; }

    bool IsActive { get; }
}

/// <summary>Resources owned by the current host or plugin instance.</summary>
public interface IResourceScope
{
    ResourceOwner Owner { get; }

    /// <summary>False once the owner has begun shutting down; no new resources are accepted.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Ties the lifetime of <paramref name="resource"/> to this owner. The runtime disposes it
    /// when the owner shuts down, in reverse registration order, after all runtime-managed
    /// handlers of the owner have drained. Disposing the returned handle disposes the resource
    /// immediately.
    /// </summary>
    /// <exception cref="PluginRuntimeException">The scope is no longer open (<c>scope_closed</c>).</exception>
    IPluginResource Track(string name, IAsyncDisposable resource);

    /// <summary>Snapshot of all resources currently owned by this owner.</summary>
    IReadOnlyList<ResourceInfo> GetResources();
}

/// <summary>
/// Owner-scoped access to the extensibility platform. Every operation performed through a
/// scope is attributed to <see cref="Owner"/>, and every resource created through it is
/// released when the owner shuts down.
/// </summary>
/// <remarks>
/// Plugins receive a scope through <see cref="IPluginContext"/>. Hosts obtain the runtime's
/// host scope from <c>PluginRuntime.Host</c> / <c>PluginEngine.Host</c>.
/// </remarks>
public interface IRuntimeScope
{
    ResourceOwner Owner { get; }

    IPluginEventBus Events { get; }

    IPluginServiceRegistry Services { get; }

    IExtensionRegistry Extensions { get; }

    IResourceScope Resources { get; }
}
