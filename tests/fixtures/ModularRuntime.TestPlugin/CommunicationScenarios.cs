using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntimeTests.Contracts;

namespace ModularRuntime.TestPlugin;

// Fixture plugins for real AssemblyLoadContext tests of typed services, binary services, and
// streams (plugin API 0.4). The shared contract assembly ModularRuntimeTests.Contracts is declared
// in plugin.json; every other type here is private to this plugin's load context.

/// <summary>A type private to the provider plugin, returned across the typed boundary on request.</summary>
public sealed class PrivateBox
{
    public int Value { get; init; }
}

/// <summary>A type private to the consumer plugin, hidden inside a contract record on request.</summary>
public sealed class ConsumerBox
{
    public string Note { get; init; } = "private";
}

/// <summary>Provides <c>test.alc.counter</c> (typed, <see cref="ICounterService"/>).</summary>
public sealed class TypedProviderScenarioPlugin : IPlugin, ICounterService
{
    private int _count;

    public int Count => _count;

    public Action? Callback { get; set; }

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.TypedServices.Register<ICounterService>(new TypedServiceDefinition("test.alc.counter", "ALC counter", new Version(1, 0)), this);
        context.Services.Register(
            new PluginServiceDefinition("test.alc.typed-provider", "ALC typed provider", new Version(1, 0), ["invoke-callback"]),
            (_, _) =>
            {
                // Invokes whatever callback the consumer handed over; the outcome is reported as text.
                try
                {
                    if (Callback is null)
                    {
                        return ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromNode(new JsonObject { ["status"] = "NoCallback" })));
                    }

                    Callback();
                    return ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromNode(new JsonObject { ["status"] = "Succeeded" })));
                }
                catch (TypedServiceException exception)
                {
                    return ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromNode(new JsonObject { ["status"] = "TypedServiceException", ["errorCode"] = exception.ErrorCode, ["message"] = exception.Message })));
                }
            });
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public int Increment(int by) => Interlocked.Add(ref _count, by);

    public CounterEnvelope Echo(CounterEnvelope envelope) => envelope;

    public CounterEnvelope PrivateEnvelope() => new("private", new PrivateBox { Value = _count });

    public async Task<int> IncrementAsync(int by, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return Increment(by);
    }

    public Task WaitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

    public CounterSnapshot Snapshot() => new(_count, "test.alc.provider");

    public object Opaque() => new PrivateBox { Value = _count };

    public void Fail() => throw new CounterException("contract failure");

    public void FailPrivately() => throw new PluginFailureException("private failure");

    public void FailWithFramework() => throw new InvalidOperationException("framework failure");

    public void FailWithPrivateInner() => throw new InvalidOperationException("framework outer", new PluginFailureException("private inner"));
}

/// <summary>
/// Resolves <c>test.alc.counter</c> at start-up and exposes <c>test.alc.typed-consumer</c> (JSON) whose
/// operations call the retained proxy and report the outcome as text.
/// </summary>
public sealed class TypedConsumerScenarioPlugin : IPlugin
{
    private ITypedServiceClient<ICounterService>? _client;
    private IPluginContext? _context;
    private string? _resolveError;
    private int _callbacks;
    private Exception? _kept;
    private CounterSnapshot? _keptSnapshot;
    private CounterEnvelope? _keptEnvelope;
    private ICallbackRegistration<Action>? _registration;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        try
        {
            _client = context.TypedServices.GetClient<ICounterService>("test.alc.counter");
        }
        catch (PluginRuntimeException exception)
        {
            _resolveError = exception.ErrorCode + ": " + exception.Message;
        }

        context.Services.Register(
            new PluginServiceDefinition("test.alc.typed-consumer", "ALC typed consumer", new Version(1, 0), ["call", "call-async", "snapshot", "keep-snapshot", "kept", "opaque", "envelope-private", "envelope-shared", "envelope-from-provider", "fail", "fail-privately", "fail-framework", "fail-private-inner", "forget", "callback", "register-callback", "clear-callback", "wait", "status"]),
            HandleAsync);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnCallback() => _callbacks++;

