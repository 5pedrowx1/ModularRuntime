using System.Security.Cryptography;
using ModularRuntime.Contracts;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;
using ModularRuntime.TestSupport;

namespace ModularRuntime.DevTools.Tests;

/// <summary>
/// <c>dotnet modularruntime registry-check</c>: the public catalog verification the registry CI runs
/// (no private key, no private tool).
/// </summary>
public sealed class RegistryCheckCommandTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new("devtools-registry-check");
    private readonly TestSigningKey _key = new("test-official-1");

    public void Dispose()
    {
        _temp.Dispose();
        _key.Dispose();
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await DevToolsApp.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private string Publish(string name, long revision, string version = "1.0.0")
    {
        var root = Path.Combine(_temp.DirectoryPath, name);
        var bytes = RandomNumberGenerator.GetBytes(64);
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        File.WriteAllBytes(Path.Combine(root, "packages", "hello.zip"), bytes);
        TestPackages.WriteRegistry(root, _key, revision,
        [
            new RegistryEntry
            {
                Id = "com.example.hello", Name = "Hello", Description = "Test", Author = "Tests", Category = "Examples",
                Version = version, ApiVersion = PluginApiVersion.Supported.ToString(2), MinimumRuntimeVersion = "0.6.0",
                PublishedAt = DateTimeOffset.UtcNow, DownloadUrl = "packages/hello.zip",
                PackageSha256 = Sha256Hex.Compute(bytes), PackageSize = bytes.Length, SigningKeyId = _key.KeyId,
            },
        ]);
        return root;
    }

    [Fact]
    public async Task ValidCatalog_Passes_AndAnOlderRevisionIsRejected()
    {
        var trust = TestPackages.WriteTrustStore(Path.Combine(_temp.DirectoryPath, "test-trust.json"), _key);
        var current = Publish("current", 2);
        var older = Publish("older", 1);

        var ok = await RunAsync("registry-check", Path.Combine(current, "registry.json"), "--test-trust-store", trust, "--packages-root", current);
        Assert.True(ok.Code == 0, ok.Error);
        Assert.Contains("OK: registry revision 2", ok.Output, StringComparison.Ordinal);
        Assert.Contains("(TEST KEY)", ok.Output, StringComparison.Ordinal);
        Assert.Contains("1 package file(s) checked", ok.Output, StringComparison.Ordinal);

        var rollback = await RunAsync("registry-check", Path.Combine(older, "registry.json"), "--test-trust-store", trust, "--previous", Path.Combine(current, "registry.json"));
        Assert.Equal(1, rollback.Code);
        Assert.Contains(RegistryErrorCodes.Rollback, rollback.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheOfficialKeysAreTrustedByDefault_AndUsageErrorsAreReported()
    {
        var current = Publish("official", 1);

        // The official trust store has no key yet, so a test-signed catalog is never "official".
        var untrusted = await RunAsync("registry-check", Path.Combine(current, "registry.json"));
        Assert.Equal(1, untrusted.Code);

        Assert.Equal(1, (await RunAsync("registry-check")).Code);
        Assert.Equal(2, (await RunAsync("registry-check", "a.json", "--unknown-option", "x")).Code);
        Assert.Equal(1, (await RunAsync("registry-check", Path.Combine(current, "registry.json"), "--max-validity-days", "0")).Code);
    }
}
