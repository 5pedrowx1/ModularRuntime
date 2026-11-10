using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;

namespace ModularRuntime.TestSupport;

/// <summary>
/// An ephemeral P-256 key generated in memory for one test run. TEST ONLY: never written to the
/// repository, never trusted outside the test that created it.
/// </summary>
public sealed class TestSigningKey : IDisposable
{
    public TestSigningKey(string keyId = "test-key-1", KeyPurposes purposes = KeyPurposes.Packages | KeyPurposes.Registry)
    {
        KeyId = keyId;
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Purposes = purposes;
    }

    public string KeyId { get; }

    public ECDsa Key { get; }

    public KeyPurposes Purposes { get; }

    public TrustedKey ToTrustedKey(bool revoked = false) =>
        new(KeyId, Key.ExportSubjectPublicKeyInfo(), Purposes, revoked, IsTestKey: true);

    public TrustStore TrustStore(bool revoked = false) => new([ToTrustedKey(revoked)]);

    public byte[] Sign(byte[] data) => SignatureEnvelope.Sign(data, Key, KeyId).Serialize();

    public void Dispose() => Key.Dispose();
}

/// <summary>Builds real plugin packages (official, unsigned, or deliberately broken) for tests.</summary>
public static class TestPackages
{
    /// <summary>Copies a staged sample plugin into a fresh content directory and optionally edits plugin.json.</summary>
    public static string CreateContent(string root, string samplePlugin, string? version = null, Action<JsonObject>? editManifest = null)
    {
        var content = Path.Combine(root, "content-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(StagedPlugins.Root, samplePlugin), content);
        if (version is not null || editManifest is not null)
        {
            var manifest = StagedPlugins.ReadManifest(content);
            if (version is not null)
            {
                manifest["version"] = version;
            }

            editManifest?.Invoke(manifest);
            StagedPlugins.WriteManifest(content, manifest);
        }

        return content;
    }

    /// <summary>
    /// Builds a package from <paramref name="content"/>. <paramref name="tamper"/> runs after the
    /// manifest was created (and signed) and before the ZIP is written, to simulate modifications.
    /// </summary>
    public static async Task<string> BuildAsync(
        string content,
        string zipPath,
        TestSigningKey? key,
        Func<IntegrityManifest, IntegrityManifest>? editManifest = null,
        Action<string>? tamper = null,
        Func<byte[], byte[]>? editManifestBytes = null)
    {
        var manifest = await PackageBuilder.CreateManifestAsync(content);
        manifest = editManifest?.Invoke(manifest) ?? manifest;
        var manifestBytes = manifest.Serialize();
        var signature = key?.Sign(manifestBytes);
        manifestBytes = editManifestBytes?.Invoke(manifestBytes) ?? manifestBytes;
        tamper?.Invoke(content);
        WriteZipUnchecked(content, manifestBytes, signature, zipPath);
        return zipPath;
    }

    /// <summary>An integrity manifest of every file in <paramref name="content"/>, without validating plugin.json.</summary>
    public static IntegrityManifest UncheckedManifest(string content, string pluginId, string version) =>
        IntegrityManifest.Create(
            ModularRuntime.Contracts.PluginId.Parse(pluginId),
            Version.Parse(version),
            Directory.GetFiles(content, "*", SearchOption.AllDirectories).Select(file =>
            {
                var bytes = File.ReadAllBytes(file);
                return new IntegrityFile(Path.GetRelativePath(content, file).Replace('\\', '/'), bytes.Length, Sha256Hex.Compute(bytes));
            }));

    /// <summary>Writes a ZIP of every file in <paramref name="content"/> plus metadata, without consistency checks.</summary>
    public static void WriteZipUnchecked(string content, byte[]? manifestBytes, byte[]? signature, string zipPath)
    {
        using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(content, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(Path.GetRelativePath(content, file).Replace('\\', '/'));
            using var stream = entry.Open();
            stream.Write(File.ReadAllBytes(file));
        }

        if (manifestBytes is not null)
        {
            using var stream = zip.CreateEntry(PackagePaths.IntegrityManifestName).Open();
            stream.Write(manifestBytes);
        }

        if (signature is not null)
        {
            using var stream = zip.CreateEntry(PackagePaths.IntegritySignatureName).Open();
            stream.Write(signature);
        }
    }

    /// <summary>Writes a ZIP with arbitrary raw entries (names are not validated) for malicious-archive tests.</summary>
    public static string WriteRawZip(string zipPath, params (string Name, byte[] Data, int ExternalAttributes)[] entries)
    {
        using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var (name, data, attributes) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.ExternalAttributes = attributes;
            using var stream = entry.Open();
            stream.Write(data);
        }

        return zipPath;
    }

