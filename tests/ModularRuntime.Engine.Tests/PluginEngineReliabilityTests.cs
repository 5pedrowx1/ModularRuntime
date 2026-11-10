using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Engine.Tests;

/// <summary>
/// v0.1.1 reliability scenarios: failing host loggers, hostile exception objects,
/// disposal during initialization, and reentrant cancellation callbacks.
/// </summary>
public sealed class PluginEngineReliabilityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ThrowingRuntimeLogger_DoesNotBreakLifecycleTransitions()
    {
        var logger = new ThrowingRuntimeLogger();
        var engine = new PluginEngine(logger);
        var descriptor = TestDescriptors.Create("com.test.throwing-logger");
        var plugin = new TestPlugin();
        var factory = new TestPluginFactory(plugin);

        Assert.True(engine.Register(descriptor, factory).IsSuccess);
        Assert.True(engine.MarkValidated(descriptor.Id).IsSuccess);

        var start = await engine.StartAsync(descriptor.Id);
        Assert.True(start.IsSuccess);
        Assert.Equal(PluginState.Running, engine.GetPlugin(descriptor.Id)!.State);

        var stop = await engine.StopAsync(descriptor.Id);
        Assert.True(stop.IsSuccess);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);

        await engine.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.True(logger.Calls > 0);
    }

    [Fact]
    public async Task ThrowingRuntimeLogger_DuringStartFailure_LeavesFailedStateAndReleases()
    {
        var engine = new PluginEngine(new ThrowingRuntimeLogger());
        var descriptor = TestDescriptors.Create("com.test.throwing-logger-failure");
        var plugin = new TestPlugin(
            onInitialize: (_, _) => throw new InvalidOperationException("init failure"));
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var start = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, start.Status);
        Assert.Equal(PluginErrorCodes.InitializationFailed, start.ErrorCode);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);

        await engine.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ThrowingRuntimeLogger_IsIsolatedFromPluginLogging()
    {
        await using var engine = new PluginEngine(new ThrowingRuntimeLogger());
        var descriptor = TestDescriptors.Create("com.test.plugin-logging-isolated");
        var plugin = new TestPlugin(
            onInitialize: (context, _) =>
            {
                context.Logger.LogInformation("information");
                context.Logger.LogError("error with exception", new HostileException());
                return Task.CompletedTask;
            });
        engine.Register(descriptor, new TestPluginFactory(plugin));
        engine.MarkValidated(descriptor.Id);

        var start = await engine.StartAsync(descriptor.Id);

        Assert.True(start.IsSuccess);
    }

    [Fact]
    public async Task HostileException_DuringInitialization_IsCapturedAsText()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger);
        var descriptor = TestDescriptors.Create("com.test.hostile-initialization");
        var plugin = new TestPlugin(onInitialize: (_, _) => throw new HostileException());
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var start = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, start.Status);
        Assert.Equal(PluginErrorCodes.InitializationFailed, start.ErrorCode);
        Assert.NotNull(start.Diagnostic);
        Assert.Equal(typeof(HostileException).FullName, start.Diagnostic.ExceptionType);
        Assert.Contains("unavailable", start.Diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(start.Diagnostic.Detail);

        var info = engine.GetPlugin(descriptor.Id)!;
        Assert.Equal(PluginState.Failed, info.State);
        Assert.Equal(typeof(HostileException).FullName, info.LastError!.ExceptionType);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.NotNull(logger.Find(LogLevel.Error, "failed during initialization"));
    }

    [Fact]
    public async Task HostileException_DuringShutdown_IsCapturedAsText()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.hostile-shutdown");
        var plugin = new TestPlugin(onShutdown: _ => throw new HostileException());
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        Assert.True((await engine.StartAsync(descriptor.Id)).IsSuccess);

        var stop = await engine.StopAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, stop.Status);
        Assert.Equal(PluginErrorCodes.ShutdownFailed, stop.ErrorCode);
        Assert.Equal(typeof(HostileException).FullName, stop.Diagnostic!.ExceptionType);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(1, factory.ReleaseCount);
    }

    [Fact]
    public async Task HostileException_FromFactory_IsCapturedAsText()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.hostile-factory");
        var factory = new TestPluginFactory(_ => throw new HostileException());
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var start = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginOperationStatus.Failed, start.Status);
        Assert.Equal(PluginErrorCodes.FactoryFailed, start.ErrorCode);
        Assert.Equal(typeof(HostileException).FullName, start.Diagnostic!.ExceptionType);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(1, factory.ReleaseCount);
    }

    [Fact]
    public async Task HostileException_InCancellationCallback_DoesNotPreventStop()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger);
        var descriptor = TestDescriptors.Create("com.test.hostile-callback");
        var plugin = new TestPlugin(
            onInitialize: (context, _) =>
            {
                context.StoppingToken.Register(() => throw new HostileException());
                return Task.CompletedTask;
            });
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        Assert.True((await engine.StartAsync(descriptor.Id)).IsSuccess);

        var stop = await engine.StopAsync(descriptor.Id);

        Assert.True(stop.IsSuccess);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        var warning = logger.Find(LogLevel.Warning, "cancellation callbacks");
        Assert.NotNull(warning);
        Assert.Equal(typeof(HostileException).FullName, warning.Diagnostic!.ExceptionType);
    }

    [Fact]
    public async Task RollbackShutdownFailure_AfterInitializationFailure_StillReleasesFactory()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger);
        var descriptor = TestDescriptors.Create("com.test.rollback-failure");
        var plugin = new TestPlugin(
            onInitialize: (_, _) => throw new InvalidOperationException("init failure"),
            onShutdown: _ => throw new InvalidOperationException("rollback failure"));
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var start = await engine.StartAsync(descriptor.Id);

        Assert.Equal(PluginErrorCodes.InitializationFailed, start.ErrorCode);
        Assert.Contains("init failure", start.Message, StringComparison.Ordinal);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
        Assert.NotNull(logger.Find(LogLevel.Warning, "Rollback shutdown failed"));
    }

    [Fact]
    public async Task DisposeAsync_CancelsInitializationInProgress()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.dispose-during-init");
        var initializationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new TestPlugin(
            onInitialize: async (_, token) =>
            {
                initializationEntered.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            });
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);

        var startTask = engine.StartAsync(descriptor.Id);
        await initializationEntered.Task.WaitAsync(Timeout);

        await engine.DisposeAsync().AsTask().WaitAsync(Timeout);
        var start = await startTask.WaitAsync(Timeout);

        Assert.Equal(PluginOperationStatus.Canceled, start.Status);
        Assert.Equal(PluginErrorCodes.OperationCanceled, start.ErrorCode);
        Assert.Contains("shutting down", start.Message, StringComparison.Ordinal);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotCancelRunningPluginsBeforeTheirOrderedStop()
    {
        var engine = new PluginEngine();
        var first = TestDescriptors.Create("com.test.ordered-first");
        var second = TestDescriptors.Create("com.test.ordered-second");
        bool? firstTokenCanceledWhenSecondStopped = null;
        var firstPlugin = new TestPlugin();
        var secondPlugin = new TestPlugin(
            onShutdown: _ =>
            {
                firstTokenCanceledWhenSecondStopped = firstPlugin.Context!.StoppingToken.IsCancellationRequested;
                return Task.CompletedTask;
            });
        engine.Register(first, new TestPluginFactory(firstPlugin));
        engine.Register(second, new TestPluginFactory(secondPlugin));
        engine.MarkValidated(first.Id);
        engine.MarkValidated(second.Id);
        await engine.StartAllAsync();

        await engine.DisposeAsync().AsTask().WaitAsync(Timeout);

        // Reverse order: the second plugin stops first while the first is still running.
        Assert.False(firstTokenCanceledWhenSecondStopped);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(first.Id)!.State);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(second.Id)!.State);
    }

    [Fact]
    public async Task CancellationCallback_ReadingEngineFromAnotherThread_DoesNotDeadlock()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.callback-cross-thread");
        PluginState? observed = null;
        var plugin = new TestPlugin(
            onInitialize: (context, _) =>
            {
                context.StoppingToken.Register(() =>
                {
                    // Blocking on another thread that needs the slot state lock would
                    // deadlock if Cancel() ran while that lock was held.
                    var read = Task.Run(() => engine.GetPlugin(descriptor.Id)!.State);
                    observed = read.Wait(Timeout) ? read.Result : null;
                });
                return Task.CompletedTask;
            });
        engine.Register(descriptor, new TestPluginFactory(plugin));
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var stop = await engine.StopAsync(descriptor.Id).WaitAsync(Timeout * 2);

        Assert.True(stop.IsSuccess);
        Assert.Equal(PluginState.Stopping, observed);
    }

    [Fact]
    public async Task CancellationCallback_RequestingStopReentrantly_DoesNotDeadlock()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.callback-reentrant-stop");
        Task<PluginOperationResult>? reentrantStop = null;
        var plugin = new TestPlugin(
            onInitialize: (context, _) =>
            {
                context.StoppingToken.Register(() => reentrantStop = engine.StopAsync(descriptor.Id));
                return Task.CompletedTask;
            });
        engine.Register(descriptor, new TestPluginFactory(plugin));
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var stop = await engine.StopAsync(descriptor.Id).WaitAsync(Timeout);

        Assert.True(stop.IsSuccess);
        Assert.NotNull(reentrantStop);
        var second = await reentrantStop.WaitAsync(Timeout);
        Assert.Equal(PluginOperationStatus.Rejected, second.Status);
        Assert.Equal(PluginErrorCodes.InvalidState, second.ErrorCode);
        Assert.Equal(1, plugin.ShutdownCount);
    }

    [Fact]
    public async Task ConcurrentStartStopAndDispose_AlwaysReachConsistentTerminalStates()
    {
        const int Iterations = 20;
        const int PluginCount = 8;

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var engine = new PluginEngine();
            var plugins = new TestPlugin[PluginCount];
            var factories = new TestPluginFactory[PluginCount];
            var descriptors = new PluginDescriptor[PluginCount];

            for (var index = 0; index < PluginCount; index++)
            {
                descriptors[index] = TestDescriptors.Create($"com.test.stress-{iteration}-{index}");
                plugins[index] = new TestPlugin(
                    onInitialize: async (_, token) => await Task.Delay(Random.Shared.Next(0, 3), token),
                    onShutdown: async _ => await Task.Yield());
                factories[index] = new TestPluginFactory(plugins[index]);
                engine.Register(descriptors[index], factories[index]);
                engine.MarkValidated(descriptors[index].Id);
            }

            var operations = new List<Task>();
            foreach (var descriptor in descriptors)
            {
                operations.Add(Task.Run(() => engine.StartAsync(descriptor.Id)));
                operations.Add(Task.Run(async () =>
                {
                    await Task.Delay(Random.Shared.Next(0, 3));
                    await engine.StopAsync(descriptor.Id);
                }));
            }

            operations.Add(Task.Run(async () =>
            {
                await Task.Delay(Random.Shared.Next(0, 4));
                await engine.DisposeAsync();
            }));

            await Task.WhenAll(operations).WaitAsync(Timeout);
            await engine.DisposeAsync().AsTask().WaitAsync(Timeout);

            for (var index = 0; index < PluginCount; index++)
            {
                var state = engine.GetPlugin(descriptors[index].Id)!.State;
                Assert.True(
                    state is PluginState.Validated or PluginState.Stopped or PluginState.Failed,
                    $"Iteration {iteration}, plugin {index}: unexpected state {state}.");
                Assert.Equal(1, factories[index].ReleaseCount);
                Assert.Equal(plugins[index].InitializeCount, plugins[index].ShutdownCount);
                Assert.True(plugins[index].InitializeCount <= 1);
            }
        }
    }
}
