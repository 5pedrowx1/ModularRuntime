using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Services;

/// <summary>
/// Service registry of one runtime: one active provider per service ID, of one kind (JSON,
/// binary, or typed).
/// </summary>
/// <remarks>
/// <para>
/// The registry holds a provider's handler (or typed instance) only while its registration is
/// active. Removal (explicit or because the provider stops) deletes the entry from both indexes
/// and clears that reference. Consumer handles (<see cref="ServiceClient"/>, typed proxies)
/// contain only the service ID and registration ID and are resolved through the registry on every
/// call, so they can never keep a provider alive.
/// </para>
/// <para>
/// Lifecycle of a provider's services during shutdown: when the provider's scope begins closing,
/// its services report <c>IsAvailable = false</c> and new calls are rejected with
/// <c>service_unavailable</c>; in-flight calls are canceled and drained; then the registrations
/// are removed and later calls return <c>service_not_found</c> (by ID) or
/// <c>service_removed</c> (through a client handle or proxy).
/// </para>
/// </remarks>
internal sealed class ServiceRegistry : ICallTargetResolver
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

        return Add(scope, new JsonServiceEntry(this, scope, definition, operations, metadata, handler));
    }

    public IServiceRegistration RegisterBinary(
        OwnershipScope scope,
        BinaryServiceDefinition definition,
        BinaryInvocationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        RuntimeName.Validate(definition.ServiceId, nameof(definition));
        Validation.ValidateDisplayName(definition.Name, nameof(definition));
        Validation.ValidateVersion(definition.Version, nameof(definition));
        var operations = Validation.CopyOperations(definition.Operations, allowEmpty: false, nameof(definition));
        var metadata = Validation.CopyMetadata(definition.Metadata, nameof(definition));
        var maxRequest = Validation.ClampLimit(definition.MaxRequestBytes, _core.Limits.MaxBinaryPayloadBytes, nameof(definition));
        var maxResponse = Validation.ClampLimit(definition.MaxResponseBytes, _core.Limits.MaxBinaryPayloadBytes, nameof(definition));
        _core.EnsureCanRegister(scope);

        return Add(scope, new BinaryServiceEntry(this, scope, definition, operations, metadata, maxRequest, maxResponse, handler));
    }

    public IServiceRegistration RegisterTyped(
        OwnershipScope scope,
        TypedServiceDefinition definition,
        Type contract,
        object instance)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(instance);
        RuntimeName.Validate(definition.ServiceId, nameof(definition));
        Validation.ValidateDisplayName(definition.Name, nameof(definition));
        Validation.ValidateVersion(definition.Version, nameof(definition));
        var metadata = Validation.CopyMetadata(definition.Metadata, nameof(definition));
        if (TypedProxyEmitter.ValidateContract(contract) is { } contractError)
        {
            throw new PluginRuntimeException(PluginErrorCodes.TypedServiceInvalidContract, contractError);
        }

        if (!contract.IsInstanceOfType(instance))
        {
            throw new ArgumentException($"The instance of type '{instance.GetType().FullName}' does not implement '{contract.FullName}'.", nameof(instance));
        }

        // Safety by construction: the contract and every type its signatures expose must be shared
        // and analyzable before any call is made (rejected in Reject mode, warned about in Warn mode).
        if (_core.SharedTypes.ValidateContract(contract, TypedProxyEmitter.SignatureTypes(contract), $"Typed service '{definition.ServiceId}'", scope.Owner) is { } sharedError)
        {
            throw new PluginRuntimeException(PluginErrorCodes.TypedServiceInvalidContract, sharedError);
        }

        _core.EnsureCanRegister(scope);
        return Add(scope, new TypedServiceEntry(this, scope, definition, contract, metadata, instance));
    }

    ICallTarget? ICallTargetResolver.FindTarget(Guid registrationId) => FindRegistration(registrationId) as TypedServiceEntry;

    private ServiceEntry Add(OwnershipScope scope, ServiceEntry entry)
    {
        lock (_sync)
        {
            if (_byId.TryGetValue(entry.ServiceId, out var existing))
            {
                throw new PluginRuntimeException(
                    PluginErrorCodes.DuplicateService,
                    $"Service '{entry.ServiceId}' is already provided by '{existing.Owner}' ({existing.ServiceKind}).");
            }

            _byId.Add(entry.ServiceId, entry);
            _byRegistration.Add(entry.ResourceId, entry);
        }

        if (!scope.TryAdd(entry))
        {
            Remove(entry);
            entry.DropOwnerReferences();
            throw RuntimeCore.ScopeClosed(scope);
        }

        _core.Log(LogLevel.Information, "Services", $"{entry.KindLabel} '{entry.ServiceId}' {entry.Version} registered.", scope.Owner);
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

    /// <summary>Resolves a typed service and creates a proxy bound to its current registration.</summary>
    public TypedServiceClient<TContract>? GetTypedClient<TContract>(OwnershipScope caller, string serviceId, Version? minimumVersion)
        where TContract : class
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        _core.EnsureCanRegister(caller);
        var entry = Find(serviceId);
        if (entry is null || entry.IsReleased ||
            (minimumVersion is not null && !Validation.IsVersionCompatible(entry.Version, minimumVersion)))
        {
            return null;
        }

        if (entry is not TypedServiceEntry typed)
        {
            return null;
        }

        if (typed.Contract != typeof(TContract))
        {
            var same = string.Equals(typed.Contract.FullName, typeof(TContract).FullName, StringComparison.Ordinal);
            throw new PluginRuntimeException(
                PluginErrorCodes.TypedServiceContractMismatch,
                same
                    ? $"Typed service '{serviceId}' implements '{typed.ContractTypeName}', but the caller asked for a different load of the same type ('{Describe(typeof(TContract))}'). " +
                      "Both plugins must declare the contract assembly in plugin.json ('sharedContracts') so that the runtime loads it once."
                    : $"Typed service '{serviceId}' implements '{typed.ContractTypeName}', not '{Describe(typeof(TContract))}'.");
        }

        var context = new TypedCallContext(_core, this, caller, typed.ServiceId, typed.ResourceId, isCallback: false);
        var proxy = (TContract)TypedProxyEmitter.CreateProxy(typed.Contract, context);
        return new TypedServiceClient<TContract>(context, proxy);
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

    public ValueTask<BinaryInvocationResult> InvokeBinaryAsync(
        OwnershipScope caller,
        string serviceId,
        string operation,
        ReadOnlyMemory<byte> payload,
        ServiceInvocationOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(operation);

        if (CheckCaller(caller, serviceId, operation) is { } rejected)
        {
            return ValueTask.FromResult(ToBinary(rejected));
        }

        var entry = Find(serviceId);
        if (entry is null)
        {
            return ValueTask.FromResult(HandlerInvoker.BinaryRejected(serviceId, operation, PluginErrorCodes.ServiceNotFound, $"Service '{serviceId}' is not registered."));
        }

        if (options?.MinimumVersion is { } minimum && !Validation.IsVersionCompatible(entry.Version, minimum))
        {
            return ValueTask.FromResult(HandlerInvoker.BinaryRejected(
                serviceId,
                operation,
                PluginErrorCodes.ServiceVersionIncompatible,
                $"Service '{serviceId}' has version {entry.Version}, which is not compatible with requested version {minimum}.",
                entry.Owner));
        }

        return InvokeBinaryEntryAsync(caller, entry, operation, payload, options?.CorrelationId, cancellationToken);
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

    internal ValueTask<BinaryInvocationResult> InvokeRegistrationBinaryAsync(
        OwnershipScope caller,
        Guid registrationId,
        string serviceId,
        string operation,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (CheckCaller(caller, serviceId, operation) is { } rejected)
        {
            return ValueTask.FromResult(ToBinary(rejected));
        }

        var entry = FindRegistration(registrationId);
        if (entry is null)
        {
            return ValueTask.FromResult(HandlerInvoker.BinaryRejected(
                serviceId,
                operation,
                PluginErrorCodes.ServiceRemoved,
                $"The registration of service '{serviceId}' bound to this client was removed."));
        }

        return InvokeBinaryEntryAsync(caller, entry, operation, payload, null, cancellationToken);
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
        _core.Log(LogLevel.Information, "Services", $"{entry.KindLabel} '{entry.ServiceId}' removed.", entry.Owner);

    internal static string Describe(Type contract) =>
        $"{contract.FullName}, {contract.Assembly.GetName().Name}, Version={contract.Assembly.GetName().Version}";

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
        if (entry is not JsonServiceEntry json)
        {
            return ValueTask.FromResult(HandlerInvoker.Rejected(
                entry.ServiceId,
                operation,
                PluginErrorCodes.ServiceKindMismatch,
                $"Service '{entry.ServiceId}' is a {entry.ServiceKind} service; it cannot be invoked with JSON.",
                entry.Owner));
        }

        if (!json.SupportsOperation(operation))
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
            json,
            entry.ServiceId,
            "Services",
            PluginErrorCodes.ServiceUnavailable,
            PluginErrorCodes.ServiceRemoved,
            request,
            cancellationToken);
    }

    private ValueTask<BinaryInvocationResult> InvokeBinaryEntryAsync(
        OwnershipScope caller,
        ServiceEntry entry,
        string operation,
        ReadOnlyMemory<byte> payload,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (entry is not BinaryServiceEntry binary)
        {
            return ValueTask.FromResult(HandlerInvoker.BinaryRejected(
                entry.ServiceId,
                operation,
                PluginErrorCodes.ServiceKindMismatch,
                $"Service '{entry.ServiceId}' is a {entry.ServiceKind} service; it cannot be invoked with bytes.",
                entry.Owner));
        }

        if (!binary.SupportsOperation(operation))
        {
            return ValueTask.FromResult(HandlerInvoker.BinaryRejected(
                entry.ServiceId,
                operation,
                PluginErrorCodes.OperationNotSupported,
                $"Service '{entry.ServiceId}' does not support operation '{operation}'.",
                entry.Owner));
        }

        var request = new BinaryRequest(operation, payload, caller.Owner, correlationId);
        return HandlerInvoker.InvokeBinaryAsync(
            _core,
            binary,
            entry.ServiceId,
            "Services",
            PluginErrorCodes.ServiceUnavailable,
            PluginErrorCodes.ServiceRemoved,
            request,
            cancellationToken);
    }

    private static BinaryInvocationResult ToBinary(PluginInvocationResult rejected) =>
        new(rejected.TargetId, rejected.Operation, rejected.Status, ReadOnlyMemory<byte>.Empty, null, rejected.ErrorCode, rejected.Message, rejected.Diagnostic, rejected.Provider);

    private ServiceEntry? Find(string serviceId)
    {
        lock (_sync)
        {
            return _byId.GetValueOrDefault(serviceId);
        }
    }
}

