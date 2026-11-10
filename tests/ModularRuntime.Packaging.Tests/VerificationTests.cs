using System.Text;
using ModularRuntime.Contracts;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Packaging.Tests;

/// <summary>Signature and integrity verification of real packages built from the HelloPlugin sample.</summary>
public sealed class VerificationTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new("verify");
    private readonly TestSigningKey _key = new("test-official-1");

    public void Dispose()
    {
        _temp.Dispose();
        _key.Dispose();
    }

    private string Content(string? version = null) => TestPackages.CreateContent(_temp.DirectoryPath, "HelloPlugin", version);

    private string ZipPath => Path.Combine(_temp.DirectoryPath, Guid.NewGuid().ToString("N") + ".zip");

    private string ExtractPath => Path.Combine(_temp.DirectoryPath, "x-" + Guid.NewGuid().ToString("N"));

    private Task<VerifiedPackage> VerifyAsync(string zip, TrustStore? trust = null, PackageExpectation? expectation = null) =>
        new PackageVerifier(trust ?? _key.TrustStore()).VerifyAndExtractAsync(zip, ExtractPath, expectation ?? new PackageExpectation());

    private async Task<PackageException> RejectAsync(string zip, TrustStore? trust = null, PackageExpectation? expectation = null) =>
        await Assert.ThrowsAsync<PackageException>(() => VerifyAsync(zip, trust, expectation));

    [Fact]
    public async Task ValidOfficialSignature_IsVerified_WithEveryFileHashed()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key);

        var package = await VerifyAsync(zip, expectation: new PackageExpectation { RequireOfficialSignature = true, SigningKeyId = _key.KeyId });

        Assert.Equal(PackageTrust.Official, package.Trust);
        Assert.Equal(_key.KeyId, package.SigningKey!.KeyId);
        Assert.True(package.SigningKey.IsTestKey);
        Assert.Equal("com.example.hello", package.PluginId.Value);
        Assert.Contains(package.Files, f => f.Path == "plugin.json");
        Assert.Contains(package.Files, f => f.Path == "HelloPlugin.dll");
        foreach (var file in package.Files)
        {
            var bytes = File.ReadAllBytes(PackagePaths.Resolve(package.ExtractedDirectory, file.Path));
            Assert.Equal(file.Size, bytes.Length);
            Assert.Equal(Sha256Hex.Compute(bytes), file.Sha256);
        }
    }

    [Fact]
    public async Task InvalidSignatureBytes_AreRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, key: null, editManifestBytes: null);
        var manifest = (await PackageBuilder.CreateManifestAsync(Content())).Serialize();
        var signature = SignatureEnvelope.Sign(manifest, _key.Key, _key.KeyId);
        signature.Signature[10] ^= 0xFF;
        TestPackages.WriteZipUnchecked(Content(), manifest, signature.Serialize(), zip);

        Assert.Equal(PackageErrorCodes.SignatureInvalid, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task UnknownSigningKey_IsRejected()
    {
        using var stranger = new TestSigningKey("someone-else");
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, stranger);

        Assert.Equal(PackageErrorCodes.UnknownSigningKey, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task KeyWithTheSameIdButADifferentKey_IsRejected()
    {
        using var impostor = new TestSigningKey(_key.KeyId);
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, impostor);

        Assert.Equal(PackageErrorCodes.SignatureInvalid, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task RevokedKey_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key);

        Assert.Equal(PackageErrorCodes.SigningKeyRevoked, (await RejectAsync(zip, _key.TrustStore(revoked: true))).Code);
    }

    [Fact]
    public async Task KeyTrustedOnlyForTheRegistry_CannotSignPackages()
    {
        using var registryOnly = new TestSigningKey("registry-only", KeyPurposes.Registry);
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, registryOnly);

        Assert.Equal(PackageErrorCodes.UnknownSigningKey, (await RejectAsync(zip, registryOnly.TrustStore())).Code);
    }

    [Fact]
    public async Task KeyRotation_NewKeyVerifies_OldRevokedKeyIsRejected()
    {
        using var newKey = new TestSigningKey("test-official-2");
        var trust = new TrustStore([_key.ToTrustedKey(revoked: true), newKey.ToTrustedKey()]);
        var signedByNew = await TestPackages.BuildAsync(Content(), ZipPath, newKey);
        var signedByOld = await TestPackages.BuildAsync(Content(), ZipPath, _key);

        Assert.Equal("test-official-2", (await VerifyAsync(signedByNew, trust)).SigningKey!.KeyId);
        Assert.Equal(PackageErrorCodes.SigningKeyRevoked, (await RejectAsync(signedByOld, trust)).Code);
    }

    [Fact]
    public async Task ModifiedManifestAfterSigning_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, editManifestBytes: bytes => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("1.0.0", "1.0.1", StringComparison.Ordinal)));

        Assert.Equal(PackageErrorCodes.SignatureInvalid, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task ModifiedDll_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, tamper: dir => File.AppendAllText(Path.Combine(dir, "HelloPlugin.dll"), "x"));

        Assert.Equal(PackageErrorCodes.IntegrityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task ModifiedDllWithTheSameSize_IsRejectedByItsHash()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, tamper: dir =>
        {
            var path = Path.Combine(dir, "HelloPlugin.dll");
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(path, bytes);
        });

        var error = await RejectAsync(zip);
        Assert.Equal(PackageErrorCodes.IntegrityMismatch, error.Code);
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModifiedDependencyFile_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, tamper: dir => File.AppendAllText(Path.Combine(dir, "HelloPlugin.deps.json"), " "));

        Assert.Equal(PackageErrorCodes.IntegrityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task AdditionalFile_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, tamper: dir => File.WriteAllText(Path.Combine(dir, "extra.dll"), "payload"));

        var error = await RejectAsync(zip);
        Assert.Equal(PackageErrorCodes.IntegrityMismatch, error.Code);
        Assert.Equal("extra.dll", error.Path);
    }

    [Fact]
    public async Task MissingFile_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, tamper: dir => File.Delete(Path.Combine(dir, "HelloPlugin.deps.json")));

        Assert.Equal(PackageErrorCodes.IntegrityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task WrongHashInTheSignedManifest_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, editManifest: m =>
            m with { Files = [.. m.Files.Select(f => f.Path == "HelloPlugin.dll" ? f with { Sha256 = new string('0', 64) } : f)] });

        Assert.Equal(PackageErrorCodes.IntegrityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task WrongSizeInTheSignedManifest_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, editManifest: m =>
            m with { Files = [.. m.Files.Select(f => f.Path == "HelloPlugin.dll" ? f with { Size = f.Size + 1 } : f)] });

        Assert.Equal(PackageErrorCodes.IntegrityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task ManifestForAnotherPluginId_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, editManifest: m => m with { PluginId = PluginId.Parse("com.example.other") });

        Assert.Equal(PackageErrorCodes.IdentityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task ManifestForAnotherVersion_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key, editManifest: m => m with { Version = new Version(9, 9, 9) });

        Assert.Equal(PackageErrorCodes.IdentityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task RegistryExpectations_AreEnforced()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, _key);

        Assert.Equal(PackageErrorCodes.PackageHashMismatch, (await RejectAsync(zip, expectation: new PackageExpectation { PackageSha256 = new string('a', 64) })).Code);
        Assert.Equal(PackageErrorCodes.IdentityMismatch, (await RejectAsync(zip, expectation: new PackageExpectation { Version = new Version(2, 0) })).Code);
        Assert.Equal(PackageErrorCodes.IdentityMismatch, (await RejectAsync(zip, expectation: new PackageExpectation { PluginId = PluginId.Parse("com.example.other") })).Code);
        Assert.Equal(PackageErrorCodes.IdentityMismatch, (await RejectAsync(zip, expectation: new PackageExpectation { SigningKeyId = "other-key" })).Code);
    }

    [Fact]
    public async Task UnsignedPackage_IsUnverified_AndRejectedWhenAnOfficialSignatureIsRequired()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, key: null);

        var package = await VerifyAsync(zip);
        Assert.Equal(PackageTrust.Unverified, package.Trust);
        Assert.Null(package.SigningKey);
        Assert.Equal(package.Files.Count, IntegrityManifest.Parse(package.IntegrityManifestBytes, PackageLimits.Default).Files.Count);

        Assert.Equal(PackageErrorCodes.SignatureMissing, (await RejectAsync(zip, expectation: new PackageExpectation { RequireOfficialSignature = true })).Code);
    }

    [Fact]
    public async Task PackageWithoutAnyManifest_IsUnverified_AndGetsAnInventory()
    {
        var content = Content();
        var zip = ZipPath;
        TestPackages.WriteZipUnchecked(content, manifestBytes: null, signature: null, zip);

        var package = await VerifyAsync(zip);

        Assert.Equal(PackageTrust.Unverified, package.Trust);
        Assert.Contains("plugin.json", Encoding.UTF8.GetString(package.IntegrityManifestBytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsignedPackageWithAnInconsistentSelfDeclaredManifest_IsRejected()
    {
        var zip = await TestPackages.BuildAsync(Content(), ZipPath, key: null, tamper: dir => File.AppendAllText(Path.Combine(dir, "HelloPlugin.dll"), "x"));

        Assert.Equal(PackageErrorCodes.IntegrityMismatch, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task InvalidPluginManifest_IsRejected()
    {
        var content = TestPackages.CreateContent(_temp.DirectoryPath, "HelloPlugin", editManifest: m => m["entryAssembly"] = "../outside.dll");
        var zip = ZipPath;
        TestPackages.WriteZipUnchecked(content, manifestBytes: null, signature: null, zip);

        Assert.Equal(PackageErrorCodes.PluginManifestInvalid, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task IncompatibleApiVersion_IsRejected()
    {
        var content = TestPackages.CreateContent(_temp.DirectoryPath, "HelloPlugin", editManifest: m => m["apiVersion"] = "0.1");
        var manifest = TestPackages.UncheckedManifest(content, "com.example.hello", "1.0.0").Serialize();
        var zip = ZipPath;
        TestPackages.WriteZipUnchecked(content, manifest, _key.Sign(manifest), zip);

        Assert.Equal(PackageErrorCodes.ApiVersionIncompatible, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task SignatureWithoutManifest_IsRejected()
    {
        var zip = ZipPath;
        TestPackages.WriteZipUnchecked(Content(), manifestBytes: null, _key.Sign([1, 2, 3]), zip);

        Assert.Equal(PackageErrorCodes.IntegrityManifestInvalid, (await RejectAsync(zip)).Code);
    }

    [Fact]
    public async Task ManifestGeneration_IsDeterministic_AndTheZipIsByteForByteReproducible()
    {
        var content = Content();
        var first = (await PackageBuilder.CreateManifestAsync(content)).Serialize();
        var second = (await PackageBuilder.CreateManifestAsync(content)).Serialize();
        Assert.Equal(first, second);
        Assert.DoesNotContain((byte)'\r', first);

        var manifest = IntegrityManifest.Parse(first, PackageLimits.Default);
        Assert.Equal(manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal), manifest.Files.Select(f => f.Path));

        // ECDSA signatures are randomized, so the same signature bytes are packed twice.
        var signature = _key.Sign(first);
        var zipA = ZipPath;
        var zipB = ZipPath;
        PackageBuilder.WriteZip(content, manifest, first, signature, zipA);
        PackageBuilder.WriteZip(content, manifest, first, signature, zipB);
        Assert.Equal(File.ReadAllBytes(zipA), File.ReadAllBytes(zipB));
        Assert.Equal(signature, ExtractSignature(zipA));
    }

    [Fact]
    public async Task Builder_RefusesFilesChangedAfterTheManifestWasCreated()
    {
        var content = Content();
        var manifest = await PackageBuilder.CreateManifestAsync(content);
        File.AppendAllText(Path.Combine(content, "HelloPlugin.dll"), "late change");

        var error = Assert.Throws<PackageException>(() => PackageBuilder.WriteZip(content, manifest, manifest.Serialize(), null, ZipPath));
        Assert.Equal(PackageErrorCodes.IntegrityMismatch, error.Code);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}],"extra":1}""")]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","pluginId":"com.b","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"00000000000000000000000000000000000000000000000000000000000000AA"}]}""")]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"MD5","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"schemaVersion":2,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"a.dll","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"},{"path":"integrity.sig","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"},{"path":"PLUGIN.JSON","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"},{"path":"../x.dll","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    public void IntegrityManifest_IsParsedStrictly(string json)
    {
        var error = Assert.Throws<PackageException>(() => IntegrityManifest.Parse(Encoding.UTF8.GetBytes(json), PackageLimits.Default));
        Assert.Equal(PackageErrorCodes.IntegrityManifestInvalid, error.Code);
    }

    [Fact]
    public void IntegrityManifest_WithByteOrderMark_IsRejected()
    {
        var json = """{"schemaVersion":1,"pluginId":"com.a","version":"1.0","hashAlgorithm":"SHA-256","files":[{"path":"plugin.json","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        Assert.Equal(PackageErrorCodes.IntegrityManifestInvalid, Assert.Throws<PackageException>(() => IntegrityManifest.Parse(bytes, PackageLimits.Default)).Code);
    }

    [Fact]
    public void TrustStore_RejectsTestKeysInTheOfficialStore_AndTheShippedStoreHasNoTestKeys()
    {
        var bytes = TrustStore.Serialize([_key.ToTrustedKey()]);
        Assert.Equal(PackageErrorCodes.TrustStoreInvalid, Assert.Throws<PackageException>(() => TrustStore.Parse(bytes, allowTestKeys: false)).Code);
        Assert.Single(TrustStore.Parse(bytes, allowTestKeys: true).Keys);

        var official = TrustStore.LoadOfficial();
        Assert.False(official.ContainsTestKeys);
    }

    private static byte[]? ExtractSignature(string zip)
    {
        using var archive = PackageArchive.Open(zip, PackageLimits.Default);
        return archive.SignatureBytes;
    }
}
