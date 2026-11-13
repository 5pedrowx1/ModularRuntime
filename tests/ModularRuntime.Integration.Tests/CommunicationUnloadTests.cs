using System.Runtime.Loader;
using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>
/// Plugin API 0.4 communication mechanisms against real plugins in collectible load contexts: typed
/// services through a shared contract assembly, binary services, and streams. Each scenario keeps
/// what the host and the other plugins received, stops plugins, and requires the stopped plugin's
/// load context (and, once nobody uses it, the shared contract context) to be collected.
/// </summary>
public sealed class CommunicationUnloadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const string Scenarios = "ModularRuntime.TestPlugin.";
    private const string ContractContextPrefix = "ModularRuntime.Contract.ModularRuntimeTests.Contracts.";

    private static readonly PluginId Provider = PluginId.Parse("com.test.typed.provider");
    private static readonly PluginId Consumer = PluginId.Parse("com.test.typed.consumer");
    private static readonly PluginId Producer = PluginId.Parse("com.test.stream.producer");
    private static readonly PluginId Subscriber = PluginId.Parse("com.test.stream.subscriber");
    private static readonly PluginId Binary = PluginId.Parse("com.test.binary.provider");

    private static async Task<JsonObject> CallAsync(PluginRuntime runtime, string serviceId, string operation, JsonPayload? payload = null)
    {
        var result = await runtime.Host.Services.InvokeAsync(serviceId, operation, payload);
        Assert.True(result.IsSuccess, $"{serviceId}/{operation}: [{result.ErrorCode}] {result.Message}");
        return (JsonObject)result.Payload!.ToNode()!;
    }

    [Fact]
    public async Task TypedService_WorksAcrossLoadContexts_ThroughOneSharedContract()
    {
        using var temporary = new TemporaryDirectory("v04-typed");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();

        var status = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "status");
        Assert.Null(status["resolveError"]?.GetValue<string>());
        Assert.True(status["available"]!.GetValue<bool>());

        Assert.Equal(1, (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "call"))["count"]!.GetValue<int>());
        Assert.Equal(2, (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "call-async"))["count"]!.GetValue<int>());
        var snapshot = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "snapshot");
        Assert.Equal(2, snapshot["count"]!.GetValue<int>());
        Assert.Equal("ModularRuntimeTests.Contracts", snapshot["type"]!.GetValue<string>());
        Assert.Equal("ContractException", (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "fail"))["status"]!.GetValue<string>());

        var descriptor = Assert.Single(harness.Runtime.Host.Services.GetServices(), s => s.ServiceId == "test.alc.counter");
        Assert.Equal(ServiceKind.Typed, descriptor.Kind);
        Assert.Contains("ModularRuntimeTests.Contracts.ICounterService, ModularRuntimeTests.Contracts, Version=1.0.0.0", descriptor.ContractTypeName, StringComparison.Ordinal);

        var contract = Assert.Single(harness.Runtime.GetSharedContracts());
        Assert.Equal("ModularRuntimeTests.Contracts", contract.Name);
        Assert.Equal(new Version(1, 0, 0, 0), contract.Version);
        Assert.Equal([Consumer, Provider], contract.Users); // ordinal order

        // The host compiled against its own copy of the contract (default context): same name, another
        // identity. The runtime explains the mismatch instead of failing a cast somewhere else.
        var mismatch = Assert.Throws<PluginRuntimeException>(() => harness.Runtime.Host.TypedServices.GetClient<ModularRuntimeTests.Contracts.ICounterService>("test.alc.counter"));
        Assert.Equal(PluginErrorCodes.TypedServiceContractMismatch, mismatch.ErrorCode);
        Assert.Contains("different load of the same type", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderStop_InvalidatesTheConsumersProxy_AndTheProviderContextIsCollected()
    {
        using var temporary = new TemporaryDirectory("v04-typed-stop");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();
        Assert.Equal(1, (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "call"))["count"]!.GetValue<int>());

        Assert.True((await harness.Runtime.StopAsync(Provider)).IsSuccess);

        var after = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "call");
        Assert.Equal("TypedServiceException", after["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.ServiceRemoved, after["errorCode"]!.GetValue<string>());
        Assert.False((await CallAsync(harness.Runtime, "test.alc.typed-consumer", "status"))["available"]!.GetValue<bool>());
        Assert.Equal(PluginState.Running, harness.Runtime.GetPlugin(Consumer)!.State);
        Assert.Equal([Consumer], Assert.Single(harness.Runtime.GetSharedContracts()).Users);

        harness.AssertCollected(Provider);
        Assert.True(harness.ContextOf(Consumer).IsAlive);
        Assert.True(ContractContextsAlive() > 0, "the shared contract context stays while the consumer uses it");

        Assert.True((await harness.Runtime.StopAsync(Consumer)).IsSuccess);
        harness.AssertCollected(Consumer);
        Assert.Empty(harness.Runtime.GetSharedContracts());
        Assert.True(WaitForContractContextsCollected(), "the shared contract context must be collected once its last user stopped");
    }

    [Fact]
    public async Task ProviderStoppingDuringACall_CancelsIt_AndSubsequentCallsSeeRemoval()
    {
        using var temporary = new TemporaryDirectory("v04-typed-cancel");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();

        var waiting = harness.Runtime.Host.Services.InvokeAsync("test.alc.typed-consumer", "wait").AsTask();
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        Assert.True((await harness.Runtime.StopAsync(Provider).WaitAsync(Timeout)).IsSuccess);

        var result = await waiting.WaitAsync(Timeout);
        Assert.True(result.IsSuccess, result.Message);
        var outcome = (JsonObject)result.Payload!.ToNode()!;
        Assert.Equal("TypedServiceException", outcome["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.OperationCanceled, outcome["errorCode"]!.GetValue<string>());
        harness.AssertCollected(Provider);
    }

    [Fact]
    public async Task PrivateTypesAcrossTheTypedBoundary_AreReportedAsText_AndDoNotPinTheProvider()
    {
        using var temporary = new TemporaryDirectory("v04-typed-private");
        await using var harness = await Harness.CreateAsync(temporary, new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();

        // Warn (opt-in): the private object crosses, once logged.
        var opaque = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "opaque");
        Assert.Equal("ModularRuntime.TestPlugin.PrivateBox", opaque["type"]!.GetValue<string>());
        Assert.NotNull(harness.Logger.Find(LogLevel.Warning, PluginErrorCodes.ValueNotShared));

        // A private exception becomes a text diagnostic.
        var failure = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "fail-privately");
        Assert.Equal("TypedServiceException", failure["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.HandlerFailed, failure["errorCode"]!.GetValue<string>());
        Assert.Equal("ModularRuntime.TestPlugin.PluginFailureException", failure["exceptionType"]!.GetValue<string>());

        Assert.True((await harness.Runtime.StopAsync(Provider)).IsSuccess);
        harness.AssertCollected(Provider);
        GC.KeepAlive(opaque);
        GC.KeepAlive(failure);
    }

    [Fact]
    public async Task SharedExceptionsThrownByTheProvider_PinItsContextOnlyWhileTheConsumerHoldsThem()
    {
        using var temporary = new TemporaryDirectory("v04-typed-exception");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();

        // A framework exception propagates unchanged, with the provider's frames in its stack trace.
        var thrown = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "fail-framework");
        Assert.Equal("FrameworkException", thrown["status"]!.GetValue<string>());
        Assert.Equal("framework failure", thrown["message"]!.GetValue<string>());
        Assert.True(thrown["hasProviderFrames"]!.GetValue<bool>());

        // A framework exception carrying a private inner exception becomes text.
        var converted = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "fail-private-inner");
        Assert.Equal("TypedServiceException", converted["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.HandlerFailed, converted["errorCode"]!.GetValue<string>());
        Assert.Equal(typeof(InvalidOperationException).FullName, converted["exceptionType"]!.GetValue<string>());
        Assert.Contains("carries a reference that is not shared", converted["message"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.True((await harness.Runtime.StopAsync(Provider)).IsSuccess);

        // The exception object the consumer kept references the provider's code through its stack
        // trace (the runtime keeps a collectible assembly alive while a thrown exception points into
        // it), so the provider's context survives until the consumer lets the exception go.
        Assert.False(GcDiagnostics.WaitForCollection(harness.ContextOf(Provider), attempts: 3), "sanity: the kept exception pins the provider's context");
        Assert.Equal("Succeeded", (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "forget"))["status"]!.GetValue<string>());
        harness.AssertCollected(Provider);
        GC.KeepAlive(thrown);
        GC.KeepAlive(converted);
    }

    [Fact]
    public async Task RejectMode_IsTheDefault_AndRefusesPrivateValues_IncludingThoseHiddenInSharedObjects()
    {
        using var temporary = new TemporaryDirectory("v04-typed-reject");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();

        var opaque = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "opaque");
        Assert.Equal("TypedServiceException", opaque["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.ValueNotShared, opaque["errorCode"]!.GetValue<string>());
        Assert.Equal("Succeeded", (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "snapshot"))["status"]!.GetValue<string>());

        // A contract record whose object field carries a private object is refused on the way in;
        // the same record carrying a string passes; the provider's own private object inside the
        // record it returns is refused on the way out.
        var hidden = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "envelope-private");
        Assert.Equal("TypedServiceException", hidden["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.ValueNotShared, hidden["errorCode"]!.GetValue<string>());
        Assert.Contains("Payload", hidden["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Succeeded", (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "envelope-shared"))["status"]!.GetValue<string>());
        var returned = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "envelope-from-provider");
        Assert.Equal("TypedServiceException", returned["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.ValueNotShared, returned["errorCode"]!.GetValue<string>());

        // A raw delegate over consumer code is refused; a registered callback passes (see below).
        var raw = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "callback");
        Assert.Equal("TypedServiceException", raw["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.ValueNotShared, raw["errorCode"]!.GetValue<string>());

        Assert.True((await harness.Runtime.StopAsync(Provider)).IsSuccess);
        harness.AssertCollected(Provider);
    }

    [Fact]
    public async Task ConsumerKeepingSharedResults_DoesNotPinTheProvider()
    {
        using var temporary = new TemporaryDirectory("v04-typed-retain");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();

        // The consumer keeps the snapshot record and the envelope it received (closed contract types).
        Assert.Equal("Succeeded", (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "keep-snapshot"))["status"]!.GetValue<string>());
        Assert.Equal("Succeeded", (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "envelope-shared"))["status"]!.GetValue<string>());

        Assert.True((await harness.Runtime.StopAsync(Provider)).IsSuccess);
        harness.AssertCollected(Provider);
        Assert.Equal(PluginState.Running, harness.Runtime.GetPlugin(Consumer)!.State);
        Assert.Equal("Succeeded", (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "kept"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConsumerWithoutTheSharedContractDeclaration_GetsAContractMismatchWithGuidance()
    {
        using var temporary = new TemporaryDirectory("v04-typed-undeclared");
        await using var harness = await Harness.CreateAsync(
            temporary,
            null,
            (Provider, "TypedProviderScenarioPlugin", 0, true),
            (Consumer, "TypedConsumerScenarioPlugin", 10, false));
        await harness.StartAllAsync();

        var status = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "status");
        var error = status["resolveError"]!.GetValue<string>();
        Assert.StartsWith(PluginErrorCodes.TypedServiceContractMismatch, error, StringComparison.Ordinal);
        Assert.Contains("sharedContracts", error, StringComparison.Ordinal);
        Assert.Equal([Provider], Assert.Single(harness.Runtime.GetSharedContracts()).Users);
    }

    [Fact]
    public async Task RawCallbackHandedToTheProvider_InWarnMode_KeepsTheConsumerAlive_UntilTheProviderDropsIt()
    {
        // Documented limitation of Warn mode: a raw delegate a consumer hands to a provider keeps
        // the consumer alive until the provider drops it. Reject mode refuses it (see above);
        // RegisterCallback is the supported way (see below).
        using var temporary = new TemporaryDirectory("v04-typed-callback");
        await using var harness = await Harness.CreateAsync(temporary, new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();
        Assert.Equal(1, (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "callback"))["callbacks"]!.GetValue<int>());

        Assert.True((await harness.Runtime.StopAsync(Consumer)).IsSuccess);
        Assert.False(GcDiagnostics.WaitForCollection(harness.ContextOf(Consumer), attempts: 3), "sanity: the provider still holds the consumer's delegate");

        Assert.True((await harness.Runtime.StopAsync(Provider)).IsSuccess);
        harness.AssertCollected(Consumer);
        harness.AssertCollected(Provider);
    }

    [Fact]
    public async Task RegisteredCallback_IsInvokedAcrossContexts_AndReleasedWhenTheConsumerStops_WithoutPinningIt()
    {
        using var temporary = new TemporaryDirectory("v04-typed-registered-callback");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));
        await harness.StartAllAsync();

        // The consumer registers its method and hands the shared wrapper to the provider; the
        // provider invokes it (consumer code runs, admitted through the consumer's scope).
        var registered = await CallAsync(harness.Runtime, "test.alc.typed-consumer", "register-callback");
        Assert.Equal("Succeeded", registered["status"]!.GetValue<string>());
        Assert.Equal(1, registered["callbacks"]!.GetValue<int>());
        Assert.Equal("Succeeded", (await CallAsync(harness.Runtime, "test.alc.typed-provider", "invoke-callback"))["status"]!.GetValue<string>());
        Assert.Equal(2, (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "status"))["callbacks"]!.GetValue<int>());
        Assert.Contains(harness.Runtime.GetResources(Consumer), r => r.Kind == ResourceKind.Callback);

        // The consumer stops while the provider still holds the wrapper: the wrapper holds nothing
        // of the consumer, so its context is collected; invoking it reports callback_released.
        Assert.True((await harness.Runtime.StopAsync(Consumer)).IsSuccess);
        harness.AssertCollected(Consumer);
        var afterStop = await CallAsync(harness.Runtime, "test.alc.typed-provider", "invoke-callback");
        Assert.Equal("TypedServiceException", afterStop["status"]!.GetValue<string>());
        Assert.Equal(PluginErrorCodes.CallbackReleased, afterStop["errorCode"]!.GetValue<string>());

        Assert.True((await harness.Runtime.StopAsync(Provider)).IsSuccess);
        harness.AssertCollected(Provider);
    }

    [Fact]
    public async Task Streams_DeliverSharedItemsAcrossContexts_AndCompleteWhenTheProducerStops()
    {
        using var temporary = new TemporaryDirectory("v04-streams");
        await using var harness = await Harness.CreateAsync(temporary, null, (Producer, "StreamProducerScenarioPlugin", 0), (Subscriber, "StreamSubscriberScenarioPlugin", 10));
        await harness.StartAllAsync();
        var status = await CallAsync(harness.Runtime, "test.alc.stream-subscriber", "status");
        Assert.True(status["subscribed"]!.GetValue<bool>());

        var written = await CallAsync(harness.Runtime, "test.alc.stream-producer", "write", JsonPayload.Parse("""{"count":"20"}"""));
        Assert.Equal(20, written["written"]!.GetValue<int>());
        await WaitUntilAsync(async () => (await CallAsync(harness.Runtime, "test.alc.stream-subscriber", "status"))["received"]!.GetValue<int>() == 20);
        status = await CallAsync(harness.Runtime, "test.alc.stream-subscriber", "status");
        Assert.Equal(20, status["lastCount"]!.GetValue<int>());
        Assert.Equal(40, status["bytesReceived"]!.GetValue<int>());
        Assert.Equal(0, status["dropped"]!.GetValue<long>());
        Assert.Equal(2, harness.Runtime.Host.Streams.GetStreams().Count);

        Assert.True((await harness.Runtime.StopAsync(Producer)).IsSuccess);
        await WaitUntilAsync(async () => (await CallAsync(harness.Runtime, "test.alc.stream-subscriber", "status"))["completion"]?.GetValue<string>() == PluginErrorCodes.StreamRemoved);
        Assert.Empty(harness.Runtime.Host.Streams.GetStreams());
        Assert.Equal(PluginState.Running, harness.Runtime.GetPlugin(Subscriber)!.State);
        harness.AssertCollected(Producer);

        Assert.True((await harness.Runtime.StopAsync(Subscriber)).IsSuccess);
        harness.AssertCollected(Subscriber);
    }

    [Fact]
    public async Task BinaryService_WorksAcrossContexts_AndTheProviderIsCollectedAfterStop()
    {
        using var temporary = new TemporaryDirectory("v04-binary");
        await using var harness = await Harness.CreateAsync(temporary, null, (Binary, "BinaryProviderScenarioPlugin", 0));
        await harness.StartAllAsync();
        var client = harness.Runtime.Host.Services.GetClient("test.alc.binary")!;

        using (var reversed = await client.InvokeBinaryAsync("reverse", new byte[] { 1, 2, 3 }))
        {
            Assert.True(reversed.IsSuccess, reversed.Message);
            Assert.Equal([3, 2, 1], reversed.Payload.ToArray());
        }

        using (var tooLarge = await client.InvokeBinaryAsync("reverse", new byte[2048]))
        {
            Assert.Equal(PluginErrorCodes.PayloadTooLarge, tooLarge.ErrorCode);
        }

        var waiting = client.InvokeBinaryAsync("wait").AsTask();
        await Task.Delay(50);
        Assert.True((await harness.Runtime.StopAsync(Binary)).IsSuccess);
        using (var canceled = await waiting.WaitAsync(Timeout))
        {
            Assert.Equal(PluginOperationStatus.Canceled, canceled.Status);
        }

        using (var removed = await client.InvokeBinaryAsync("reverse"))
        {
            Assert.Equal(PluginErrorCodes.ServiceRemoved, removed.ErrorCode);
        }

        harness.AssertCollected(Binary);
        GC.KeepAlive(client);
    }

    [Fact]
    public async Task RepeatedRestarts_LeaveNoRetainedContexts_AndReloadTheContractOnDemand()
    {
        using var temporary = new TemporaryDirectory("v04-restarts");
        await using var harness = await Harness.CreateAsync(temporary, null, (Provider, "TypedProviderScenarioPlugin", 0), (Consumer, "TypedConsumerScenarioPlugin", 10));

        for (var cycle = 0; cycle < 5; cycle++)
        {
            await harness.StartAllAsync();
            Assert.Equal(1, (await CallAsync(harness.Runtime, "test.alc.typed-consumer", "call"))["count"]!.GetValue<int>());
            Assert.Equal(2, Assert.Single(harness.Runtime.GetSharedContracts()).UserCount);
            foreach (var result in await harness.Runtime.StopAllAsync())
            {
                Assert.True(result.IsSuccess, result.Message);
            }

            Assert.Empty(harness.Runtime.GetSharedContracts());
            Assert.True(await harness.Runtime.WaitForUnloadAsync(Provider, Timeout, PluginUnloadWait.InduceCollection), $"cycle {cycle}: provider contexts retained");
            Assert.True(await harness.Runtime.WaitForUnloadAsync(Consumer, Timeout, PluginUnloadWait.InduceCollection), $"cycle {cycle}: consumer contexts retained");
        }

        Assert.True(WaitForContractContextsCollected(), "no shared contract context may survive once every user is gone");
    }

    private static int ContractContextsAlive() =>
        AssemblyLoadContext.All.Count(context => context.Name is { } name && name.StartsWith(ContractContextPrefix, StringComparison.Ordinal));

    private static bool WaitForContractContextsCollected()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (ContractContextsAlive() == 0)
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return ContractContextsAlive() == 0;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(25);
        }
    }

    /// <summary>Stages copies of the test plugin as independent installations and runs them through <see cref="PluginRuntime"/>.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Dictionary<PluginId, WeakReference> _contexts = [];

        private Harness(PluginRuntime runtime, CollectingLogger logger)
        {
            Runtime = runtime;
            Logger = logger;
        }

        public PluginRuntime Runtime { get; }

        public CollectingLogger Logger { get; }

        public static async Task<Harness> CreateAsync(TemporaryDirectory temporary, RuntimeLimits? limits, params (PluginId Id, string EntryType, int StartOrder)[] plugins) =>
            await CreateAsync(temporary, limits, plugins.Select(p => (p.Id, p.EntryType, p.StartOrder, true)).ToArray());

        public static async Task<Harness> CreateAsync(TemporaryDirectory temporary, RuntimeLimits? limits, params (PluginId Id, string EntryType, int StartOrder, bool DeclareContract)[] plugins)
        {
            var root = Path.Combine(temporary.DirectoryPath, "plugins");
            Directory.CreateDirectory(root);
            foreach (var (id, entryType, startOrder, declare) in plugins)
            {
                var directory = StagedPlugins.CopyPlugin(root, "ModularRuntime.TestPlugin", id.Value);
                var manifest = StagedPlugins.ReadManifest(directory);
                manifest["id"] = id.Value;
                manifest["entryType"] = Scenarios + entryType;
                manifest["startOrder"] = startOrder;
                if (!declare)
                {
                    manifest.Remove("sharedContracts");
                }

                StagedPlugins.WriteManifest(directory, manifest);
            }

            var logger = new CollectingLogger();
            var runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = root, Logger = logger, Limits = limits ?? new RuntimeLimits() });
            var discovery = await runtime.DiscoverAsync();
            Assert.Empty(discovery.Discovery.Errors);
            Assert.Empty(discovery.RegistrationFailures);
            return new Harness(runtime, logger);
        }

        public async Task StartAllAsync()
        {
            foreach (var result in await Runtime.StartAllAsync())
            {
                Assert.True(result.IsSuccess, $"{result.PluginId}: [{result.ErrorCode}] {result.Message}");
            }

            // Capture the live contexts by name (the runtime keeps only weak references to retired ones).
            foreach (var plugin in Runtime.Plugins)
            {
                var prefix = $"ModularRuntime.Plugin.{plugin.Descriptor.Id}.";
                var context = AssemblyLoadContext.All.FirstOrDefault(c => c.Name is { } name && name.StartsWith(prefix, StringComparison.Ordinal));
                if (context is not null)
                {
                    _contexts[plugin.Descriptor.Id] = new WeakReference(context);
                }
            }
        }

        public WeakReference ContextOf(PluginId id) => _contexts[id];

        public void AssertCollected(PluginId id) =>
            Assert.True(GcDiagnostics.WaitForCollection(ContextOf(id)), $"The load context of '{id}' should be collectible after it stopped.");

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
}
