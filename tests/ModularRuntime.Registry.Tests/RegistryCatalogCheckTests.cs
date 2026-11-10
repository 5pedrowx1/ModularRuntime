using System.Security.Cryptography;
using ModularRuntime.Contracts;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Registry.Tests;

/// <summary>
/// The public verification of a proposed catalog revision (what the registry CI runs, with no
/// private key): signature and key purpose, validity, rollback, immutability of published versions,
/// revocations, package files, and the supported plugin API.
/// </summary>
public sealed class RegistryCatalogCheckTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new("registry-check");
    private readonly TestSigningKey _key = new("test-official-1");

    public void Dispose()
    {
        _temp.Dispose();
        _key.Dispose();
    }

    private string Dir(string name) => Path.Combine(_temp.DirectoryPath, name);

    private static string Api => PluginApiVersion.Supported.ToString(2);

    /// <summary>A package file with random bytes under <paramref name="root"/>/packages and its entry.</summary>
    private RegistryEntry Package(string root, string version, string? status = null, string? reason = null, string? keyId = null, string? api = null)
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var name = $"com.example.hello-{version}.zip";
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        File.WriteAllBytes(Path.Combine(root, "packages", name), bytes);
        return new RegistryEntry
        {
            Id = "com.example.hello",
            Name = "Hello",
            Description = "Test entry",
            Author = "Tests",
            Category = "Examples",
            Version = version,
            ApiVersion = api ?? Api,
            MinimumRuntimeVersion = "0.6.0",
            PublishedAt = DateTimeOffset.UtcNow,
            DownloadUrl = "packages/" + name,
            PackageSha256 = Sha256Hex.Compute(bytes),
            PackageSize = bytes.Length,
            SigningKeyId = keyId ?? _key.KeyId,
            Status = status,
            StatusReason = reason,
        };
    }

    private (byte[] Registry, byte[] Signature) Write(string directory, long revision, IReadOnlyList<RegistryEntry> entries, TestSigningKey? key = null, IReadOnlyList<RevokedKey>? revokedKeys = null)
    {
        TestPackages.WriteRegistry(directory, key ?? _key, revision, entries, revokedKeys: revokedKeys);
        return (File.ReadAllBytes(Path.Combine(directory, "registry.json")), File.ReadAllBytes(Path.Combine(directory, "registry.sig")));
    }

    [Fact]
    public void SignedCatalog_WithMatchingPackages_IsAccepted()
    {
        var root = Dir("r1");
        var (registry, signature) = Write(root, 1, [Package(root, "1.0.0"), Package(root, "1.1.0")]);

        var result = RegistryCatalogCheck.Check(registry, signature, _key.TrustStore(), new RegistryCheckOptions { PackagesRoot = root });

        Assert.Equal(1, result.Document.Revision);
        Assert.Equal(_key.KeyId, result.SigningKey.KeyId);
        Assert.Equal(2, result.CheckedPackages);
    }

    [Fact]
    public void TamperedCatalog_UntrustedKey_AndPackagesOnlyKey_AreRejected()
    {
        var root = Dir("sig");
        var (registry, signature) = Write(root, 1, [Package(root, "1.0.0")]);

        var tampered = (byte[])registry.Clone();
        tampered[^3] ^= 0x01;
        Assert.ThrowsAny<PackageException>(() => RegistryCatalogCheck.Check(tampered, signature, _key.TrustStore()));

        using var stranger = new TestSigningKey("someone-else");
        Assert.ThrowsAny<PackageException>(() => RegistryCatalogCheck.Check(registry, signature, stranger.TrustStore()));

        using var packagesOnly = new TestSigningKey("packages-only", KeyPurposes.Packages);
        var (other, otherSignature) = Write(Dir("packages-only"), 1, [Package(root, "1.0.0")], packagesOnly);
        Assert.ThrowsAny<PackageException>(() => RegistryCatalogCheck.Check(other, otherSignature, packagesOnly.TrustStore()));

        Assert.ThrowsAny<PackageException>(() => RegistryCatalogCheck.Check(registry, signature, _key.TrustStore(revoked: true)));
    }

    [Fact]
    public void OlderOrEqualRevision_IsRejectedAsRollback()
    {
        var root = Dir("rollback");
        var first = Package(root, "1.0.0");
        var (revision2, _) = Write(Dir("rev2"), 2, [first]);
        var (revision1, signature1) = Write(Dir("rev1"), 1, [first]);
        var (again2, signatureAgain2) = Write(Dir("rev2b"), 2, [first]);

        Assert.Equal(RegistryErrorCodes.Rollback, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(revision1, signature1, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = revision2 })).Code);
        Assert.Equal(RegistryErrorCodes.Rollback, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(again2, signatureAgain2, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = revision2 })).Code);
    }

    [Fact]
    public void PublishedVersions_AreImmutable_AndCannotBeDropped()
    {
        var root = Dir("immutable");
        var published = Package(root, "1.0.0");
        var (previous, _) = Write(Dir("prev"), 1, [published]);

        var changed = published with { PackageSha256 = new string('b', 64) };
        var (withChange, changeSignature) = Write(Dir("changed"), 2, [changed]);
        Assert.Equal(RegistryErrorCodes.Conflict, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(withChange, changeSignature, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = previous })).Code);

        var (dropped, droppedSignature) = Write(Dir("dropped"), 2, [Package(root, "1.1.0")]);
        Assert.Equal(RegistryErrorCodes.Conflict, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(dropped, droppedSignature, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = previous })).Code);

        var (added, addedSignature) = Write(Dir("added"), 2, [published, Package(root, "1.1.0")]);
        Assert.Equal(2, RegistryCatalogCheck.Check(added, addedSignature, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = previous }).Document.Revision);
    }

    [Fact]
    public void Revocations_StayRevoked()
    {
        var root = Dir("revoked");
        var published = Package(root, "1.0.0");
        var revoked = published with { Status = RegistryEntryStatus.Revoked, StatusReason = "Malicious behavior." };
        RevokedKey[] revokedKeys = [new() { KeyId = "old-key", Reason = "Exposed.", RevokedAt = DateTimeOffset.UtcNow.AddDays(-1) }];
        var (previous, _) = Write(Dir("prev"), 1, [revoked], revokedKeys: revokedKeys);

        var (reinstated, reinstatedSignature) = Write(Dir("reinstated"), 2, [published], revokedKeys: revokedKeys);
        Assert.Equal(RegistryErrorCodes.Conflict, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(reinstated, reinstatedSignature, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = previous })).Code);

        var (keyDropped, keyDroppedSignature) = Write(Dir("key-dropped"), 2, [revoked], revokedKeys: []);
        Assert.Equal(RegistryErrorCodes.Conflict, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(keyDropped, keyDroppedSignature, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = previous })).Code);
    }

    [Fact]
    public void PackageFiles_MustMatchTheirEntries_AndPublishedOnesNeedATrustedPackagesKey()
    {
        var root = Dir("files");
        var entry = Package(root, "1.0.0");
        var (registry, signature) = Write(root, 1, [entry]);

        File.WriteAllBytes(Path.Combine(root, "packages", "com.example.hello-1.0.0.zip"), RandomNumberGenerator.GetBytes(64));
        Assert.Equal(RegistryErrorCodes.Invalid, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(registry, signature, _key.TrustStore(), new RegistryCheckOptions { PackagesRoot = root })).Code);

        File.Delete(Path.Combine(root, "packages", "com.example.hello-1.0.0.zip"));
        Assert.Equal(RegistryErrorCodes.Invalid, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(registry, signature, _key.TrustStore(), new RegistryCheckOptions { PackagesRoot = root })).Code);

        var otherRoot = Dir("untrusted-package-key");
        var (foreign, foreignSignature) = Write(otherRoot, 1, [Package(otherRoot, "1.0.0", keyId: "not-trusted")]);
        Assert.Equal(RegistryErrorCodes.Invalid, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(foreign, foreignSignature, _key.TrustStore(), new RegistryCheckOptions { PackagesRoot = otherRoot })).Code);
    }

    [Fact]
    public void NewVersions_MustTargetTheSupportedPluginApi()
    {
        var root = Dir("api");
        var (previous, _) = Write(Dir("prev"), 1, [Package(root, "1.0.0")]);
        var (stale, staleSignature) = Write(Dir("stale"), 2, [RegistryDocument.Parse(previous).Plugins[0], Package(root, "1.1.0", api: "0.2")]);

        Assert.Equal(RegistryErrorCodes.Invalid, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(stale, staleSignature, _key.TrustStore(), new RegistryCheckOptions { PreviousRegistry = previous })).Code);
    }

    [Fact]
    public void ExpiredCatalogs_AndOverlyLongValidity_AreRejected()
    {
        var root = Dir("validity");
        var (registry, signature) = Write(root, 1, [Package(root, "1.0.0")]);

        Assert.Equal(RegistryErrorCodes.Expired, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(registry, signature, _key.TrustStore(), new RegistryCheckOptions { Now = DateTimeOffset.UtcNow.AddDays(31) })).Code);
        Assert.Equal(RegistryErrorCodes.Invalid, Assert.Throws<RegistryException>(() =>
            RegistryCatalogCheck.Check(registry, signature, _key.TrustStore(), new RegistryCheckOptions { MaxValidity = TimeSpan.FromDays(7) })).Code);
    }
}
