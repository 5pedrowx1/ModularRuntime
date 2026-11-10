using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>
/// Start → Stop → Start with real assemblies: every run gets a new registration, factory, instance,
/// and collectible load context; the previous ones are released and collected.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync, which xUnit calls.")]
public sealed class PluginRestartTests : IAsyncLifetime
{
    private static readonly PluginId HelloId = PluginId.Parse("com.example.hello");
    private static readonly PluginId FailingId = PluginId.Parse("com.example.failing");

    private readonly TemporaryDirectory _temporary = new("restart");
    private readonly CollectingLogger _logger = new();
    private PluginRuntime _runtime = null!;

    private string Root => Path.Combine(_temporary.DirectoryPath, "plugins");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        StagedPlugins.CopyPlugin(Root, "HelloPlugin");
        StagedPlugins.CopyPlugin(Root, "FailingPlugin");
        _runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = Root, Logger = _logger });
        await _runtime.DiscoverAsync();
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        _temporary.Dispose();
    }

    private int InitializedCount => _logger.Entries.Count(e => e.Message.Contains("HelloPlugin initialized", StringComparison.Ordinal));

    [Fact]
    public async Task ReadyRunningStoppedRunningStopped_RepeatsWithANewInstanceEachTime()
    {
        var instances = new HashSet<Guid>();
        for (var cycle = 1; cycle <= 5; cycle++)
        {
            var start = await _runtime.StartAsync(HelloId);
            Assert.True(start.IsSuccess, $"cycle {cycle}: {start.Message}");
            Assert.Equal(PluginState.Running, _runtime.GetPlugin(HelloId)!.State);
            Assert.True(instances.Add(_runtime.GetPlugin(HelloId)!.InstanceId!.Value), "Every start must create a new instance.");

            var stop = await _runtime.StopAsync(HelloId);
            Assert.True(stop.IsSuccess, $"cycle {cycle}: {stop.Message}");
            Assert.Equal(PluginState.Stopped, _runtime.GetPlugin(HelloId)!.State);
        }

        Assert.Equal(5, InitializedCount);
        Assert.Equal(5, instances.Count);
    }

    [Fact]
    public async Task OldLoadContexts_AreCollected_AfterRepeatedRestarts()
    {
        for (var cycle = 0; cycle < 4; cycle++)
        {
            Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
            Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess);
        }

        // Retire the last registration too, then every load context created by the four runs
        // must be collectible: nothing retains assemblies of earlier instances.
        Assert.True((await _runtime.UnregisterAsync(HelloId)).IsSuccess);
        Assert.True(await _runtime.WaitForUnloadAsync(HelloId, TimeSpan.FromSeconds(20), PluginUnloadWait.InduceCollection), "A previous load context is still alive.");
    }

    [Fact]
    public async Task Restart_StopsAndStartsWithANewInstance()
    {
        Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
        var first = _runtime.GetPlugin(HelloId)!.InstanceId;

        var restart = await _runtime.RestartAsync(HelloId);

        Assert.True(restart.IsSuccess, restart.Message);
        Assert.Equal("restart", restart.Operation);
        Assert.Equal(PluginState.Running, _runtime.GetPlugin(HelloId)!.State);
        Assert.NotEqual(first, _runtime.GetPlugin(HelloId)!.InstanceId);
        Assert.True(_logger.HasMessageContaining("HelloPlugin shut down."));
        Assert.Equal(2, InitializedCount);
    }

    [Fact]
    public async Task RestartOfAStoppedPlugin_StartsIt()
    {
        Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
        Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess);

        Assert.True((await _runtime.RestartAsync(HelloId)).IsSuccess);
        Assert.Equal(PluginState.Running, _runtime.GetPlugin(HelloId)!.State);
    }

    [Fact]
    public async Task ConcurrentStartsOfAStoppedPlugin_CreateExactlyOneInstance()
    {
        Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
        Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _runtime.StartAsync(HelloId))));

        Assert.Single(results, r => r.IsSuccess);
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal(PluginErrorCodes.InvalidState, r.ErrorCode));
        Assert.Equal(2, InitializedCount);
        Assert.Equal(PluginState.Running, _runtime.GetPlugin(HelloId)!.State);
    }

    [Fact]
    public async Task FailedPlugin_CanBeRetried_AndReportsItsDiagnosticEveryTime()
    {
        var first = await _runtime.StartAsync(FailingId);
        Assert.False(first.IsSuccess);
        Assert.Equal(PluginState.Failed, _runtime.GetPlugin(FailingId)!.State);

        var retry = await _runtime.StartAsync(FailingId);

        Assert.False(retry.IsSuccess);
        Assert.Equal(PluginErrorCodes.InitializationFailed, retry.ErrorCode);
        Assert.NotNull(retry.Diagnostic);
        Assert.Contains("intentionally fails", retry.Message, StringComparison.Ordinal);
        Assert.Equal(2, _logger.Entries.Count(e => e.Message.Contains("FailingPlugin initialization started", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StartAll_StartsStoppedPluginsAgain_ButDoesNotRetryFailedOnes()
    {
        await _runtime.StartAllAsync();
        Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess);

        var results = await _runtime.StartAllAsync();

        Assert.True(results.Single(r => r.PluginId == HelloId).IsSuccess);
        Assert.Equal(PluginErrorCodes.InvalidState, results.Single(r => r.PluginId == FailingId).ErrorCode);
        Assert.Equal(PluginState.Running, _runtime.GetPlugin(HelloId)!.State);
    }

    [Fact]
    public async Task InstallationChangedWhileStopped_IsReadAgainBeforeTheNewStart()
    {
        Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
        Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess);
        var directory = _runtime.GetInstallation(HelloId)!.PluginDirectory;
        StagedPlugins.WriteRawManifest(directory, "{ \"id\": \"com.example.hello\" }");

        var start = await _runtime.StartAsync(HelloId);

        Assert.False(start.IsSuccess);
        Assert.Contains("no longer valid", start.Message, StringComparison.Ordinal);
        Assert.Equal(PluginState.Stopped, _runtime.GetPlugin(HelloId)!.State); // the stopped registration is kept
    }
}