    private async ValueTask<PluginResponse> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == "status")
        {
            return Reply(new JsonObject { ["resolveError"] = _resolveError, ["available"] = _client?.IsAvailable ?? false, ["callbacks"] = _callbacks });
        }

        if (_client is null)
        {
            return PluginResponse.Failure("no_client", _resolveError ?? "The typed service was not available at start-up.");
        }

        var service = _client.Service;
        try
        {
            switch (request.Operation)
            {
                case "call":
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["count"] = service.Increment(1), ["available"] = _client.IsAvailable });
                case "call-async":
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["count"] = await service.IncrementAsync(1, cancellationToken).ConfigureAwait(false) });
                case "snapshot":
                    var snapshot = service.Snapshot();
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["count"] = snapshot.Count, ["provider"] = snapshot.Provider, ["type"] = snapshot.GetType().Assembly.GetName().Name });
                case "keep-snapshot":
                    _keptSnapshot = service.Snapshot();
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["count"] = _keptSnapshot.Count });
                case "kept":
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["snapshot"] = _keptSnapshot?.Count, ["envelope"] = _keptEnvelope?.Label });
                case "opaque":
                    var opaque = service.Opaque();
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["type"] = opaque.GetType().FullName });
                case "envelope-private":
                    // A contract record whose object field hides an object private to this plugin.
                    service.Echo(new CounterEnvelope("private", new ConsumerBox()));
                    return PluginResponse.Failure("unexpected", "Echo accepted a private payload.");
                case "envelope-shared":
                    _keptEnvelope = service.Echo(new CounterEnvelope("shared", new List<string> { "a", "b" }));
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["label"] = _keptEnvelope.Label });
                case "envelope-from-provider":
                    var fromProvider = service.PrivateEnvelope();
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["payloadType"] = fromProvider.Payload?.GetType().FullName });
                case "register-callback":
                    // The supported way to hand a callback to another plugin: a registration owned by
                    // this plugin whose shared wrapper holds nothing of it.
                    _registration = _context!.TypedServices.RegisterCallback<Action>("counter-callback", OnCallback);
                    service.Callback = _registration.Callback;
                    service.Callback();
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["callbacks"] = _callbacks, ["sameObject"] = ReferenceEquals(_registration.Callback.Target, this) });
                case "fail":
                    service.Fail();
                    return PluginResponse.Failure("unexpected", "Fail did not throw.");
                case "fail-privately":
                    service.FailPrivately();
                    return PluginResponse.Failure("unexpected", "FailPrivately did not throw.");
                case "fail-framework":
                    try
                    {
                        service.FailWithFramework();
                        return PluginResponse.Failure("unexpected", "FailWithFramework did not throw.");
                    }
                    catch (InvalidOperationException exception)
                    {
                        // Kept on purpose: the test checks what holding a provider's exception retains.
                        _kept = exception;
                        return Reply(new JsonObject { ["status"] = "FrameworkException", ["message"] = exception.Message, ["hasProviderFrames"] = exception.StackTrace?.Contains(nameof(TypedProviderScenarioPlugin), StringComparison.Ordinal) ?? false });
                    }

                case "fail-private-inner":
                    service.FailWithPrivateInner();
                    return PluginResponse.Failure("unexpected", "FailWithPrivateInner did not throw.");
                case "forget":
                    _kept = null;
                    return Reply(new JsonObject { ["status"] = "Succeeded" });
                case "callback":
                    service.Callback = OnCallback;
                    service.Callback();
                    return Reply(new JsonObject { ["status"] = "Succeeded", ["callbacks"] = _callbacks });
                case "clear-callback":
                    service.Callback = null;
                    return Reply(new JsonObject { ["status"] = "Succeeded" });
                case "wait":
                    await service.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return Reply(new JsonObject { ["status"] = "Succeeded" });
                default:
                    return PluginResponse.Failure("unexpected", request.Operation);
            }
        }
        catch (CounterException exception)
        {
            return Reply(new JsonObject { ["status"] = "ContractException", ["message"] = exception.Message });
        }
        catch (TypedServiceException exception)
        {
            return Reply(new JsonObject { ["status"] = "TypedServiceException", ["errorCode"] = exception.ErrorCode, ["exceptionType"] = exception.Diagnostic?.ExceptionType, ["message"] = exception.Message });
        }
        catch (OperationCanceledException)
        {
            return Reply(new JsonObject { ["status"] = "Canceled" });
        }
    }

    private static PluginResponse Reply(JsonObject node) => PluginResponse.Success(JsonPayload.FromNode(node));
}

