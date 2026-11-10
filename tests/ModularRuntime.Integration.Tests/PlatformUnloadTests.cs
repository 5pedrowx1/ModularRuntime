using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>
/// v0.2 platform scenarios against real plugins loaded by the real loader into collectible
/// load contexts. Each test keeps the runtime alive, stops the plugin, keeps everything the host
/// received (results, descriptors, diagnostics, logs, handles), and requires the plugin's load
/// context to be collected.
/// </summary>
public sealed class PlatformUnloadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const string Scenarios = "ModularRuntime.TestPlugin.";

    private static readonly PluginId EventPlugin = PluginId.Parse("com.test.alc.events");
    private static readonly PluginId ProviderPlugin = PluginId.Parse("com.test.alc.provider");
    private static readonly PluginId ConsumerPlugin = PluginId.Parse("com.test.alc.consumer");
    private static readonly PluginId ContributorPlugin = PluginId.Parse("com.test.alc.contributor");
    private static readonly PluginId FailurePlugin = PluginId.Parse("com.test.alc.failure");

    // Scenario A — Event Bus: the bus must not keep the plugin's handler delegate.
    [Fact]
    public async Task A_EventHandler_IsReleasedAndLoadContextCollectedAfterStop()
    {
        using var temporary = new TemporaryDirectory("v02-a");
        await using var harness = await ScenarioHarness.CreateAsync(temporary, null, (EventPlugin, "EventHandlerScenarioPlugin", 0));
        Assert.True((await harness.Engine.StartAsync(EventPlugin)).IsSuccess);

        var delivered = await harness.Engine.Host.Events.PublishAsync("test.alc.event", JsonPayload.Parse("{}"));
        var failed = await harness.Engine.Host.Events.PublishAsync("test.alc.event", JsonPayload.Parse("""{"mode":"fail"}"""));
        var subscriptions = harness.Engine.Host.Events.GetSubscriptions();
        Assert.True((await harness.Engine.StopAsync(EventPlugin)).IsSuccess);

        Assert.Equal(1, delivered.SucceededCount);
        Assert.Equal("ModularRuntime.TestPlugin.PluginFailureException", Assert.Single(failed.Deliveries).Diagnostic!.ExceptionType);
        Assert.Single(subscriptions);
        Assert.Empty(harness.Engine.Host.Events.GetSubscriptions());
        harness.AssertCollected(EventPlugin);
        GC.KeepAlive(delivered);
        GC.KeepAlive(failed);
        GC.KeepAlive(subscriptions);
    }

    // Scenario B — Service Registry: a consumer keeps a service handle after the provider stops.
    [Fact]
    public async Task B_RetainedServiceHandles_DoNotKeepTheProviderAlive()
    {
        using var temporary = new TemporaryDirectory("v02-b");
        await using var harness = await ScenarioHarness.CreateAsync(
            temporary,
            null,
            (ProviderPlugin, "ServiceProviderScenarioPlugin", 10),
            (ConsumerPlugin, "ServiceConsumerScenarioPlugin", 20));
        await harness.Engine.StartAllAsync();
        var hostClient = harness.Engine.Host.Services.GetClient("test.alc.service")!;
        var descriptor = hostClient.GetDescriptor();
        var before = await harness.Engine.Host.Services.InvokeAsync("test.alc.consumer", "call", JsonPayload.Parse("{}"));

        Assert.True((await harness.Engine.StopAsync(ProviderPlugin)).IsSuccess);
        var after = await harness.Engine.Host.Services.InvokeAsync("test.alc.consumer", "call", JsonPayload.Parse("{}"));
        var hostAfter = await hostClient.InvokeAsync("echo");

        Assert.Equal("Succeeded", before.Payload!.GetString("status"));
        Assert.Equal("Rejected", after.Payload!.GetString("status"));
        Assert.Equal(PluginErrorCodes.ServiceRemoved, after.Payload!.GetString("errorCode"));
        Assert.Equal(PluginErrorCodes.ServiceRemoved, hostAfter.ErrorCode);
        Assert.False(hostClient.IsAvailable);
        Assert.Equal(PluginState.Running, harness.Engine.GetPlugin(ConsumerPlugin)!.State);

        harness.AssertCollected(ProviderPlugin);
        Assert.True(harness.ContextOf(ConsumerPlugin).IsAlive, "Sanity check: the running consumer's context must stay alive.");
        GC.KeepAlive(hostClient);
        GC.KeepAlive(descriptor);
    }

    // Scenario C — Extension Points: the host keeps contribution snapshots and results.
    [Fact]
    public async Task C_RetainedContributionSnapshots_DoNotKeepTheContributorAlive()
    {
        using var temporary = new TemporaryDirectory("v02-c");
        await using var harness = await ScenarioHarness.CreateAsync(temporary, null, (ContributorPlugin, "ExtensionContributorScenarioPlugin", 0));
        harness.Engine.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.alc.point", "ALC point", new Version(1, 0)));
        Assert.True((await harness.Engine.StartAsync(ContributorPlugin)).IsSuccess);

        var contributions = harness.Engine.Host.Extensions.GetContributions("test.alc.point");
        var invocation = await harness.Engine.Host.Extensions.InvokeAllAsync("test.alc.point", "run");
        var failure = await harness.Engine.Host.Extensions.InvokeAsync("test.alc.point", "alc", "fail");
        Assert.True((await harness.Engine.StopAsync(ContributorPlugin)).IsSuccess);

        Assert.Equal("test", Assert.Single(contributions).Metadata["kind"]);
        Assert.True(invocation.IsSuccess);
        Assert.Equal("ModularRuntime.TestPlugin.PluginFailureException", failure.Diagnostic!.ExceptionType);
        Assert.Empty(harness.Engine.Host.Extensions.GetContributions("test.alc.point"));
        Assert.NotNull(harness.Engine.Host.Extensions.GetExtensionPoint("test.alc.point"));
        harness.AssertCollected(ContributorPlugin);
        GC.KeepAlive(contributions);
        GC.KeepAlive(invocation);
        GC.KeepAlive(failure);
    }

    // Scenario D — Failed initialization after creating every kind of resource.
    [Fact]
    public async Task D_FailedInitialization_ReleasesAllResourcesAndTheLoadContext()
    {
        using var temporary = new TemporaryDirectory("v02-d");
        await using var harness = await ScenarioHarness.CreateAsync(temporary, null, (FailurePlugin, "ResourceFailureScenarioPlugin", 0));
        harness.Engine.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.alc.point", "ALC point", new Version(1, 0)));

        var result = await harness.Engine.StartAsync(FailurePlugin);

        Assert.Equal(PluginErrorCodes.InitializationFailed, result.ErrorCode);
        Assert.Equal("ModularRuntime.TestPlugin.PluginFailureException", result.Diagnostic!.ExceptionType);
        Assert.Empty(harness.Engine.Host.Events.GetSubscriptions());
        Assert.Empty(harness.Engine.Host.Services.GetServices());
        Assert.Null(harness.Engine.Host.Extensions.GetExtensionPoint("test.alc.plugin-point"));
        Assert.Empty(harness.Engine.Host.Extensions.GetContributions("test.alc.point"));
        Assert.Empty(harness.Engine.GetResources(FailurePlugin));
        Assert.True(harness.Logger.HasMessageContaining("Released 5 resource(s)"));
        harness.AssertCollected(FailurePlugin);
        GC.KeepAlive(result);
    }

    // Scenario E — A cooperative handler is still running when the plugin is stopped.
    [Fact]
    public async Task E_InFlightHandler_IsCanceledAndDrainedBeforeTheContextIsReleased()
    {
        using var temporary = new TemporaryDirectory("v02-e");
        await using var harness = await ScenarioHarness.CreateAsync(temporary, null, (ProviderPlugin, "ServiceProviderScenarioPlugin", 0));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.Host.Services.Register(
            new PluginServiceDefinition("test.alc.signal", "Signal", new Version(1, 0), ["entered"]),
            (_, _) => { entered.TrySetResult(); return ValueTask.FromResult(PluginResponse.Success()); });
        Assert.True((await harness.Engine.StartAsync(ProviderPlugin)).IsSuccess);

        var invocation = harness.Engine.Host.Services.InvokeAsync("test.alc.service", "wait").AsTask();
        await entered.Task.WaitAsync(Timeout);
        var stop = await harness.Engine.StopAsync(ProviderPlugin).WaitAsync(Timeout);
        var result = await invocation.WaitAsync(Timeout);

        Assert.True(stop.IsSuccess);
        Assert.Equal(PluginOperationStatus.Canceled, result.Status);
        var messages = harness.Logger.Entries.Select(e => e.Message).ToList();
        var exited = messages.FindIndex(m => m.Contains("wait handler exited", StringComparison.Ordinal));
        var shutdown = messages.FindIndex(m => m.Contains("ServiceProviderScenarioPlugin shutdown", StringComparison.Ordinal));
        var unload = messages.FindIndex(m => m.Contains("Unload requested", StringComparison.Ordinal));
        Assert.True(exited >= 0 && exited < shutdown && shutdown < unload, "Handler drain, then ShutdownAsync, then unload.");
        harness.AssertCollected(ProviderPlugin);
        GC.KeepAlive(result);
    }

    // Scenario E (non-cooperative) — a handler ignores cancellation: teardown continues after the
    // drain timeout, the context stays alive while the handler runs, and becomes collectible after.
    [Fact]
    public async Task E_NonCooperativeHandler_KeepsTheContextAliveOnlyUntilItFinishes()
    {
        using var temporary = new TemporaryDirectory("v02-e2");
        await using var harness = await ScenarioHarness.CreateAsync(
            temporary,
            new RuntimeLimits { DrainTimeout = TimeSpan.FromMilliseconds(200) },
            (ProviderPlugin, "ServiceProviderScenarioPlugin", 0));
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.Host.Services.Register(
            new PluginServiceDefinition("test.alc.gate", "Gate", new Version(1, 0), ["wait"]),
            async (_, _) =>
            {
                gateEntered.TrySetResult();
                await gate.Task;
                return PluginResponse.Success();
            });
        Assert.True((await harness.Engine.StartAsync(ProviderPlugin)).IsSuccess);

        var invocation = harness.Engine.Host.Services.InvokeAsync("test.alc.service", "stubborn").AsTask();
        await gateEntered.Task.WaitAsync(Timeout);
        var stop = await harness.Engine.StopAsync(ProviderPlugin).WaitAsync(Timeout);

        Assert.True(stop.IsSuccess);
        Assert.True(harness.Logger.HasMessageContaining(PluginErrorCodes.DrainTimeout));
        Assert.DoesNotContain(harness.Engine.Host.Services.GetServices(), s => s.ServiceId == "test.alc.service");
        Assert.False(
            GcDiagnostics.WaitForCollection(harness.ContextOf(ProviderPlugin), attempts: 3),
            "The context cannot be collected while plugin code is still running.");

        gate.TrySetResult();
        var result = await invocation.WaitAsync(Timeout);

        Assert.True(result.IsSuccess);
        harness.AssertCollected(ProviderPlugin);
    }

    // Scenario E (asynchronous failure) — a handler faults after completing asynchronously; the
    // faulted task (carrying a plugin exception) must not be retained by the invocation engine.
    [Fact]
    public async Task E_AsynchronouslyFailingHandler_DoesNotRetainTheFaultedTask()
    {
        using var temporary = new TemporaryDirectory("v02-e3");
        await using var harness = await ScenarioHarness.CreateAsync(temporary, null, (ProviderPlugin, "ServiceProviderScenarioPlugin", 0));
        Assert.True((await harness.Engine.StartAsync(ProviderPlugin)).IsSuccess);

        // Run without xUnit's SynchronizationContext, like a console or service host: there,
        // continuations run inline on the completing thread, which is where retention shows up.
        var results = await Task.Run(async () =>
        {
            var collected = new List<PluginInvocationResult>();
            for (var i = 0; i < 3; i++)
            {
                collected.Add(await harness.Engine.Host.Services.InvokeAsync("test.alc.service", "fail-async"));
            }

            Assert.True((await harness.Engine.StopAsync(ProviderPlugin)).IsSuccess);
            Assert.All(collected, r => Assert.Equal("ModularRuntime.TestPlugin.PluginFailureException", r.Diagnostic!.ExceptionType));
            harness.AssertCollected(ProviderPlugin);
            return collected;
        });

        GC.KeepAlive(results);
    }

    // v0.2.1 — A release started by the plugin (fire-and-forget from ShutdownAsync) runs
    // concurrently with the teardown: unload must not be requested before it completes.
    [Fact]
    public async Task ConcurrentResourceRelease_DelaysUnloadUntilCleanupCompletes()
    {
        using var temporary = new TemporaryDirectory("v021-release");
        var slowPlugin = PluginId.Parse("com.test.alc.slow-cleanup");
        await using var harness = await ScenarioHarness.CreateAsync(temporary, null, (slowPlugin, "SlowCleanupScenarioPlugin", 0));
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.Host.Services.Register(
            new PluginServiceDefinition("test.alc.gate", "Gate", new Version(1, 0), ["wait"]),
            async (_, _) =>
            {
                gateEntered.TrySetResult();
                await gate.Task;
                return PluginResponse.Success();
            });
        Assert.True((await harness.Engine.StartAsync(slowPlugin)).IsSuccess);

        var stop = harness.Engine.StopAsync(slowPlugin);
        await gateEntered.Task.WaitAsync(Timeout);

        // Correct ordering makes this bounded wait elapse: the factory cannot be released while
        // the cleanup is blocked on the gate.
        await Task.WhenAny(harness.FactoryReleased(slowPlugin), Task.Delay(300));
        var releasedWhileCleanupPending = harness.FactoryReleased(slowPlugin).IsCompleted;
        gate.TrySetResult();
        var result = await stop.WaitAsync(Timeout);

        Assert.False(releasedWhileCleanupPending, "Unload was requested while plugin resource cleanup was still running.");
        Assert.True(result.IsSuccess);
        Assert.True(harness.Logger.HasMessageContaining("slow cleanup finished"));
        Assert.Empty(harness.Engine.GetResources(slowPlugin));
        harness.AssertCollected(slowPlugin);
    }

    // Scenario F — The host keeps results, metadata, diagnostics, and logs from every component.
    [Fact]
    public async Task F_RetainedResultsMetadataAndDiagnostics_DoNotKeepPluginsAlive()
    {
        using var temporary = new TemporaryDirectory("v02-f");
        await using var harness = await ScenarioHarness.CreateAsync(
            temporary,
            null,
            (EventPlugin, "EventHandlerScenarioPlugin", 0),
            (ProviderPlugin, "ServiceProviderScenarioPlugin", 0),
            (ContributorPlugin, "ExtensionContributorScenarioPlugin", 0));
        var host = harness.Engine.Host;
        host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("test.alc.point", "ALC point", new Version(1, 0)));
        await harness.Engine.StartAllAsync();

        var retained = new List<object>
        {
            await host.Events.PublishAsync("test.alc.event", JsonPayload.Parse("""{"mode":"fail"}""")),
            await host.Services.InvokeAsync("test.alc.service", "fail"),
            await host.Services.InvokeAsync("test.alc.service", "echo", JsonPayload.Parse("""{"x":1}""")),
            await host.Extensions.InvokeAllAsync("test.alc.point", "fail"),
            host.Events.GetSubscriptions(),
            host.Services.GetServices(),
            host.Extensions.GetExtensionPoints(),
            host.Extensions.GetContributions("test.alc.point"),
            harness.Engine.GetResources(EventPlugin),
            harness.Engine.GetResources(ProviderPlugin),
            harness.Engine.GetResources(ContributorPlugin),
        };
        var stops = await harness.Engine.StopAllAsync();
        retained.Add(stops);
        retained.Add(harness.Engine.GetPlugins());
        retained.Add(harness.Logger.Entries);

        Assert.All(stops, stop => Assert.True(stop.IsSuccess));
        Assert.Contains(harness.Logger.Entries, e => e.Diagnostic?.ExceptionType == "ModularRuntime.TestPlugin.PluginFailureException");
        harness.AssertCollected(EventPlugin);
        harness.AssertCollected(ProviderPlugin);
        harness.AssertCollected(ContributorPlugin);
        GC.KeepAlive(retained);
    }

    // Negative control: if the runtime retained a plugin's handler delegate, the test would see it.
    [Fact]
    public async Task NegativeControl_RetainedHandlerDelegate_IsDetected()
    {
        using var temporary = new TemporaryDirectory("v02-negative");
        await using var harness = await ScenarioHarness.CreateAsync(temporary, null, (EventPlugin, "EventHandlerScenarioPlugin", 0));
        Assert.True((await harness.Engine.StartAsync(EventPlugin)).IsSuccess);

        var leak = new List<Delegate>();
        StealSubscriptionHandler(harness.Engine, "test.alc.event", leak);
        Assert.True((await harness.Engine.StopAsync(EventPlugin)).IsSuccess);

        Assert.False(
            GcDiagnostics.WaitForCollection(harness.ContextOf(EventPlugin), attempts: 5),
            "A retained handler delegate must keep the plugin's load context alive; otherwise the unload tests prove nothing.");

        leak.Clear();
        harness.AssertCollected(EventPlugin);
    }

    /// <summary>Simulates a runtime bug: copies the plugin's handler delegate out of the bus.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StealSubscriptionHandler(PluginEngine engine, string topic, List<Delegate> leak)
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var core = typeof(PluginEngine).GetField("_core", Private)!.GetValue(engine)!;
        var bus = core.GetType().GetProperty("Events")!.GetValue(core)!;
        var byTopic = (IDictionary)bus.GetType().GetField("_byTopic", Private)!.GetValue(bus)!;
        var entry = ((Array)byTopic[topic]!).GetValue(0)!;
        leak.Add((Delegate)entry.GetType().GetProperty("Handler")!.GetValue(entry)!);
    }

    /// <summary>
    /// Stages copies of the fixture plugin with different entry types, registers them with an
    /// engine through factories that record a weak reference to each load context.
    /// </summary>
    private sealed class ScenarioHarness : IAsyncDisposable
    {
        private readonly Dictionary<PluginId, TrackingFactory> _factories;

        private ScenarioHarness(PluginEngine engine, CollectingLogger logger, Dictionary<PluginId, TrackingFactory> factories)
        {
            Engine = engine;
            Logger = logger;
            _factories = factories;
        }

        public PluginEngine Engine { get; }

        public CollectingLogger Logger { get; }

        public static async Task<ScenarioHarness> CreateAsync(
            TemporaryDirectory temporary,
            RuntimeLimits? limits,
            params (PluginId Id, string EntryType, int StartOrder)[] plugins)
        {
            var root = Path.Combine(temporary.DirectoryPath, "plugins");
            Directory.CreateDirectory(root);
            foreach (var (id, entryType, startOrder) in plugins)
            {
                var directory = StagedPlugins.CopyPlugin(root, "ModularRuntime.TestPlugin", id.Value);
                var manifest = StagedPlugins.ReadManifest(directory);
                manifest["id"] = id.Value;
                manifest["entryType"] = Scenarios + entryType;
                manifest["startOrder"] = startOrder;
                StagedPlugins.WriteManifest(directory, manifest);
            }

            var logger = new CollectingLogger();
            var loader = new PluginLoader(logger);
            var discovery = await loader.DiscoverAsync(root);
            Assert.Empty(discovery.Errors);
            var engine = new PluginEngine(logger, limits);
            var factories = new Dictionary<PluginId, TrackingFactory>();
            foreach (var candidate in discovery.Plugins)
            {
                var factory = new TrackingFactory(loader.CreateInstanceFactory(candidate));
                factories.Add(candidate.Descriptor.Id, factory);
                Assert.True(engine.Register(candidate.Descriptor, factory).IsSuccess);
                Assert.True(engine.MarkValidated(candidate.Descriptor.Id).IsSuccess);
            }

            return new ScenarioHarness(engine, logger, factories);
        }

        public WeakReference ContextOf(PluginId id) =>
            _factories[id].LoadContextReference ?? throw new InvalidOperationException($"Plugin '{id}' was never loaded.");

        public void AssertCollected(PluginId id) =>
            Assert.True(
                GcDiagnostics.WaitForCollection(ContextOf(id)),
                $"The load context of '{id}' should be collectible after it stopped, while the runtime and the host's retained data stay alive.");

        public Task FactoryReleased(PluginId id) => _factories[id].Released;

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private sealed class TrackingFactory(AssemblyPluginInstanceFactory inner) : IPluginInstanceFactory
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WeakReference? LoadContextReference { get; private set; }

        /// <summary>Completes when the engine releases the factory (that is, requests unload).</summary>
        public Task Released => _released.Task;

        public async ValueTask<IPlugin> CreateAsync(PluginDescriptor descriptor, CancellationToken cancellationToken)
        {
            var instance = await inner.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
            LoadContextReference = inner.CreateLoadContextWeakReference();
            return instance;
        }

        public ValueTask ReleaseAsync()
        {
            _released.TrySetResult();
            return inner.ReleaseAsync();
        }
    }
}
