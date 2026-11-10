using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Engine.Tests;

public sealed class PluginEngineLifecycleTests
{
    [Fact]
    public async Task StartAsync_InitializesPluginAndMovesToRunning()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.start");
        var plugin = new TestPlugin();
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        Assert.True(engine.MarkValidated(descriptor.Id).IsSuccess);

        var result = await engine.StartAsync(descriptor.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginState.Running, result.State);
        Assert.Equal(1, plugin.InitializeCount);
        Assert.NotNull(plugin.Context);
        Assert.Equal(descriptor.Id, plugin.Context.PluginId);
        Assert.NotEqual(Guid.Empty, plugin.Context.InstanceId);
        Assert.Equal(plugin.Context.InstanceId, engine.GetPlugin(descriptor.Id)!.InstanceId);
    }

    [Fact]
    public async Task StopAsync_ShutsDownPluginAndMovesToStopped()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.stop");
        var plugin = new TestPlugin();
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var result = await engine.StopAsync(descriptor.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginState.Stopped, result.State);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task Start_BeforeValidation_IsRejectedWithoutCreatingInstance()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.start-unvalidated");
        var plugin = new TestPlugin();
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);

        var result = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Rejected, result.Status);
        Assert.Equal(PluginErrorCodes.InvalidState, result.ErrorCode);
        Assert.Equal(PluginState.Discovered, result.State);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(PluginState.Discovered, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task Stop_BeforeRunning_IsRejected()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.stop-validated");
        engine.Register(descriptor, new TestPluginFactory(new TestPlugin()));
        engine.MarkValidated(descriptor.Id);

        var result = await engine.StopAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Rejected, result.Status);
        Assert.Equal(PluginErrorCodes.InvalidState, result.ErrorCode);
        Assert.Equal(PluginState.Validated, result.State);
    }

    [Fact]
    public async Task Start_AfterStop_IsRejectedBecauseInstancesAreTerminal()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.no-restart");
        var factory = new TestPluginFactory(new TestPlugin());
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);
        await engine.StopAsync(descriptor.Id);

        var secondStart = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Rejected, secondStart.Status);
        Assert.Equal(PluginErrorCodes.InvalidState, secondStart.ErrorCode);
        Assert.Equal(PluginState.Stopped, secondStart.State);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task InitializationFailure_MarksFailedAndReleasesFactory()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.init-failure");
        var plugin = new TestPlugin(
            onInitialize: (_, _) => throw new InvalidOperationException("intentional init failure"));
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var result = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, result.Status);
        Assert.Equal(PluginErrorCodes.InitializationFailed, result.ErrorCode);
        Assert.Equal(PluginState.Failed, result.State);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.Equal(1, plugin.ShutdownCount);

        var info = engine.GetPlugin(descriptor.Id)!;
        Assert.Equal(PluginState.Failed, info.State);
        Assert.NotNull(info.LastError);
        Assert.Equal(PluginErrorCodes.InitializationFailed, info.LastError.Code);
        Assert.Contains("InvalidOperationException", info.LastError.ExceptionType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoryFailure_MarksFailedWithoutInvokingPlugin()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.factory-failure");
        var factory = new TestPluginFactory(
            _ => throw new InvalidOperationException("intentional factory failure"));
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var result = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, result.Status);
        Assert.Equal(PluginErrorCodes.FactoryFailed, result.ErrorCode);
        Assert.Equal(PluginState.Failed, result.State);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.Null(engine.GetPlugin(descriptor.Id)!.InstanceId);
    }

    [Fact]
    public async Task ShutdownFailure_MarksFailedButStillReleasesFactory()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.stop-failure");
        var plugin = new TestPlugin(
            onShutdown: _ => throw new InvalidOperationException("intentional shutdown failure"));
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var result = await engine.StopAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, result.Status);
        Assert.Equal(PluginErrorCodes.ShutdownFailed, result.ErrorCode);
        Assert.Equal(PluginState.Failed, result.State);
        Assert.Equal(1, factory.ReleaseCount);
    }

    [Fact]
    public async Task InitializationFailure_DoesNotPreventIndependentPluginsFromRunning()
    {
        await using var engine = new PluginEngine();
        var failingDescriptor = TestDescriptors.Create("com.test.isolated-failing");
        var healthyDescriptor = TestDescriptors.Create("com.test.isolated-healthy");
        var failingPlugin = new TestPlugin(
            onInitialize: (_, _) => throw new InvalidOperationException("intentional isolation failure"));
        var healthyPlugin = new TestPlugin();
        var failingFactory = new TestPluginFactory(failingPlugin);
        var healthyFactory = new TestPluginFactory(healthyPlugin);

        engine.Register(failingDescriptor, failingFactory);
        engine.Register(healthyDescriptor, healthyFactory);
        engine.MarkValidated(failingDescriptor.Id);
        engine.MarkValidated(healthyDescriptor.Id);

        var failingResult = await engine.StartAsync(failingDescriptor.Id);
        var healthyResult = await engine.StartAsync(healthyDescriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, failingResult.Status);
        Assert.Equal(PluginState.Failed, failingResult.State);
        Assert.True(healthyResult.IsSuccess);
        Assert.Equal(PluginState.Running, healthyResult.State);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(failingDescriptor.Id)!.State);
        Assert.Equal(PluginState.Running, engine.GetPlugin(healthyDescriptor.Id)!.State);

        var stopResult = await engine.StopAsync(healthyDescriptor.Id);
        Assert.True(stopResult.IsSuccess);
    }
}