    /// <summary>
    /// Publishes a test registry into <paramref name="directory"/>: packages/*.zip, registry.json,
    /// registry.sig. Returns the entries.
    /// </summary>
    public static async Task<IReadOnlyList<RegistryEntry>> PublishRegistryAsync(
        string directory,
        TestSigningKey key,
        long revision,
        IEnumerable<(string Sample, string Version)> plugins,
        string workRoot,
        TimeSpan? validity = null,
        string minimumRuntimeVersion = "0.5.0",
        TestSigningKey? packageKey = null,
        Func<RegistryEntry, RegistryEntry>? editEntry = null,
        IReadOnlyList<RevokedKey>? revokedKeys = null)
    {
        packageKey ??= key;
        var packages = Path.Combine(directory, "packages");
        Directory.CreateDirectory(packages);
        var entries = new List<RegistryEntry>();
        foreach (var (sample, version) in plugins)
        {
            var content = CreateContent(workRoot, sample, version);
            var candidate = await PackageVerifier.ReadPluginManifestAsync(content, CancellationToken.None);
            var fileName = $"{candidate.Descriptor.Id}-{version}.zip";
            var zipPath = Path.Combine(packages, fileName);
            if (File.Exists(zipPath) && File.Exists(zipPath + ".entry"))
            {
                // Already published in an earlier revision of this test registry: reuse it unchanged.
                var existing = System.Text.Json.JsonSerializer.Deserialize<RegistryEntry>(File.ReadAllBytes(zipPath + ".entry"), EntryJson)!;
                if (existing.SigningKeyId == packageKey.KeyId && existing.MinimumRuntimeVersion == minimumRuntimeVersion &&
                    Sha256Hex.Equal(existing.PackageSha256, Sha256Hex.Compute(File.ReadAllBytes(zipPath))))
                {
                    entries.Add(editEntry?.Invoke(existing) ?? existing);
                    continue;
                }

                File.Delete(zipPath);
            }

            var zip = await BuildAsync(content, zipPath, packageKey);
            var bytes = await File.ReadAllBytesAsync(zip);
            var entry = new RegistryEntry
            {
                Id = candidate.Descriptor.Id.Value,
                Name = candidate.Descriptor.Name,
                Description = $"{candidate.Descriptor.Name} (test package)",
                Author = "ModularRuntime tests",
                Category = "Examples",
                Version = version,
                ApiVersion = candidate.Descriptor.ApiVersion.ToString(2),
                MinimumRuntimeVersion = minimumRuntimeVersion,
                PublishedAt = DateTimeOffset.UtcNow,
                DownloadUrl = "packages/" + fileName,
                PackageSha256 = Sha256Hex.Compute(bytes),
                PackageSize = bytes.Length,
                SigningKeyId = packageKey.KeyId,
            };
            File.WriteAllBytes(zip + ".entry", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entry, EntryJson));
            entries.Add(editEntry?.Invoke(entry) ?? entry);
        }

        WriteRegistry(directory, key, revision, entries, validity, revokedKeys);
        return entries;
    }

    private static readonly JsonSerializerOptions EntryJson = new(JsonSerializerDefaults.Web);

    public static void WriteRegistry(string directory, TestSigningKey key, long revision, IReadOnlyList<RegistryEntry> entries, TimeSpan? validity = null, IReadOnlyList<RevokedKey>? revokedKeys = null)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var usesSchema2 = revokedKeys is not null || entries.Any(e => e.Status is not null || e.BuildId is not null || e.ApprovalSha256 is not null);
        var document = new RegistryDocument
        {
            SchemaVersion = usesSchema2 ? 2 : 1,
            RevokedKeys = revokedKeys,
            Revision = revision,
            GeneratedAt = now.AddMinutes(-1),
            ExpiresAt = now + (validity ?? TimeSpan.FromDays(30)),
            Plugins = entries,
        };
        var bytes = document.Serialize();
        File.WriteAllBytes(Path.Combine(directory, "registry.json"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "registry.sig"), key.Sign(bytes));
    }

    /// <summary>A test trust store file for <c>--test-trust-store</c>.</summary>
    public static string WriteTrustStore(string path, params TestSigningKey[] keys)
    {
        File.WriteAllBytes(path, TrustStore.Serialize(keys.Select(k => k.ToTrustedKey())));
        return path;
    }

    public static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    public static void WriteJson(string path, JsonObject json) =>
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
