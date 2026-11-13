using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

// Contracts used by the typed-service tests. Everything here lives in the default load context,
// so the shared-type policy of the engine treats it as shared; tests that need "non-shared" types
// install a custom policy.
public interface ICalculator
{
    int Add(int a, int b);

    string Name { get; set; }

    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken);

    ValueTask<string> EchoAsync(string text);

    Task WaitAsync(CancellationToken cancellationToken);

    ValueTask NoopAsync();

    T Identity<T>(T value) where T : class;

    void Split(string text, out string head, ref int count);

    void Fail();

    Task FailAsync();

    object Opaque();

    Task<object> OpaqueAsync();

    int Twice(int value) => Add(value, value);
}

public interface IAdvancedCalculator : ICalculator
{
    long Multiply(long a, long b);
}

public sealed class CalculatorException(string message) : Exception(message);

/// <summary>A type the tests mark as "not shared" through a custom policy.</summary>
public sealed class PrivateValue
{
    public int Number { get; init; }
}

public sealed class PrivateException(string message) : Exception(message);

public sealed class TypedServiceTests
{
    private const string ServiceId = "test.calculator";

    private sealed class Calculator : IAdvancedCalculator
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<object>? OpaqueFactory { get; init; }

        public int Add(int a, int b) => a + b;

        public string Name { get; set; } = "calc";

        public async Task<int> AddAsync(int a, int b, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return a + b;
        }

