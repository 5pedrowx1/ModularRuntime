using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Extensions;

/// <summary>
/// Extension points and contributions of one runtime.
/// </summary>
/// <remarks>
/// <para>
/// One lock guards the point index and every point's contribution list. Contribution handlers are
/// invoked through <see cref="HandlerInvoker"/>, the same engine used by services, with no lock held.
/// </para>
/// <para>
/// Removing a point (explicitly or because its owner stops) removes it from the index, detaches
/// all its contributions under the lock, and then releases each contribution outside the lock,
/// which also removes it from its contributor's scope and clears its handler. Removing a
/// contribution affects nothing else.
/// </para>
/// </remarks>
internal sealed class ExtensionRegistry
{
    private readonly RuntimeCore _core;
    private readonly object _sync = new();
    private readonly Dictionary<string, PointEntry> _points = new(StringComparer.Ordinal);
    private long _sequence;

    public ExtensionRegistry(RuntimeCore core)
    {
        _core = core;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _points.Count;
            }
        }
    }

    public IExtensionPointRegistration Declare(OwnershipScope scope, ExtensionPointDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RuntimeName.Validate(definition.ExtensionPointId, nameof(definition));
        Validation.ValidateDisplayName(definition.Name, nameof(definition));
        Validation.ValidateVersion(definition.ContractVersion, nameof(definition));
        var operations = Validation.CopyOperations(definition.Operations, allowEmpty: true, nameof(definition));
        var metadata = Validation.CopyMetadata(definition.Metadata, nameof(definition));
        _core.EnsureCanRegister(scope);

        var point = new PointEntry(this, scope, definition, operations, metadata);
        lock (_sync)
        {
            if (_points.TryGetValue(definition.ExtensionPointId, out var existing))
            {
                throw new PluginRuntimeException(
                    PluginErrorCodes.DuplicateExtensionPoint,
                    $"Extension point '{definition.ExtensionPointId}' is already declared by '{existing.Owner}'.");
            }

            _points.Add(point.ExtensionPointId, point);
        }

        if (!scope.TryAdd(point))
        {
            lock (_sync)
            {
                _points.Remove(point.ExtensionPointId);
            }

            throw RuntimeCore.ScopeClosed(scope);
        }

        _core.Log(LogLevel.Information, "Extensions", $"Extension point '{point.ExtensionPointId}' {point.ContractVersion} declared.", scope.Owner);
        return point;
    }

    public IExtensionContribution Contribute(
        OwnershipScope scope,
        ExtensionContributionDefinition definition,
        PluginInvocationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        RuntimeName.Validate(definition.ContributionId, nameof(definition));
        RuntimeName.Validate(definition.ExtensionPointId, nameof(definition));
        Validation.ValidateVersion(definition.ContractVersion, nameof(definition));
        var displayName = definition.DisplayName ?? definition.ContributionId;
        Validation.ValidateDisplayName(displayName, nameof(definition));
        var metadata = Validation.CopyMetadata(definition.Metadata, nameof(definition));
        _core.EnsureCanRegister(scope);

        PointEntry? point;
        lock (_sync)
        {
            point = _points.GetValueOrDefault(definition.ExtensionPointId);
        }

        if (point is null || point.IsReleased)
        {
            throw new PluginRuntimeException(
                PluginErrorCodes.ExtensionPointNotFound,
                $"Extension point '{definition.ExtensionPointId}' is not declared.");
        }

        // Owner state is read outside the registry lock (locks are never nested).
        if (!point.IsActive)
        {
            throw new PluginRuntimeException(
                PluginErrorCodes.ExtensionPointUnavailable,
                $"Extension point '{definition.ExtensionPointId}' is being removed because its owner is shutting down.");
        }

        if (!Validation.IsContractCompatible(point.ContractVersion, definition.ContractVersion))
        {
            throw new PluginRuntimeException(
                PluginErrorCodes.ExtensionVersionIncompatible,
                $"Contribution '{definition.ContributionId}' targets contract {definition.ContractVersion}, but extension point " +
                $"'{definition.ExtensionPointId}' provides {point.ContractVersion}.");
        }

        var contribution = new ContributionEntry(this, scope, point, definition, displayName, metadata, handler);
        lock (_sync)
        {
            // The point may have been removed since it was looked up; removal sets IsReleased
            // before it takes this lock to detach contributions.
            if (point.IsReleased)
            {
                throw new PluginRuntimeException(
                    PluginErrorCodes.ExtensionPointNotFound,
                    $"Extension point '{definition.ExtensionPointId}' was removed.");
            }

            if (point.Contributions.Any(existing => string.Equals(existing.ContributionId, contribution.ContributionId, StringComparison.Ordinal)))
            {
                throw new PluginRuntimeException(
                    PluginErrorCodes.DuplicateContribution,
                    $"Extension point '{point.ExtensionPointId}' already has a contribution '{contribution.ContributionId}'.");
            }

            contribution.Sequence = ++_sequence;
            point.Contributions.Add(contribution);
        }

        if (!scope.TryAdd(contribution))
        {
            RemoveContribution(contribution);
            throw RuntimeCore.ScopeClosed(scope);
        }

        _core.Log(
            LogLevel.Information,
            "Extensions",
            $"Contribution '{contribution.ContributionId}' added to extension point '{point.ExtensionPointId}'.",
            scope.Owner);
        return contribution;
    }

    public IReadOnlyList<ExtensionPointDescriptor> GetExtensionPoints()
    {
        (PointEntry Point, int Count)[] snapshot;
        lock (_sync)
        {
            snapshot = _points.Values.Select(point => (point, point.Contributions.Count)).ToArray();
        }

        return snapshot
            .OrderBy(item => item.Point.ExtensionPointId, StringComparer.Ordinal)
            .Select(item => item.Point.ToDescriptor(item.Count))
            .ToArray();
    }

    public ExtensionPointDescriptor? GetExtensionPoint(string extensionPointId)
    {
        ArgumentNullException.ThrowIfNull(extensionPointId);
        PointEntry? point;
        int count;
        lock (_sync)
        {
            point = _points.GetValueOrDefault(extensionPointId);
            count = point?.Contributions.Count ?? 0;
        }

        return point?.ToDescriptor(count);
    }

    public IReadOnlyList<ExtensionContributionDescriptor> GetContributions(string extensionPointId)
    {
        ArgumentNullException.ThrowIfNull(extensionPointId);
        return SnapshotContributions(extensionPointId)
            .Where(contribution => contribution.IsActive)
            .Select(contribution => contribution.Descriptor)
            .ToArray();
    }

    public ValueTask<PluginInvocationResult> InvokeAsync(
        OwnershipScope caller,
        string extensionPointId,
        string contributionId,
        string operation,
        JsonPayload? payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extensionPointId);
        ArgumentNullException.ThrowIfNull(contributionId);
        ArgumentNullException.ThrowIfNull(operation);
        var targetId = $"{extensionPointId}/{contributionId}";
        payload ??= JsonPayload.Null;

        if (CheckInvocation(caller, extensionPointId, operation, payload, out var point) is { } rejection)
        {
            return ValueTask.FromResult(rejection with { TargetId = targetId });
        }

        ContributionEntry? contribution;
        lock (_sync)
        {
            contribution = point!.Contributions.FirstOrDefault(entry => string.Equals(entry.ContributionId, contributionId, StringComparison.Ordinal));
        }

        if (contribution is null)
        {
            return ValueTask.FromResult(HandlerInvoker.Rejected(
                targetId,
                operation,
                PluginErrorCodes.ContributionNotFound,
                $"Extension point '{extensionPointId}' has no contribution '{contributionId}'."));
        }

        return InvokeContributionAsync(caller, contribution, operation, payload, cancellationToken);
    }

    public async ValueTask<ExtensionInvocationResult> InvokeAllAsync(
        OwnershipScope caller,
        string extensionPointId,
        string operation,
        JsonPayload? payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extensionPointId);
        ArgumentNullException.ThrowIfNull(operation);
        payload ??= JsonPayload.Null;

        if (CheckInvocation(caller, extensionPointId, operation, payload, out _) is { } rejection)
        {
            return new ExtensionInvocationResult(extensionPointId, operation, PluginOperationStatus.Rejected, [], rejection.ErrorCode, rejection.Message);
        }

        var contributions = SnapshotContributions(extensionPointId);
        var results = new List<PluginInvocationResult>(contributions.Length);
        foreach (var contribution in contributions)
        {
            if (contribution.IsReleased)
            {
                continue;
            }

            results.Add(await InvokeContributionAsync(caller, contribution, operation, payload, cancellationToken).ConfigureAwait(false));
        }

        var status = cancellationToken.IsCancellationRequested && results.Any(result => result.Status == PluginOperationStatus.Canceled)
            ? PluginOperationStatus.Canceled
            : results.All(result => result.IsSuccess)
                ? PluginOperationStatus.Succeeded
                : PluginOperationStatus.Failed;
        return new ExtensionInvocationResult(extensionPointId, operation, status, results);
    }

    internal void RemoveContribution(ContributionEntry contribution)
    {
        lock (_sync)
        {
            contribution.Point?.Contributions.Remove(contribution);
        }
    }

    internal async ValueTask RemovePointAsync(PointEntry point)
    {
        ContributionEntry[] contributions;
        lock (_sync)
        {
            if (_points.TryGetValue(point.ExtensionPointId, out var current) && ReferenceEquals(current, point))
            {
                _points.Remove(point.ExtensionPointId);
            }

            contributions = [.. point.Contributions];
            point.Contributions.Clear();
        }

        foreach (var contribution in contributions)
        {
            // Removes the contribution from its contributor's scope and clears its handler.
            await contribution.ReleaseAsync().ConfigureAwait(false);
        }

        _core.Log(
            LogLevel.Information,
            "Extensions",
            $"Extension point '{point.ExtensionPointId}' removed with {contributions.Length} contribution(s).",
            point.Owner);
    }

    private PluginInvocationResult? CheckInvocation(
        OwnershipScope caller,
        string extensionPointId,
        string operation,
        JsonPayload payload,
        out PointEntry? point)
    {
        point = null;
        if (_core.IsDisposed)
        {
            return HandlerInvoker.Rejected(extensionPointId, operation, PluginErrorCodes.EngineDisposed, "The runtime has been disposed.");
        }

        if (!caller.CanCallOut)
        {
            return HandlerInvoker.Rejected(extensionPointId, operation, PluginErrorCodes.ScopeClosed, $"Owner '{caller.Owner}' has stopped and cannot invoke contributions.");
        }

        if (!RuntimeName.IsValid(extensionPointId) || !RuntimeName.IsValid(operation))
        {
            return HandlerInvoker.Rejected(extensionPointId, operation, PluginErrorCodes.InvalidIdentifier, "The extension point ID or operation name is not a valid runtime name.");
        }

        lock (_sync)
        {
            point = _points.GetValueOrDefault(extensionPointId);
        }

        if (point is null || point.IsReleased)
        {
            return HandlerInvoker.Rejected(extensionPointId, operation, PluginErrorCodes.ExtensionPointNotFound, $"Extension point '{extensionPointId}' is not declared.");
        }

        if (!point.IsActive)
        {
            return HandlerInvoker.Rejected(extensionPointId, operation, PluginErrorCodes.ExtensionPointUnavailable, $"Extension point '{extensionPointId}' is being removed.", point.Owner);
        }

        if (!point.SupportsOperation(operation))
        {
            return HandlerInvoker.Rejected(extensionPointId, operation, PluginErrorCodes.OperationNotSupported, $"Extension point '{extensionPointId}' does not define operation '{operation}'.", point.Owner);
        }

        if (!_core.IsPayloadWithinLimit(payload))
        {
            return HandlerInvoker.Rejected(extensionPointId, operation, PluginErrorCodes.PayloadTooLarge, _core.PayloadTooLargeMessage(payload));
        }

        return null;
    }

    private ValueTask<PluginInvocationResult> InvokeContributionAsync(
        OwnershipScope caller,
        ContributionEntry contribution,
        string operation,
        JsonPayload payload,
        CancellationToken cancellationToken)
    {
        var request = new PluginRequest(operation, payload, caller.Owner, null);
        return HandlerInvoker.InvokeAsync(
            _core,
            contribution,
            contribution.TargetId,
            "Extensions",
            PluginErrorCodes.ContributionUnavailable,
            PluginErrorCodes.ContributionNotFound,
            request,
            cancellationToken);
    }

    private ContributionEntry[] SnapshotContributions(string extensionPointId)
    {
        lock (_sync)
        {
            return _points.TryGetValue(extensionPointId, out var point) ? [.. point.Contributions] : [];
        }
    }
}

