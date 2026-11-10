using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Registry.Tests;

public sealed class RegistryClientTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new("registry");
    private readonly TestSigningKey _key = new("test-registry-1");

    public void Dispose()
    {
        _temp.Dispose();
        _key.Dispose();
    }

    private string Published => Path.Combine(_temp.DirectoryPath, "published");

    private string State => Path.Combine(_temp.DirectoryPath, "state");

    private static Uri DirectoryUri(string path) => new(Path.GetFullPath(path) + Path.DirectorySeparatorChar);

    private RegistryClient Client(IRegistryTransport? transport = null, TrustStore? trust = null, Uri? location = null) =>
        new(new RegistryOptions { Location = location ?? DirectoryUri(Published) }, trust ?? _key.TrustStore(), transport ?? new DefaultRegistryTransport(new HttpClient()), State);

    private static RegistryEntry Entry(string id = "com.example.hello", string version = "1.0.0", string url = "packages/hello.zip") => new()
    {
        Id = id,
        Name = "Hello",
        Description = "Test entry",
        Author = "Tests",
        Category = "Examples",
        Version = version,
        ApiVersion = "0.3",
        MinimumRuntimeVersion = "0.4.0",
        PublishedAt = DateTimeOffset.UtcNow,
        DownloadUrl = url,
        PackageSha256 = new string('a', 64),
        PackageSize = 10,
        SigningKeyId = "test-official-1",
    };

    private void Publish(long revision, params RegistryEntry[] entries) => TestPackages.WriteRegistry(Published, _key, revision, entries.Length == 0 ? [Entry()] : entries);

    private static async Task<RegistryException> RefreshFailsAsync(RegistryClient client) =>
        await Assert.ThrowsAsync<RegistryException>(() => client.RefreshAsync());

    [Fact]
    public async Task SignedRegistry_IsAccepted_CachedAndAvailableOffline()
    {
        Publish(3);
        using (var client = Client())
        {
            var snapshot = await client.RefreshAsync();
            Assert.Equal(3, snapshot.Document.Revision);
            Assert.Equal(_key.KeyId, snapshot.SigningKeyId);
            Assert.Equal(3, client.HighestAcceptedRevision);
        }

        Directory.Delete(Published, recursive: true);
        using var offline = Client(new FailingTransport());
        var cached = offline.LoadCached();
        Assert.NotNull(cached);
        Assert.True(cached.FromCache);
        Assert.Equal("com.example.hello", cached.Document.Plugins.Single().Id);
        Assert.Equal(RegistryErrorCodes.Unavailable, (await RefreshFailsAsync(offline)).Code);
    }

    [Fact]
    public async Task TamperedRegistry_IsRejected()
    {
        Publish(1);
        var path = Path.Combine(Published, "registry.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Test entry", "Evil entry", StringComparison.Ordinal));

        using var client = Client();
        Assert.Equal(RegistryErrorCodes.SignatureInvalid, (await RefreshFailsAsync(client)).Code);
        Assert.Null(client.LoadCached());
    }

    [Fact]
    public async Task RegistrySignedByAnUnknownKey_IsRejected()
    {
        using var other = new TestSigningKey("other-key");
        TestPackages.WriteRegistry(Published, other, 1, [Entry()]);

        using var client = Client();
        Assert.Equal(RegistryErrorCodes.SignatureInvalid, (await RefreshFailsAsync(client)).Code);
    }

    [Fact]
    public async Task KeyTrustedOnlyForPackages_CannotSignTheRegistry()
    {
        using var packagesOnly = new TestSigningKey("packages-only", KeyPurposes.Packages);
        TestPackages.WriteRegistry(Published, packagesOnly, 1, [Entry()]);

        using var client = Client(trust: packagesOnly.TrustStore());
        Assert.Equal(RegistryErrorCodes.SignatureInvalid, (await RefreshFailsAsync(client)).Code);
    }

    [Fact]
    public async Task OlderRevision_IsRejectedAsRollback_AndNewerIsAccepted()
    {
        using var client = Client();
        Publish(5);
        await client.RefreshAsync();

        Publish(4);
        Assert.Equal(RegistryErrorCodes.Rollback, (await RefreshFailsAsync(client)).Code);
        Assert.Equal(5, client.HighestAcceptedRevision);

        Publish(6, Entry(version: "1.1.0"));
        Assert.Equal(6, (await client.RefreshAsync()).Document.Revision);
    }

    [Fact]
    public async Task SameRevisionWithDifferentContent_IsRejectedAsConflict()
    {
        using var client = Client();
        Publish(2);
        await client.RefreshAsync();

        Publish(2, Entry(version: "9.9.9"));

        Assert.Equal(RegistryErrorCodes.Conflict, (await RefreshFailsAsync(client)).Code);
    }

    [Fact]
    public async Task ExpiredRegistry_IsRejected_EvenFromTheCache()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        Publish(1);
        using (var client = new RegistryClient(new RegistryOptions { Location = DirectoryUri(Published) }, _key.TrustStore(), new DefaultRegistryTransport(new HttpClient()), State, time))
        {
            await client.RefreshAsync();
            time.Now = time.Now.AddDays(31);
            Assert.Equal(RegistryErrorCodes.Expired, (await RefreshFailsAsync(client)).Code);
            Assert.Equal(RegistryErrorCodes.Expired, Assert.Throws<RegistryException>(() => client.LoadCached()).Code);
        }
    }

    [Fact]
    public async Task RegistryFromTheFuture_IsRejected()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow.AddDays(-5));
        Publish(1);
        using var client = new RegistryClient(new RegistryOptions { Location = DirectoryUri(Published) }, _key.TrustStore(), new DefaultRegistryTransport(new HttpClient()), State, time);

        Assert.Equal(RegistryErrorCodes.NotYetValid, (await RefreshFailsAsync(client)).Code);
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("duplicate-entry")]
    [InlineData("bad-hash")]
    [InlineData("http-url")]
    [InlineData("traversal-url")]
    [InlineData("missing-field")]
    [InlineData("bad-id")]
    [InlineData("long-validity")]
    public async Task InvalidDocuments_AreRejectedEvenWhenSigned(string defect)
    {
        var json = JsonNode.Parse(new RegistryDocument
        {
            SchemaVersion = 1,
            Revision = 1,
            GeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(10),
            Plugins = [Entry()],
        }.Serialize())!.AsObject();
        var plugin = json["plugins"]![0]!.AsObject();
        switch (defect)
        {
            case "unknown-field": plugin["installScript"] = "rm -rf"; break;
            case "duplicate-entry": json["plugins"]!.AsArray().Add(plugin.DeepClone()); break;
            case "bad-hash": plugin["packageSha256"] = "ABC"; break;
            case "http-url": plugin["downloadUrl"] = "http://example.com/a.zip"; break;
            case "traversal-url": plugin["downloadUrl"] = "../../secret.zip"; break;
            case "missing-field": plugin.Remove("signingKeyId"); break;
            case "bad-id": plugin["id"] = "Not A Valid Id"; break;
            case "long-validity": json["expiresAt"] = DateTimeOffset.UtcNow.AddYears(5).ToString("O"); break;
        }

        Directory.CreateDirectory(Published);
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        File.WriteAllBytes(Path.Combine(Published, "registry.json"), bytes);
        File.WriteAllBytes(Path.Combine(Published, "registry.sig"), _key.Sign(bytes));

        using var client = Client();
        Assert.Equal(RegistryErrorCodes.Invalid, (await RefreshFailsAsync(client)).Code);
    }

    [Fact]
    public void DownloadUrls_AreResolvedSafely()
    {
        using var https = Client(location: new Uri("https://registry.example/catalog/"));
        Assert.Equal("https://registry.example/catalog/packages/hello.zip", https.ResolveDownloadUrl(Entry()).ToString());
        Assert.Equal("https://cdn.example/a.zip", https.ResolveDownloadUrl(Entry(url: "https://cdn.example/a.zip")).ToString());

        using var mirror = Client();
        Assert.True(mirror.ResolveDownloadUrl(Entry()).IsFile);
    }

    [Fact]
    public async Task Downloads_EnforceTheSignedSize()
    {
        Directory.CreateDirectory(Path.Combine(Published, "packages"));
        File.WriteAllBytes(Path.Combine(Published, "packages", "hello.zip"), new byte[50]);
        Publish(1);
        using var client = Client();

        var tooBig = await Assert.ThrowsAsync<RegistryException>(() => client.DownloadPackageAsync(Entry(), Path.Combine(_temp.DirectoryPath, "a.zip"), null, CancellationToken.None));
        Assert.Equal(RegistryErrorCodes.DownloadTooLarge, tooBig.Code);

        var shorter = Entry() with { PackageSize = 100 };
        var mismatch = await Assert.ThrowsAsync<RegistryException>(() => client.DownloadPackageAsync(shorter, Path.Combine(_temp.DirectoryPath, "b.zip"), null, CancellationToken.None));
        Assert.Equal(RegistryErrorCodes.DownloadFailed, mismatch.Code);
    }

    [Fact]
    public async Task HttpTransport_ReportsServerErrorsAndOversizedResponses()
    {
        var transport = new DefaultRegistryTransport(new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("missing.zip", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) })));

        using var target = new MemoryStream();
        var notFound = await Assert.ThrowsAsync<RegistryException>(() => transport.DownloadAsync(new Uri("https://x.example/missing.zip"), target, 10_000, null, CancellationToken.None));
        Assert.Equal(RegistryErrorCodes.DownloadFailed, notFound.Code);

        var oversized = await Assert.ThrowsAsync<RegistryException>(() => transport.DownloadAsync(new Uri("https://x.example/a.zip"), target, 100, null, CancellationToken.None));
        Assert.Equal(RegistryErrorCodes.DownloadTooLarge, oversized.Code);

        var insecure = await Assert.ThrowsAsync<RegistryException>(() => transport.DownloadAsync(new Uri("http://x.example/a.zip"), target, 100, null, CancellationToken.None));
        Assert.Equal(RegistryErrorCodes.DownloadFailed, insecure.Code);
    }

    [Fact]
    public async Task NetworkFailure_IsReportedAsUnavailable()
    {
        var transport = new DefaultRegistryTransport(new HttpClient(new StubHandler(_ => throw new HttpRequestException("No such host is known."))));
        using var client = Client(transport, location: new Uri("https://registry.example/"));

        var error = await RefreshFailsAsync(client);

        Assert.Equal(RegistryErrorCodes.Unavailable, error.Code);
        Assert.Contains("No such host", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotConfigured_IsReported()
    {
        using var client = new RegistryClient(new RegistryOptions(), _key.TrustStore(), new FailingTransport(), State);

        Assert.False(client.IsConfigured);
        Assert.Equal(RegistryErrorCodes.NotConfigured, (await RefreshFailsAsync(client)).Code);
    }

    [Fact]
    public void Find_ReturnsTheHighestVersion_OrTheRequestedOne()
    {
        var document = new RegistryDocument
        {
            SchemaVersion = 1,
            Revision = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Plugins = [Entry(version: "1.0.0"), Entry(version: "1.10.0"), Entry(version: "1.9")],
        };

        Assert.Equal("1.10.0", document.Find(Contracts.PluginId.Parse("com.example.hello"))!.Version);
        Assert.Equal("1.9", document.Find(Contracts.PluginId.Parse("com.example.hello"), new Version(1, 9, 0))!.Version);
    }

    private sealed class FailingTransport : IRegistryTransport
    {
        public Task DownloadAsync(Uri uri, Stream destination, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken) =>
            throw new RegistryException(RegistryErrorCodes.DownloadFailed, "offline");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
