namespace ModularRuntime.Contracts;

/// <summary>Definition supplied when declaring an extension point.</summary>
/// <param name="ExtensionPointId">Unique identifier (a <see cref="RuntimeName"/>).</param>
/// <param name="Name">Display name, 1-200 characters.</param>
/// <param name="ContractVersion">Version of the contract contributions implement.</param>
public sealed record ExtensionPointDefinition(
    string ExtensionPointId,
    string Name,
    Version ContractVersion)
{
    /// <summary>Operations contributions support. Empty means the point does not restrict operations.</summary>
    public IReadOnlyList<string> Operations { get; init; } = [];

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Immutable snapshot describing an extension point.</summary>
public sealed record ExtensionPointDescriptor(
    Guid RegistrationId,
    string ExtensionPointId,
    string Name,
    Version ContractVersion,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, string> Metadata,
    ResourceOwner Owner,
    bool IsActive,
    int ContributionCount,
    DateTimeOffset CreatedAt);

/// <summary>Definition supplied when contributing to an extension point.</summary>
/// <param name="ContributionId">Identifier, unique within the extension point.</param>
/// <param name="ExtensionPointId">Target extension point.</param>
/// <param name="ContractVersion">
/// Contract version the contribution implements: same major as the point and minor
/// less than or equal to the point's minor.
/// </param>
public sealed record ExtensionContributionDefinition(
    string ContributionId,
    string ExtensionPointId,
    Version ContractVersion)
{
    /// <summary>Display name; defaults to <see cref="ContributionId"/>.</summary>
    public string? DisplayName { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Immutable snapshot describing a contribution.</summary>
public sealed record ExtensionContributionDescriptor(
    Guid RegistrationId,
    string ContributionId,
    string ExtensionPointId,
    Version ContractVersion,
    string DisplayName,
    IReadOnlyDictionary<string, string> Metadata,
    ResourceOwner Owner,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>Handle held by the owner of an extension point. Disposing it removes the point and all contributions.</summary>
public interface IExtensionPointRegistration : IPluginResource
{
    ExtensionPointDescriptor Descriptor { get; }
}

/// <summary>Handle held by a contributor. Disposing it removes only this contribution.</summary>
public interface IExtensionContribution : IPluginResource
{
    ExtensionContributionDescriptor Descriptor { get; }
}

/// <summary>Aggregated outcome of invoking every contribution of an extension point.</summary>
/// <remarks>
/// <see cref="Results"/> is in contribution order. <see cref="Status"/> is
/// <see cref="PluginOperationStatus.Succeeded"/> when all invocations succeeded (or there were
/// none), <see cref="PluginOperationStatus.Failed"/> when any failed or was rejected,
/// <see cref="PluginOperationStatus.Canceled"/> when canceled, and
/// <see cref="PluginOperationStatus.Rejected"/> when the point itself was not usable.
/// </remarks>
public sealed record ExtensionInvocationResult(
    string ExtensionPointId,
    string Operation,
    PluginOperationStatus Status,
    IReadOnlyList<PluginInvocationResult> Results,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool IsSuccess => Status == PluginOperationStatus.Succeeded;
}

/// <summary>
/// Registry of extension points and contributions for one runtime.
/// </summary>
/// <remarks>
/// Extension points may be declared by the host or by plugins. When the owner of a point
/// shuts down (or disposes its registration), the point and all its contributions are
/// removed. When a contributor shuts down, only its contributions are removed.
/// </remarks>
public interface IExtensionRegistry
{
    /// <exception cref="PluginRuntimeException"><c>duplicate_extension_point</c>, <c>scope_closed</c>, <c>engine_disposed</c>.</exception>
    IExtensionPointRegistration DeclareExtensionPoint(ExtensionPointDefinition definition);

    /// <exception cref="PluginRuntimeException">
    /// <c>extension_point_not_found</c>, <c>extension_point_unavailable</c>,
    /// <c>extension_version_incompatible</c>, <c>duplicate_contribution</c>, <c>scope_closed</c>,
    /// <c>engine_disposed</c>.
    /// </exception>
    IExtensionContribution Contribute(ExtensionContributionDefinition definition, PluginInvocationHandler handler);

    IReadOnlyList<ExtensionPointDescriptor> GetExtensionPoints();

    ExtensionPointDescriptor? GetExtensionPoint(string extensionPointId);

    /// <summary>Active contributions of a point in contribution order; empty when the point does not exist.</summary>
    IReadOnlyList<ExtensionContributionDescriptor> GetContributions(string extensionPointId);

    ValueTask<PluginInvocationResult> InvokeAsync(
        string extensionPointId,
        string contributionId,
        string operation,
        JsonPayload? payload = null,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes every active contribution sequentially in contribution order.</summary>
    ValueTask<ExtensionInvocationResult> InvokeAllAsync(
        string extensionPointId,
        string operation,
        JsonPayload? payload = null,
        CancellationToken cancellationToken = default);
}
