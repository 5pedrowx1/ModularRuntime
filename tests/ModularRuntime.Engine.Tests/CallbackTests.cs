using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

public delegate string Formatter(string text, int count);

public delegate Task<int> AsyncWorker(int input, CancellationToken cancellationToken);

public interface ICallbackSink
{
    void Attach(Action? callback);

    void SetFormatter(Formatter? formatter);

    object? Relay(Func<object?, object?> callback, object? argument);
}

public sealed class CallbackTests
{
    private static readonly Type[] PrivateTypes = [typeof(PrivateValue), typeof(PrivateTargets), typeof(PrivateException)];

    private sealed class Sink : ICallbackSink
    {
        public Action? Callback { get; private set; }

        public Formatter? Formatter { get; private set; }

        public void Attach(Action? callback) => Callback = callback;

        public void SetFormatter(Formatter? formatter) => Formatter = formatter;

        public object? Relay(Func<object?, object?> callback, object? argument) => callback(argument);
    }

    private static PluginEngine CreateEngine(RuntimeLimits? limits = null, IRuntimeLogger? logger = null) =>
        new(logger, limits, new ExcludingPolicy(PrivateTypes));

    [Fact]
    public async Task RegisteredCallback_IsASharedWrapper_ThatForwardsToTheTarget()
    {
        await using var engine = CreateEngine();
        var calls = 0;
        var registration = engine.Host.TypedServices.RegisterCallback<Action>("counter", () => calls++);

        Assert.Equal(ResourceKind.Callback, registration.Kind);
        Assert.Equal("counter", registration.Name);
        Assert.True(registration.IsActive);
        Assert.NotNull(registration.Callback);
        registration.Callback();
        Assert.Equal(1, calls);
        Assert.Null(registration.Callback.Method.DeclaringType);
        Assert.IsType<Services.TypedCallContext>(registration.Callback.Target);
        Assert.Contains(engine.Host.Resources.GetResources(), r => r.Kind == ResourceKind.Callback && r.Name == "counter");

        // The wrapper passes the shared-value check where the raw delegate does not.
        var sink = new Sink();
        engine.Host.TypedServices.Register<ICallbackSink>(new TypedServiceDefinition("test.sink", "Sink", new Version(1, 0)), sink);
        var proxy = engine.Host.TypedServices.Resolve<ICallbackSink>("test.sink")!;
        var targets = new PrivateTargets();
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => proxy.Attach(targets.Handle)).ErrorCode);
        var wrapped = engine.Host.TypedServices.RegisterCallback<Action>("private-target", targets.Handle);
        proxy.Attach(wrapped.Callback);
        sink.Callback!();
        Assert.Equal(1, targets.Calls);

        // A custom delegate type with arguments and a result.
        var formatter = engine.Host.TypedServices.RegisterCallback<Formatter>("format", (text, count) => $"{text}×{count}");
        proxy.SetFormatter(formatter.Callback);
        Assert.Equal("a×3", sink.Formatter!("a", 3));
    }

    [Fact]
    public async Task Release_InvalidatesTheWrapper_AndDropsTheTarget()
    {
        await using var engine = CreateEngine();
        var (registration, reference) = RegisterWithTarget(engine);
        registration.Callback();

        await registration.DisposeAsync();

        Assert.False(registration.IsActive);
        var released = Assert.Throws<TypedServiceException>(() => registration.Callback());
        Assert.Equal(PluginErrorCodes.CallbackReleased, released.ErrorCode);
        Assert.Null(released.ServiceId);
        Assert.True(GcDiagnostics.WaitForCollection(reference), "the registered target must be collectible after release while the wrapper is still held");
        GC.KeepAlive(registration.Callback);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ICallbackRegistration<Action> Registration, WeakReference Target) RegisterWithTarget(PluginEngine engine)
    {
        var targets = new PrivateTargets();
        return (engine.Host.TypedServices.RegisterCallback<Action>("target", targets.Handle), new WeakReference(targets));
    }

    [Fact]
    public async Task OwnerStop_ReleasesCallbacks_TheHolderGetsCallbackReleased_AndRetainsNothing()
    {
        await using var engine = CreateEngine();
        var sink = new Sink();
        engine.Host.TypedServices.Register<ICallbackSink>(new TypedServiceDefinition("test.sink", "Sink", new Version(1, 0)), sink);
        var reference = await StartConsumerAsync(engine);
        sink.Callback!();

        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.consumer"))).IsSuccess);

        var released = Assert.Throws<TypedServiceException>(() => sink.Callback!());
        Assert.Equal(PluginErrorCodes.CallbackReleased, released.ErrorCode);
        Assert.Empty(engine.GetResources(PluginId.Parse("com.test.consumer")));
        Assert.True(GcDiagnostics.WaitForCollection(reference), "the consumer's target must be collectible while the provider keeps the wrapper");
        GC.KeepAlive(sink.Callback);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StartConsumerAsync(PluginEngine engine)
    {
        var reference = new WeakReference(null);
        await StartPluginAsync(engine, "com.test.consumer", c =>
        {
            var targets = new PrivateTargets();
            reference.Target = targets;
            var registration = c.TypedServices.RegisterCallback<Action>("consumer-callback", targets.Handle);
            c.TypedServices.Resolve<ICallbackSink>("test.sink")!.Attach(registration.Callback);
            return Task.CompletedTask;
        });
        return reference;
    }

    [Fact]
    public async Task ArgumentsAndResults_OfCallbacks_AreChecked_AndExceptionsTranslated()
    {
        await using var engine = CreateEngine();
        var sink = new Sink();
        engine.Host.TypedServices.Register<ICallbackSink>(new TypedServiceDefinition("test.sink", "Sink", new Version(1, 0)), sink);
        var proxy = engine.Host.TypedServices.Resolve<ICallbackSink>("test.sink")!;
        object? received = null;
        Func<object?, object?>? behavior = null;
        var registration = engine.Host.TypedServices.RegisterCallback<Func<object?, object?>>("relay", argument =>
        {
            received = argument;
            return behavior is null ? argument : behavior(argument);
        });

        // Through the provider: the provider's private object is refused on the way in (by the wrapper).
        Assert.Equal("ok", proxy.Relay(registration.Callback, "ok"));
        Assert.Equal("ok", received);
        var inbound = Assert.Throws<TypedServiceException>(() => registration.Callback(new PrivateValue()));
        Assert.Equal(PluginErrorCodes.ValueNotShared, inbound.ErrorCode);
        Assert.Contains("Callback 'relay', argument 'arg'", inbound.Message, StringComparison.Ordinal);
        Assert.Equal("ok", received);

        // The callback's own result is refused on the way out.
        behavior = _ => new PrivateValue();
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<TypedServiceException>(() => registration.Callback("x")).ErrorCode);

        // Exceptions: private ones become handler_failed, shared ones propagate.
        behavior = _ => throw new PrivateException("private");
        var translated = Assert.Throws<TypedServiceException>(() => registration.Callback("x"));
        Assert.Equal(PluginErrorCodes.HandlerFailed, translated.ErrorCode);
        Assert.Equal(typeof(PrivateException).FullName, translated.Diagnostic!.ExceptionType);
        behavior = _ => throw new CalculatorException("shared");
        Assert.Equal("shared", Assert.Throws<CalculatorException>(() => registration.Callback("x")).Message);
    }

    [Fact]
    public async Task AsynchronousCallbacks_AreAdmitted_Canceled_AndDrained_WhenTheOwnerStops()
    {
        await using var engine = CreateEngine(new RuntimeLimits { DrainTimeout = TimeSpan.FromSeconds(5) });
        AsyncWorker? wrapper = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartPluginAsync(engine, "com.test.consumer", c =>
        {
            var registration = c.TypedServices.RegisterCallback<AsyncWorker>("worker", async (input, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return input;
            });
            wrapper = registration.Callback;
            return Task.CompletedTask;
        });

        var call = wrapper!(1, CancellationToken.None);
        await entered.Task.WaitAsync(TestTimeout);
        var stop = engine.StopAsync(PluginId.Parse("com.test.consumer"));

        var canceled = await Assert.ThrowsAsync<TypedServiceException>(() => call.WaitAsync(TestTimeout));
        Assert.Equal(PluginErrorCodes.OperationCanceled, canceled.ErrorCode);
        Assert.True((await stop.WaitAsync(TestTimeout)).IsSuccess);
        var afterStop = wrapper(2, CancellationToken.None);
        Assert.True(afterStop.IsFaulted);
        Assert.Equal(PluginErrorCodes.CallbackReleased, (await Assert.ThrowsAsync<TypedServiceException>(() => afterStop)).ErrorCode);
    }

    [Fact]
    public async Task ConcurrentInvocations_DuringRelease_SeeOnlyExpectedOutcomes()
    {
        await using var engine = CreateEngine();
        var calls = 0;
        var registration = engine.Host.TypedServices.RegisterCallback<Action>("counter", () => Interlocked.Increment(ref calls));
        var wrapper = registration.Callback;
        var outcomes = new int[2];

        // Each invoker runs until it has seen the release a few times (bounded), so the release
        // always happens under traffic.
        var invokers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var refused = 0;
            for (var round = 0; round < 1_000_000 && refused < 10; round++)
            {
                try
                {
                    wrapper();
                    Interlocked.Increment(ref outcomes[0]);
                }
                catch (TypedServiceException exception) when (exception.ErrorCode is PluginErrorCodes.CallbackReleased or PluginErrorCodes.CallbackUnavailable)
                {
                    refused++;
                    Interlocked.Increment(ref outcomes[1]);
                }
            }
        })).ToArray();
        await Task.Delay(20);
        await registration.DisposeAsync();
        await Task.WhenAll(invokers).WaitAsync(TestTimeout);

        Assert.Equal(outcomes[0], calls);
        Assert.Equal(80, outcomes[1]);
        Assert.True(outcomes[0] > 0, "invocations before the release succeeded");
    }

    [Fact]
    public async Task InvalidCallbackRegistrations_AreRefused()
    {
        await using var engine = CreateEngine();
        var typed = engine.Host.TypedServices;

        Assert.Throws<ArgumentNullException>(() => typed.RegisterCallback<Action>("x", null!));
        Assert.Throws<ArgumentException>(() => typed.RegisterCallback<Action>(string.Empty, static () => { }));
        var signature = Assert.Throws<PluginRuntimeException>(() => typed.RegisterCallback<Action<PrivateValue>>("private-arg", static _ => { }));
        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, signature.ErrorCode);
        Assert.Contains(nameof(PrivateValue), signature.Message, StringComparison.Ordinal);
        var result = Assert.Throws<PluginRuntimeException>(() => typed.RegisterCallback<Func<PrivateValue>>("private-result", static () => new PrivateValue()));
        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, result.ErrorCode);
        var hidden = Assert.Throws<PluginRuntimeException>(() => typed.RegisterCallback<HiddenDelegate>("hidden", static () => { }));
        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, hidden.ErrorCode);
        Assert.Empty(engine.Host.Resources.GetResources());

        // In Warn mode the private signature is accepted with a warning; in None silently.
        var logger = new CollectingLogger();
        await using var warning = CreateEngine(new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, logger);
        Assert.True(warning.Host.TypedServices.RegisterCallback<Action<PrivateValue>>("private-arg", static _ => { }).IsActive);
        Assert.NotNull(logger.Find(LogLevel.Warning, PluginErrorCodes.TypedServiceInvalidContract));
    }

    private delegate void HiddenDelegate();

    [Fact]
    public async Task StoppedOwner_CannotRegister_AndDisposedRuntime_RefusesInvocations()
    {
        var engine = CreateEngine();
        IPluginContext? context = null;
        await StartPluginAsync(engine, "com.test.consumer", c =>
        {
            context = c;
            return Task.CompletedTask;
        });
        var registration = engine.Host.TypedServices.RegisterCallback<Action>("host", static () => { });
        Assert.True((await engine.StopAsync(PluginId.Parse("com.test.consumer"))).IsSuccess);

        Assert.Equal(PluginErrorCodes.ScopeClosed, Assert.Throws<PluginRuntimeException>(() => context!.TypedServices.RegisterCallback<Action>("late", static () => { })).ErrorCode);
        await engine.DisposeAsync();
        var disposed = Assert.Throws<TypedServiceException>(() => registration.Callback());
        Assert.True(disposed.ErrorCode is PluginErrorCodes.EngineDisposed or PluginErrorCodes.CallbackReleased, disposed.ErrorCode);
    }
}
