using System.Diagnostics;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Daemon.Tests;

/// <summary>
/// The package manager through the real daemon and CLI processes: an isolated profile, a test
/// registry published to a directory and signed with an ephemeral test key that only this test's
/// daemon trusts (<c>--test-trust-store</c>). Packages are real, plugins load in the daemon's real
/// load contexts.
/// </summary>
[Collection(ProcessTestGroup.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync, which xUnit calls.")]
public sealed class PackageManagerProcessTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly TestSigningKey _key = new("test-official-e2e");
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
        _trustStore = TestPackages.WriteTrustStore(Path.Combine(root, "test-trust.json"), _key);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _profile.DisposeAsync();
        _key.Dispose();
    }

    private string PluginDirectory(string id) => Path.Combine(_profile.Profile.DefaultPluginDirectory, id);

    private string RecordDirectory(string id) => Path.Combine(_profile.ProfileDirectory, "packages", "installed", id);

    private string StagingDirectory => Path.Combine(_profile.Profile.DefaultPluginDirectory, ".staging");

    private Process StartDaemon(params string[] extra) =>
        _profile.StartDaemon(["--registry", _registry, "--test-trust-store", _trustStore, .. extra]);

    private async Task<Process> StartDaemonReadyAsync(params string[] extra)
    {
        var daemon = StartDaemon(extra);
        await TestProfile.WaitUntilAsync(_profile.IsDaemonRunningAsync, "daemon ready");
        return daemon;
    }

    private async Task<string> CliOkAsync(params string[] args)
    {
        var (code, output) = await _profile.RunCliAsync(args);
        Assert.True(code == 0, $"'{string.Join(' ', args)}' exited with {code}: {output}");
        return output;
    }

    private async Task PublishAsync(long revision, params (string Sample, string Version)[] plugins) =>
        await TestPackages.PublishRegistryAsync(_registry, _key, revision, plugins, _work);

    private void AssertNoPartialInstallations()
    {
        Assert.True(!Directory.Exists(StagingDirectory) || Directory.GetDirectories(StagingDirectory).Length == 0, "Staging must be empty.");
        foreach (var directory in Directory.GetDirectories(_profile.Profile.DefaultPluginDirectory).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            Assert.True(Directory.Exists(RecordDirectory(Path.GetFileName(directory))), $"'{directory}' exists without a package record.");
        }
    }

    [Fact]
    public async Task OfficialPackage_IsInstalledVerifiedAndRuns_ThenTamperingIsDetectedAndTheLoadRejected_ThenItIsRemoved()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();

        Assert.Contains("Revision:          1", await CliOkAsync("registry", "refresh"), StringComparison.Ordinal);
        Assert.Contains("com.example.hello", await CliOkAsync("registry", "list"), StringComparison.Ordinal);
        Assert.Contains("completed", await CliOkAsync("plugins", "install", "com.example.hello"), StringComparison.Ordinal);
        Assert.Contains("official (TEST KEY)", await CliOkAsync("plugins", "packages"), StringComparison.Ordinal);
        Assert.Contains("Verified", await CliOkAsync("plugins", "verify", "com.example.hello"), StringComparison.Ordinal);
        AssertNoPartialInstallations();

        // Modify an installed file of the stopped plugin.
        File.AppendAllText(Path.Combine(PluginDirectory("com.example.hello"), "HelloPlugin.dll"), "tampered");

        var (verifyCode, verifyOutput) = await _profile.RunCliAsync("plugins", "verify", "com.example.hello");
        Assert.Equal(1, verifyCode);
        Assert.Contains("Modified", verifyOutput, StringComparison.Ordinal);
        Assert.Contains("HelloPlugin.dll", verifyOutput, StringComparison.Ordinal);

        var (startCode, startOutput) = await _profile.RunCliAsync("plugins", "start", "com.example.hello");
        Assert.Equal(1, startCode);
        Assert.Contains("was not loaded", startOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("HelloPlugin initialized", _profile.ReadLogs(), StringComparison.Ordinal);
        Assert.Contains("MODIFIED", await CliOkAsync("plugins", "packages"), StringComparison.Ordinal);

        Assert.Contains("completed", await CliOkAsync("plugins", "remove", "com.example.hello", "--yes"), StringComparison.Ordinal);
        Assert.False(Directory.Exists(PluginDirectory("com.example.hello")));
        Assert.False(Directory.Exists(RecordDirectory("com.example.hello")));
        AssertNoPartialInstallations();
    }

    [Fact]
    public async Task InstalledPlugin_StartsAndStops_AndSurvivesADaemonRestart()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");

        await CliOkAsync("plugins", "start", "com.example.hello");
        Assert.Contains("HelloPlugin initialized", _profile.ReadLogs(), StringComparison.Ordinal);
        await CliOkAsync("plugins", "stop", "com.example.hello");
        await CliOkAsync("daemon", "stop");

        await StartDaemonReadyAsync();
        var packages = await CliOkAsync("plugins", "packages");
        Assert.Contains("com.example.hello", packages, StringComparison.Ordinal);
        Assert.Contains("official (TEST KEY)", packages, StringComparison.Ordinal);
        await CliOkAsync("plugins", "start", "com.example.hello");
    }

    [Fact]
    public async Task InstalledPlugin_StartStopStart_InTheSameDaemon_IsReverifiedBeforeEveryLoad()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await CliOkAsync("plugins", "start", "com.example.hello");
            await CliOkAsync("plugins", "stop", "com.example.hello");
        }

        await CliOkAsync("plugins", "start", "com.example.hello");
        await CliOkAsync("plugins", "restart", "com.example.hello");
        await CliOkAsync("plugins", "stop", "com.example.hello");
        Assert.Contains("registered again for a new start", _profile.ReadLogs(), StringComparison.Ordinal);

        // A file of the stopped installation is modified: the next start and restart are rejected
        // before anything is loaded. The integrity check is never bypassed for a restart.
        File.AppendAllText(Path.Combine(PluginDirectory("com.example.hello"), "HelloPlugin.deps.json"), " ");
        var (startCode, startOutput) = await _profile.RunCliAsync("plugins", "start", "com.example.hello");
        Assert.Equal(1, startCode);
        Assert.Contains("was not loaded", startOutput, StringComparison.Ordinal);
        var (restartCode, restartOutput) = await _profile.RunCliAsync("plugins", "restart", "com.example.hello");
        Assert.Equal(1, restartCode);
        Assert.Contains("was not loaded", restartOutput, StringComparison.Ordinal);
        Assert.Contains("MODIFIED", await CliOkAsync("plugins", "packages"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsignedLocalZip_RequiresExplicitAcceptance_IsNeverOfficial_AndIsNotAutoStarted()
    {
        var content = TestPackages.CreateContent(_work, "EventSubscriberPlugin");
        var zip = await TestPackages.BuildAsync(content, Path.Combine(_work, "subscriber.zip"), key: null);
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();

        var (refused, refusedOutput) = await _profile.RunCliAsync("plugins", "install-zip", zip);
        Assert.Equal(4, refused);
        Assert.Contains("no official signature", refusedOutput, StringComparison.Ordinal);
        Assert.False(Directory.Exists(PluginDirectory("com.example.events.subscriber")));

        var installed = await CliOkAsync("plugins", "install-zip", zip, "--allow-unverified");
        Assert.Contains("local, unverified", installed, StringComparison.Ordinal);
        Assert.DoesNotContain("official", (await CliOkAsync("plugins", "packages")).Split('\n').Single(l => l.Contains("events.subscriber", StringComparison.Ordinal)), StringComparison.Ordinal);
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");
        await CliOkAsync("daemon", "stop");

        // Auto-start starts the official plugin but never the unverified local one.
        await StartDaemonReadyAsync("--auto-start");
        await using var client = await _profile.ConnectAsync();
        var plugins = (await client.ListPluginsAsync()).Plugins.ToDictionary(p => p.Id);
        Assert.Equal("Running", plugins["com.example.hello"].State);
        Assert.Equal("Validated", plugins["com.example.events.subscriber"].State);
        Assert.Contains("is not started automatically", _profile.ReadLogs(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfficialSignedZip_InstalledLocally_IsRecognizedAsOfficialOffline()
    {
        var content = TestPackages.CreateContent(_work, "HelloPlugin");
        var zip = await TestPackages.BuildAsync(content, Path.Combine(_work, "hello-signed.zip"), _key);
        await StartDaemonReadyAsync(); // no registry published: offline

        var output = await CliOkAsync("plugins", "install-zip", zip);

        Assert.Contains("official (TEST KEY)", output, StringComparison.Ordinal);
        Assert.Contains("Verified", await CliOkAsync("plugins", "verify", "com.example.hello"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_RequiresStoppingARunningPlugin_ThenReplacesIt()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");
        await CliOkAsync("plugins", "install", "com.example.hello");
        await CliOkAsync("plugins", "start", "com.example.hello");

        await PublishAsync(2, ("HelloPlugin", "1.1.0"));
        await CliOkAsync("registry", "refresh");
        Assert.Contains("1.0.0 -> 1.1.0", await CliOkAsync("plugins", "updates"), StringComparison.Ordinal);

        var (refused, refusedOutput) = await _profile.RunCliAsync("plugins", "update", "com.example.hello");
        Assert.Equal(4, refused);
        Assert.Contains("plugin_running", refusedOutput, StringComparison.Ordinal);

        Assert.Contains("completed", await CliOkAsync("plugins", "update", "com.example.hello", "--stop-if-running"), StringComparison.Ordinal);
        await using var client = await _profile.ConnectAsync();
        var hello = (await client.ListPluginsAsync()).Plugins.Single(p => p.Id == "com.example.hello");
        Assert.Equal("1.1.0", hello.Version);
        Assert.Equal("Validated", hello.State);
        Assert.Contains("Every installed package is up to date", await CliOkAsync("plugins", "updates"), StringComparison.Ordinal);
        await CliOkAsync("plugins", "start", "com.example.hello");
        AssertNoPartialInstallations();
    }

    [Fact]
    public async Task PackageAlteredAfterPublication_IsRejected_AndNothingIsInstalled()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        var zip = Directory.GetFiles(Path.Combine(_registry, "packages"), "*.zip").Single();
        File.AppendAllText(zip, "x");
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");

        var (code, output) = await _profile.RunCliAsync("plugins", "install", "com.example.hello");

        Assert.Equal(1, code);
        Assert.Contains("download_too_large", output, StringComparison.Ordinal); // larger than the signed packageSize
        Assert.False(Directory.Exists(PluginDirectory("com.example.hello")));
        AssertNoPartialInstallations();
    }

    [Fact]
    public async Task PackageReplacedWithTheSameSize_IsRejectedByItsHash()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        var zip = Directory.GetFiles(Path.Combine(_registry, "packages"), "*.zip").Single();
        var bytes = File.ReadAllBytes(zip);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");

        var (code, output) = await _profile.RunCliAsync("plugins", "install", "com.example.hello");

        Assert.Equal(1, code);
        Assert.Contains(PackageErrorCodes.PackageHashMismatch, output, StringComparison.Ordinal);
        AssertNoPartialInstallations();
    }

    [Fact]
    public async Task RegistryRollbackAndTampering_AreRejected_AndMissingPackagesFailCleanly()
    {
        await PublishAsync(2, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();
        await CliOkAsync("registry", "refresh");

        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        var (rollback, rollbackOutput) = await _profile.RunCliAsync("registry", "refresh");
        Assert.Equal(4, rollback);
        Assert.Contains("registry_rollback", rollbackOutput, StringComparison.Ordinal);

        await PublishAsync(3, ("HelloPlugin", "1.0.0"));
        File.AppendAllText(Path.Combine(_registry, "registry.json"), " ");
        var (tampered, tamperedOutput) = await _profile.RunCliAsync("registry", "refresh");
        Assert.Equal(4, tampered);
        Assert.Contains("registry_signature_invalid", tamperedOutput, StringComparison.Ordinal);

        // The previously accepted revision 2 stays in use; its package now disappears.
        foreach (var file in Directory.GetFiles(Path.Combine(_registry, "packages")))
        {
            File.Delete(file);
        }

        var (missing, missingOutput) = await _profile.RunCliAsync("plugins", "install", "com.example.hello");
        Assert.Equal(1, missing);
        Assert.Contains("download_failed", missingOutput, StringComparison.Ordinal);
        AssertNoPartialInstallations();
    }

    [Fact]
    public async Task TwoClientsInstallingTheSamePlugin_ProduceExactlyOneInstallation()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();
        await using var first = await _profile.ConnectAsync("first");
        await using var second = await _profile.ConnectAsync("second");
        await first.RefreshRegistryAsync();

        var attempts = await Task.WhenAll(
            TryInstallAsync(first),
            TryInstallAsync(second));

        Assert.Contains(attempts, a => a.Operation is not null);
        foreach (var operation in attempts.Where(a => a.Operation is not null).Select(a => a.Operation!))
        {
            await WaitForOperationAsync(first, operation.OperationId);
        }

        Assert.All(attempts.Where(a => a.Error is not null), a => Assert.Contains(a.Error, new[] { IpcErrorCodes.OperationInProgress, IpcErrorCodes.AlreadyInstalled }));
        var packages = (await first.ListPackagesAsync()).Packages.Where(p => p.PluginId == "com.example.hello").ToList();
        Assert.Single(packages);
        Assert.Equal(TrustStateDto.OfficialVerified, packages[0].Trust);
        AssertNoPartialInstallations();

        static async Task<(PackageOperationDto? Operation, string? Error)> TryInstallAsync(RuntimeClient client)
        {
            try
            {
                return (await client.InstallOfficialAsync("com.example.hello"), null);
            }
            catch (IpcClientException exception)
            {
                return (null, exception.Code);
            }
        }
    }

    [Fact]
    public async Task DaemonKilledDuringAnInstallation_RecoversToAConsistentState()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"), ("ServiceProviderPlugin", "1.0.0"));
        var daemon = await StartDaemonReadyAsync();
        await using (var client = await _profile.ConnectAsync())
        {
            await client.RefreshRegistryAsync();
            await client.InstallOfficialAsync("com.example.hello");
            await client.InstallOfficialAsync("com.example.services.provider");
        }

        daemon.Kill(); // the crash being tested, at an arbitrary point of the two installations
        await daemon.WaitForExitAsync().WaitAsync(Timeout);

        await StartDaemonReadyAsync();
        AssertNoPartialInstallations();
        await using var after = await _profile.ConnectAsync();
        foreach (var package in (await after.ListPackagesAsync()).Packages)
        {
            Assert.Equal("Verified", (await after.VerifyPackageAsync(package.PluginId)).Status);
        }
    }

    [Fact]
    public async Task InstallationProgress_IsBroadcastToEveryClient()
    {
        await PublishAsync(1, ("HelloPlugin", "1.0.0"));
        await StartDaemonReadyAsync();
        await using var watcher = await _profile.ConnectAsync("watcher");
        await using var actor = await _profile.ConnectAsync("actor");
        var stages = new List<PackageOperationStageDto>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.NotificationReceived += (_, n) =>
        {
            if (n.Name == IpcNotifications.PackageOperation && n.Read<PackageOperationDto>() is { } operation)
            {
                lock (stages)
                {
                    stages.Add(operation.Stage);
                }

                if (operation.Stage == PackageOperationStageDto.Completed)
                {
                    completed.TrySetResult();
                }
            }
        };
        await actor.RefreshRegistryAsync();

        await actor.InstallOfficialAsync("com.example.hello");
        await completed.Task.WaitAsync(Timeout);

        lock (stages)
        {
            Assert.Contains(PackageOperationStageDto.Downloading, stages);
            Assert.Contains(PackageOperationStageDto.Verifying, stages);
            Assert.Contains(PackageOperationStageDto.Installing, stages);
            Assert.True(stages.IndexOf(PackageOperationStageDto.Verifying) < stages.IndexOf(PackageOperationStageDto.Installing));
        }
    }

    private static async Task<PackageOperationDto> WaitForOperationAsync(RuntimeClient client, string operationId)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var operation = await client.GetOperationAsync(operationId);
            if (operation.IsFinished)
            {
                return operation;
            }

            Assert.True(DateTime.UtcNow < deadline, "The operation did not finish.");
            await Task.Delay(100);
        }
    }
}
