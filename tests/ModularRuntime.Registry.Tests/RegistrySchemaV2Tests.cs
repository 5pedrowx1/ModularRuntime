using System.Text;
using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Registry.Tests;

/// <summary>Registry schema 2: version status, signed revocations, provenance, and offline revocation.</summary>
public sealed class RegistrySchemaV2Tests : IDisposable
{
    private static readonly string PackageHash = new('a', 64);
    private readonly TemporaryDirectory _temp = new("registry-v2");
    private readonly TestSigningKey _key = new("test-registry-2");

    public void Dispose()
    {
        _temp.Dispose();
        _key.Dispose();
    }

    private string Published => Path.Combine(_temp.DirectoryPath, "published");

    private string State => Path.Combine(_temp.DirectoryPath, "state");

    private static RegistryEntry Entry(string version, string? status = null, string? reason = null, string key = "test-packages-1") => new()
    {
        Id = "com.example.hello",
        Name = "Hello",
        Description = "Test entry",
        Author = "Tests",
        Category = "Examples",
        Version = version,
        ApiVersion = "0.3",
        MinimumRuntimeVersion = "0.6.0",
        PublishedAt = DateTimeOffset.UtcNow,
        DownloadUrl = $"packages/hello-{version}.zip",
        PackageSha256 = PackageHash,
        PackageSize = 10,
        SigningKeyId = key,
        Status = status,
        StatusReason = reason,
        BuildId = "build-1",
        ApprovalSha256 = new string('b', 64),
    };

    private static RegistryDocument Document(int schema, IReadOnlyList<RegistryEntry> entries, IReadOnlyList<RevokedKey>? revoked = null) => new()
    {
        SchemaVersion = schema,
        Revision = 1,
        GeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(10),
        Plugins = entries,
        RevokedKeys = revoked,
    };

    [Fact]
    public void SchemaOneCatalogs_AreStillRead_AndCannotCarrySchemaTwoFields()
    {
        var v1 = Document(1, [Entry("1.0.0") with { BuildId = null, ApprovalSha256 = null }]);
        Assert.Equal(1, RegistryDocument.Parse(v1.Serialize()).SchemaVersion);

        var mixed = Encoding.UTF8.GetBytes(JsonNode.Parse(Document(2, [Entry("1.0.0", "withdrawn", "old")]).Serialize())!.AsObject().Also(o => o["schemaVersion"] = 1).ToJsonString());
        Assert.Equal(RegistryErrorCodes.Invalid, Assert.Throws<RegistryException>(() => RegistryDocument.Parse(mixed)).Code);

        var future = Encoding.UTF8.GetBytes(JsonNode.Parse(v1.Serialize())!.AsObject().Also(o => o["schemaVersion"] = 3).ToJsonString());
        Assert.Throws<RegistryException>(() => RegistryDocument.Parse(future));
    }

    [Fact]
    public void Statuses_AreValidated_AndARevocationNeedsAReason()
    {
        Assert.Throws<RegistryException>(() => Document(2, [Entry("1.0.0", "deleted")]).Validate());
        Assert.Throws<RegistryException>(() => Document(2, [Entry("1.0.0", "revoked")]).Validate());
        Assert.Throws<RegistryException>(() => Document(2, [Entry("1.0.0") with { AdvisoryUrl = "http://example.com/a" }]).Validate());
        Assert.Throws<RegistryException>(() => Document(2, [Entry("1.0.0")], [new RevokedKey { KeyId = "k", Reason = "x", RevokedAt = DateTimeOffset.UtcNow }]).Validate());
        Document(2, [Entry("1.0.0", "revoked", "Malicious.")], [new RevokedKey { KeyId = "old-key", Reason = "Exposed.", RevokedAt = DateTimeOffset.UtcNow }]).Validate();
    }

    [Fact]
    public void LatestVersion_IgnoresWithdrawnAndRevokedVersions_ButEveryVersionStaysListed()
    {
        var document = Document(2, [Entry("1.0.0"), Entry("1.1.0", "withdrawn", "Publishing mistake."), Entry("1.2.0", "revoked", "Malicious.")]);
        var id = PluginId.Parse("com.example.hello");

        Assert.Equal("1.0.0", document.Find(id)!.Version);
        Assert.Equal("withdrawn", document.Find(id, new Version(1, 1, 0))!.EffectiveStatus);
        Assert.Equal(["1.2.0", "1.1.0", "1.0.0"], document.Versions(id).Select(v => v.Version));
    }

