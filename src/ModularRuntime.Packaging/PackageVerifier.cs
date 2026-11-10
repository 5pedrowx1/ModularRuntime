using ModularRuntime.Contracts;
using ModularRuntime.Loader;

namespace ModularRuntime.Packaging;

/// <summary>Trust level established for a package or installation.</summary>
public enum PackageTrust
{
    /// <summary>Integrity manifest signed by a trusted official key, every file matches it.</summary>
    Official,

    /// <summary>No official signature (a local package). Structure, limits, and the file inventory are
    /// validated; who made the package is not.</summary>
    Unverified,
}

/// <summary>What the caller expects the package to be (from the authenticated registry entry).</summary>
public sealed record PackageExpectation
{
    public PluginId? PluginId { get; init; }

    public Version? Version { get; init; }

    /// <summary>SHA-256 of the whole package file, from the signed registry.</summary>
    public string? PackageSha256 { get; init; }

    /// <summary>Key ID the registry says signed the package.</summary>
    public string? SigningKeyId { get; init; }

    /// <summary>Reject packages without a valid official signature.</summary>
    public bool RequireOfficialSignature { get; init; }
}

/// <summary>Stages of package verification, for progress reporting.</summary>
public enum VerificationStage
{
    HashingPackage,
    ReadingArchive,
    CheckingSignature,
    Extracting,
    CheckingFiles,
    CheckingManifest,
}

/// <summary>A package that passed every check, extracted into a daemon-owned directory.</summary>
public sealed record VerifiedPackage(
    PluginCandidate Candidate,
    PackageTrust Trust,
    TrustedKey? SigningKey,
    string PackageSha256,
    long PackageSize,
    byte[] IntegrityManifestBytes,
    byte[]? SignatureBytes,
    IReadOnlyList<IntegrityFile> Files,
    string ExtractedDirectory)
{
    public PluginId PluginId => Candidate.Descriptor.Id;

    public Version Version => Candidate.Descriptor.Version;
}

/// <summary>
/// Verifies a staged package and extracts it. No plugin assembly is loaded or executed: archive
/// entries are only read as bytes, and <c>plugin.json</c> is validated by the Loader's manifest
/// reader, which reads JSON and checks that files exist.
/// </summary>
/// <remarks>
/// Order (every failure stops the verification and is reported with a <see cref="PackageErrorCodes"/> code):
/// <list type="number">
/// <item>SHA-256 of the package file, compared with the authenticated registry value when given.</item>
/// <item>Archive structure and limits (<see cref="PackageArchive"/>).</item>
/// <item>Signature of the exact <c>integrity.json</c> bytes with a trusted key. A signature that
/// is present but invalid or by an unknown key rejects the package; it is never downgraded to unsigned.</item>
/// <item>Integrity manifest parsed; plugin ID and version compared with the expectation; the set of
/// archive content entries must equal the listed files (no extra, no missing).</item>
/// <item>Extraction with real size limits; every extracted file's size and SHA-256 compared.</item>
/// <item><c>plugin.json</c> validated; its ID and version must equal the manifest's.</item>
/// <item>Plugin API compatibility.</item>
/// </list>
/// </remarks>
public sealed class PackageVerifier(TrustStore trustStore, PackageLimits? limits = null)
{
    private readonly PackageLimits _limits = limits ?? PackageLimits.Default;

    public PackageLimits Limits => _limits;

    public async Task<VerifiedPackage> VerifyAndExtractAsync(
        string stagedPackagePath,
        string extractDirectory,
        PackageExpectation expectation,
        IProgress<VerificationStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);

        progress?.Report(VerificationStage.HashingPackage);
        var packageSize = new FileInfo(stagedPackagePath).Length;
        if (packageSize > _limits.MaxPackageBytes)
        {
            throw new PackageException(PackageErrorCodes.PackageTooLarge, $"The package is larger than {_limits.MaxPackageBytes} bytes.");
        }

        var packageSha256 = await Sha256Hex.ComputeFileAsync(stagedPackagePath, cancellationToken).ConfigureAwait(false);
        if (expectation.PackageSha256 is { } expectedHash && !Sha256Hex.Equal(expectedHash, packageSha256))
        {
            throw new PackageException(PackageErrorCodes.PackageHashMismatch, "The package's SHA-256 does not match the value in the signed registry.");
        }

        progress?.Report(VerificationStage.ReadingArchive);
        using var archive = PackageArchive.Open(stagedPackagePath, _limits);

        progress?.Report(VerificationStage.CheckingSignature);
        TrustedKey? signingKey = null;
        if (archive.SignatureBytes is not null)
        {
            if (archive.IntegrityManifestBytes is null)
            {
                throw new PackageException(PackageErrorCodes.IntegrityManifestInvalid, "The package has integrity.sig but no integrity.json.");
            }

            var envelope = SignatureEnvelope.Parse(archive.SignatureBytes, _limits.MaxSignatureBytes);
            signingKey = trustStore.Verify(archive.IntegrityManifestBytes, envelope, KeyPurposes.Packages);
            if (expectation.SigningKeyId is { } expectedKey && !string.Equals(expectedKey, signingKey.KeyId, StringComparison.Ordinal))
            {
                throw new PackageException(PackageErrorCodes.IdentityMismatch, $"The package is signed by key '{signingKey.KeyId}', but the registry names '{expectedKey}'.");
            }
        }
        else if (expectation.RequireOfficialSignature)
        {
            throw new PackageException(PackageErrorCodes.SignatureMissing, "The package has no official signature (integrity.sig).");
        }

