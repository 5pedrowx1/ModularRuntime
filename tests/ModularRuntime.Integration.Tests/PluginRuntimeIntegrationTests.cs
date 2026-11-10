using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

public sealed class PluginRuntimeIntegrationTests
{
    private static readonly PluginId HelloId = PluginId.Parse("com.example.hello");
    private static readonly PluginId FailingId = PluginId.Parse("com.example.failing");

    [Fact]
    public async Task PluginRuntime_StartsAndStopsHelloPlugin()
    {
        using var temporary = new TemporaryDirectory("runtime-hello");
        var root = CreatePluginRoot(temporary);
        StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var logger = new CollectingLogger();
        await using var runtime = new PluginRuntime(new PluginRuntimeOptions
        {
            PluginDirectory = root,
            Logger = logger,
        });

        var discovery = await runtime.DiscoverAsync();

        Assert.False(discovery.HasErrors);
        Assert.Equal(1, discovery.RegisteredCount);
        var validated = Assert.Single(runtime.Plugins);
        Assert.Equal(HelloId, validated.Descriptor.Id);
        Assert.Equal(PluginState.Validated, validated.State);

        var start = await runtime.StartAsync(HelloId);

        Assert.True(start.IsSuccess);
        Assert.Equal(PluginState.Running, start.State);
        var running = runtime.GetPlugin(HelloId);
        Assert.NotNull(running);
        Assert.Equal(PluginState.Running, running.State);
        Assert.NotNull(running.InstanceId);
        Assert.NotNull(running.StartedAt);
        Assert.True(logger.HasMessageContaining("HelloPlugin initialized"));
        Assert.True(logger.HasMessageContaining("Plugin 'com.example.hello' started"));

        var secondStart = await runtime.StartAsync(HelloId);

        Assert.Equal(PluginOperationStatus.Rejected, secondStart.Status);
        Assert.Equal(PluginErrorCodes.InvalidState, secondStart.ErrorCode);
        Assert.Equal(PluginState.Running, secondStart.State);

        var stop = await runtime.StopAsync(HelloId);

        Assert.True(stop.IsSuccess);
        Assert.Equal(PluginState.Stopped, stop.State);
        var stopped = runtime.GetPlugin(HelloId);
        Assert.NotNull(stopped);
        Assert.Equal(PluginState.Stopped, stopped.State);
        Assert.NotNull(stopped.StoppedAt);
        Assert.True(logger.HasMessageContaining("HelloPlugin shut down."));

        // Since v0.5.0 a stopped plugin starts again with a new registration, instance, and load context.
        var restart = await runtime.StartAsync(HelloId);

        Assert.True(restart.IsSuccess, restart.Message);
        Assert.Equal(PluginState.Running, restart.State);
        Assert.NotEqual(running.InstanceId, runtime.GetPlugin(HelloId)!.InstanceId);
    }

    [Fact]
    public async Task FailingPlugin_IsIsolatedFromHelloPlugin()
    {
        using var temporary = new TemporaryDirectory("runtime-isolation");
        var root = CreatePluginRoot(temporary);
        StagedPlugins.CopyPlugin(root, "HelloPlugin");
        StagedPlugins.CopyPlugin(root, "FailingPlugin");
        var logger = new CollectingLogger();
        await using var runtime = new PluginRuntime(new PluginRuntimeOptions
        {
            PluginDirectory = root,
            Logger = logger,
        });

        var discovery = await runtime.DiscoverAsync();

        Assert.False(discovery.HasErrors);
        Assert.Equal(2, discovery.RegisteredCount);

        var startResults = await runtime.StartAllAsync();

        Assert.Equal(2, startResults.Count);
        var helloStart = Assert.Single(startResults, result => result.PluginId == HelloId);
        Assert.True(helloStart.IsSuccess);
        Assert.Equal(PluginState.Running, helloStart.State);
        var failingStart = Assert.Single(startResults, result => result.PluginId == FailingId);
        Assert.Equal(PluginOperationStatus.Failed, failingStart.Status);
        Assert.Equal(PluginErrorCodes.InitializationFailed, failingStart.ErrorCode);
        Assert.Equal(PluginState.Failed, failingStart.State);

        var hello = runtime.GetPlugin(HelloId);
        Assert.NotNull(hello);
        Assert.Equal(PluginState.Running, hello.State);
        var failing = runtime.GetPlugin(FailingId);
        Assert.NotNull(failing);
        Assert.Equal(PluginState.Failed, failing.State);
        Assert.NotNull(failing.LastError);
        Assert.Equal(PluginErrorCodes.InitializationFailed, failing.LastError.Code);
        Assert.Contains("InvalidOperationException", failing.LastError.ExceptionType);
        Assert.True(logger.HasMessageContaining("FailingPlugin intentionally fails"));

        var stopResults = await runtime.StopAllAsync();

        Assert.Contains(stopResults, result => result.PluginId == HelloId && result.IsSuccess);
        Assert.Contains(
            stopResults,
            result => result.PluginId == FailingId &&
                      result.Status == PluginOperationStatus.Rejected &&
                      result.ErrorCode == PluginErrorCodes.InvalidState);
        Assert.Equal(PluginState.Stopped, runtime.GetPlugin(HelloId)!.State);
        Assert.Equal(PluginState.Failed, runtime.GetPlugin(FailingId)!.State);
    }

