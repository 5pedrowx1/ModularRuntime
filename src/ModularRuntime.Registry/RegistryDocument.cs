using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModularRuntime.Contracts;
using ModularRuntime.Packaging;

namespace ModularRuntime.Registry;

/// <summary>Stable error codes of the registry client.</summary>
public static class RegistryErrorCodes
{
    public const string NotConfigured = "registry_not_configured";
    public const string Unavailable = "registry_unavailable";
    public const string SignatureInvalid = "registry_signature_invalid";
    public const string Invalid = "registry_invalid";
    public const string Rollback = "registry_rollback";
    public const string Conflict = "registry_conflict";
    public const string Expired = "registry_expired";
    public const string NotYetValid = "registry_not_yet_valid";
    public const string DownloadFailed = "download_failed";
    public const string DownloadTooLarge = "download_too_large";
    public const string PluginNotFound = "registry_plugin_not_found";
    public const string VersionWithdrawn = "package_withdrawn";
    public const string VersionRevoked = "package_revoked";
}

/// <summary>Publication state of one version in the signed catalog (schema 2).</summary>
public static class RegistryEntryStatus
{
    /// <summary>Available in the Store.</summary>
    public const string Published = "published";

    /// <summary>
    /// Removed from the Store (for example a publishing mistake or the end of support). Not offered
    /// for installation; existing installations keep working.
    /// </summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>
    /// Revoked for security (malicious package, serious vulnerability, compromised provenance).
    /// Never installed; an installed copy is not loaded again.
    /// </summary>
    public const string Revoked = "revoked";

    public static bool IsValid(string? status) => status is Published or Withdrawn or Revoked;
}

/// <summary>A package signing key revoked through the signed catalog (schema 2).</summary>
public sealed record RevokedKey
{
    public required string KeyId { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset RevokedAt { get; init; }
}

public sealed class RegistryException : Exception
{
    public RegistryException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public RegistryException()
        : this(RegistryErrorCodes.Invalid, "The registry is invalid.")
    {
    }

    public RegistryException(string message)
        : this(RegistryErrorCodes.Invalid, message)
    {
    }

    public RegistryException(string message, Exception innerException)
        : this(RegistryErrorCodes.Invalid, message, innerException)
    {
    }

    public string Code { get; }
}

/// <summary>One approved plugin version in the official registry.</summary>
public sealed record RegistryEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Author { get; init; }

    public required string Category { get; init; }

    public required string Version { get; init; }

    /// <summary>Plugin API version (<c>major.minor</c>) the package targets.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Lowest ModularRuntime version that may install the package.</summary>
    public required string MinimumRuntimeVersion { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>Absolute <c>https</c> URL, or a path relative to the registry location.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>SHA-256 of the package file (64 lowercase hex characters).</summary>
    public required string PackageSha256 { get; init; }

    public required long PackageSize { get; init; }

    /// <summary>Key ID that signed the package's integrity manifest.</summary>
    public required string SigningKeyId { get; init; }

    public string? License { get; init; }

    /// <summary>Repository of the approved source code.</summary>
    public string? SourceRepository { get; init; }

    /// <summary>Commit of the approved source the package was built from.</summary>
    public string? SourceCommit { get; init; }

    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// Schema 2: <see cref="RegistryEntryStatus"/>; <see langword="null"/> means published (schema 1).
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Schema 2: why the version was withdrawn or revoked (required when revoked).</summary>
    public string? StatusReason { get; init; }

    /// <summary>Schema 2: when the status last changed.</summary>
    public DateTimeOffset? StatusChangedAt { get; init; }

    /// <summary>Schema 2: an https link to the security advisory of a revocation.</summary>
    public string? AdvisoryUrl { get; init; }

    /// <summary>Schema 2: the controlled build the package was made from (build record ID).</summary>
    public string? BuildId { get; init; }

    /// <summary>Schema 2: SHA-256 of the signed approval record the package was signed under.</summary>
    public string? ApprovalSha256 { get; init; }

    [JsonIgnore]
    public string EffectiveStatus => Status ?? RegistryEntryStatus.Published;

    [JsonIgnore]
    public bool IsPublished => EffectiveStatus == RegistryEntryStatus.Published;

    [JsonIgnore]
    public bool IsRevoked => EffectiveStatus == RegistryEntryStatus.Revoked;

    [JsonIgnore]
    public PluginId PluginId => PluginId.Parse(Id);

    [JsonIgnore]
    public Version ParsedVersion => PluginVersions.TryParse(Version, out var v) ? v : new Version(0, 0);
}

/// <summary>
/// The official catalog (<c>registry.json</c>). Authenticated by <c>registry.sig</c> over its exact
/// bytes; never trusted before that signature is verified with an official registry key.
/// </summary>
/// <remarks>
/// <c>revision</c> increases with every publication and protects against rollback to an older,
/// validly signed catalog; <c>expiresAt</c> bounds how long a captured catalog can be replayed.
/// </remarks>
public sealed partial record RegistryDocument
{
    /// <summary>
    /// Schema 2 (v0.6) adds version status (withdrawn, revoked), build provenance, and revoked package
    /// signing keys. Schema 1 catalogs are still read.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public const int MinimumSchemaVersion = 1;
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(400);

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required int SchemaVersion { get; init; }

