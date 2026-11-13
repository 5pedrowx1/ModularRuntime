namespace ModularRuntime.Contracts;

/// <summary>How a service is invoked. One service ID has exactly one kind.</summary>
public enum ServiceKind
{
    /// <summary>Request/response with <see cref="JsonPayload"/> (<see cref="PluginInvocationHandler"/>).</summary>
    Json = 0,

    /// <summary>Request/response with bytes (<see cref="BinaryInvocationHandler"/>).</summary>
    Binary = 1,

    /// <summary>A C# interface from a shared contract assembly (<see cref="ITypedServiceRegistry"/>).</summary>
    Typed = 2,
}

/// <summary>Definition supplied when registering a JSON service.</summary>
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

/// <summary>Definition supplied when registering a binary service.</summary>
/// <param name="ServiceId">Unique service identifier (a <see cref="RuntimeName"/>).</param>
/// <param name="Name">Display name, 1-200 characters.</param>
/// <param name="Version">Service contract version (major.minor[.build]).</param>
/// <param name="Operations">Supported operation names; at least one, at most 64.</param>
public sealed record BinaryServiceDefinition(
    string ServiceId,
    string Name,
    Version Version,
    IReadOnlyList<string> Operations)
{
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Maximum request size accepted by this service, or <see langword="null"/> for the runtime
    /// limit. A value above the runtime limit is reduced to it.
    /// </summary>
    public int? MaxRequestBytes { get; init; }

    /// <summary>Maximum response size this service may return, or <see langword="null"/> for the runtime limit.</summary>
    public int? MaxResponseBytes { get; init; }
}

/// <summary>Immutable snapshot describing a registered service of any kind.</summary>
/// <param name="RegistrationId">Identity of this particular registration.</param>
/// <param name="Operations">Declared operations (empty for typed services).</param>
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
    DateTimeOffset RegisteredAt)
{
    /// <summary>How the service is invoked. Added in plugin API 0.4.</summary>
    public ServiceKind Kind { get; init; } = ServiceKind.Json;

    /// <summary>
    /// For typed services, the contract interface as <c>Namespace.Type, AssemblyName, Version=…</c>
    /// (text only, never a <see cref="Type"/>). <see langword="null"/> for other kinds.
    /// </summary>
    public string? ContractTypeName { get; init; }

    /// <summary>For binary services, the effective request size limit in bytes.</summary>
    public int? MaxRequestBytes { get; init; }

    /// <summary>For binary services, the effective response size limit in bytes.</summary>
    public int? MaxResponseBytes { get; init; }
}

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

    /// <summary>Invokes a JSON service. A binary or typed service answers <c>service_kind_mismatch</c>.</summary>
    ValueTask<PluginInvocationResult> InvokeAsync(
        string operation,
        JsonPayload? payload = null,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a binary service. A JSON or typed service answers <c>service_kind_mismatch</c>. Dispose the result.</summary>
    ValueTask<BinaryInvocationResult> InvokeBinaryAsync(
        string operation,
        ReadOnlyMemory<byte> payload = default,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional settings for <see cref="IPluginServiceRegistry.InvokeAsync"/> and <see cref="IPluginServiceRegistry.InvokeBinaryAsync"/>.</summary>
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
/// One active provider per service ID, whatever its kind; duplicate registrations are rejected.
/// Version compatibility: same major version and provider version &gt;= requested minimum.
/// JSON and binary services are registered and invoked here; typed services through
/// <see cref="ITypedServiceRegistry"/>, but they share this ID namespace and appear in
/// <see cref="GetServices"/>.
/// </remarks>
public interface IPluginServiceRegistry
{
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="PluginRuntimeException">
    /// <c>duplicate_service</c>, <c>scope_closed</c>, or <c>engine_disposed</c>.
    /// </exception>
    IServiceRegistration Register(PluginServiceDefinition definition, PluginInvocationHandler handler);

    /// <summary>Registers a binary service. Same exceptions as <see cref="Register"/>.</summary>
    IServiceRegistration RegisterBinary(BinaryServiceDefinition definition, BinaryInvocationHandler handler);

    IReadOnlyList<PluginServiceDescriptor> GetServices();

    PluginServiceDescriptor? GetService(string serviceId);

    /// <summary>
    /// Returns a client bound to the current registration of <paramref name="serviceId"/>, or
    /// <see langword="null"/> when the service is not registered or its version is incompatible.
    /// </summary>
    IPluginServiceClient? GetClient(string serviceId, Version? minimumVersion = null);

    /// <summary>Resolves the JSON service by ID at call time and invokes it.</summary>
    ValueTask<PluginInvocationResult> InvokeAsync(
        string serviceId,
        string operation,
        JsonPayload? payload = null,
        ServiceInvocationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the binary service by ID at call time and invokes it. The request bytes are
    /// borrowed until the call completes; dispose the result when its payload is no longer needed.
    /// </summary>
    ValueTask<BinaryInvocationResult> InvokeBinaryAsync(
        string serviceId,
        string operation,
        ReadOnlyMemory<byte> payload = default,
        ServiceInvocationOptions? options = null,
        CancellationToken cancellationToken = default);
}
