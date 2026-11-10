using ModularRuntime.Contracts;
using ModularRuntime.Packaging;

namespace ModularRuntime.Registry;

/// <summary>Options of <see cref="RegistryCatalogCheck.Check"/>.</summary>
public sealed record RegistryCheckOptions
{
    /// <summary>The previous signed revision (bytes of its <c>registry.json</c>), to enforce rollback and immutability rules.</summary>
    public byte[]? PreviousRegistry { get; init; }

    /// <summary>Folder that holds relative <c>downloadUrl</c> files; each one is checked against its entry.</summary>
    public string? PackagesRoot { get; init; }

    /// <summary>Longest allowed validity window (<c>expiresAt - generatedAt</c>).</summary>
    public TimeSpan MaxValidity { get; init; } = TimeSpan.FromDays(90);

    /// <summary>The current time (injectable for tests).</summary>
    public DateTimeOffset? Now { get; init; }
}

/// <summary>What <see cref="RegistryCatalogCheck.Check"/> verified.</summary>
public sealed record RegistryCheckResult(RegistryDocument Document, TrustedKey SigningKey, int CheckedPackages);

/// <summary>
/// Public verification of a signed catalog revision, without any private key: the signature by a
/// trusted registry key, the schema, the validity window, a revision greater than the previous
/// one, published versions unchanged (immutable), revoked versions and keys kept revoked, new
/// versions on the supported plugin API, and every local package matching its entry. The official
/// publishing tools apply the same rules before signing; the public registry's CI and anyone else
/// can run them on a proposed revision (<c>dotnet modularruntime registry-check</c>).
/// </summary>
/// <remarks>
/// Signature problems throw <see cref="PackageException"/>; every other problem throws
/// <see cref="RegistryException"/>.
/// </remarks>
public static class RegistryCatalogCheck
{
    /// <summary>Maximum size of <c>registry.sig</c>.</summary>
    public const int MaxSignatureBytes = 16 * 1024;

    public static RegistryCheckResult Check(byte[] registry, byte[] signature, TrustStore trust, RegistryCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(trust);
        options ??= new RegistryCheckOptions();

        // An invalid, untrusted, revoked, or wrong-purpose signature throws PackageException (signature_* / unknown_signing_key).
        var key = trust.Verify(registry, SignatureEnvelope.Parse(signature, MaxSignatureBytes), KeyPurposes.Registry);
        var document = RegistryDocument.Parse(registry);
        if (document.ExpiresAt - document.GeneratedAt > options.MaxValidity)
        {
            throw new RegistryException(RegistryErrorCodes.Invalid, $"The catalog is valid for more than {options.MaxValidity.TotalDays:0} days.");
        }

        if (document.ExpiresAt <= (options.Now ?? DateTimeOffset.UtcNow))
        {
            throw new RegistryException(RegistryErrorCodes.Expired, $"The catalog expired at {document.ExpiresAt:O}.");
        }

        if (options.PreviousRegistry is { } previousBytes)
        {
            var previous = RegistryDocument.Parse(previousBytes);
            if (document.Revision <= previous.Revision)
            {
                throw new RegistryException(RegistryErrorCodes.Rollback, $"Revision {document.Revision} is not greater than the previous revision {previous.Revision}.");
            }

            CheckPublicationRules(document.Plugins, previous, allowRemoval: false);
            foreach (var revoked in previous.RevokedKeys ?? [])
            {
                if (document.RevokedKeys?.Any(k => k.KeyId == revoked.KeyId) != true)
                {
                    throw new RegistryException(RegistryErrorCodes.Conflict, $"Key '{revoked.KeyId}' was revoked in the previous revision and is no longer listed as revoked.");
                }
            }
        }

        var checkedPackages = 0;
        if (options.PackagesRoot is { } packagesRoot)
        {
            var root = Path.GetFullPath(packagesRoot);
            foreach (var entry in document.Plugins.Where(e => !Uri.TryCreate(e.DownloadUrl, UriKind.Absolute, out _)))
            {
                var file = PackagePaths.Resolve(root, entry.DownloadUrl);
                if (!File.Exists(file))
                {
                    throw new RegistryException(RegistryErrorCodes.Invalid, $"'{entry.Id}' {entry.Version}: {entry.DownloadUrl} is missing.");
                }

                var bytes = File.ReadAllBytes(file);
                if (bytes.Length != entry.PackageSize || !Sha256Hex.Equal(Sha256Hex.Compute(bytes), entry.PackageSha256))
                {
                    throw new RegistryException(RegistryErrorCodes.Invalid, $"'{entry.Id}' {entry.Version}: {entry.DownloadUrl} does not match its size and SHA-256 in the catalog.");
                }

                if (entry.IsPublished &&
                    (trust.Keys.FirstOrDefault(k => k.KeyId == entry.SigningKeyId) is not { Revoked: false } packageKey || !packageKey.Purposes.HasFlag(KeyPurposes.Packages)))
                {
                    throw new RegistryException(RegistryErrorCodes.Invalid, $"'{entry.Id}' {entry.Version} is published but signed by '{entry.SigningKeyId}', which is not a trusted packages key.");
                }

                checkedPackages++;
            }
        }

        return new RegistryCheckResult(document, key, checkedPackages);
    }