        public ValueTask<string> EchoAsync(string text) => new(text);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
        }

        public ValueTask NoopAsync() => ValueTask.CompletedTask;

        public T Identity<T>(T value) where T : class => value;

        public void Split(string text, out string head, ref int count)
        {
            head = text[..1];
            count++;
        }

        public void Fail() => throw new CalculatorException("boom");

        public async Task FailAsync()
        {
            await Task.Yield();
            throw new CalculatorException("boom async");
        }

        public object Opaque() => OpaqueFactory?.Invoke() ?? "shared";

        public async Task<object> OpaqueAsync()
        {
            await Task.Yield();
            return OpaqueFactory?.Invoke() ?? "shared";
        }

        public long Multiply(long a, long b) => a * b;
    }

    private static TypedServiceDefinition Definition(string version = "1.0") => new(ServiceId, "Calculator", Version.Parse(version));

    [Fact]
    public async Task Register_Resolve_AndCallEveryKindOfMember()
    {
        await using var engine = new PluginEngine();
        var calculator = new Calculator();
        await StartPluginAsync(engine, "com.test.provider", c =>
        {
            c.TypedServices.Register<IAdvancedCalculator>(Definition("1.2") with { Metadata = new Dictionary<string, string> { ["kind"] = "math" } }, calculator);
            return Task.CompletedTask;
        });

        var descriptor = Assert.Single(engine.Host.Services.GetServices());
        Assert.Equal(ServiceKind.Typed, descriptor.Kind);
        Assert.Equal(ServiceId, descriptor.ServiceId);
        Assert.Empty(descriptor.Operations);
        Assert.StartsWith(typeof(IAdvancedCalculator).FullName!, descriptor.ContractTypeName, StringComparison.Ordinal);
        Assert.Equal("math", descriptor.Metadata["kind"]);

        var client = engine.Host.TypedServices.GetClient<IAdvancedCalculator>(ServiceId, new Version(1, 0))!;
        var proxy = client.Service;
        Assert.True(client.IsAvailable);
        Assert.Equal(descriptor.RegistrationId, client.RegistrationId);
        Assert.NotSame(calculator, proxy);
        Assert.IsAssignableFrom<Services.TypedServiceProxy>(proxy);

        Assert.Equal(5, proxy.Add(2, 3));
        Assert.Equal(42L, proxy.Multiply(6, 7));
        Assert.Equal(8, proxy.Twice(4));
        proxy.Name = "renamed";
        Assert.Equal("renamed", calculator.Name);
        Assert.Equal("renamed", proxy.Name);
        Assert.Equal(7, await proxy.AddAsync(3, 4, CancellationToken.None));
        Assert.Equal("hi", await proxy.EchoAsync("hi"));
        await proxy.NoopAsync();
        Assert.Equal("same", proxy.Identity("same"));
        var count = 1;
        proxy.Split("hello", out var head, ref count);
        Assert.Equal("h", head);
        Assert.Equal(2, count);
        Assert.Equal("shared", proxy.Opaque());
        Assert.Equal("shared", await proxy.OpaqueAsync());

        var second = engine.Host.TypedServices.Resolve<IAdvancedCalculator>(ServiceId);
        Assert.NotNull(second);
        Assert.NotSame(proxy, second);
        Assert.Equal(5, second.Add(2, 3));
    }

    [Fact]
    public async Task ContractExceptions_PropagateUnchanged_SyncAndAsync()
    {
        await using var engine = new PluginEngine();
        engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        var sync = Assert.Throws<CalculatorException>(proxy.Fail);
        Assert.Equal("boom", sync.Message);
        var async = await Assert.ThrowsAsync<CalculatorException>(proxy.FailAsync);
        Assert.Equal("boom async", async.Message);
    }

    [Fact]
    public async Task NonSharedExceptions_AreConvertedToTypedServiceException_WithDiagnostic()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger, sharedTypePolicy: new ExcludingPolicy(typeof(PrivateException)));
        engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator { OpaqueFactory = () => throw new PrivateException("secret") });
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        var sync = Assert.Throws<TypedServiceException>(proxy.Opaque);
        Assert.Equal(PluginErrorCodes.HandlerFailed, sync.ErrorCode);
        Assert.Equal(ServiceId, sync.ServiceId);
        Assert.Equal(typeof(PrivateException).FullName, sync.Diagnostic!.ExceptionType);
        Assert.Contains("secret", sync.Diagnostic.Message, StringComparison.Ordinal);
        var async = await Assert.ThrowsAsync<TypedServiceException>(proxy.OpaqueAsync);
        Assert.Equal(PluginErrorCodes.HandlerFailed, async.ErrorCode);
        Assert.NotNull(logger.Find(LogLevel.Warning, "threw"));
    }

    [Fact]
    public async Task ValueCheck_WarnsOnce_OrRejects_NonSharedArgumentsAndResults()
    {
        var logger = new CollectingLogger();
        await using var warning = new PluginEngine(logger, new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, new ExcludingPolicy(typeof(PrivateValue)));
        warning.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator { OpaqueFactory = () => new PrivateValue { Number = 1 } });
        var proxy = warning.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        Assert.IsType<PrivateValue>(proxy.Opaque());
        Assert.IsType<PrivateValue>(proxy.Opaque());
        Assert.IsType<PrivateValue>(await proxy.OpaqueAsync());
        Assert.IsType<PrivateValue>(proxy.Identity(new PrivateValue()));
        Assert.Single(logger.Entries, e => e.Message.Contains(PluginErrorCodes.ValueNotShared, StringComparison.Ordinal));

        // Reject is the default.
        await using var rejecting = new PluginEngine(sharedTypePolicy: new ExcludingPolicy(typeof(PrivateValue)));
        rejecting.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator { OpaqueFactory = () => new PrivateValue() });
        var strict = rejecting.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        Assert.Equal(TypedValueCheck.Reject, rejecting.Limits.TypedValueCheck);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(strict.Opaque).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, (await Assert.ThrowsAsync<TypedServiceException>(strict.OpaqueAsync)).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new PrivateValue())).ErrorCode);
        Assert.Equal("fine", strict.Identity("fine"));

        await using var uncheckedEngine = new PluginEngine(limits: new RuntimeLimits { TypedValueCheck = TypedValueCheck.None }, sharedTypePolicy: new ExcludingPolicy(typeof(PrivateValue)));
        uncheckedEngine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator { OpaqueFactory = () => new PrivateValue() });
        Assert.IsType<PrivateValue>(uncheckedEngine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!.Opaque());
    }

    [Fact]
    public async Task InvalidContracts_AreRejected()
    {
        await using var engine = new PluginEngine();
        var services = engine.Host.TypedServices;

        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, Assert.Throws<PluginRuntimeException>(() => services.Register<Calculator>(Definition(), new Calculator())).ErrorCode);
        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, Assert.Throws<PluginRuntimeException>(() => services.Register<IInternalContract>(Definition(), new InternalImplementation())).ErrorCode);
        Assert.Contains("static abstract", Services.TypedProxyEmitter.ValidateContract(typeof(IStaticContract)), StringComparison.Ordinal);
        Assert.Null(Services.TypedProxyEmitter.ValidateContract(typeof(IAdvancedCalculator)));
        Assert.Throws<ArgumentException>(() => services.Register<ICalculator>(new TypedServiceDefinition("Bad Id", "x", new Version(1, 0)), new Calculator()));
        Assert.Throws<ArgumentNullException>(() => services.Register<ICalculator>(Definition(), null!));
        Assert.Empty(engine.Host.Services.GetServices());
    }

    [Fact]
    public async Task OneIdNamespace_AcrossKinds_AndKindMismatches()
    {
        await using var engine = new PluginEngine();
        engine.Host.Services.Register(Service(ServiceId), Echo);

        Assert.Equal(PluginErrorCodes.DuplicateService, Assert.Throws<PluginRuntimeException>(() => engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator())).ErrorCode);
        Assert.Null(engine.Host.TypedServices.Resolve<ICalculator>(ServiceId));
        Assert.Null(engine.Host.TypedServices.GetClient<ICalculator>(ServiceId));

        await using var typedEngine = new PluginEngine();
        typedEngine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        var json = await typedEngine.Host.Services.InvokeAsync(ServiceId, "add");
        Assert.Equal(PluginErrorCodes.ServiceKindMismatch, json.ErrorCode);
        using var binary = await typedEngine.Host.Services.InvokeBinaryAsync(ServiceId, "add");
        Assert.Equal(PluginErrorCodes.ServiceKindMismatch, binary.ErrorCode);
        Assert.Equal(PluginErrorCodes.ServiceKindMismatch, (await typedEngine.Host.Services.GetClient(ServiceId)!.InvokeAsync("add")).ErrorCode);
        Assert.Equal(PluginErrorCodes.DuplicateService, Assert.Throws<PluginRuntimeException>(() => typedEngine.Host.Services.Register(Service(ServiceId), Echo)).ErrorCode);
    }

    [Fact]
    public async Task DifferentContractType_IsAContractMismatch()
    {
        await using var engine = new PluginEngine();
        engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());

        var mismatch = Assert.Throws<PluginRuntimeException>(() => engine.Host.TypedServices.Resolve<IAdvancedCalculator>(ServiceId));
        Assert.Equal(PluginErrorCodes.TypedServiceContractMismatch, mismatch.ErrorCode);
        Assert.Null(engine.Host.TypedServices.Resolve<ICalculator>(ServiceId, new Version(2, 0)));
    }

    [Fact]
    public async Task ProviderStop_RemovesTheService_InvalidatesProxies_AndDropsTheInstance()
    {
        await using var engine = new PluginEngine();
        var reference = await StartProviderAsync(engine);
        var client = engine.Host.TypedServices.GetClient<ICalculator>(ServiceId)!;
        Assert.Equal(3, client.Service.Add(1, 2));

        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.provider"))).IsSuccess);

        Assert.False(client.IsAvailable);
        Assert.Null(client.GetDescriptor());
        Assert.Empty(engine.Host.Services.GetServices());
        var sync = Assert.Throws<TypedServiceException>(() => client.Service.Add(1, 2));
        Assert.Equal(PluginErrorCodes.ServiceRemoved, sync.ErrorCode);
        var task = client.Service.AddAsync(1, 2, CancellationToken.None);
        Assert.True(task.IsFaulted, "a removed service fails asynchronous calls through the task");
        Assert.Equal(PluginErrorCodes.ServiceRemoved, (await Assert.ThrowsAsync<TypedServiceException>(() => task)).ErrorCode);
        Assert.Equal(PluginErrorCodes.ServiceRemoved, (await Assert.ThrowsAsync<TypedServiceException>(() => client.Service.EchoAsync("x").AsTask())).ErrorCode);

        // The runtime (registry, proxy, client) holds no reference to the provider's object.
        Assert.True(GcDiagnostics.WaitForCollection(reference), "the provider instance must be collectible after the provider stopped while the host keeps the proxy");
        GC.KeepAlive(client);

        // A new provider with the same ID does not revive the old proxy.
        engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        Assert.Equal(PluginErrorCodes.ServiceRemoved, Assert.Throws<TypedServiceException>(() => client.Service.Add(1, 2)).ErrorCode);
        Assert.Equal(3, engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!.Add(1, 2));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StartProviderAsync(PluginEngine engine)
    {
        // The instance is created inside the plugin: the test's own closures (kept by the test
        // factory, which the engine retains for the slot) must not reference it.
        var reference = new WeakReference(null);
        await StartPluginAsync(engine, "com.test.provider", c =>
        {
            var calculator = new Calculator();
            reference.Target = calculator;
            c.TypedServices.Register<ICalculator>(Definition(), calculator);
            return Task.CompletedTask;
        });
        return reference;
    }

    [Fact]
    public async Task ProviderStoppingDuringACall_CancelsTheCall_DrainsIt_AndRejectsNewCalls()
    {
        await using var engine = new PluginEngine();
        var calculator = new Calculator();
        var shutdownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartPluginAsync(
            engine,
            "com.test.provider",
            c =>
            {
                c.TypedServices.Register<ICalculator>(Definition(), calculator);
                return Task.CompletedTask;
            },
            onShutdown: async _ =>
            {
                shutdownEntered.TrySetResult();
                await releaseShutdown.Task;
            });
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        var call = proxy.WaitAsync(CancellationToken.None);
        await calculator.Entered.Task.WaitAsync(TestTimeout);
        var stop = engine.StopAsync(PluginId.Parse("com.test.provider"));

        var canceled = await Assert.ThrowsAsync<TypedServiceException>(() => call.WaitAsync(TestTimeout));
        Assert.Equal(PluginErrorCodes.OperationCanceled, canceled.ErrorCode);
        await shutdownEntered.Task.WaitAsync(TestTimeout);

        // While the provider shuts down the service is unavailable, not removed.
        var unavailable = Assert.Throws<TypedServiceException>(() => proxy.Add(1, 1));
        Assert.Equal(PluginErrorCodes.ServiceUnavailable, unavailable.ErrorCode);
        Assert.False(engine.Host.Services.GetService(ServiceId)!.IsAvailable);

        releaseShutdown.SetResult();
        Assert.True((await stop.WaitAsync(TestTimeout)).IsSuccess);
        Assert.Equal(PluginErrorCodes.ServiceRemoved, Assert.Throws<TypedServiceException>(() => proxy.Add(1, 1)).ErrorCode);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceled()
    {
        await using var engine = new PluginEngine();
        var calculator = new Calculator();
        engine.Host.TypedServices.Register<ICalculator>(Definition(), calculator);
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;
        using var cancellation = new CancellationTokenSource();

        var call = proxy.WaitAsync(cancellation.Token);
        await calculator.Entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TestTimeout));
        Assert.IsNotType<TypedServiceException>(exception);
    }

    [Fact]
    public async Task StoppedCaller_CannotCallOut()
    {
        await using var engine = new PluginEngine();
        engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        ICalculator? proxy = null;
        await StartPluginAsync(engine, "com.test.consumer", c =>
        {
            proxy = c.TypedServices.Resolve<ICalculator>(ServiceId);
            Assert.Equal(2, proxy!.Add(1, 1));
            return Task.CompletedTask;
        });

        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.consumer"))).IsSuccess);

        Assert.Equal(PluginErrorCodes.ScopeClosed, Assert.Throws<TypedServiceException>(() => proxy!.Add(1, 1)).ErrorCode);
    }

    [Fact]
    public async Task StopRequestedFromInsideATypedCall_IsRejectedAsReentrant()
    {
        await using var engine = new PluginEngine();
        var providerId = PluginId.Parse("com.test.provider");
        PluginOperationResult? reentrant = null;
        engine.Host.Services.Register(Service("test.host.stopper"), async (_, ct) =>
        {
            reentrant = await engine.StopAsync(providerId, ct);
            return PluginResponse.Success();
        });
        IPluginContext? providerContext = null;
        await StartPluginAsync(engine, "com.test.provider", c =>
        {
            providerContext = c;
            c.TypedServices.Register<ICalculator>(Definition(), new Calculator
            {
                // Called on behalf of the host; from inside the call the provider's plugin asks a host service to stop it.
                OpaqueFactory = () => providerContext!.Services.InvokeAsync("test.host.stopper", "echo").AsTask().GetAwaiter().GetResult().Status.ToString(),
            });
            return Task.CompletedTask;
        });
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        Assert.Equal("Succeeded", proxy.Opaque());
        Assert.Equal(PluginErrorCodes.ReentrantLifecycleOperation, reentrant!.ErrorCode);
        Assert.Equal(PluginState.Running, engine.GetPlugin(providerId)!.State);
    }

    [Fact]
    public async Task ConcurrentCalls_AllSucceed_AndAStopDuringThem_LeavesNoCallInFlight()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.provider", c =>
        {
            c.TypedServices.Register<ICalculator>(Definition(), new Calculator());
            return Task.CompletedTask;
        });
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        var sums = await Task.WhenAll(Enumerable.Range(0, 2000).Select(i => Task.Run(() => proxy.AddAsync(i, 1, CancellationToken.None))));
        Assert.Equal(Enumerable.Range(1, 2000), sums);
        Assert.Equal(2000, Enumerable.Range(0, 2000).AsParallel().Select(i => proxy.Add(i, 0)).Distinct().Count());

        var outcomes = new int[2];
        var calls = Enumerable.Range(0, 500).Select(_ => Task.Run(async () =>
        {
            try
            {
                await proxy.AddAsync(1, 1, CancellationToken.None);
                Interlocked.Increment(ref outcomes[0]);
            }
            catch (TypedServiceException exception) when (exception.ErrorCode is PluginErrorCodes.ServiceRemoved or PluginErrorCodes.ServiceUnavailable or PluginErrorCodes.OperationCanceled)
            {
                Interlocked.Increment(ref outcomes[1]);
            }
        })).ToArray();
        var stop = await engine.StopAsync(PluginId.Parse("com.test.provider")).WaitAsync(TestTimeout);
        await Task.WhenAll(calls).WaitAsync(TestTimeout);

        Assert.True(stop.IsSuccess, stop.Message);
        Assert.Equal(500, outcomes[0] + outcomes[1]);
        Assert.Equal(PluginErrorCodes.ServiceRemoved, Assert.Throws<TypedServiceException>(() => proxy.Add(1, 1)).ErrorCode);
    }

    [Fact]
    public async Task DisposedRuntime_RejectsCalls()
    {
        var engine = new PluginEngine();
        engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;
        await engine.DisposeAsync();

        var exception = Assert.Throws<TypedServiceException>(() => proxy.Add(1, 1));
        Assert.True(exception.ErrorCode is PluginErrorCodes.EngineDisposed or PluginErrorCodes.ServiceRemoved or PluginErrorCodes.ScopeClosed, exception.ErrorCode);
    }

    /// <summary>A type the tests mark as "not shared"; its methods serve as delegate targets.</summary>
    private sealed class PrivateCallbacks
    {
        public int Calls { get; private set; }

        public void Handle() => Calls++;

        public static void Ignore()
        {
        }
    }

    [Fact]
    public async Task ValueCheck_InspectsDelegateTargets_AndTheTypeArgumentsOfSharedContainers()
    {
        var logger = new CollectingLogger();
        await using var warning = new PluginEngine(logger, new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, new ExcludingPolicy(typeof(PrivateValue), typeof(PrivateCallbacks)));
        warning.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        var proxy = warning.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;
        var callbacks = new PrivateCallbacks();

        // Warn: everything passes, one warning per offending type (the delegate's target, not Action).
        Assert.NotNull(proxy.Identity<Action>(callbacks.Handle));
        Assert.NotNull(proxy.Identity<Action>(PrivateCallbacks.Ignore));
        Assert.NotNull(proxy.Identity((Action)(() => { }) + callbacks.Handle));
        Assert.NotNull(proxy.Identity(new List<PrivateValue>()));
        Assert.NotNull(proxy.Identity(new Dictionary<string, PrivateValue[]>()));
        Assert.NotNull(proxy.Identity(new PrivateValue[1]));
        Assert.NotNull(proxy.Identity<Action>(static () => { }));
        Assert.NotNull(proxy.Identity(new List<string>()));
        var warnings = logger.Entries.Where(e => e.Message.Contains(PluginErrorCodes.ValueNotShared, StringComparison.Ordinal)).Select(e => e.Message).ToList();
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("target method 'Handle'", StringComparison.Ordinal) && w.Contains(nameof(PrivateCallbacks), StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("List`1[[", StringComparison.Ordinal) && w.Contains(nameof(PrivateValue), StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Dictionary`2[[", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("PrivateValue[]", StringComparison.Ordinal));

        // Reject: the same values are refused; shared delegates and containers pass.
        await using var rejecting = new PluginEngine(limits: new RuntimeLimits { TypedValueCheck = TypedValueCheck.Reject }, sharedTypePolicy: new ExcludingPolicy(typeof(PrivateValue), typeof(PrivateCallbacks)));
        rejecting.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        var strict = rejecting.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity<Action>(callbacks.Handle)).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity<Action>(PrivateCallbacks.Ignore)).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity((Action)(() => { }) + callbacks.Handle)).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new List<PrivateValue>())).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(Array.Empty<PrivateValue>())).ErrorCode);
        Assert.NotNull(strict.Identity<Action>(static () => { }));
        Assert.NotNull(strict.Identity(new List<string>()));
        Assert.Equal(0, callbacks.Calls);
    }

    [Fact]
    public async Task ValueCheck_InspectsCollections_Deeply_AndNeverEnumeratesLazySequences()
    {
        var logger = new CollectingLogger();
        await using var warning = new PluginEngine(logger, new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, new ExcludingPolicy(typeof(PrivateValue)));
        warning.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        var proxy = warning.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        // Warn: a private element hidden under object is found and warned once (per offending type).
        Assert.NotNull(proxy.Identity(new List<object> { "shared", 1, new PrivateValue() }));
        Assert.NotNull(proxy.Identity(new object[] { new PrivateValue() }));
        Assert.NotNull(proxy.Identity(new Dictionary<string, object> { ["box"] = new PrivateValue() }));
        var warnings = logger.Entries.Where(e => e.Message.Contains(PluginErrorCodes.ValueNotShared, StringComparison.Ordinal)).Select(e => e.Message).ToList();
        var single = Assert.Single(warnings);
        Assert.Contains("element [2]", single, StringComparison.Ordinal);

        await using var rejecting = new PluginEngine(sharedTypePolicy: new ExcludingPolicy(typeof(PrivateValue), typeof(PrivateCallbacksHolder)));
        rejecting.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator());
        var strict = rejecting.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        // Reject: elements, dictionary keys and values, nested levels, arrays of object, delegates inside a list.
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new List<object> { "shared", new PrivateValue() })).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new object[] { 1, new PrivateValue() })).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new Dictionary<string, object> { ["box"] = new PrivateValue() })).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new Dictionary<object, string> { [new PrivateValue()] = "key" })).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new List<object> { new List<object> { new PrivateValue() } })).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new List<Action> { new PrivateCallbacksHolder().Handle })).ErrorCode);

        // Shared contents pass, including collections whose element type needs no scan.
        Assert.NotNull(strict.Identity(new List<object> { "a", 1, new List<string> { "b" } }));
        Assert.NotNull(strict.Identity(new Dictionary<string, object> { ["n"] = 1 }));
        Assert.NotNull(strict.Identity(new List<string> { "a" }));
        Assert.NotNull(strict.Identity(new System.Collections.ArrayList { "a" }));

        // No shallow budget any more: a private element far down a list, or deep in nested lists, is found.
        var farDown = new List<object>(Enumerable.Repeat<object>("x", 1000)) { [999] = new PrivateValue() };
        var late = Assert.Throws<TypedServiceException>(() => strict.Identity(farDown));
        Assert.Equal(PluginErrorCodes.ValueNotShared, late.ErrorCode);
        Assert.Contains("element [999]", late.Message, StringComparison.Ordinal);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => strict.Identity(new List<object> { new List<object> { new List<object> { new List<object> { new PrivateValue() } } } })).ErrorCode);

        // A lazy sequence is inspected by its fields (its selector targets shared code), never enumerated.
        var (lazy, enumerated) = LazyPrivateSequence();
        Assert.NotNull(strict.Identity(lazy));
        Assert.Equal(0, enumerated.Value);
    }

    private static (IEnumerable<object> Sequence, StrongBox<int> Enumerated) LazyPrivateSequence()
    {
        var enumerated = new StrongBox<int>(0);
        return (Enumerable.Range(0, 1).Select(_ => { enumerated.Value++; return (object)new PrivateValue(); }), enumerated);
    }

    private sealed class PrivateCallbacksHolder
    {
        public int Calls { get; private set; }

        public void Handle() => Calls++;
    }

    [Fact]
    public async Task ExceptionsCarryingPrivateParts_AreConvertedToTypedServiceException()
    {
        await using var engine = new PluginEngine(sharedTypePolicy: new ExcludingPolicy(typeof(PrivateException), typeof(PrivateValue)));
        Func<object>? factory = null;
        engine.Host.TypedServices.Register<ICalculator>(Definition(), new Calculator { OpaqueFactory = () => factory!() });
        var proxy = engine.Host.TypedServices.Resolve<ICalculator>(ServiceId)!;

        // A framework exception wrapping a private one.
        factory = () => throw new InvalidOperationException("outer", new PrivateException("inner"));
        var wrapped = Assert.Throws<TypedServiceException>(proxy.Opaque);
        Assert.Equal(PluginErrorCodes.HandlerFailed, wrapped.ErrorCode);
        Assert.Equal(typeof(InvalidOperationException).FullName, wrapped.Diagnostic!.ExceptionType);
        Assert.Contains("carries a reference that is not shared", wrapped.Message, StringComparison.Ordinal);
        Assert.Contains("InnerException", wrapped.Message, StringComparison.Ordinal);
        Assert.Contains("inner", wrapped.Diagnostic.Detail, StringComparison.Ordinal);
        Assert.Equal(PluginErrorCodes.HandlerFailed, (await Assert.ThrowsAsync<TypedServiceException>(proxy.OpaqueAsync)).ErrorCode);

        // An aggregate of private exceptions, and a shared exception carrying a private object in Data.
        factory = () => throw new AggregateException(new PrivateException("a"), new PrivateException("b"));
        Assert.Equal(PluginErrorCodes.HandlerFailed, Assert.Throws<TypedServiceException>(proxy.Opaque).ErrorCode);
        factory = () =>
        {
            var exception = new InvalidOperationException("with data");
            exception.Data["box"] = new PrivateValue();
            throw exception;
        };
        Assert.Equal(PluginErrorCodes.HandlerFailed, Assert.Throws<TypedServiceException>(proxy.Opaque).ErrorCode);

        // Control: a shared exception whose whole chain is shared propagates unchanged.
        factory = () => throw new InvalidOperationException("plain", new CalculatorException("shared inner"));
        var unchanged = Assert.Throws<InvalidOperationException>(proxy.Opaque);
        Assert.IsType<CalculatorException>(unchanged.InnerException);
    }

    [Fact]
    public async Task ProviderRestartsUnderConcurrentTraffic_LeaveNoInFlightWork_AndOnlyExpectedOutcomes()
    {
        await using var engine = new PluginEngine(limits: new RuntimeLimits { DrainTimeout = TimeSpan.FromSeconds(5) });
        var providerId = PluginId.Parse("com.test.provider");
        var plugin = new TestPlugin((c, startToken) =>
        {
            c.TypedServices.Register<ICalculator>(Definition(), new Calculator());
            c.Services.RegisterBinary(
                new BinaryServiceDefinition("test.bytes", "Bytes", new Version(1, 0), ["echo"]),
                (request, _) => ValueTask.FromResult(BinaryResponse.Success(request.Payload.ToArray())));
            var ticks = c.Streams.Create<int>(new StreamDefinition("test.ticks", "Ticks", new Version(1, 0)) { DefaultCapacity = 8, DefaultFullMode = StreamFullMode.DropOldest });
            var stopping = c.StoppingToken;
            var writer = Task.Run(
                async () =>
                {
                    for (var tick = 0; !stopping.IsCancellationRequested; tick++)
                    {
                        ticks.TryWrite(tick);
                        try
                        {
                            await Task.Delay(1, stopping);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                },
                CancellationToken.None);
            GC.KeepAlive(writer);
            return Task.CompletedTask;
        });
        Assert.True(engine.Register(Create(providerId.Value), new TestPluginFactory(plugin)).IsSuccess);
        Assert.True(engine.MarkValidated(providerId).IsSuccess);
        Assert.True((await engine.StartAsync(providerId)).IsSuccess);

        using var stopCallers = new CancellationTokenSource();
        var unexpected = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var successes = 0;
        var expectedFailures = 0;
        static bool IsExpected(string? code) => code is PluginErrorCodes.ServiceNotFound or PluginErrorCodes.ServiceUnavailable or PluginErrorCodes.ServiceRemoved or PluginErrorCodes.OperationCanceled;
        // The callers are a continuous load, so they run on dedicated threads: a busy loop on a
        // thread-pool thread (the binary call completes synchronously, so an async loop never
        // yields) would hold one of the pool's few starting threads (one per processor) for the
        // whole test, starving the continuations of the restarts under test and of every other
        // test running in parallel.
        var callers = Enumerable.Range(0, 6).Select(index => Task.Factory.StartNew(
            () =>
            {
                while (!stopCallers.IsCancellationRequested)
                {
                    try
                    {
                        if (engine.Host.TypedServices.Resolve<ICalculator>(ServiceId) is { } proxy)
                        {
                            var sum = index % 2 == 0 ? proxy.Add(1, 1) : proxy.AddAsync(1, 1, CancellationToken.None).GetAwaiter().GetResult();
                            Assert.Equal(2, sum);
                            Interlocked.Increment(ref successes);
                        }

                        using var binary = engine.Host.Services.InvokeBinaryAsync("test.bytes", "echo", new byte[] { 1, 2, 3 }).AsTask().GetAwaiter().GetResult();
                        if (binary.IsSuccess)
                        {
                            Assert.Equal([1, 2, 3], binary.Payload.ToArray());
                            Interlocked.Increment(ref successes);
                        }
                        else if (IsExpected(binary.ErrorCode))
                        {
                            Interlocked.Increment(ref expectedFailures);
                        }
                        else
                        {
                            unexpected.Enqueue($"binary: [{binary.ErrorCode}] {binary.Message}");
                        }
                    }
                    catch (TypedServiceException exception) when (IsExpected(exception.ErrorCode))
                    {
                        Interlocked.Increment(ref expectedFailures);
                    }
                    catch (Exception exception)
                    {
                        unexpected.Enqueue(exception.ToString());
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        var received = 0;
        var reader = Task.Run(async () =>
        {
            while (!stopCallers.IsCancellationRequested)
            {
                var subscription = engine.Host.Streams.Subscribe<int>("test.ticks");
                if (subscription is null)
                {
                    await Task.Delay(5);
                    continue;
                }

                try
                {
                    await foreach (var _ in subscription.ReadAllAsync(stopCallers.Token))
                    {
                        Interlocked.Increment(ref received);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (subscription.Completion?.ErrorCode is not (PluginErrorCodes.StreamRemoved or PluginErrorCodes.StreamCompleted))
                {
                    unexpected.Enqueue("stream: " + subscription.Completion?.ErrorCode);
                }
            }
        });

        for (var cycle = 0; cycle < 10; cycle++)
        {
            await Task.Delay(30);
            var stop = await engine.StopAsync(providerId).WaitAsync(TestTimeout);
            Assert.True(stop.IsSuccess, $"cycle {cycle} stop: [{stop.ErrorCode}] {stop.Message}");
            Assert.Empty(engine.Host.Services.GetServices());
            Assert.Empty(engine.Host.Streams.GetStreams());

            // Instances are terminal: a new start needs a new registration (what the loader does on restart).
            var unregister = await engine.UnregisterAsync(providerId).WaitAsync(TestTimeout);
            Assert.True(unregister.IsSuccess, $"cycle {cycle} unregister: [{unregister.ErrorCode}] {unregister.Message}");
            Assert.True(engine.Register(Create(providerId.Value), new TestPluginFactory(plugin)).IsSuccess);
            Assert.True(engine.MarkValidated(providerId).IsSuccess);
            var start = await engine.StartAsync(providerId).WaitAsync(TestTimeout);
            Assert.True(start.IsSuccess, $"cycle {cycle} start: [{start.ErrorCode}] {start.Message}");
        }

        await Task.Delay(30);
        stopCallers.Cancel();
        await Task.WhenAll(callers).WaitAsync(TestTimeout);
        await reader.WaitAsync(TestTimeout);

        Assert.Empty(unexpected);
        Assert.True(successes > 0, "calls succeeded between restarts");
        Assert.True(expectedFailures > 0, "the restarts were observed by callers");
        Assert.True(received > 0, "stream items were received");
        Assert.True((await engine.StopAsync(providerId)).IsSuccess);
        Assert.Equal(11, plugin.InitializeCount);
        Assert.Equal(11, plugin.ShutdownCount);
        Assert.Empty(engine.Host.Services.GetServices());
        Assert.Empty(engine.Host.Streams.GetStreams());
    }

    internal interface IInternalContract
    {
        void Run();
    }

    private sealed class InternalImplementation : IInternalContract
    {
        public void Run()
        {
        }
    }

    public interface IStaticContract
    {
        static abstract int Create();

        void Run();
    }

}