/// <summary>
/// Creates the typed stream <c>test.alc.stream</c> (<see cref="CounterSnapshot"/>) and the binary stream
/// <c>test.alc.bytes</c>; <c>test.alc.stream-producer</c>/<c>write</c> writes <c>{"count":n}</c> items.
/// </summary>
public sealed class StreamProducerScenarioPlugin : IPlugin
{
    private IStreamProducer<CounterSnapshot>? _snapshots;
    private IStreamProducer<ReadOnlyMemory<byte>>? _bytes;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _snapshots = context.Streams.Create<CounterSnapshot>(new StreamDefinition("test.alc.stream", "ALC stream", new Version(1, 0)) { DefaultCapacity = 16 });
        _bytes = context.Streams.Create<ReadOnlyMemory<byte>>(new StreamDefinition("test.alc.bytes", "ALC bytes", new Version(1, 0)));
        context.Services.Register(
            new PluginServiceDefinition("test.alc.stream-producer", "ALC stream producer", new Version(1, 0), ["write", "complete"]),
            HandleAsync);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask<PluginResponse> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == "complete")
        {
            _snapshots!.Complete();
            _bytes!.Complete();
            return PluginResponse.Success();
        }

        var count = int.Parse(request.Payload.GetString("count") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var written = 0;
        for (var index = 1; index <= count; index++)
        {
            if (await _snapshots!.WriteAsync(new CounterSnapshot(index, "test.alc.stream-producer"), cancellationToken).ConfigureAwait(false))
            {
                written++;
            }

            await _bytes!.WriteAsync(new byte[] { (byte)index, (byte)(index * 2) }, cancellationToken).ConfigureAwait(false);
        }

        return PluginResponse.Success(JsonPayload.FromNode(new JsonObject { ["written"] = written, ["sequence"] = _snapshots!.Sequence }));
    }
}

/// <summary>
/// Subscribes to <c>test.alc.stream</c> and <c>test.alc.bytes</c> at start-up, reads in the background,
/// and reports through <c>test.alc.stream-subscriber</c>/<c>status</c>.
/// </summary>
public sealed class StreamSubscriberScenarioPlugin : IPlugin
{
    private IStreamSubscription<CounterSnapshot>? _snapshots;
    private IStreamSubscription<ReadOnlyMemory<byte>>? _bytes;
    private int _received;
    private int _bytesReceived;
    private int _lastCount;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _snapshots = context.Streams.Subscribe<CounterSnapshot>("test.alc.stream", new StreamSubscribeOptions { Capacity = 8 });
        _bytes = context.Streams.Subscribe<ReadOnlyMemory<byte>>("test.alc.bytes");
        if (_snapshots is not null)
        {
            context.Resources.Track("snapshot-reader", new ReaderTask(ReadSnapshotsAsync(context.StoppingToken)));
        }

        if (_bytes is not null)
        {
            context.Resources.Track("bytes-reader", new ReaderTask(ReadBytesAsync(context.StoppingToken)));
        }

        context.Services.Register(
            new PluginServiceDefinition("test.alc.stream-subscriber", "ALC stream subscriber", new Version(1, 0), ["status"]),
            (_, _) => ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromNode(new JsonObject
            {
                ["subscribed"] = _snapshots is not null,
                ["received"] = Volatile.Read(ref _received),
                ["bytesReceived"] = Volatile.Read(ref _bytesReceived),
                ["lastCount"] = Volatile.Read(ref _lastCount),
                ["dropped"] = _snapshots?.DroppedCount ?? -1,
                ["completion"] = _snapshots?.Completion?.ErrorCode,
            }))));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReadSnapshotsAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var message in _snapshots!.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _received);
                Volatile.Write(ref _lastCount, message.Item.Count);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadBytesAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var message in _bytes!.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                Interlocked.Add(ref _bytesReceived, message.Item.Length);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ReaderTask(Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await task.ConfigureAwait(false);
    }
}

/// <summary>Provides the binary service <c>test.alc.binary</c> with <c>reverse</c> and <c>wait</c>.</summary>
public sealed class BinaryProviderScenarioPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Services.RegisterBinary(
            new BinaryServiceDefinition("test.alc.binary", "ALC binary", new Version(1, 0), ["reverse", "wait"]) { MaxRequestBytes = 1024 },
            HandleAsync);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async ValueTask<BinaryResponse> HandleAsync(BinaryRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == "wait")
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return BinaryResponse.Empty;
        }

        var reversed = request.Payload.ToArray();
        Array.Reverse(reversed);
        return BinaryResponse.Success(reversed);
    }
}