    [Fact]
    public void Revocations_MatchTheExactPackage_OrItsSigningKey()
    {
        var id = PluginId.Parse("com.example.hello");
        var document = Document(2, [Entry("1.0.0", "revoked", "Malicious.")], [new RevokedKey { KeyId = "exposed-key", Reason = "Key exposed.", RevokedAt = DateTimeOffset.UtcNow }]);

        Assert.Equal(RegistryRevocationKind.Package, document.FindRevocation(id, new Version(1, 0, 0), PackageHash, "test-packages-1")!.Kind);

        // Another package under the same version is not "this" package (a local build, for example).
        Assert.Null(document.FindRevocation(id, new Version(1, 0, 0), new string('c', 64), null));

        var byKey = document.FindRevocation(id, new Version(2, 0, 0), new string('c', 64), "exposed-key");
        Assert.Equal(RegistryRevocationKind.SigningKey, byKey!.Kind);
        Assert.Equal("exposed-key", byKey.KeyId);
        Assert.Null(document.FindRevocation(id, new Version(2, 0, 0), new string('c', 64), "test-packages-1"));
    }

    [Fact]
    public async Task ExpiredCatalog_IsNotUsedForInstallations_ButItsAuthenticRevocationsStillApplyOffline()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var revoked = Document(2, [Entry("1.0.0", "revoked", "Malicious.")]) with { Revision = 2 };
        Directory.CreateDirectory(Published);
        var bytes = revoked.Serialize();
        File.WriteAllBytes(Path.Combine(Published, "registry.json"), bytes);
        File.WriteAllBytes(Path.Combine(Published, "registry.sig"), _key.Sign(bytes));

        using var client = new RegistryClient(
            new RegistryOptions { Location = new Uri(Path.GetFullPath(Published) + Path.DirectorySeparatorChar) }, _key.TrustStore(), new DefaultRegistryTransport(new HttpClient()), State, time);
        await client.RefreshAsync();
        Directory.Delete(Published, recursive: true); // offline from now on
        time.Now = time.Now.AddDays(30);

        Assert.Equal(RegistryErrorCodes.Expired, Assert.Throws<RegistryException>(() => client.LoadCached()).Code);
        var source = client.LoadCachedRevocationSource();
        Assert.NotNull(source);
        Assert.NotNull(source.FindRevocation(PluginId.Parse("com.example.hello"), new Version(1, 0, 0), PackageHash, null));

        // A tampered cache is not a revocation source (and not a source of anything else).
        var cached = Path.Combine(State, "registry.json");
        File.WriteAllText(cached, File.ReadAllText(cached).Replace("Malicious.", "Harmless.", StringComparison.Ordinal));
        Assert.Null(client.LoadCachedRevocationSource());
    }

    [Fact]
    public async Task CanceledRefreshAndDownload_LeaveTheAcceptedStateUnchanged()
    {
        var bytes = (Document(2, [Entry("1.0.0")]) with { Revision = 5 }).Serialize();
        Directory.CreateDirectory(Published);
        File.WriteAllBytes(Path.Combine(Published, "registry.json"), bytes);
        File.WriteAllBytes(Path.Combine(Published, "registry.sig"), _key.Sign(bytes));
        var location = new Uri(Path.GetFullPath(Published) + Path.DirectorySeparatorChar);
        using (var client = new RegistryClient(new RegistryOptions { Location = location }, _key.TrustStore(), new DefaultRegistryTransport(new HttpClient()), State))
        {
            await client.RefreshAsync();
        }

        var cachedBefore = File.ReadAllBytes(Path.Combine(State, "registry.json"));
        using var blocking = new RegistryClient(new RegistryOptions { Location = location }, _key.TrustStore(), new BlockingTransport(), State);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocking.RefreshAsync(cancel.Token));
        Assert.Equal(5, blocking.HighestAcceptedRevision);
        Assert.Equal(cachedBefore, File.ReadAllBytes(Path.Combine(State, "registry.json")));

        using var cancelDownload = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var target = Path.Combine(_temp.DirectoryPath, "download.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocking.DownloadPackageAsync(Entry("1.0.0"), target, null, cancelDownload.Token));
    }

    /// <summary>A transport that never completes until it is canceled (a stalled network).</summary>
    private sealed class BlockingTransport : IRegistryTransport
    {
        public Task DownloadAsync(Uri uri, Stream destination, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

internal static class JsonNodeExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }
}