internal sealed class PointEntry : OwnedResource, IExtensionPointRegistration
{
    private readonly ExtensionRegistry _registry;
    private readonly string _displayName;
    private readonly IReadOnlyList<string> _operations;
    private readonly IReadOnlyDictionary<string, string> _metadata;

    public PointEntry(
        ExtensionRegistry registry,
        OwnershipScope scope,
        ExtensionPointDefinition definition,
        IReadOnlyList<string> operations,
        IReadOnlyDictionary<string, string> metadata)
        : base(scope, ResourceKind.ExtensionPoint, definition.ExtensionPointId)
    {
        _registry = registry;
        ExtensionPointId = definition.ExtensionPointId;
        _displayName = definition.Name;
        ContractVersion = definition.ContractVersion;
        _operations = operations;
        _metadata = metadata;
    }

    public string ExtensionPointId { get; }

    public Version ContractVersion { get; }

    /// <summary>Guarded by the registry lock.</summary>
    public List<ContributionEntry> Contributions { get; } = [];

    public ExtensionPointDescriptor Descriptor => _registry.GetExtensionPoint(ExtensionPointId) is { } current && current.RegistrationId == ResourceId
        ? current
        : ToDescriptor(0);

    public bool SupportsOperation(string operation) =>
        _operations.Count == 0 || _operations.Contains(operation, StringComparer.Ordinal);

