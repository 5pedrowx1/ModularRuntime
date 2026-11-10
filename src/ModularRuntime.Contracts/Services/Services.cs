namespace ModularRuntime.Contracts;

/// <summary>Definition supplied when registering a service.</summary>
/// <param name="ServiceId">Unique service identifier (a <see cref="RuntimeName"/>).</param>
/// <param name="Name">Display name, 1-200 characters.</param>
/// <param name="Version">Service contract version (major.minor[.build]).</param>
/// <param name="Operations">Supported operation names; at least one, at most 64.</param>
public sealed record PluginServiceDefinition(
    string ServiceId,
    string Name,
    Version Version,
    IReadOnlyList<string> Operations)
{
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Immutable snapshot describing a registered service.</summary>
/// <param name="RegistrationId">Identity of this particular registration.</param>
/// <param name="IsAvailable">False while the owner is shutting down.</param>
public sealed record PluginServiceDescriptor(
    Guid RegistrationId,
    string ServiceId,
    string Name,
    Version Version,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, string> Metadata,
    ResourceOwner Owner,
    bool IsAvailable,
    DateTimeOffset RegisteredAt);

/// <summary>Registration handle held by the service provider.</summary>
public interface IServiceRegistration : IPluginResource
{
    PluginServiceDescriptor Descriptor { get; }
}

/// <summary>
/// Consumer handle bound to one service registration.
/// </summary>
/// <remarks>
/// The handle stores only identifiers. It never references the provider's instance, handler,
/// types, or assembly, so a consumer may keep it after the provider stops without preventing
/// the provider's load context from unloading. Once the bound registration is removed the
/// handle is permanently inactive and invocations return <c>service_removed</c>, even if
/// another provider later registers the same service ID.
/// </remarks>
public interface IPluginServiceClient
{
    string ServiceId { get; }

    Guid RegistrationId { get; }

    bool IsAvailable { get; }

    /// <summary>Current descriptor of the bound registration, or <see langword="null"/> after removal.</summary>
    PluginServiceDescriptor? GetDescriptor();

    ValueTask<PluginInvocationResult> InvokeAsync(
        string operation,
        JsonPayload? payload = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional settings for <see cref="IPluginServiceRegistry.InvokeAsync"/>.</summary>
public sealed record ServiceInvocationOptions
{
    /// <summary>
    /// Minimum compatible version: the provider must have the same major version and a version
    /// greater than or equal to this one.
    /// </summary>
    public Version? MinimumVersion { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>
/// Registry of services provided by the host and plugins of one runtime.
/// </summary>
/// <remarks>
/// One active provider per service ID; duplicate registrations are rejected. Version
/// compatibility: same major version and provider version &gt;= requested minimum.
/// </remarks>
public interface IPluginServiceRegistry
{
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="PluginRuntimeException">
    /// <c>duplicate_service</c>, <c>scope_closed</c>, or <c>engine_disposed</c>.
    /// </exception>
    IServiceRegistration Register(PluginServiceDefinition definition, PluginInvocationHandler handler);

    IReadOnlyList<PluginServiceDescriptor> GetServices();

    PluginServiceDescriptor? GetService(string serviceId);

    /// <summary>
    /// Returns a client bound to the current registration of <paramref name="serviceId"/>, or
    /// <see langword="null"/> when the service is not registered or its version is incompatible.
    /// </summary>
    IPluginServiceClient? GetClient(string serviceId, Version? minimumVersion = null);

    /// <summary>Resolves the service by ID at call time and invokes it.</summary>
    ValueTask<PluginInvocationResult> InvokeAsync(
        string serviceId,
        string operation,
        JsonPayload? payload = null,
        ServiceInvocationOptions? options = null,
        CancellationToken cancellationToken = default);
}
