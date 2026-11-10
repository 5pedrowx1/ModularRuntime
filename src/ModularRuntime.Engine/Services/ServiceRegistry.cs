using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Services;

/// <summary>
/// Service registry of one runtime: one active provider per service ID.
/// </summary>
/// <remarks>
/// <para>
/// The registry holds a provider's handler only while its registration is active. Removal
/// (explicit or because the provider stops) deletes the entry from both indexes and clears the
/// handler reference. Consumer handles (<see cref="ServiceClient"/>) contain only the service ID
/// and registration ID and are resolved through the registry on every call, so they can never
/// keep a provider alive.
/// </para>
/// <para>
/// Lifecycle of a provider's services during shutdown: when the provider's scope begins closing,
/// its services report <c>IsAvailable = false</c> and new calls are rejected with
/// <c>service_unavailable</c>; in-flight calls are canceled and drained; then the registrations
/// are removed and later calls return <c>service_not_found</c> (by ID) or
/// <c>service_removed</c> (through a client handle).
/// </para>
/// </remarks>
internal sealed class ServiceRegistry
{
    private readonly RuntimeCore _core;
    private readonly object _sync = new();
    private readonly Dictionary<string, ServiceEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ServiceEntry> _byRegistration = [];

    public ServiceRegistry(RuntimeCore core)
    {
        _core = core;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byId.Count;
            }
        }
    }

    public IServiceRegistration Register(
        OwnershipScope scope,
        PluginServiceDefinition definition,
        PluginInvocationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        RuntimeName.Validate(definition.ServiceId, nameof(definition));
        Validation.ValidateDisplayName(definition.Name, nameof(definition));
        Validation.ValidateVersion(definition.Version, nameof(definition));
        var operations = Validation.CopyOperations(definition.Operations, allowEmpty: false, nameof(definition));
        var metadata = Validation.CopyMetadata(definition.Metadata, nameof(definition));
        _core.EnsureCanRegister(scope);

        var entry = new ServiceEntry(this, scope, definition, operations, metadata, handler);
        lock (_sync)
        {
            if (_byId.TryGetValue(definition.ServiceId, out var existing))
            {
                throw new PluginRuntimeException(
                    PluginErrorCodes.DuplicateService,
                    $"Service '{definition.ServiceId}' is already provided by '{existing.Owner}'.");
            }

            _byId.Add(entry.ServiceId, entry);
            _byRegistration.Add(entry.ResourceId, entry);
        }

        if (!scope.TryAdd(entry))
        {
            Remove(entry);
            throw RuntimeCore.ScopeClosed(scope);
        }

        _core.Log(LogLevel.Information, "Services", $"Service '{entry.ServiceId}' {entry.Version} registered.", scope.Owner);
        return entry;
    }

    public IReadOnlyList<PluginServiceDescriptor> GetServices()
    {
        ServiceEntry[] entries;
        lock (_sync)
        {
            entries = [.. _byId.Values];
        }

        return entries
            .OrderBy(entry => entry.ServiceId, StringComparer.Ordinal)
            .Select(entry => entry.Descriptor)
            .ToArray();
    }

    public PluginServiceDescriptor? GetService(string serviceId) => Find(serviceId)?.Descriptor;

    public IPluginServiceClient? GetClient(OwnershipScope caller, string serviceId, Version? minimumVersion)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        var entry = Find(serviceId);
        if (entry is null || entry.IsReleased ||
            (minimumVersion is not null && !Validation.IsVersionCompatible(entry.Version, minimumVersion)))
        {
            return null;
        }

        return new ServiceClient(this, caller, entry.ServiceId, entry.ResourceId);
    }

    public ValueTask<PluginInvocationResult> InvokeAsync(
        OwnershipScope caller,
        string serviceId,
        string operation,
        JsonPayload? payload,
        ServiceInvocationOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(operation);

        if (CheckCaller(caller, serviceId, operation) is { } rejected)
        {
            return ValueTask.FromResult(rejected);
        }

        var entry = Find(serviceId);
        if (entry is null)
        {
            return ValueTask.FromResult(HandlerInvoker.Rejected(serviceId, operation, PluginErrorCodes.ServiceNotFound, $"Service '{serviceId}' is not registered."));
        }

        if (options?.MinimumVersion is { } minimum && !Validation.IsVersionCompatible(entry.Version, minimum))
        {
            return ValueTask.FromResult(HandlerInvoker.Rejected(
                serviceId,
                operation,
                PluginErrorCodes.ServiceVersionIncompatible,
                $"Service '{serviceId}' has version {entry.Version}, which is not compatible with requested version {minimum}.",
                entry.Owner));
        }

        return InvokeEntryAsync(caller, entry, operation, payload, options?.CorrelationId, cancellationToken);
    }

    internal ValueTask<PluginInvocationResult> InvokeRegistrationAsync(
        OwnershipScope caller,
        Guid registrationId,
        string serviceId,
        string operation,
        JsonPayload? payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (CheckCaller(caller, serviceId, operation) is { } rejected)
        {
            return ValueTask.FromResult(rejected);
        }

        var entry = FindRegistration(registrationId);
        if (entry is null)
        {
            return ValueTask.FromResult(HandlerInvoker.Rejected(
                serviceId,
                operation,
                PluginErrorCodes.ServiceRemoved,
                $"The registration of service '{serviceId}' bound to this client was removed."));
        }

        return InvokeEntryAsync(caller, entry, operation, payload, null, cancellationToken);
    }

    internal ServiceEntry? FindRegistration(Guid registrationId)
    {
        lock (_sync)
        {
            return _byRegistration.GetValueOrDefault(registrationId);
        }
    }

    internal void Remove(ServiceEntry entry)
    {
        lock (_sync)
        {
            if (_byId.TryGetValue(entry.ServiceId, out var current) && ReferenceEquals(current, entry))
            {
                _byId.Remove(entry.ServiceId);
            }

            _byRegistration.Remove(entry.ResourceId);
        }
    }

    internal void LogRemoved(ServiceEntry entry) =>
        _core.Log(LogLevel.Information, "Services", $"Service '{entry.ServiceId}' removed.", entry.Owner);

    private PluginInvocationResult? CheckCaller(OwnershipScope caller, string serviceId, string operation)
    {
        if (_core.IsDisposed)
        {
            return HandlerInvoker.Rejected(serviceId, operation, PluginErrorCodes.EngineDisposed, "The runtime has been disposed.");
        }

        if (!caller.CanCallOut)
        {
            return HandlerInvoker.Rejected(serviceId, operation, PluginErrorCodes.ScopeClosed, $"Owner '{caller.Owner}' has stopped and cannot invoke services.");
        }

        if (!RuntimeName.IsValid(serviceId) || !RuntimeName.IsValid(operation))
        {
            return HandlerInvoker.Rejected(serviceId, operation, PluginErrorCodes.InvalidIdentifier, "The service ID or operation name is not a valid runtime name.");
        }

        return null;
    }

    private ValueTask<PluginInvocationResult> InvokeEntryAsync(
        OwnershipScope caller,
        ServiceEntry entry,
        string operation,
        JsonPayload? payload,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        payload ??= JsonPayload.Null;
        if (!entry.SupportsOperation(operation))
        {
            return ValueTask.FromResult(HandlerInvoker.Rejected(
                entry.ServiceId,
                operation,
                PluginErrorCodes.OperationNotSupported,
                $"Service '{entry.ServiceId}' does not support operation '{operation}'.",
                entry.Owner));
        }

        if (!_core.IsPayloadWithinLimit(payload))
        {
            return ValueTask.FromResult(HandlerInvoker.Rejected(entry.ServiceId, operation, PluginErrorCodes.PayloadTooLarge, _core.PayloadTooLargeMessage(payload), entry.Owner));
        }

        var request = new PluginRequest(operation, payload, caller.Owner, correlationId);
        return HandlerInvoker.InvokeAsync(
            _core,
            entry,
            entry.ServiceId,
            "Services",
            PluginErrorCodes.ServiceUnavailable,
            PluginErrorCodes.ServiceRemoved,
            request,
            cancellationToken);
    }

    private ServiceEntry? Find(string serviceId)
    {
        lock (_sync)
        {
            return _byId.GetValueOrDefault(serviceId);
        }
    }
}

