using ModularRuntime.Ipc.Contracts;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Daemon.Tests;

/// <summary>
/// Revocation and withdrawal through the signed catalog, with the real daemon and CLI: a separate
/// registry key and packages key (test keys), revoked versions, revoked package keys, a plugin that
/// is running when its revocation arrives, and revocations applied offline.
/// </summary>
[Collection(ProcessTestGroup.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync, which xUnit calls.")]
public sealed class RevocationProcessTests : IAsyncLifetime
{
    private readonly TestSigningKey _registryKey = new("test-registry-key", KeyPurposes.Registry);
    private readonly TestSigningKey _packageKey = new("test-packages-key", KeyPurposes.Packages);
    private TestProfile _profile = null!;
    private string _registry = null!;
    private string _trustStore = null!;
    private string _work = null!;

    public Task InitializeAsync()
    {
        _profile = TestProfile.Create();
        var root = Path.GetDirectoryName(_profile.ProfileDirectory)!;
        _registry = Path.Combine(root, "registry");
        _work = Path.Combine(root, "work");
        Directory.CreateDirectory(_work);
        _trustStore = TestPackages.WriteTrustStore(Path.Combine(root, "test-trust.json"), _registryKey, _packageKey);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _profile.DisposeAsync();
        _registryKey.Dispose();
        _packageKey.Dispose();
    }

    private string PluginDirectory(string id) => Path.Combine(_profile.Profile.DefaultPluginDirectory, id);

    private async Task StartDaemonReadyAsync()
    {
        _profile.StartDaemon(["--registry", _registry, "--test-trust-store", _trustStore]);
        await TestProfile.WaitUntilAsync(_profile.IsDaemonRunningAsync, "daemon ready");
    }

    private Task<IReadOnlyList<RegistryEntry>> PublishAsync(long revision, (string Sample, string Version)[] plugins, Func<RegistryEntry, RegistryEntry>? edit = null, IReadOnlyList<RevokedKey>? revokedKeys = null) =>
        TestPackages.PublishRegistryAsync(_registry, _registryKey, revision, plugins, _work, packageKey: _packageKey, editEntry: edit, revokedKeys: revokedKeys, minimumRuntimeVersion: "0.6.0");

    private async Task<string> CliOkAsync(params string[] args)
    {
        var (code, output) = await _profile.RunCliAsync(args);
        Assert.True(code == 0, $"'{string.Join(' ', args)}' exited with {code}: {output}");
        return output;
    }

    private async Task<string> CliFailsAsync(int expected, params string[] args)
    {
        var (code, output) = await _profile.RunCliAsync(args);
        Assert.True(code == expected, $"'{string.Join(' ', args)}' exited with {code} (expected {expected}): {output}");
        return output;
    }

    private static Func<RegistryEntry, RegistryEntry> Revoke(string version, string reason) =>
        e => e.Version == version ? e with { Status = RegistryEntryStatus.Revoked, StatusReason = reason, StatusChangedAt = DateTimeOffset.UtcNow, AdvisoryUrl = "https://example.com/advisories/42" } : e;

    [Fact]
    public async Task RevokedVersion_KeepsRunning_IsNeverLoadedAgain_IsKeptOnDisk_AndCannotBeInstalled()
    {
        await PublishAsync(1, [("HelloPlugin", "1.0.0")]);
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");
        await CliOkAsync("plugins", "start", "com.example.hello");

        // Revision 2 revokes exactly this package.
        await PublishAsync(2, [("HelloPlugin", "1.0.0")], Revoke("1.0.0", "Sends data to an undisclosed server."));
        await CliOkAsync("registry", "refresh");

        await using var client = await _profile.ConnectAsync();
        var package = await client.GetPackageAsync("com.example.hello");
        Assert.Equal(TrustStateDto.Revoked, package.Trust);
        Assert.Equal("package", package.Revocation!.Kind);
        Assert.Equal("Sends data to an undisclosed server.", package.Revocation.Reason);
        Assert.Contains("REVOKED", await CliOkAsync("plugins", "packages"), StringComparison.Ordinal);

        // Policy: a running plugin is not stopped automatically; the daemon warns.
        Assert.Equal("Running", (await client.GetPluginAsync("com.example.hello")).Summary.State);
        Assert.Contains("keeps running until it is stopped", _profile.ReadLogs(), StringComparison.Ordinal);

        // It is never loaded again, and the installation is not deleted silently.
        await CliOkAsync("plugins", "stop", "com.example.hello");
        var start = await CliFailsAsync(1, "plugins", "start", "com.example.hello");
        Assert.Contains("revoked", start, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(PluginDirectory("com.example.hello")));

        // The Store shows it as revoked and does not offer it.
        var listed = (await client.ListRegistryAsync()).Plugins.Single(p => p.Id == "com.example.hello");
        Assert.Equal("revoked", listed.Status);
        Assert.False(listed.Installable);
        Assert.Contains("[revoked]", await CliOkAsync("registry", "list"), StringComparison.Ordinal);

        // After removal it cannot be installed again, from the Store or as a local ZIP.
        await CliOkAsync("plugins", "remove", "com.example.hello", "--yes");
        Assert.Contains(RegistryErrorCodes.VersionRevoked, await CliFailsAsync(4, "plugins", "install", "com.example.hello", "--version", "1.0.0"), StringComparison.Ordinal);
        var zip = Path.Combine(_registry, "packages", "com.example.hello-1.0.0.zip");
        Assert.Contains(RegistryErrorCodes.VersionRevoked, await CliFailsAsync(1, "plugins", "install-zip", zip), StringComparison.Ordinal);
        Assert.False(Directory.Exists(PluginDirectory("com.example.hello")));
    }

    [Fact]
    public async Task RevocationsOfTheLastVerifiedCatalog_ApplyOffline_AfterADaemonRestart()
    {
        await PublishAsync(1, [("HelloPlugin", "1.0.0")]);
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");
        await PublishAsync(2, [("HelloPlugin", "1.0.0")], Revoke("1.0.0", "Serious vulnerability."));
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("daemon", "stop");

        // The registry is no longer reachable.
        Directory.Delete(_registry, recursive: true);
        await StartDaemonReadyAsync();

        Assert.Contains("revoked", await CliFailsAsync(1, "plugins", "start", "com.example.hello"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKED", await CliOkAsync("plugins", "packages"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithdrawnVersion_IsNotOffered_ButExistingInstallationsKeepWorking()
    {
        await PublishAsync(1, [("HelloPlugin", "1.0.0")]);
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");

        await PublishAsync(2, [("HelloPlugin", "1.0.0"), ("HelloPlugin", "1.1.0")],
            e => e.Version == "1.0.0" ? e with { Status = RegistryEntryStatus.Withdrawn, StatusReason = "Published by mistake." } : e);
        await CliOkAsync("registry", "refresh");

        await using var client = await _profile.ConnectAsync();
        var package = await client.GetPackageAsync("com.example.hello");
        Assert.Equal(TrustStateDto.OfficialVerified, package.Trust);
        Assert.Equal("withdrawn", package.CatalogStatus);
        Assert.Contains("withdrawn from the Store", await CliOkAsync("plugins", "packages"), StringComparison.Ordinal);
        await CliOkAsync("plugins", "start", "com.example.hello");
        await CliOkAsync("plugins", "stop", "com.example.hello");

        var listed = (await client.ListRegistryAsync()).Plugins.Single(p => p.Id == "com.example.hello");
        Assert.Equal("1.1.0", listed.Version);
        Assert.Contains(listed.Versions!, v => v.Version == "1.0.0" && v.Status == "withdrawn");
        Assert.Contains("1.0.0 -> 1.1.0", await CliOkAsync("plugins", "updates"), StringComparison.Ordinal);

        await CliOkAsync("plugins", "remove", "com.example.hello", "--yes");
        Assert.Contains(RegistryErrorCodes.VersionWithdrawn, await CliFailsAsync(4, "plugins", "install", "com.example.hello", "--version", "1.0.0"), StringComparison.Ordinal);
        Assert.Contains("completed", await CliOkAsync("plugins", "install", "com.example.hello"), StringComparison.Ordinal);
        Assert.Equal("1.1.0", (await client.GetPackageAsync("com.example.hello")).Version);
    }

    [Fact]
    public async Task PackageKeyRevokedByTheCatalog_BlocksInstallationAndLoading_OfEverythingItSigned()
    {
        await PublishAsync(1, [("HelloPlugin", "1.0.0"), ("ServiceProviderPlugin", "1.0.0")]);
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");

        await PublishAsync(2, [("HelloPlugin", "1.0.0"), ("ServiceProviderPlugin", "1.0.0")],
            revokedKeys: [new RevokedKey { KeyId = _packageKey.KeyId, Reason = "The key was exposed.", RevokedAt = DateTimeOffset.UtcNow }]);
        await CliOkAsync("registry", "refresh");

        await using var client = await _profile.ConnectAsync();
        var package = await client.GetPackageAsync("com.example.hello");
        Assert.Equal(TrustStateDto.Revoked, package.Trust);
        Assert.Equal("signing-key", package.Revocation!.Kind);
        Assert.Contains("signing key", await CliFailsAsync(1, "plugins", "start", "com.example.hello"), StringComparison.Ordinal);

        var provider = (await client.ListRegistryAsync()).Plugins.Single(p => p.Id == "com.example.services.provider");
        Assert.True(provider.SigningKeyRevoked);
        Assert.False(provider.Installable);
        Assert.Contains(RegistryErrorCodes.VersionRevoked, await CliFailsAsync(4, "plugins", "install", "com.example.services.provider"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevocationInAnUnsignedOrForeignCatalog_IsIgnored()
    {
        await PublishAsync(1, [("HelloPlugin", "1.0.0")]);
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");

        // A revocation signed by the packages key (not a registry key) is rejected with the whole catalog.
        await PublishAsync(2, [("HelloPlugin", "1.0.0")], Revoke("1.0.0", "Forged."));
        var bytes = File.ReadAllBytes(Path.Combine(_registry, "registry.json"));
        File.WriteAllBytes(Path.Combine(_registry, "registry.sig"), _packageKey.Sign(bytes));
        Assert.Contains("registry_signature_invalid", await CliFailsAsync(4, "registry", "refresh"), StringComparison.Ordinal);

        await using var client = await _profile.ConnectAsync();
        Assert.Equal(TrustStateDto.OfficialVerified, (await client.GetPackageAsync("com.example.hello")).Trust);
        await CliOkAsync("plugins", "start", "com.example.hello");
    }
}
