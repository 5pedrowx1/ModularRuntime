using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

public sealed class PluginEngineShutdownTests
{
    [Fact]
    public async Task StopAll_StopsInReverseRegistrationOrder()
    {
        await using var engine = new PluginEngine();
        var shutdownOrder = new List<string>();
        var plugins = new List<(PluginDescriptor Descriptor, TestPluginFactory Factory)>();

        foreach (var name in new[] { "alpha", "beta", "gamma" })
        {
            var descriptor = TestDescriptors.Create($"com.test.order-{name}");
            var plugin = new TestPlugin(
                onShutdown: _ =>
                {
                    shutdownOrder.Add(name);
                    return Task.CompletedTask;
                });
            var factory = new TestPluginFactory(plugin);
            plugins.Add((descriptor, factory));
            engine.Register(descriptor, factory);
            engine.MarkValidated(descriptor.Id);
        }

        var startResults = await engine.StartAllAsync();
        Assert.Equal(3, startResults.Count);
        Assert.All(startResults, result => Assert.True(result.IsSuccess));

        var stopResults = await engine.StopAllAsync();

        Assert.Equal(3, stopResults.Count);
        Assert.All(stopResults, result => Assert.True(result.IsSuccess));
        Assert.Equal(["gamma", "beta", "alpha"], shutdownOrder);
        Assert.All(engine.GetPlugins(), info => Assert.Equal(PluginState.Stopped, info.State));
    }

    [Fact]
    public async Task StopAll_ContinuesAfterOnePluginShutdownFails()
    {
        await using var engine = new PluginEngine();
        var failingDescriptor = TestDescriptors.Create("com.test.stop-all-failing");
        var healthyDescriptor = TestDescriptors.Create("com.test.stop-all-healthy");
        var failing = new TestPlugin(
            onShutdown: _ => throw new InvalidOperationException("intentional stop-all failure"));
        var healthy = new TestPlugin();
        engine.Register(failingDescriptor, new TestPluginFactory(failing));
        engine.Register(healthyDescriptor, new TestPluginFactory(healthy));
        engine.MarkValidated(failingDescriptor.Id);
        engine.MarkValidated(healthyDescriptor.Id);
        await engine.StartAllAsync();

        var results = await engine.StopAllAsync();

        Assert.Equal(2, results.Count);
        Assert.Single(results, result => result.Status == PluginOperationStatus.Failed);
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(failingDescriptor.Id)!.State);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(healthyDescriptor.Id)!.State);
    }

    [Fact]
    public async Task DisposeAsync_StopsRunningPluginsAndReleasesFactories()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.dispose");
        var plugin = new TestPlugin();
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        await engine.DisposeAsync();

        Assert.True(engine.IsShuttingDown);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);

        var afterDispose = await engine.StartAsync(descriptor.Id);
        Assert.Equal(PluginOperationStatus.Rejected, afterDispose.Status);
        Assert.Equal(PluginErrorCodes.EngineDisposed, afterDispose.ErrorCode);

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ReleasesFactoriesOfPluginsThatNeverStarted()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.dispose-unstarted");
        var factory = new TestPluginFactory(new TestPlugin());
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        await engine.DisposeAsync();

        Assert.Equal(1, factory.ReleaseCount);
        Assert.Equal(PluginState.Validated, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.dispose-idempotent");
        var factory = new TestPluginFactory(new TestPlugin());
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        await engine.DisposeAsync();
        await engine.DisposeAsync();

        Assert.Equal(1, factory.ReleaseCount);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallsRunCleanupOnce()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.dispose-concurrent");
        var plugin = new TestPlugin();
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var disposals = Enumerable.Range(0, 8)
            .Select(_ => engine.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals);

        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task DisposeAsync_RetriesFactoryReleaseAfterFailure()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.dispose-release-retry");
        var releaseCount = 0;
        var factory = new TestPluginFactory(
            new TestPlugin(),
            release: () =>
            {
                if (Interlocked.Increment(ref releaseCount) == 1)
                {
                    return ValueTask.FromException(
                        new InvalidOperationException("intentional release failure"));
                }

                return ValueTask.CompletedTask;
            });
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var stop = await engine.StopAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, stop.Status);
        Assert.Equal(PluginErrorCodes.ReleaseFailed, stop.ErrorCode);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(1, releaseCount);

        await engine.DisposeAsync();

        Assert.Equal(2, releaseCount);
    }
}