/// <summary>A registered service of any kind.</summary>
internal abstract class ServiceEntry : OwnedResource, IServiceRegistration
{
    private readonly ServiceRegistry _registry;
    private readonly string _displayName;
    private readonly IReadOnlyDictionary<string, string> _metadata;

    protected ServiceEntry(
        ServiceRegistry registry,
        OwnershipScope scope,
        string serviceId,
        string displayName,
        Version version,
        IReadOnlyDictionary<string, string> metadata)
        : base(scope, ResourceKind.Service, serviceId)
    {
        _registry = registry;
        ServiceId = serviceId;
        _displayName = displayName;
        Version = version;
        _metadata = metadata;
    }

    public string ServiceId { get; }

    public Version Version { get; }

    public abstract ServiceKind ServiceKind { get; }

    /// <summary>"Service" for JSON (the historical log wording), "Binary service" / "Typed service" otherwise.</summary>
    public string KindLabel => ServiceKind == ServiceKind.Json ? "Service" : $"{ServiceKind} service";

    protected virtual IReadOnlyList<string> Operations => [];

    public PluginServiceDescriptor Descriptor =>
        Decorate(new PluginServiceDescriptor(ResourceId, ServiceId, _displayName, Version, Operations, _metadata, Owner, IsActive, CreatedAt) { Kind = ServiceKind });

