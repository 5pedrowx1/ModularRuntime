namespace ModularRuntime.Packaging;

/// <summary>Stable error codes of package inspection, verification, and installation.</summary>
public static class PackageErrorCodes
{
    public const string PackageTooLarge = "package_too_large";
    public const string PackageCorrupt = "package_corrupt";
    public const string UnsafeEntryPath = "unsafe_entry_path";
    public const string DuplicateEntry = "duplicate_entry";
    public const string UnsupportedEntryType = "unsupported_entry_type";
    public const string TooManyEntries = "too_many_entries";
    public const string EntryTooLarge = "entry_too_large";
    public const string ExpansionLimitExceeded = "expansion_limit_exceeded";
    public const string IntegrityManifestInvalid = "integrity_manifest_invalid";
    public const string SignatureInvalid = "signature_invalid";
    public const string SignatureMissing = "signature_missing";
    public const string UnknownSigningKey = "unknown_signing_key";
    public const string SigningKeyRevoked = "signing_key_revoked";
    public const string IntegrityMismatch = "integrity_mismatch";
    public const string IdentityMismatch = "identity_mismatch";
    public const string PluginManifestInvalid = "plugin_manifest_invalid";
    public const string ApiVersionIncompatible = "api_version_incompatible";
    public const string RuntimeVersionIncompatible = "runtime_version_incompatible";
    public const string PackageHashMismatch = "package_hash_mismatch";
    public const string InstallationConflict = "installation_conflict";
    public const string InstallationNotFound = "installation_not_found";
    public const string InstallationModified = "installation_modified";
    public const string InstallationFailed = "installation_failed";
    public const string FilesInUse = "files_in_use";
    public const string TrustStoreInvalid = "trust_store_invalid";
}

/// <summary>A package or installation failed a check. The message is safe to show to users.</summary>
public sealed class PackageException : Exception
{
    public PackageException(string code, string message, string? path = null)
        : base(message)
    {
        Code = code;
        Path = path;
    }

    public PackageException()
        : this(PackageErrorCodes.PackageCorrupt, "The package is invalid.")
    {
    }

    public PackageException(string message)
        : this(PackageErrorCodes.PackageCorrupt, message)
    {
    }

    public PackageException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = PackageErrorCodes.PackageCorrupt;
    }

    /// <summary>One of <see cref="PackageErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>The package-relative path concerned, when there is one.</summary>
    public string? Path { get; }
}

/// <summary>
/// Limits applied while reading and extracting packages. Sizes are checked against the bytes
/// actually produced by decompression, not only against the sizes declared by the archive.
/// </summary>
public sealed record PackageLimits
{
    /// <summary>Maximum size of the package file itself.</summary>
    public long MaxPackageBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Maximum number of archive entries (files and directory entries).</summary>
    public int MaxEntries { get; init; } = 4096;

    /// <summary>Maximum decompressed size of one file.</summary>
    public long MaxFileBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Maximum total decompressed size of all files.</summary>
    public long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Maximum ratio between decompressed and compressed size of an entry larger than 1 MiB.</summary>
    public int MaxCompressionRatio { get; init; } = 200;

    /// <summary>Maximum number of path segments.</summary>
    public int MaxDepth { get; init; } = 16;

    /// <summary>Maximum length of a package-relative path, in characters.</summary>
    public int MaxPathLength { get; init; } = 200;

    /// <summary>Maximum size of <c>integrity.json</c>.</summary>
    public int MaxManifestBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Maximum size of a signature file.</summary>
    public int MaxSignatureBytes { get; init; } = 16 * 1024;

    public static PackageLimits Default { get; } = new();
}