    /// <summary>
    /// The publication rules between two revisions: a version listed before keeps its package, key,
    /// download location, API, and minimum runtime (immutable); a revoked version stays revoked
    /// unless <paramref name="allowReinstate"/>; nothing is dropped unless <paramref name="allowRemoval"/>;
    /// and every new version targets the plugin API this release supports.
    /// </summary>
    public static void CheckPublicationRules(IReadOnlyList<RegistryEntry> entries, RegistryDocument? previous, bool allowRemoval, bool allowReinstate = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var published = previous?.Plugins.ToDictionary(e => $"{e.Id}@{e.ParsedVersion}", StringComparer.Ordinal) ?? [];
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = $"{entry.Id}@{entry.ParsedVersion}";
            current.Add(key);
            if (published.TryGetValue(key, out var earlier))
            {
                if (!Sha256Hex.Equal(earlier.PackageSha256, entry.PackageSha256) || earlier.PackageSize != entry.PackageSize ||
                    earlier.SigningKeyId != entry.SigningKeyId || earlier.DownloadUrl != entry.DownloadUrl ||
                    earlier.ApiVersion != entry.ApiVersion || earlier.MinimumRuntimeVersion != entry.MinimumRuntimeVersion)
                {
                    throw new RegistryException(
                        RegistryErrorCodes.Conflict,
                        $"'{entry.Id}' {entry.Version} is already published (package SHA-256 {earlier.PackageSha256}). A published version is immutable: " +
                        "publish the change as a new version.");
                }

                if (earlier.IsRevoked && !entry.IsRevoked && !allowReinstate)
                {
                    throw new RegistryException(RegistryErrorCodes.Conflict, $"'{entry.Id}' {entry.Version} was revoked; it stays revoked unless it is explicitly reinstated after an incident review.");
                }

                continue;
            }

            if (!Version.TryParse(entry.ApiVersion, out var api) || !PluginApiVersion.IsCompatible(api))
            {
                throw new RegistryException(
                    RegistryErrorCodes.Invalid,
                    $"'{entry.Id}' {entry.Version} targets plugin API {entry.ApiVersion}, but this release of ModularRuntime supports API {PluginApiVersion.Supported.ToString(2)}. " +
                    "Rebuild it against the current contracts before publishing.");
            }
        }

        var dropped = published.Keys.Where(k => !current.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (dropped.Count > 0 && !allowRemoval)
        {
            throw new RegistryException(
                RegistryErrorCodes.Conflict,
                $"The previous revision lists {string.Join(", ", dropped)}, which this revision drops. Keep published versions (withdraw or revoke them instead), or pass --allow-removal.");
        }
    }
}
