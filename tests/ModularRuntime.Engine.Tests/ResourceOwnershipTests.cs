using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

public sealed class ResourceOwnershipTests
{
    private const string PluginIdText = "com.test.owner";
    private static readonly PluginId Id = PluginId.Parse(PluginIdText);

    private sealed class CountingDisposable(List<string>? order = null, string name = "", bool fail = false) : IAsyncDisposable
    {
        private int _count;

        public int DisposeCount => Volatile.Read(ref _count);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _count);
            lock (order ?? [])
            {
                order?.Add(name);
            }

            return fail ? ValueTask.FromException(new InvalidOperationException($"dispose of {name} failed")) : ValueTask.CompletedTask;
        }
    }

    private static Task RegisterEverything(IPluginContext c, CountingDisposable tracked)
    {
        c.Events.Subscribe("test.a", (_, _) => ValueTask.CompletedTask);
        c.Events.Subscribe("test.b", (_, _) => ValueTask.CompletedTask);
        c.Services.Register(Service("test.service"), Echo);
        c.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.plugin-point", "Plugin point", new Version(1, 0)));
        c.Extensions.Contribute(new ExtensionContributionDefinition("own", "test.plugin-point", new Version(1, 0)), Echo);
        c.Extensions.Contribute(new ExtensionContributionDefinition("to-host", "test.host-point", new Version(1, 0)), Echo);
        c.Resources.Track("tracked", tracked);
        return Task.CompletedTask;
    }

    private static PluginEngine EngineWithHostPoint(IRuntimeLogger? logger = null, RuntimeLimits? limits = null)
    {
        var engine = new PluginEngine(logger, limits);
        engine.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.host-point", "Host point", new Version(1, 0)));
        return engine;
    }

    private static void AssertNoPluginResources(PluginEngine engine)
    {
        Assert.Empty(engine.GetResources(Id));
        Assert.Empty(engine.Host.Events.GetSubscriptions());
        Assert.Empty(engine.Host.Services.GetServices());
        Assert.Null(engine.Host.Extensions.GetExtensionPoint("test.plugin-point"));
        Assert.Empty(engine.Host.Extensions.GetContributions("test.host-point"));
    }

    [Fact]
    public async Task Stop_ReleasesEveryResourceKind()
    {
        await using var engine = EngineWithHostPoint();
        var tracked = new CountingDisposable();
        await StartPluginAsync(engine, PluginIdText, c => RegisterEverything(c, tracked));

        var resources = engine.GetResources(Id);
        Assert.Equal(7, resources.Count);
        Assert.Contains(resources, r => r.Kind == ResourceKind.EventSubscription);
        Assert.Contains(resources, r => r.Kind == ResourceKind.Service);
        Assert.Contains(resources, r => r.Kind == ResourceKind.ExtensionPoint);
        Assert.Contains(resources, r => r.Kind == ResourceKind.ExtensionContribution);
        Assert.Contains(resources, r => r.Kind == ResourceKind.Custom);
        Assert.All(resources, r => Assert.Equal(engine.RuntimeId, r.Owner.RuntimeId));

        Assert.True((await engine.StopAsync(Id)).IsSuccess);

        AssertNoPluginResources(engine);
        Assert.Equal(1, tracked.DisposeCount);
        Assert.NotNull(engine.Host.Extensions.GetExtensionPoint("test.host-point"));
    }

    [Fact]
    public async Task FailedStart_ReleasesResourcesCreatedBeforeTheFailure()
    {
        await using var engine = EngineWithHostPoint();
        var tracked = new CountingDisposable();

        var (plugin, result) = await TryStartPluginAsync(engine, PluginIdText, async c =>
        {
            await RegisterEverything(c, tracked);
            throw new InvalidOperationException("initialization fails after creating resources");
        });

        Assert.Equal(PluginErrorCodes.InitializationFailed, result.ErrorCode);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(Id)!.State);
        Assert.Equal(1, plugin.ShutdownCount);
        AssertNoPluginResources(engine);
        Assert.Equal(1, tracked.DisposeCount);
    }

    [Fact]
    public async Task FailedShutdown_StillReleasesResources()
    {
        await using var engine = EngineWithHostPoint();
        var tracked = new CountingDisposable();
        await StartPluginAsync(engine, PluginIdText, c => RegisterEverything(c, tracked), _ => throw new InvalidOperationException("shutdown failed"));

        var result = await engine.StopAsync(Id);

        Assert.Equal(PluginErrorCodes.ShutdownFailed, result.ErrorCode);
        AssertNoPluginResources(engine);
        Assert.Equal(1, tracked.DisposeCount);
    }

    [Fact]
    public async Task TrackedResources_AreReleasedInReverseOrder_AndFailuresAreReported()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger);
        var order = new List<string>();
        await StartPluginAsync(engine, PluginIdText, c =>
        {
            c.Resources.Track("first", new CountingDisposable(order, "first"));
            c.Resources.Track("failing", new CountingDisposable(order, "failing", fail: true));
            c.Resources.Track("last", new CountingDisposable(order, "last"));
            return Task.CompletedTask;
        });

        var result = await engine.StopAsync(Id);

        Assert.Equal(["last", "failing", "first"], order);
        Assert.Equal(PluginOperationStatus.Failed, result.Status);
        Assert.Equal(PluginErrorCodes.ResourceCleanupFailed, result.ErrorCode);
        Assert.Equal(PluginState.Failed, engine.GetPlugin(Id)!.State);
        Assert.NotNull(logger.Find(LogLevel.Warning, PluginErrorCodes.ResourceCleanupFailed));
    }

    [Fact]
    public async Task ExplicitlyDisposedResource_IsNotDisposedAgainAtStop()
    {
        await using var engine = new PluginEngine();
        var tracked = new CountingDisposable();
        IPluginResource? handle = null;
        await StartPluginAsync(engine, PluginIdText, c => { handle = c.Resources.Track("tracked", tracked); return Task.CompletedTask; });

        await handle!.DisposeAsync();
        await handle.DisposeAsync();
        await engine.StopAsync(Id);

        Assert.Equal(1, tracked.DisposeCount);
        Assert.False(handle.IsActive);
    }

    [Fact]
    public async Task ConcurrentDisposeCalls_ReleaseResourcesExactlyOnce()
    {
        var engine = EngineWithHostPoint();
        var tracked = new CountingDisposable();
        var hostTracked = new CountingDisposable();
        engine.Host.Resources.Track("host-tracked", hostTracked);
        await StartPluginAsync(engine, PluginIdText, c => RegisterEverything(c, tracked));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await engine.DisposeAsync()))).WaitAsync(TestTimeout);

        Assert.Equal(1, tracked.DisposeCount);
        Assert.Equal(1, hostTracked.DisposeCount);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(Id)!.State);
        Assert.Empty(engine.Host.Resources.GetResources());
        Assert.Empty(engine.Host.Extensions.GetExtensionPoints());
    }

    [Fact]
    public async Task RetainedContext_CannotCreateResourcesAfterStop()
    {
        await using var engine = new PluginEngine();
        IPluginContext? context = null;
        await StartPluginAsync(engine, PluginIdText, c => { context = c; return Task.CompletedTask; });
        await engine.StopAsync(Id);

        Assert.False(context!.Resources.IsOpen);
        AssertRuntimeError(PluginErrorCodes.ScopeClosed, () => context.Events.Subscribe("test.late", (_, _) => ValueTask.CompletedTask));
        AssertRuntimeError(PluginErrorCodes.ScopeClosed, () => context.Services.Register(Service("test.late"), Echo));
        AssertRuntimeError(PluginErrorCodes.ScopeClosed, () => context.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.late", "Late", new Version(1, 0))));
        AssertRuntimeError(PluginErrorCodes.ScopeClosed, () => context.Resources.Track("late", new CountingDisposable()));
        Assert.Equal(PluginErrorCodes.ScopeClosed, (await context.Services.InvokeAsync("test.any", "echo")).ErrorCode);
        Assert.Empty(engine.Host.Events.GetSubscriptions());
    }

    [Fact]
    public async Task RegistrationDuringShutdown_IsRejected()
    {
        await using var engine = new PluginEngine();
        IPluginContext? context = null;
        PluginRuntimeException? error = null;
        await StartPluginAsync(
            engine,
            PluginIdText,
            c => { context = c; return Task.CompletedTask; },
            _ =>
            {
                error = Assert.Throws<PluginRuntimeException>(() => context!.Services.Register(Service("test.during-shutdown"), Echo));
                return Task.CompletedTask;
            });

        await engine.StopAsync(Id);

        Assert.Equal(PluginErrorCodes.ScopeClosed, error!.ErrorCode);
        Assert.Empty(engine.Host.Services.GetServices());
    }

    [Fact]
    public async Task StopFromThePluginsOwnHandler_IsRejectedInsteadOfDeadlocking()
    {
        await using var engine = new PluginEngine();
        PluginOperationResult? reentrant = null;
        engine.Host.Events.Subscribe("test.please-stop", async (e, _) =>
        {
            // Host handler invoked from the plugin's service handler: stopping the plugin here
            // would wait for the very handler chain that is calling it.
            reentrant = await engine.StopAsync(Id, CancellationToken.None);
        });
        await StartPluginAsync(engine, PluginIdText, c =>
        {
            c.Services.Register(Service("test.stopper"), async (request, ct) =>
            {
                await c.Events.PublishAsync("test.please-stop", request.Payload, cancellationToken: ct);
                return PluginResponse.Success();
            });
            return Task.CompletedTask;
        });

        var invocation = await engine.Host.Services.InvokeAsync("test.stopper", "echo").AsTask().WaitAsync(TestTimeout);

        Assert.True(invocation.IsSuccess);
        Assert.Equal(PluginErrorCodes.ReentrantLifecycleOperation, reentrant!.ErrorCode);
        Assert.Equal(PluginState.Running, engine.GetPlugin(Id)!.State);
        Assert.True((await engine.StopAsync(Id).WaitAsync(TestTimeout)).IsSuccess);
    }

    [Fact]
    public async Task StopFromInsideInitialization_IsRejectedInsteadOfDeadlocking()
    {
        await using var engine = new PluginEngine();
        PluginOperationResult? reentrant = null;
        engine.Host.Events.Subscribe("test.init-stop", async (_, _) => reentrant = await engine.StopAsync(Id, CancellationToken.None));

        await StartPluginAsync(engine, PluginIdText, async c => await c.Events.PublishAsync("test.init-stop", Json("{}")));

        Assert.Equal(PluginErrorCodes.ReentrantLifecycleOperation, reentrant!.ErrorCode);
        Assert.Equal(PluginState.Running, engine.GetPlugin(Id)!.State);
    }

    [Fact]
    public async Task StoppingAnotherPluginFromAHandler_IsAllowed()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.victim");
        PluginOperationResult? stop = null;
        await StartPluginAsync(engine, PluginIdText, c =>
        {
            c.Services.Register(Service("test.stop-other"), Echo);
            return Task.CompletedTask;
        });
        engine.Host.Events.Subscribe("test.stop-victim", async (_, _) => stop = await engine.StopAsync(PluginId.Parse("com.test.victim"), CancellationToken.None));

        await engine.Host.Events.PublishAsync("test.stop-victim", Json("{}")).AsTask().WaitAsync(TestTimeout);

        Assert.True(stop!.IsSuccess);
    }

    [Fact]
    public async Task DisposeFromInsideAHandler_IsRejected()
    {
        await using var engine = new PluginEngine();
        PluginRuntimeException? error = null;
        engine.Host.Events.Subscribe("test.dispose", async (_, _) =>
        {
            error = await Assert.ThrowsAsync<PluginRuntimeException>(async () => await engine.DisposeAsync());
        });

        await engine.Host.Events.PublishAsync("test.dispose", Json("{}")).AsTask().WaitAsync(TestTimeout);

        Assert.Equal(PluginErrorCodes.ReentrantLifecycleOperation, error!.ErrorCode);
        Assert.False(engine.IsShuttingDown);
    }

    [Fact]
    public async Task HandlerIgnoringCancellation_IsBoundedByTheDrainTimeout()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger, new RuntimeLimits { DrainTimeout = TimeSpan.FromMilliseconds(100) });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartPluginAsync(engine, PluginIdText, c =>
        {
            c.Services.Register(Service("test.stubborn"), async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task; // ignores its cancellation token
                return PluginResponse.Success();
            });
            return Task.CompletedTask;
        });

        var invocation = engine.Host.Services.InvokeAsync("test.stubborn", "echo").AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        var stop = await engine.StopAsync(Id).WaitAsync(TestTimeout);

        Assert.True(stop.IsSuccess);
        Assert.NotNull(logger.Find(LogLevel.Warning, PluginErrorCodes.DrainTimeout));
        Assert.Empty(engine.Host.Services.GetServices());

        release.TrySetResult();
        Assert.True((await invocation.WaitAsync(TestTimeout)).IsSuccess);
    }

    [Fact]
    public async Task CancellationCallbacksOfInFlightHandlers_DoNotRunUnderTheScopeLock()
    {
        await using var engine = new PluginEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? queriedFromOtherThread = null;
        await StartPluginAsync(engine, PluginIdText, c =>
        {
            c.Services.Register(Service("test.callback"), async (_, ct) =>
            {
                ct.Register(() =>
                {
                    // Blocks on another thread that needs the owner scope's lock. This deadlocks
                    // if the scope token is canceled while that lock is held.
                    var query = Task.Run(() => engine.GetResources(Id).Count);
                    queriedFromOtherThread = query.Wait(TestTimeout);
                });
                entered.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
                return PluginResponse.Success();
            });
            return Task.CompletedTask;
        });

        var invocation = engine.Host.Services.InvokeAsync("test.callback", "echo").AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        var stop = await engine.StopAsync(Id).WaitAsync(TestTimeout * 2);
        await invocation.WaitAsync(TestTimeout);

        Assert.True(stop.IsSuccess);
        Assert.True(queriedFromOtherThread);
    }

    [Fact]
    public async Task Handlers_CanUseEveryRegistryFromAnotherThreadSynchronously()
    {
        await using var engine = new PluginEngine();
        var observations = new List<bool>();

        bool QueryEverythingFromAnotherThread()
        {
            var query = Task.Run(() =>
                engine.Host.Events.GetSubscriptions().Count +
                engine.Host.Services.GetServices().Count +
                engine.Host.Extensions.GetContributions("test.point").Count +
                engine.Host.Resources.GetResources().Count +
                engine.GetResources(Id).Count);
            return query.Wait(TestTimeout);
        }

        engine.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.point", "Point", new Version(1, 0)));
        await StartPluginAsync(engine, PluginIdText, c =>
        {
            c.Events.Subscribe("test.sync", (_, _) => { observations.Add(QueryEverythingFromAnotherThread()); return ValueTask.CompletedTask; });
            c.Services.Register(Service("test.sync"), (r, ct) => { observations.Add(QueryEverythingFromAnotherThread()); return Echo(r, ct); });
            c.Extensions.Contribute(new ExtensionContributionDefinition("sync", "test.point", new Version(1, 0)), (r, ct) => { observations.Add(QueryEverythingFromAnotherThread()); return Echo(r, ct); });
            return Task.CompletedTask;
        });

        await engine.Host.Events.PublishAsync("test.sync", Json("{}")).AsTask().WaitAsync(TestTimeout * 2);
        await engine.Host.Services.InvokeAsync("test.sync", "echo").AsTask().WaitAsync(TestTimeout * 2);
        await engine.Host.Extensions.InvokeAllAsync("test.point", "run").AsTask().WaitAsync(TestTimeout * 2);

        Assert.Equal([true, true, true], observations);
    }

    [Fact]
    public async Task HostResources_AreReleasedWhenTheEngineIsDisposed_AfterPlugins()
    {
        var engine = new PluginEngine();
        var hostSawPluginGoodbye = false;
        var hostTracked = new CountingDisposable();
        engine.Host.Events.Subscribe("test.goodbye", (_, _) => { hostSawPluginGoodbye = true; return ValueTask.CompletedTask; });
        engine.Host.Services.Register(Service("test.host-service"), Echo);
        engine.Host.Resources.Track("host", hostTracked);
        IPluginContext? context = null;
        await StartPluginAsync(
            engine,
            PluginIdText,
            c => { context = c; return Task.CompletedTask; },
            async _ => await context!.Events.PublishAsync("test.goodbye", Json("{}"), cancellationToken: CancellationToken.None));

        await engine.DisposeAsync();

        Assert.True(hostSawPluginGoodbye, "Host subscriptions must stay active while plugins shut down.");
        Assert.Equal(1, hostTracked.DisposeCount);
        Assert.Empty(engine.Host.Services.GetServices());
        Assert.Empty(engine.Host.Resources.GetResources());
    }

    [Fact]
    public async Task StartAll_UsesStartOrderAndStopAllReversesIt()
    {
        await using var engine = new PluginEngine();
        var order = new List<string>();
        foreach (var (id, startOrder) in new[] { ("com.test.c", 30), ("com.test.a", 10), ("com.test.b", 20), ("com.test.d", 10) })
        {
            var descriptor = Create(id, startOrder);
            var plugin = new TestPlugin(
                (_, _) => { order.Add("start:" + id); return Task.CompletedTask; },
                _ => { order.Add("stop:" + id); return Task.CompletedTask; });
            engine.Register(descriptor, new TestPluginFactory(plugin));
            engine.MarkValidated(descriptor.Id);
        }

        await engine.StartAllAsync();
        await engine.StopAllAsync();

        Assert.Equal(
            [
                "start:com.test.a", "start:com.test.d", "start:com.test.b", "start:com.test.c",
                "stop:com.test.c", "stop:com.test.b", "stop:com.test.d", "stop:com.test.a",
            ],
            order);
    }
}
