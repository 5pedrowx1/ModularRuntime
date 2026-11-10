using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>
/// Unload tracking with real collectible load contexts: a plugin that keeps itself reachable
/// after shutdown must stay tracked across timeouts and cancellations until it is released.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync, which xUnit calls.")]
public sealed class UnloadTrackingTests : IAsyncLifetime
{
    private const string RootKey = "ModularRuntime.TestPlugin.RetainingPlugin:";
    private static readonly PluginId RetainingId = PluginId.Parse("test.retaining");
    private static readonly PluginId HelloId = PluginId.Parse("com.example.hello");
    private static readonly PluginId JsonId = PluginId.Parse("test.json-user");

    private readonly TemporaryDirectory _temporary = new("unload-tracking");
    private readonly List<Guid> _instances = [];
    private PluginRuntime _runtime = null!;

    private string Root => Path.Combine(_temporary.DirectoryPath, "plugins");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        StagedPlugins.CopyPlugin(Root, "HelloPlugin");
        var directory = StagedPlugins.CopyPlugin(Root, "ModularRuntime.TestPlugin", "test.retaining");
        var manifest = StagedPlugins.ReadManifest(directory);
        manifest["id"] = "test.retaining";
        manifest["entryType"] = "ModularRuntime.TestPlugin.RetainingPlugin";
        StagedPlugins.WriteManifest(directory, manifest);
        var jsonDirectory = StagedPlugins.CopyPlugin(Root, "ModularRuntime.TestPlugin", "test.json-user");
        var jsonManifest = StagedPlugins.ReadManifest(jsonDirectory);
        jsonManifest["id"] = "test.json-user";
        jsonManifest["entryType"] = "ModularRuntime.TestPlugin.JsonUsingPlugin";
        StagedPlugins.WriteManifest(jsonDirectory, jsonManifest);
        _runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = Root });
        await _runtime.DiscoverAsync();
    }

    public async Task DisposeAsync()
    {
        ReleaseRoots();
        await _runtime.DisposeAsync();
        _temporary.Dispose();
    }

    private void ReleaseRoots()
    {
        foreach (var instance in _instances)
        {
            AppDomain.CurrentDomain.SetData(RootKey + instance.ToString("N"), null);
        }
    }

    private async Task RunRetainingCyclesAsync(int cycles)
    {
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            Assert.True((await _runtime.StartAsync(RetainingId)).IsSuccess);
            _instances.Add(_runtime.GetPlugin(RetainingId)!.InstanceId!.Value);
            Assert.True((await _runtime.StopAsync(RetainingId)).IsSuccess);
        }

        Assert.True((await _runtime.UnregisterAsync(RetainingId)).IsSuccess);
    }

    [Fact]
    public async Task Timeout_KeepsLiveContextsTracked_AndTheWaitCanBeRepeated()
    {
        await RunRetainingCyclesAsync(3);

        // The plugin keeps every instance reachable: unload is never reported as complete.
        Assert.False(await _runtime.WaitForUnloadAsync(RetainingId, TimeSpan.FromMilliseconds(300), PluginUnloadWait.InduceCollection));
        Assert.Equal(3, _runtime.GetPendingUnloadCount(RetainingId));
        Assert.False(await _runtime.WaitForUnloadAsync(RetainingId, TimeSpan.FromMilliseconds(300), PluginUnloadWait.InduceCollection));
        Assert.Equal(3, _runtime.GetPendingUnloadCount(RetainingId));

        // Once released, the same tracking observes the collection of all three contexts.
        ReleaseRoots();
        Assert.True(await _runtime.WaitForUnloadAsync(RetainingId, TimeSpan.FromSeconds(20), PluginUnloadWait.InduceCollection));
        Assert.Equal(0, _runtime.GetPendingUnloadCount(RetainingId));
    }

    [Fact]
    public async Task Cancellation_KeepsLiveContextsTracked()
    {
        await RunRetainingCyclesAsync(2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runtime.WaitForUnloadAsync(RetainingId, TimeSpan.FromMinutes(5), PluginUnloadWait.InduceCollection, cancellation.Token));

        Assert.Equal(2, _runtime.GetPendingUnloadCount(RetainingId));
        ReleaseRoots();
        Assert.True(await _runtime.WaitForUnloadAsync(RetainingId, TimeSpan.FromSeconds(20), PluginUnloadWait.InduceCollection));
    }

    [Fact]
    public async Task ARetainingPlugin_DoesNotHideTheCollectionOfAWellBehavedOne()
    {
        await RunRetainingCyclesAsync(1);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True((await _runtime.StartAsync(HelloId)).IsSuccess);
            Assert.True((await _runtime.StopAsync(HelloId)).IsSuccess);
        }

        Assert.True((await _runtime.UnregisterAsync(HelloId)).IsSuccess);

        Assert.True(await _runtime.WaitForUnloadAsync(HelloId, TimeSpan.FromSeconds(20), PluginUnloadWait.InduceCollection));
        Assert.Equal(1, _runtime.GetPendingUnloadCount(RetainingId));
    }

    [Fact]
    public async Task ConcurrentWaits_AllObserveTheCollection_WithoutDeadlock()
    {
        await RunRetainingCyclesAsync(2);
        var waits = Enumerable.Range(0, 6)
            .Select(i => _runtime.WaitForUnloadAsync(RetainingId, TimeSpan.FromSeconds(30), i % 2 == 0 ? PluginUnloadWait.Observe : PluginUnloadWait.InduceCollection))
            .ToList();
        await Task.Delay(200);
        Assert.All(waits, w => Assert.False(w.IsCompleted));

        ReleaseRoots();

        var results = await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(40));
        Assert.All(results, Assert.True);
    }

    [Fact]
    public async Task PluginsUsingSystemTextJson_AreCollected_WhenCollectionIsInduced()
    {
        // System.Text.Json lives in the shared context and caches reflection-emitted accessors per
        // type, evicting them only lazily; without flushing those caches the plugin's types (and its
        // load context) stay reachable after it stopped.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True((await _runtime.StartAsync(JsonId)).IsSuccess);
            Assert.True((await _runtime.StopAsync(JsonId)).IsSuccess);
        }

        Assert.True((await _runtime.UnregisterAsync(JsonId)).IsSuccess);

        Assert.True(await _runtime.WaitForUnloadAsync(JsonId, TimeSpan.FromSeconds(20), PluginUnloadWait.InduceCollection), "A load context of a plugin that used System.Text.Json is still alive.");
        Assert.Equal(0, _runtime.GetPendingUnloadCount(JsonId));
    }
}