        // An unsigned package may still carry an author-written integrity.json; it must then be
        // consistent, but it proves nothing about who made the package.
        IntegrityManifest? manifest = archive.IntegrityManifestBytes is null
            ? null
            : IntegrityManifest.Parse(archive.IntegrityManifestBytes, _limits);

        if (manifest is not null)
        {
            CheckIdentity(manifest.PluginId, manifest.Version, expectation, "integrity manifest");
            CompareListedEntries(manifest, archive);
        }

        progress?.Report(VerificationStage.Extracting);
        var extracted = await archive.ExtractAsync(extractDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);

        progress?.Report(VerificationStage.CheckingFiles);
        if (manifest is not null)
        {
            CompareFiles(manifest.Files, extracted);
        }

        progress?.Report(VerificationStage.CheckingManifest);
        var candidate = await ReadPluginManifestAsync(extractDirectory, cancellationToken).ConfigureAwait(false);
        var descriptor = candidate.Descriptor;
        if (manifest is not null &&
            (descriptor.Id != manifest.PluginId || PluginVersions.Compare(descriptor.Version, manifest.Version) != 0))
        {
            throw new PackageException(PackageErrorCodes.IdentityMismatch, $"plugin.json declares '{descriptor.Id}' {descriptor.Version}, but the integrity manifest is for '{manifest.PluginId}' {manifest.Version}.");
        }

        CheckIdentity(descriptor.Id, descriptor.Version, expectation, "plugin.json");
        if (!PluginApiVersion.IsCompatible(descriptor.ApiVersion))
        {
            throw new PackageException(PackageErrorCodes.ApiVersionIncompatible, $"The plugin requires API {descriptor.ApiVersion}; this runtime supports {PluginApiVersion.Supported}.");
        }

        var inventory = manifest is not null && signingKey is not null
            ? archive.IntegrityManifestBytes!
            : IntegrityManifest.Create(descriptor.Id, descriptor.Version, extracted).Serialize();

        return new VerifiedPackage(
            candidate,
            signingKey is null ? PackageTrust.Unverified : PackageTrust.Official,
            signingKey,
            packageSha256,
            packageSize,
            inventory,
            signingKey is null ? null : archive.SignatureBytes,
            extracted,
            Path.GetFullPath(extractDirectory));
    }

    /// <summary>Reads and validates the extracted <c>plugin.json</c> with the Loader's rules.</summary>
    public static async Task<PluginCandidate> ReadPluginManifestAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await new PluginLoader().ReadInstallationAsync(directory, cancellationToken).ConfigureAwait(false);
        if (result.Plugins.Count == 1)
        {
            return result.Plugins[0];
        }

        var reason = result.Errors.Count > 0 ? result.Errors[0].Message : "plugin.json is missing.";
        var code = result.Errors.Any(e => e.Code == PluginDiscoveryErrorCodes.ApiVersionIncompatible)
            ? PackageErrorCodes.ApiVersionIncompatible
            : PackageErrorCodes.PluginManifestInvalid;
        throw new PackageException(code, code == PackageErrorCodes.ApiVersionIncompatible ? reason : $"Invalid plugin.json: {reason}");
    }

    private static void CheckIdentity(PluginId pluginId, Version version, PackageExpectation expectation, string source)
    {
        if (expectation.PluginId is { } expectedId && expectedId != pluginId)
        {
            throw new PackageException(PackageErrorCodes.IdentityMismatch, $"The {source} is for plugin '{pluginId}', but '{expectedId}' was expected.");
        }

        if (expectation.Version is { } expectedVersion && PluginVersions.Compare(expectedVersion, version) != 0)
        {
            throw new PackageException(PackageErrorCodes.IdentityMismatch, $"The {source} is for version {version}, but {expectedVersion} was expected.");
        }
    }

    private static void CompareListedEntries(IntegrityManifest manifest, PackageArchive archive)
    {
        var listed = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var entry in archive.ContentEntries)
        {
            if (!listed.Remove(entry.FullName, out var file))
            {
                throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{PackagePaths.Describe(entry.FullName)}' is in the package but not in the integrity manifest.", entry.FullName);
            }

            if (file.Size != entry.Length)
            {
                throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{entry.FullName}' has size {entry.Length}, but the integrity manifest says {file.Size}.", entry.FullName);
            }
        }

        if (listed.Count > 0)
        {
            var missing = listed.Keys.Order(StringComparer.Ordinal).First();
            throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{missing}' is listed in the integrity manifest but missing from the package.", missing);
        }
    }

    internal static void CompareFiles(IReadOnlyList<IntegrityFile> expected, IReadOnlyList<IntegrityFile> actual)
    {
        var byPath = actual.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var file in expected)
        {
            if (!byPath.Remove(file.Path, out var found))
            {
                throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{file.Path}' is missing.", file.Path);
            }

            if (found.Size != file.Size)
            {
                throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{file.Path}' has size {found.Size}; expected {file.Size}.", file.Path);
            }

            if (!Sha256Hex.Equal(found.Sha256, file.Sha256))
            {
                throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{file.Path}' does not match its SHA-256.", file.Path);
            }
        }

        if (byPath.Count > 0)
        {
            var extra = byPath.Keys.Order(StringComparer.Ordinal).First();
            throw new PackageException(PackageErrorCodes.IntegrityMismatch, $"'{extra}' is not listed in the integrity manifest.", extra);
        }
    }
}