    protected virtual PluginServiceDescriptor Decorate(PluginServiceDescriptor descriptor) => descriptor;

    /// <summary>Drops the handler or instance supplied by the owner.</summary>
    public abstract void DropOwnerReferences();

    protected override ValueTask ReleaseCoreAsync()
    {
        _registry.Remove(this);
        DropOwnerReferences();
        _registry.LogRemoved(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class JsonServiceEntry : ServiceEntry, IJsonInvocable
{
    private readonly IReadOnlyList<string> _operations;
    private PluginInvocationHandler? _handler;

    public JsonServiceEntry(
        ServiceRegistry registry,
        OwnershipScope scope,
        PluginServiceDefinition definition,
        IReadOnlyList<string> operations,
        IReadOnlyDictionary<string, string> metadata,
        PluginInvocationHandler handler)
        : base(registry, scope, definition.ServiceId, definition.Name, definition.Version, metadata)
    {
        _operations = operations;
        _handler = handler;
    }

    public override ServiceKind ServiceKind => ServiceKind.Json;

    protected override IReadOnlyList<string> Operations => _operations;

    public PluginInvocationHandler? Handler => Volatile.Read(ref _handler);

    OwnershipScope IInvocableResource.Scope => Scope;

    public bool SupportsOperation(string operation) => _operations.Contains(operation, StringComparer.Ordinal);

    public override void DropOwnerReferences() => Volatile.Write(ref _handler, null);
}

internal sealed class BinaryServiceEntry : ServiceEntry, IBinaryInvocable
{
    private readonly IReadOnlyList<string> _operations;
    private BinaryInvocationHandler? _handler;

    public BinaryServiceEntry(
        ServiceRegistry registry,
        OwnershipScope scope,
        BinaryServiceDefinition definition,
        IReadOnlyList<string> operations,
        IReadOnlyDictionary<string, string> metadata,
        int maxRequestBytes,
        int maxResponseBytes,
        BinaryInvocationHandler handler)
        : base(registry, scope, definition.ServiceId, definition.Name, definition.Version, metadata)
    {
        _operations = operations;
        _handler = handler;
        MaxRequestBytes = maxRequestBytes;
        MaxResponseBytes = maxResponseBytes;
    }

    public override ServiceKind ServiceKind => ServiceKind.Binary;

    protected override IReadOnlyList<string> Operations => _operations;

    public BinaryInvocationHandler? Handler => Volatile.Read(ref _handler);

    public int MaxRequestBytes { get; }

    public int MaxResponseBytes { get; }

    OwnershipScope IInvocableResource.Scope => Scope;

    public bool SupportsOperation(string operation) => _operations.Contains(operation, StringComparer.Ordinal);

    protected override PluginServiceDescriptor Decorate(PluginServiceDescriptor descriptor) =>
        descriptor with { MaxRequestBytes = MaxRequestBytes, MaxResponseBytes = MaxResponseBytes };

    public override void DropOwnerReferences() => Volatile.Write(ref _handler, null);
}

internal sealed class TypedServiceEntry : ServiceEntry, ICallTarget
{
    private object? _instance;

    public TypedServiceEntry(
        ServiceRegistry registry,
        OwnershipScope scope,
        TypedServiceDefinition definition,
        Type contract,
        IReadOnlyDictionary<string, string> metadata,
        object instance)
        : base(registry, scope, definition.ServiceId, definition.Name, definition.Version, metadata)
    {
        Contract = contract;
        ContractTypeName = ServiceRegistry.Describe(contract);
        _instance = instance;
    }

    public override ServiceKind ServiceKind => ServiceKind.Typed;

    /// <summary>The contract interface (a type of a shared context; the entry dies with the registration).</summary>
    public Type Contract { get; }

    public string ContractTypeName { get; }

    /// <summary>The provider's object, or <see langword="null"/> after release.</summary>
    public object? Instance => Volatile.Read(ref _instance);

    OwnershipScope ICallTarget.Scope => Scope;

    protected override PluginServiceDescriptor Decorate(PluginServiceDescriptor descriptor) =>
        descriptor with { ContractTypeName = ContractTypeName };

    public override void DropOwnerReferences() => Volatile.Write(ref _instance, null);
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

    public ValueTask<BinaryInvocationResult> InvokeBinaryAsync(
        string operation,
        ReadOnlyMemory<byte> payload = default,
        CancellationToken cancellationToken = default) =>
        _registry.InvokeRegistrationBinaryAsync(_caller, RegistrationId, ServiceId, operation, payload, cancellationToken);
}

/// <summary>Typed consumer handle: the proxy plus identifiers; never the provider's object.</summary>
internal sealed class TypedServiceClient<TContract> : ITypedServiceClient<TContract>
    where TContract : class
{
    private readonly TypedCallContext _context;

    public TypedServiceClient(TypedCallContext context, TContract proxy)
    {
        _context = context;
        Service = proxy;
    }

    public string ServiceId => _context.ServiceId;

    public Guid RegistrationId => _context.RegistrationId;

    public bool IsAvailable => _context.IsAvailable;

    public TContract Service { get; }

    public PluginServiceDescriptor? GetDescriptor() => _context.GetDescriptor();
}