    [Fact]
    public async Task TwoRuntimeInstances_LoadTheSamePluginIndependently()
    {
        using var temporary = new TemporaryDirectory("runtime-independent");
        var root = CreatePluginRoot(temporary);
        StagedPlugins.CopyPlugin(root, "HelloPlugin");
        await using var first = new PluginRuntime(new PluginRuntimeOptions
        {
            PluginDirectory = root,
            Logger = new CollectingLogger(),
        });
        await using var second = new PluginRuntime(new PluginRuntimeOptions
        {
            PluginDirectory = root,
            Logger = new CollectingLogger(),
        });

        Assert.Equal(1, (await first.DiscoverAsync()).RegisteredCount);
        Assert.Equal(1, (await second.DiscoverAsync()).RegisteredCount);
        Assert.True((await first.StartAsync(HelloId)).IsSuccess);
        Assert.True((await second.StartAsync(HelloId)).IsSuccess);

        var firstInstance = first.GetPlugin(HelloId)!.InstanceId;
        var secondInstance = second.GetPlugin(HelloId)!.InstanceId;
        Assert.NotNull(firstInstance);
        Assert.NotNull(secondInstance);
        Assert.NotEqual(firstInstance, secondInstance);
        Assert.Equal(PluginState.Running, second.GetPlugin(HelloId)!.State);

        Assert.True((await first.StopAsync(HelloId)).IsSuccess);

        Assert.Equal(PluginState.Stopped, first.GetPlugin(HelloId)!.State);
        Assert.Equal(PluginState.Running, second.GetPlugin(HelloId)!.State);

        Assert.True((await second.StopAsync(HelloId)).IsSuccess);
        Assert.Equal(PluginState.Stopped, second.GetPlugin(HelloId)!.State);
    }

    [Fact]
    public async Task EngineDispose_CollectsThePluginLoadContext()
    {
        using var temporary = new TemporaryDirectory("runtime-unload");
        var root = CreatePluginRoot(temporary);
        StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var logger = new CollectingLogger();
        var loader = new PluginLoader(logger);
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = loader.CreateInstanceFactory(candidate);
        await using var engine = new PluginEngine(logger);

        Assert.True(engine.Register(candidate.Descriptor, factory).IsSuccess);
        Assert.True(engine.MarkValidated(candidate.Descriptor.Id).IsSuccess);
        Assert.True((await engine.StartAsync(candidate.Descriptor.Id)).IsSuccess);

        var weakContext = factory.CreateLoadContextWeakReference();
        Assert.NotNull(weakContext);

        await engine.DisposeAsync();

        Assert.Equal(PluginState.Stopped, engine.GetPlugin(candidate.Descriptor.Id)!.State);
        Assert.True(
            GcDiagnostics.WaitForCollection(weakContext),
            "The plugin load context should be collectible after the engine is disposed.");
    }

    private static string CreatePluginRoot(TemporaryDirectory temporary)
    {
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        return root;
    }
}
