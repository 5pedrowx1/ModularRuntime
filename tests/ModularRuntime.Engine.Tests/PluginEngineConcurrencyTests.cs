using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

public sealed class PluginEngineConcurrencyTests
{
    [Fact]
    public async Task ConcurrentStarts_SerializeAndOnlyOneSucceeds()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.concurrent-start");
        var initializationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new TestPlugin(
            onInitialize: async (_, cancellationToken) =>
            {
                initializationEntered.TrySetResult();
                await releaseInitialization.Task.WaitAsync(cancellationToken);
            });
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var firstStart = engine.StartAsync(descriptor.Id);
        await initializationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondStart = engine.StartAsync(descriptor.Id);

        releaseInitialization.TrySetResult();
        var results = await Task.WhenAll(firstStart, secondStart)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Status == PluginOperationStatus.Rejected);
        Assert.Equal(1, plugin.InitializeCount);
        Assert.Equal(PluginState.Running, engine.GetPlugin(descriptor.Id)!.State);

        Assert.True((await engine.StopAsync(descriptor.Id)).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentStops_SerializeAndOnlyOneSucceeds()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.concurrent-stop");
        var shutdownEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new TestPlugin(
            onShutdown: async cancellationToken =>
            {
                shutdownEntered.TrySetResult();
                await releaseShutdown.Task.WaitAsync(cancellationToken);
            });
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var firstStop = engine.StopAsync(descriptor.Id);
        await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondStop = engine.StopAsync(descriptor.Id);

        releaseShutdown.TrySetResult();
        var results = await Task.WhenAll(firstStop, secondStop)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Status == PluginOperationStatus.Rejected);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task StartCancellation_IsCooperativeAndMarksPluginFailed()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.cancel-start");
        var initializationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new TestPlugin(
            onInitialize: async (context, cancellationToken) =>
            {
                initializationEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Assert.True(context.StoppingToken.IsCancellationRequested ||
                        cancellationToken.IsCancellationRequested);
                    throw;
                }
            });
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        using var cancellation = new CancellationTokenSource();
        var start = engine.StartAsync(descriptor.Id, cancellation.Token);
        await initializationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var result = await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PluginOperationStatus.Canceled, result.Status);
        Assert.Equal(PluginErrorCodes.OperationCanceled, result.ErrorCode);
        Assert.Equal(PluginState.Failed, result.State);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.Equal(1, plugin.ShutdownCount);
    }

    [Fact]
    public async Task StopCancellationWhileWaiting_LeavesPluginRunning()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.cancel-stop");
        var shutdownEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new TestPlugin(
            onShutdown: async cancellationToken =>
            {
                shutdownEntered.TrySetResult();
                await releaseShutdown.Task.WaitAsync(cancellationToken);
            });
        engine.Register(descriptor, new TestPluginFactory(plugin));
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var firstStop = engine.StopAsync(descriptor.Id);
        await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledStop = await engine.StopAsync(descriptor.Id, cancellation.Token);

        Assert.Equal(PluginOperationStatus.Canceled, canceledStop.Status);
        Assert.Equal(PluginState.Stopping, canceledStop.State);

        releaseShutdown.TrySetResult();
        Assert.True((await firstStop.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task TwoEngineInstances_DoNotShareMutableState()
    {
        await using var firstEngine = new PluginEngine();
        await using var secondEngine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.shared-id");
        var firstPlugin = new TestPlugin();
        var secondPlugin = new TestPlugin();
        firstEngine.Register(descriptor, new TestPluginFactory(firstPlugin));
        secondEngine.Register(descriptor, new TestPluginFactory(secondPlugin));
        firstEngine.MarkValidated(descriptor.Id);
        secondEngine.MarkValidated(descriptor.Id);

        Assert.True((await firstEngine.StartAsync(descriptor.Id)).IsSuccess);
        Assert.True((await secondEngine.StartAsync(descriptor.Id)).IsSuccess);

        Assert.NotNull(firstPlugin.Context);
        Assert.NotNull(secondPlugin.Context);
        Assert.NotEqual(firstPlugin.Context.InstanceId, secondPlugin.Context.InstanceId);

        Assert.True((await firstEngine.StopAsync(descriptor.Id)).IsSuccess);
        Assert.Equal(PluginState.Stopped, firstEngine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(PluginState.Running, secondEngine.GetPlugin(descriptor.Id)!.State);

        Assert.True((await secondEngine.StopAsync(descriptor.Id)).IsSuccess);
    }

    [Fact]
    public async Task StopAndDispose_ConcurrentCallsLeaveTerminalState()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.stop-dispose-race");
        var plugin = new TestPlugin();
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var stopTask = engine.StopAsync(descriptor.Id);
        var disposeTask = engine.DisposeAsync().AsTask();
        await Task.WhenAll(stopTask, disposeTask);

        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);
    }
}