    public required long Revision { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required IReadOnlyList<RegistryEntry> Plugins { get; init; }

    /// <summary>Schema 2: package signing keys revoked by this catalog.</summary>
    public IReadOnlyList<RevokedKey>? RevokedKeys { get; init; }

    /// <summary>Parses and validates bytes whose signature was already verified.</summary>
    public static RegistryDocument Parse(ReadOnlySpan<byte> bytes)
    {
        RegistryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RegistryDocument>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid($"not valid registry JSON ({exception.Message})");
        }

        if (document is null)
        {
            throw Invalid("empty document");
        }

        document.Validate();
        return document;
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    /// <summary>
    /// The entry of a specific version (in any status), or without <paramref name="version"/> the
    /// highest <em>published</em> version: withdrawn and revoked versions are never offered as the
    /// latest one.
    /// </summary>
    public RegistryEntry? Find(PluginId pluginId, Version? version = null) =>
        Plugins.Where(e => e.Id == pluginId.Value && (version is null ? e.IsPublished : PluginVersions.Compare(e.ParsedVersion, version) == 0))
            .OrderByDescending(e => e.ParsedVersion, Comparer<Version>.Create(PluginVersions.Compare))
            .FirstOrDefault();

    /// <summary>Every version of a plugin, highest first.</summary>
    public IReadOnlyList<RegistryEntry> Versions(PluginId pluginId) =>
        [.. Plugins.Where(e => e.Id == pluginId.Value).OrderByDescending(e => e.ParsedVersion, Comparer<Version>.Create(PluginVersions.Compare))];

    /// <summary>
    /// Why an installed package must not be loaded according to this (verified) catalog: its exact
    /// version and package are revoked, or its signing key is revoked. <see langword="null"/> otherwise.
    /// </summary>
    public RegistryRevocation? FindRevocation(PluginId pluginId, Version version, string packageSha256, string? signingKeyId)
    {
        var entry = Plugins.FirstOrDefault(e => e.Id == pluginId.Value && PluginVersions.Compare(e.ParsedVersion, version) == 0);
        if (entry is { IsRevoked: true } && Sha256Hex.Equal(entry.PackageSha256, packageSha256))
        {
            return new RegistryRevocation(RegistryRevocationKind.Package, entry.StatusReason ?? "Revoked.", entry.AdvisoryUrl, entry.StatusChangedAt, null);
        }

        if (signingKeyId is not null && RevokedKeys?.FirstOrDefault(k => k.KeyId == signingKeyId) is { } key)
        {
            return new RegistryRevocation(RegistryRevocationKind.SigningKey, key.Reason, null, key.RevokedAt, key.KeyId);
        }

        return null;
    }

    public void Validate()
    {
        if (SchemaVersion is < MinimumSchemaVersion or > CurrentSchemaVersion)
        {
            throw Invalid($"unsupported schemaVersion {SchemaVersion}");
        }

        if (SchemaVersion < 2 && (RevokedKeys is not null || Plugins?.Any(UsesSchema2Fields) == true))
        {
            throw Invalid("status, provenance, and revoked keys require schemaVersion 2");
        }

        if (RevokedKeys is { } revokedKeys)
        {
            if (revokedKeys.Count > 1000)
            {
                throw Invalid("too many revoked keys");
            }

            foreach (var key in revokedKeys)
            {
                if (key is null || !TrustedKey.IsValidKeyId(key.KeyId))
                {
                    throw Invalid("a revoked key has an invalid keyId");
                }

                Text(key.Reason, 1, 1000, key.KeyId, "reason");
            }

            if (revokedKeys.Select(k => k.KeyId).Distinct(StringComparer.Ordinal).Count() != revokedKeys.Count)
            {
                throw Invalid("a key is revoked more than once");
            }
        }

        if (Revision < 1)
        {
            throw Invalid("revision must be a positive integer");
        }

        if (ExpiresAt <= GeneratedAt || ExpiresAt - GeneratedAt > MaxValidity)
        {
            throw Invalid($"expiresAt must be after generatedAt and at most {MaxValidity.TotalDays:0} days later");
        }

        if (Plugins is null || Plugins.Count > 10_000)
        {
            throw Invalid("plugins is missing or has more than 10000 entries");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Plugins)
        {
            ValidateEntry(entry);
            if (!seen.Add($"{entry.Id}@{entry.ParsedVersion}"))
            {
                throw Invalid($"'{entry.Id}' {entry.Version} is listed more than once");
            }
        }
    }

    private static void ValidateEntry(RegistryEntry entry)
    {
        if (entry is null)
        {
            throw Invalid("null plugin entry");
        }

        var who = PluginId.IsValid(entry.Id) ? entry.Id : "(invalid id)";
        if (!PluginId.IsValid(entry.Id))
        {
            throw Invalid("a plugin entry has an invalid id");
        }

        Text(entry.Name, 1, 200, who, "name");
        Text(entry.Description, 1, 1000, who, "description");
        Text(entry.Author, 1, 200, who, "author");
        if (!CategoryPattern().IsMatch(entry.Category ?? string.Empty))
        {
            throw Invalid($"'{who}': category must be 1-50 letters, digits, spaces, '&' or '-'");
        }

        if (!PluginVersions.TryParse(entry.Version, out _))
        {
            throw Invalid($"'{who}': invalid version");
        }

        if (!ApiVersionPattern().IsMatch(entry.ApiVersion ?? string.Empty))
        {
            throw Invalid($"'{who}': apiVersion must be major.minor");
        }

        if (!PluginVersions.TryParse(entry.MinimumRuntimeVersion, out _))
        {
            throw Invalid($"'{who}': invalid minimumRuntimeVersion");
        }

        if (!IsValidDownloadUrl(entry.DownloadUrl))
        {
            throw Invalid($"'{who}': downloadUrl must be an absolute https URL or a simple relative path");
        }

        if (!Sha256Hex.IsValid(entry.PackageSha256))
        {
            throw Invalid($"'{who}': packageSha256 must be 64 lowercase hexadecimal characters");
        }

        if (entry.PackageSize is < 1 or > 1024L * 1024 * 1024)
        {
            throw Invalid($"'{who}': invalid packageSize");
        }

        if (!TrustedKey.IsValidKeyId(entry.SigningKeyId))
        {
            throw Invalid($"'{who}': invalid signingKeyId");
        }

        if (entry.License is not null)
        {
            Text(entry.License, 1, 100, who, "license");
        }

        if (entry.SourceRepository is not null &&
            (!Uri.TryCreate(entry.SourceRepository, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps || entry.SourceRepository.Length > 500))
        {
            throw Invalid($"'{who}': sourceRepository must be an https URL");
        }

        if (entry.SourceCommit is not null && !CommitPattern().IsMatch(entry.SourceCommit))
        {
            throw Invalid($"'{who}': sourceCommit must be 7-64 lowercase hexadecimal characters");
        }

        if (entry.ReleaseNotes is not null)
        {
            Text(entry.ReleaseNotes, 1, 4000, who, "releaseNotes");
        }

        if (entry.Status is not null && !RegistryEntryStatus.IsValid(entry.Status))
        {
            throw Invalid($"'{who}': status must be published, withdrawn, or revoked");
        }

        if (entry.StatusReason is not null)
        {
            Text(entry.StatusReason, 1, 1000, who, "statusReason");
        }

        if (entry.IsRevoked && entry.StatusReason is null)
        {
            throw Invalid($"'{who}' {entry.Version}: a revoked version needs a statusReason");
        }

        if (entry.AdvisoryUrl is not null &&
            (!Uri.TryCreate(entry.AdvisoryUrl, UriKind.Absolute, out var advisory) || advisory.Scheme != Uri.UriSchemeHttps || entry.AdvisoryUrl.Length > 500))
        {
            throw Invalid($"'{who}': advisoryUrl must be an https URL");
        }

        if (entry.BuildId is not null && !IdentifierPattern().IsMatch(entry.BuildId))
        {
            throw Invalid($"'{who}': buildId must be 1-100 letters, digits, '.', '_' or '-'");
        }

        if (entry.ApprovalSha256 is not null && !Sha256Hex.IsValid(entry.ApprovalSha256))
        {
            throw Invalid($"'{who}': approvalSha256 must be 64 lowercase hexadecimal characters");
        }
    }

    private static bool UsesSchema2Fields(RegistryEntry? entry) =>
        entry is not null &&
        (entry.Status is not null || entry.StatusReason is not null || entry.StatusChangedAt is not null ||
         entry.AdvisoryUrl is not null || entry.BuildId is not null || entry.ApprovalSha256 is not null);

    internal static bool IsValidDownloadUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 1000)
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && !url.StartsWith('/'))
        {
            return absolute.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(absolute.UserInfo);
        }