    public ExtensionPointDescriptor ToDescriptor(int contributionCount) =>
        new(ResourceId, ExtensionPointId, _displayName, ContractVersion, _operations, _metadata, Owner, IsActive, contributionCount, CreatedAt);

    protected override ValueTask ReleaseCoreAsync() => _registry.RemovePointAsync(this);
}

internal sealed class ContributionEntry : InvocableEntry, IExtensionContribution
{
    private readonly ExtensionRegistry _registry;
    private readonly string _displayName;
    private readonly IReadOnlyDictionary<string, string> _metadata;
    private PointEntry? _point;

    public ContributionEntry(
        ExtensionRegistry registry,
        OwnershipScope scope,
        PointEntry point,
        ExtensionContributionDefinition definition,
        string displayName,
        IReadOnlyDictionary<string, string> metadata,
        PluginInvocationHandler handler)
        : base(scope, ResourceKind.ExtensionContribution, $"{definition.ExtensionPointId}/{definition.ContributionId}", handler)
    {
        _registry = registry;
        _point = point;
        ContributionId = definition.ContributionId;
        ExtensionPointId = definition.ExtensionPointId;
        ContractVersion = definition.ContractVersion;
        _displayName = displayName;
        _metadata = metadata;
    }

    public string ContributionId { get; }

    public string ExtensionPointId { get; }

    public Version ContractVersion { get; }

    public long Sequence { get; set; }

    public string TargetId => Name;

    /// <summary>The target point, or <see langword="null"/> after release.</summary>
    public PointEntry? Point => Volatile.Read(ref _point);

    public ExtensionContributionDescriptor Descriptor =>
        new(ResourceId, ContributionId, ExtensionPointId, ContractVersion, _displayName, _metadata, Owner, IsActive, CreatedAt);

    protected override ValueTask ReleaseCoreAsync()
    {
        _registry.RemoveContribution(this);
        ClearHandler();
        Volatile.Write(ref _point, null);
        return ValueTask.CompletedTask;
    }
}
