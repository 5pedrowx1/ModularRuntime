namespace ModularRuntime.Ipc.Contracts;

/// <summary>
/// Trust shown for an installed or previewed plugin. Only <see cref="OfficialVerified"/> means an
/// official signature verified and every file matched; none of the states means the code is safe.
/// </summary>
public enum TrustStateDto
{
    /// <summary>Signed by a trusted official key; signature and every file verified.</summary>
    OfficialVerified,

    /// <summary>Installed from a local ZIP without an official signature. Origin not verified.</summary>
    LocalUnverified,

    /// <summary>The official signature does not verify (bad signature, unknown or revoked key).</summary>
    VerificationFailed,

    /// <summary>Files changed since installation; the plugin will not be loaded.</summary>
    Modified,

    /// <summary>Not compatible with this runtime (plugin API or minimum runtime version).</summary>
    Incompatible,

    /// <summary>Copied into the plugin folder by hand; not managed by the package manager.</summary>
    Unmanaged,

    /// <summary>
    /// Revoked by the signed official catalog (the package, or the key that signed it). The
    /// installation is kept but is not loaded again.
    /// </summary>
    Revoked,
}

/// <summary>A revocation that applies to an installed package, from the signed catalog.</summary>
/// <param name="Kind">"package" (this exact package is revoked) or "signing-key" (the key that signed it is revoked).</param>
public sealed record RevocationDto(string Kind, string Reason, string? AdvisoryUrl, DateTimeOffset? RevokedAt, string? KeyId);

/// <summary>One version of a plugin in the catalog (the Store's version history).</summary>
public sealed record RegistryVersionDto(string Version, string Status, string ApiVersion, string MinimumRuntimeVersion, DateTimeOffset PublishedAt, bool Compatible, string? StatusReason);

public enum PackageOperationKindDto
{
    InstallOfficial,
    InstallLocal,
    Update,
    Remove,
}

public enum PackageOperationStageDto
{
    Queued,
    Downloading,
    Verifying,
    Stopping,
    Installing,
    Removing,
    Completed,
    Failed,
    Canceled,
}

public sealed record RegistryStatusDto(
    bool Configured,
    string? Location,
    bool Loaded,
    long? Revision,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? VerifiedAt,
    string? SigningKeyId,
    bool TestKeysTrusted,
    int PluginCount,
    long HighestAcceptedRevision,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record RegistryPluginDto(
    string Id,
    string Name,
    string Description,
    string Author,
    string Category,
    string Version,
    string ApiVersion,
    string MinimumRuntimeVersion,
    DateTimeOffset PublishedAt,
    long PackageSize,
    string PackageSha256,
    string SigningKeyId,
    string? License,
    string? SourceRepository,
    string? SourceCommit,
    string? ReleaseNotes,
    bool Compatible,
    string? IncompatibilityReason,
    string? InstalledVersion,
    bool UpdateAvailable,
    bool SigningKeyTrusted = false,
    bool SigningKeyIsTest = false,
    string Status = "published",
    string? StatusReason = null,
    string? AdvisoryUrl = null,
    string? BuildId = null,
    string? ApprovalSha256 = null,
    IReadOnlyList<RegistryVersionDto>? Versions = null,
    bool SigningKeyRevoked = false)
{
    /// <summary>The Store offers installation only for a published, compatible version signed by a trusted key.</summary>
    public bool Installable => Status == "published" && Compatible && SigningKeyTrusted && !SigningKeyRevoked;
}

public sealed record RegistryListResponse(RegistryStatusDto Status, IReadOnlyList<RegistryPluginDto> Plugins);

public sealed record VerificationDto(string Status, string Message, IReadOnlyList<string> Problems, DateTimeOffset CheckedAt);

public sealed record InstalledPackageDto(
    string PluginId,
    string Name,
    string Version,
    string? Author,
    string? Description,
    string Source,
    TrustStateDto Trust,
    string? SigningKeyId,
    bool SignedWithTestKey,
    string? PackageSha256,
    int FileCount,
    DateTimeOffset? InstalledAt,
    string? EngineState,
    VerificationDto? LastVerification,
    string? AvailableVersion,
    RevocationDto? Revocation = null,
    string? CatalogStatus = null,
    string? CatalogStatusReason = null);

public sealed record PackagesListResponse(long StateVersion, IReadOnlyList<InstalledPackageDto> Packages);

public sealed record PackageUpdateDto(string PluginId, string Name, string InstalledVersion, string AvailableVersion, string? ReleaseNotes, bool Compatible, string? IncompatibilityReason);

public sealed record UpdatesResponse(RegistryStatusDto Registry, IReadOnlyList<PackageUpdateDto> Updates);

public sealed record PackageOperationDto(
    string OperationId,
    PackageOperationKindDto Kind,
    string? PluginId,
    PackageOperationStageDto Stage,
    double? Progress,
    string Message,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    InstalledPackageDto? Result)
{
    public bool IsFinished => Stage is PackageOperationStageDto.Completed or PackageOperationStageDto.Failed or PackageOperationStageDto.Canceled;
}

public sealed record OperationsResponse(IReadOnlyList<PackageOperationDto> Operations);

public sealed record RegistryGetRequest(string PluginId);

public sealed record InstallOfficialRequest(string PluginId, string? Version = null);

/// <param name="StopIfRunning">Stop the plugin first when it is running; otherwise a running plugin is rejected with plugin_running.</param>
public sealed record PackageChangeRequest(string PluginId, bool StopIfRunning = false);

public sealed record UploadBeginRequest(string FileName, long Size);

public sealed record UploadBeginResponse(Guid UploadId, int MaxChunkBytes);

/// <param name="Data">Base64 bytes; chunks must be sent in order (<paramref name="Offset"/> = bytes received so far).</param>
public sealed record UploadChunkRequest(Guid UploadId, long Offset, string Data);

public sealed record UploadIdRequest(Guid UploadId);

/// <summary>What the daemon found in an uploaded package, before anything is installed.</summary>
public sealed record PackagePreviewDto(
    Guid UploadId,
    bool Valid,
    string? ErrorCode,
    string? ErrorMessage,
    string? PluginId,
    string? Name,
    string? Version,
    string? Author,
    string? Description,
    string? ApiVersion,
    TrustStateDto Trust,
    string? SigningKeyId,
    bool SignedWithTestKey,
    int FileCount,
    long PackageSize,
    string? PackageSha256,
    bool Compatible,
    string? InstalledVersion,
    bool ReplacesExisting,
    bool ExistingIsRunning);

/// <param name="AcceptUnverified">Required for packages without an official signature: the user confirmed the warning.</param>
public sealed record UploadInstallRequest(Guid UploadId, bool AcceptUnverified = false, bool StopIfRunning = false);

public sealed record OperationIdRequest(string OperationId);
