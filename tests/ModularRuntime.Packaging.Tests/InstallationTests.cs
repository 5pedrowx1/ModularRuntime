using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.Packaging;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Packaging.Tests;

/// <summary>
/// End-to-end installation with the real store, the real Loader, and real collectible load
/// contexts: verify before load, tamper detection, file locking, updates, rollback, recovery.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync, which xUnit calls.")]
public sealed class InstallationTests : IAsyncLifetime
{
    private static readonly PluginId Hello = PluginId.Parse("com.example.hello");

    private readonly TemporaryDirectory _temp = new("install");
    private readonly TestSigningKey _key = new("test-official-1");
    private readonly CollectingLogger _logger = new();
    private PackageStore _store = null!;
    private PluginRuntime _runtime = null!;

    private string PluginRoot => Path.Combine(_temp.DirectoryPath, "plugins");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(PluginRoot);
        _store = new PackageStore(PluginRoot, Path.Combine(_temp.DirectoryPath, "packages"), _key.TrustStore());
        _runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = PluginRoot, Logger = _logger, LoadVerifier = new PackageLoadVerifier(_store) });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _temp.Dispose();
        _key.Dispose();
    }

    private async Task<VerifiedPackage> PrepareAsync(string version, bool signed, string operationId, Action<string>? tamper = null)
    {
        var content = TestPackages.CreateContent(_temp.DirectoryPath, "HelloPlugin", version);
        var zip = await TestPackages.BuildAsync(content, Path.Combine(_temp.DirectoryPath, $"{operationId}.zip"), signed ? _key : null, tamper: tamper);
        var operation = _store.CreateOperationDirectory(operationId);
        var staged = Path.Combine(operation, "package.zip");
        File.Copy(zip, staged);
        return await new PackageVerifier(_key.TrustStore()).VerifyAndExtractAsync(staged, Path.Combine(operation, "content"), new PackageExpectation());
    }

    private async Task<InstalledPackageRecord> InstallAsync(string version = "1.0.0", bool signed = true, string operationId = "op-install-1")
    {
        var package = await PrepareAsync(version, signed, operationId);
        var record = _store.Publish(Path.Combine(_store.StagingRoot, operationId), package, PackageSource.LocalZip, registryRevision: null);
        var registration = await _runtime.RegisterInstallationAsync(_store.GetInstallationDirectory(Hello));
        Assert.Equal(1, registration.RegisteredCount);
        return record;
    }

    private async Task TakeOutAsync()
    {
        if (_runtime.GetPlugin(Hello)?.State == PluginState.Running)
        {
            Assert.True((await _runtime.StopAsync(Hello)).IsSuccess);
        }

        Assert.True((await _runtime.UnregisterAsync(Hello)).IsSuccess);
        Assert.True(await _runtime.WaitForUnloadAsync(Hello, TimeSpan.FromSeconds(15), PluginUnloadWait.InduceCollection), "The plugin's load context should unload.");
    }

    [Fact]
    public async Task OfficialPackage_IsInstalledAtomically_RecordedAsOfficial_AndLoadsInARealLoadContext()
    {
        var record = await InstallAsync();

        Assert.Equal(PackageTrust.Official, record.Trust);
        Assert.Equal(_key.KeyId, record.SigningKeyId);
        Assert.True(record.SignedWithTestKey);
        Assert.Equal(InstallationStatus.Verified, _store.Verify(Hello).Status);
        Assert.Empty(Directory.GetDirectories(_store.StagingRoot));

        var start = await _runtime.StartAsync(Hello);
        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(PluginState.Running, _runtime.GetPlugin(Hello)!.State);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("HelloPlugin initialized", StringComparison.Ordinal));
        await TakeOutAsync();
    }

    [Fact]
    public async Task StartStopStart_ReverifiesTheInstallationBeforeEveryNewLoad()
    {
        await InstallAsync();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True((await _runtime.StartAsync(Hello)).IsSuccess, $"cycle {cycle}");
            Assert.True((await _runtime.StopAsync(Hello)).IsSuccess);
        }

        // Modify a signed file while the plugin is stopped: the next start must be refused.
        File.AppendAllText(Path.Combine(_store.GetInstallationDirectory(Hello), "HelloPlugin.deps.json"), " ");

        var start = await _runtime.StartAsync(Hello);

        Assert.False(start.IsSuccess);
        Assert.Contains("was not loaded", start.Message, StringComparison.Ordinal);
        Assert.Equal(3, _logger.Entries.Count(e => e.Message.Contains("HelloPlugin initialized", StringComparison.Ordinal)));
        Assert.Equal(PluginState.Failed, _runtime.GetPlugin(Hello)!.State);

        // Restarting does not bypass the check either.
        Assert.False((await _runtime.RestartAsync(Hello)).IsSuccess);
        Assert.Equal(3, _logger.Entries.Count(e => e.Message.Contains("HelloPlugin initialized", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UnsignedPackage_IsInventoried_AndLaterModificationsAreDetected()
    {
        var record = await InstallAsync(signed: false);
        Assert.Equal(PackageTrust.Unverified, record.Trust);
        Assert.Null(record.SigningKeyId);
        Assert.Equal(InstallationStatus.Verified, _store.Verify(Hello).Status);

        File.AppendAllText(Path.Combine(_store.GetInstallationDirectory(Hello), "HelloPlugin.dll"), "x");

        Assert.Equal(InstallationStatus.Modified, _store.Verify(Hello).Status);
    }

    [Fact]
    public async Task ModifiedDllAfterInstallation_IsDetected_AndTheLoadIsRejectedBeforeAnyCodeRuns()
    {
        await InstallAsync();
        var dll = Path.Combine(_store.GetInstallationDirectory(Hello), "HelloPlugin.dll");
        var bytes = File.ReadAllBytes(dll);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(dll, bytes);

        var verification = _store.Verify(Hello);
        Assert.Equal(InstallationStatus.Modified, verification.Status);
        Assert.Contains(verification.Problems, p => p.StartsWith("HelloPlugin.dll", StringComparison.Ordinal));

        var start = await _runtime.StartAsync(Hello);
        Assert.False(start.IsSuccess);
        Assert.Equal(PluginErrorCodes.FactoryFailed, start.ErrorCode);
        Assert.Contains("was not loaded", start.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("HelloPlugin initialized", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddedFile_IsDetected()
    {
        await InstallAsync();
        File.WriteAllText(Path.Combine(_store.GetInstallationDirectory(Hello), "injected.dll"), "payload");

        var verification = _store.Verify(Hello);
        Assert.Equal(InstallationStatus.Modified, verification.Status);
        Assert.Contains("injected.dll: not part of the package", verification.Problems);
        Assert.False((await _runtime.StartAsync(Hello)).IsSuccess);
    }

    [Fact]
    public async Task DeletedFile_IsDetected()
    {
        await InstallAsync();
        File.Delete(Path.Combine(_store.GetInstallationDirectory(Hello), "HelloPlugin.deps.json"));

        Assert.Contains("HelloPlugin.deps.json: missing", _store.Verify(Hello).Problems);
    }

    [Fact]
    public async Task TamperedRecordedSignature_IsDetected()
    {
        await InstallAsync();
        var signaturePath = Path.Combine(_store.RecordsRoot, Hello.Value, PackagePaths.IntegritySignatureName);
        var envelope = SignatureEnvelope.Parse(File.ReadAllBytes(signaturePath), 16 * 1024);
        envelope.Signature[5] ^= 0xFF;
        File.WriteAllBytes(signaturePath, envelope.Serialize());

        Assert.Equal(InstallationStatus.SignatureInvalid, _store.Verify(Hello).Status);
        Assert.False((await _runtime.StartAsync(Hello)).IsSuccess);
    }

    [Fact]
    public async Task KeyRevokedAfterInstallation_PreventsLoading()
    {
        await InstallAsync();
        var revokedStore = new PackageStore(PluginRoot, _store.PackagesRoot, _key.TrustStore(revoked: true));

        Assert.Equal(InstallationStatus.SignatureInvalid, revokedStore.Verify(Hello).Status);
    }

    [Fact]
    public async Task WhileRunning_TheVerifiedFilesCannotBeModifiedOrMoved()
    {
        await InstallAsync();
        Assert.True((await _runtime.StartAsync(Hello)).IsSuccess);
        var directory = _store.GetInstallationDirectory(Hello);

        Assert.ThrowsAny<IOException>(() => File.AppendAllText(Path.Combine(directory, "HelloPlugin.dll"), "x"));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(Path.Combine(directory, "plugin.json"), "{}"));
        Assert.ThrowsAny<Exception>(() => Directory.Move(directory, directory + "-moved"));

        await TakeOutAsync();
        Assert.Equal(InstallationStatus.Verified, _store.Verify(Hello).Status);
    }

    [Fact]
    public async Task Update_ReplacesTheInstallationAndRecord_AndTheNewVersionLoads()
    {
        await InstallAsync("1.0.0");
        Assert.True((await _runtime.StartAsync(Hello)).IsSuccess);
        await TakeOutAsync();

        var update = await PrepareAsync("1.1.0", signed: true, "op-update-1");
        var record = _store.Publish(Path.Combine(_store.StagingRoot, "op-update-1"), update, PackageSource.Registry, registryRevision: 7);
        await _runtime.RegisterInstallationAsync(_store.GetInstallationDirectory(Hello));

        Assert.Equal("1.1.0", record.Version);
        Assert.Equal(7, record.RegistryRevision);
        Assert.Equal(new Version(1, 1, 0), _runtime.GetPlugin(Hello)!.Descriptor.Version);
        Assert.Equal(InstallationStatus.Verified, _store.Verify(Hello).Status);
        Assert.True((await _runtime.StartAsync(Hello)).IsSuccess);
        Assert.Empty(Directory.GetDirectories(_store.StagingRoot));
        await TakeOutAsync();
    }

    [Fact]
    public async Task UpdateThatCannotSwap_KeepsThePreviousVersionUntouched()
    {
        await InstallAsync("1.0.0");
        await TakeOutAsync();
        var update = await PrepareAsync("1.1.0", signed: true, "op-update-2");

        // Something holds a file of the current installation open without delete sharing.
        using (new FileStream(Path.Combine(_store.GetInstallationDirectory(Hello), "plugin.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<PackageException>(() => _store.Publish(Path.Combine(_store.StagingRoot, "op-update-2"), update, PackageSource.Registry, null));
            Assert.Equal(PackageErrorCodes.FilesInUse, error.Code);
        }

        _store.DeleteOperationDirectory(Path.Combine(_store.StagingRoot, "op-update-2"));
        Assert.Equal("1.0.0", _store.GetRecord(Hello)!.Version);
        Assert.Equal(InstallationStatus.Verified, _store.Verify(Hello).Status);
        await _runtime.RegisterInstallationAsync(_store.GetInstallationDirectory(Hello));
        Assert.Equal(new Version(1, 0, 0), _runtime.GetPlugin(Hello)!.Descriptor.Version);
    }

    [Fact]
    public async Task Remove_DeletesTheInstallationAndItsRecord()
    {
        await InstallAsync();
        await TakeOutAsync();

        _store.Remove(_store.CreateOperationDirectory("op-remove-1"), Hello);

        Assert.False(Directory.Exists(_store.GetInstallationDirectory(Hello)));
        Assert.Null(_store.GetRecord(Hello));
        Assert.Empty(Directory.GetDirectories(_store.StagingRoot));
        Assert.Equal(InstallationStatus.Missing, _store.Verify(Hello).Status);
    }

    [Fact]
    public async Task UnmanagedFolderWithTheSameId_IsNotOverwritten()
    {
        StagedPlugins.CopyPlugin(PluginRoot, "HelloPlugin", Hello.Value);
        var package = await PrepareAsync("1.0.0", signed: true, "op-conflict");

        var error = Assert.Throws<PackageException>(() => _store.Publish(Path.Combine(_store.StagingRoot, "op-conflict"), package, PackageSource.LocalZip, null));

        Assert.Equal(PackageErrorCodes.InstallationConflict, error.Code);
        Assert.True(File.Exists(Path.Combine(PluginRoot, Hello.Value, "HelloPlugin.dll")));
    }

    [Theory]
    [InlineData("prepared", "1.0.0")]
    [InlineData("backed-up", "1.0.0")]
    [InlineData("placed", "1.1.0")]
    [InlineData("committed", "1.1.0")]
    public async Task CrashDuringUpdate_IsRecoveredToAConsistentVersion(string crashAfter, string expectedVersion)
    {
        await InstallAsync("1.0.0");
        await TakeOutAsync();
        var update = await PrepareAsync("1.1.0", signed: true, "op-crash");
        _store.AfterJournalPhase = phase =>
        {
            if (phase == crashAfter)
            {
                throw new SimulatedCrashException();
            }
        };

        Assert.Throws<SimulatedCrashException>(() => _store.Publish(Path.Combine(_store.StagingRoot, "op-crash"), update, PackageSource.Registry, null));
        _store.AfterJournalPhase = null;

        var messages = _store.Recover();

        Assert.NotNull(messages);
        Assert.Equal(expectedVersion, _store.GetRecord(Hello)!.Version);
        Assert.Equal(InstallationStatus.Verified, _store.Verify(Hello).Status);
        Assert.Empty(Directory.GetDirectories(_store.StagingRoot));
        var reread = await new PluginLoader().ReadInstallationAsync(_store.GetInstallationDirectory(Hello));
        Assert.Equal(Version.Parse(expectedVersion), reread.Plugins.Single().Descriptor.Version);
    }

    [Fact]
    public async Task CrashDuringRemoval_IsCompletedByRecovery()
    {
        await InstallAsync();
        await TakeOutAsync();
        _store.AfterJournalPhase = phase => throw new SimulatedCrashException();

        Assert.Throws<SimulatedCrashException>(() => _store.Remove(_store.CreateOperationDirectory("op-remove-crash"), Hello));
        _store.AfterJournalPhase = null;
        _store.Recover();

        Assert.False(Directory.Exists(_store.GetInstallationDirectory(Hello)));
        Assert.Null(_store.GetRecord(Hello));
        Assert.Empty(Directory.GetDirectories(_store.StagingRoot));
    }

    [Fact]
    public async Task FailedOrCanceledVerification_LeavesThePluginRootUntouched()
    {
        var content = TestPackages.CreateContent(_temp.DirectoryPath, "HelloPlugin");
        var zip = await TestPackages.BuildAsync(content, Path.Combine(_temp.DirectoryPath, "bad.zip"), _key, tamper: dir => File.AppendAllText(Path.Combine(dir, "HelloPlugin.dll"), "x"));
        var operation = _store.CreateOperationDirectory("op-bad");
        File.Copy(zip, Path.Combine(operation, "package.zip"));

        await Assert.ThrowsAsync<PackageException>(() =>
            new PackageVerifier(_key.TrustStore()).VerifyAndExtractAsync(Path.Combine(operation, "package.zip"), Path.Combine(operation, "content"), new PackageExpectation()));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PackageVerifier(_key.TrustStore()).VerifyAndExtractAsync(zip, Path.Combine(operation, "content2"), new PackageExpectation(), cancellationToken: cancel.Token));
        _store.DeleteOperationDirectory(operation);

        Assert.DoesNotContain(Directory.GetFileSystemEntries(PluginRoot), p => Path.GetFileName(p) != ".staging");
        Assert.Empty(Directory.GetDirectories(_store.StagingRoot));
        Assert.Empty(_store.ListRecords());
    }

    [Fact]
    public async Task ManuallyCopiedPlugins_StillLoad_AndAreNotReportedAsVerified()
    {
        StagedPlugins.CopyPlugin(PluginRoot, "HelloPlugin", "hello-manual");
        await _runtime.DiscoverAsync();

        Assert.Null(_store.FindRecordForDirectory(Path.Combine(PluginRoot, "hello-manual")));
        Assert.True((await _runtime.StartAsync(Hello)).IsSuccess);
        await TakeOutAsync();
    }

    private sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException()
            : base("Simulated crash.")
        {
        }
    }
}