        return RelativePathPattern().IsMatch(url) && !url.Split('/').Any(s => s is "." or "..");
    }

    private static void Text(string? value, int min, int max, string who, string field)
    {
        if (value is null || value.Trim().Length < min || value.Length > max || value.Any(c => char.IsControl(c) && c != '\n'))
        {
            throw Invalid($"'{who}': {field} must be {min}-{max} characters without control characters");
        }
    }

    private static RegistryException Invalid(string message) => new(RegistryErrorCodes.Invalid, $"The registry is invalid: {message}.");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 &\-]{0,49}$")]
    private static partial Regex CategoryPattern();

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}$")]
    private static partial Regex ApiVersionPattern();

    [GeneratedRegex(@"^[0-9a-f]{7,64}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]+(/[A-Za-z0-9._\-]+)*$")]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$")]
    private static partial Regex IdentifierPattern();
}

public enum RegistryRevocationKind
{
    /// <summary>The exact package of this version is revoked.</summary>
    Package,

    /// <summary>The key that signed the package is revoked.</summary>
    SigningKey,
}

/// <summary>A revocation found in a verified catalog.</summary>
public sealed record RegistryRevocation(RegistryRevocationKind Kind, string Reason, string? AdvisoryUrl, DateTimeOffset? RevokedAt, string? KeyId);