internal sealed class ServiceEntry : InvocableEntry, IServiceRegistration
{
    private readonly ServiceRegistry _registry;
    private readonly string _displayName;
    private readonly IReadOnlyList<string> _operations;
    private readonly IReadOnlyDictionary<string, string> _metadata;

    public ServiceEntry(
        ServiceRegistry registry,
        OwnershipScope scope,
        PluginServiceDefinition definition,
        IReadOnlyList<string> operations,
        IReadOnlyDictionary<string, string> metadata,
        PluginInvocationHandler handler)
        : base(scope, ResourceKind.Service, definition.ServiceId, handler)
    {
        _registry = registry;
        ServiceId = definition.ServiceId;
        _displayName = definition.Name;
        Version = definition.Version;
        _operations = operations;
        _metadata = metadata;
    }

    public string ServiceId { get; }

    public Version Version { get; }

    public PluginServiceDescriptor Descriptor =>
        new(ResourceId, ServiceId, _displayName, Version, _operations, _metadata, Owner, IsActive, CreatedAt);

    public bool SupportsOperation(string operation) => _operations.Contains(operation, StringComparer.Ordinal);

    protected override ValueTask ReleaseCoreAsync()
    {
        _registry.Remove(this);
        ClearHandler();
        _registry.LogRemoved(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Consumer handle: identifiers only, resolved through the registry on each call.</summary>
internal sealed class ServiceClient : IPluginServiceClient
{
    private readonly ServiceRegistry _registry;
    private readonly OwnershipScope _caller;

    public ServiceClient(ServiceRegistry registry, OwnershipScope caller, string serviceId, Guid registrationId)
    {
        _registry = registry;
        _caller = caller;
        ServiceId = serviceId;
        RegistrationId = registrationId;
    }

    public string ServiceId { get; }

    public Guid RegistrationId { get; }

    public bool IsAvailable => _registry.FindRegistration(RegistrationId)?.IsActive ?? false;

    public PluginServiceDescriptor? GetDescriptor() => _registry.FindRegistration(RegistrationId)?.Descriptor;

    public ValueTask<PluginInvocationResult> InvokeAsync(
        string operation,
        JsonPayload? payload = null,
        CancellationToken cancellationToken = default) =>
        _registry.InvokeRegistrationAsync(_caller, RegistrationId, ServiceId, operation, payload, cancellationToken);
}
