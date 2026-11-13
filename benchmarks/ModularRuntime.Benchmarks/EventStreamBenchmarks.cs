using BenchmarkDotNet.Attributes;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;

namespace ModularRuntime.Benchmarks;

/// <summary>
/// Continuous delivery: the event bus (one awaited handler per delivery) against streams
/// (bounded channels), for one and four subscribers, and the sustained throughput of a stream
/// with concurrent producers.
/// </summary>
[MemoryDiagnoser]
public class EventStreamBenchmarks
{
    public readonly record struct Tick(long Sequence, double Value);

    private PluginEngine _engine = null!;
    private JsonPayload _payload = JsonPayload.Null;
    private IStreamProducer<Tick> _typedStream = null!;
    private IStreamProducer<JsonPayload> _jsonStream = null!;
    private readonly List<IStreamSubscription<Tick>> _typedSubscriptions = [];
    private readonly List<IStreamSubscription<JsonPayload>> _jsonSubscriptions = [];
    private readonly List<Task> _readers = [];
    private CancellationTokenSource _stop = new();

    [Params(1, 4)]
    public int Subscribers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _engine = new PluginEngine();
        _payload = JsonPayload.Parse("""{"sequence":1,"value":0.5}""");
        _stop = new CancellationTokenSource();
        for (var index = 0; index < Subscribers; index++)
        {
            _engine.Host.Events.Subscribe("bench.topic", (_, _) => ValueTask.CompletedTask);
        }

        _typedStream = _engine.Host.Streams.Create<Tick>(new StreamDefinition("bench.typed", "Typed", new Version(1, 0)) { DefaultCapacity = 1024 });
        _jsonStream = _engine.Host.Streams.Create<JsonPayload>(new StreamDefinition("bench.json", "Json", new Version(1, 0)) { DefaultCapacity = 1024 });
        for (var index = 0; index < Subscribers; index++)
        {
            var typed = _engine.Host.Streams.Subscribe<Tick>("bench.typed")!;
            var json = _engine.Host.Streams.Subscribe<JsonPayload>("bench.json")!;
            _typedSubscriptions.Add(typed);
            _jsonSubscriptions.Add(json);
            _readers.Add(Task.Run(() => DrainAsync(typed, _stop.Token)));
            _readers.Add(Task.Run(() => DrainAsync(json, _stop.Token)));
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _stop.Cancel();
        await Task.WhenAll(_readers);
        await _engine.DisposeAsync();
    }

    [Benchmark(Baseline = true, Description = "Event bus: PublishAsync one JSON event")]
    public ValueTask<EventPublishResult> EventBusPublish() => _engine.Host.Events.PublishAsync("bench.topic", _payload);

    [Benchmark(Description = "Stream<JsonPayload>: WriteAsync one item")]
    public ValueTask<bool> JsonStreamWrite() => _jsonStream.WriteAsync(_payload);

    [Benchmark(Description = "Stream<Tick struct>: WriteAsync one item")]
    public ValueTask<bool> TypedStreamWrite() => _typedStream.WriteAsync(new Tick(1, 0.5));

    [Benchmark(Description = "Stream<Tick struct>: TryWrite one item")]
    public bool TypedStreamTryWrite() => _typedStream.TryWrite(new Tick(1, 0.5));

    [Benchmark(Description = "Stream<Tick struct>: 10 000 items from 4 concurrent producers")]
    public async Task TypedStreamConcurrent()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var index = 0; index < 2500; index++)
            {
                await _typedStream.WriteAsync(new Tick(index, 0.5));
            }
        })));
    }

    private static async Task DrainAsync<T>(IStreamSubscription<T> subscription, CancellationToken stop)
    {
        try
        {
            await foreach (var _ in subscription.ReadAllAsync(stop))
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Registration and removal costs: service registration, stream creation and subscription.</summary>
[MemoryDiagnoser]
public class RegistrationBenchmarks
{
    private PluginEngine _engine = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup() => _engine = new PluginEngine();

    [GlobalCleanup]
    public Task Cleanup() => _engine.DisposeAsync().AsTask();

    [Benchmark(Description = "Register + dispose a JSON service")]
    public async Task JsonService()
    {
        var registration = _engine.Host.Services.Register(
            new PluginServiceDefinition($"bench.s{_counter++}", "S", new Version(1, 0), ["echo"]),
            (request, _) => ValueTask.FromResult(PluginResponse.Success(request.Payload)));
        await registration.DisposeAsync();
    }

    [Benchmark(Description = "Register + dispose a typed service (proxy type cached)")]
    public async Task TypedService()
    {
        var registration = _engine.Host.TypedServices.Register<ICalculator>(new TypedServiceDefinition($"bench.t{_counter++}", "T", new Version(1, 0)), new Calculator());
        _ = _engine.Host.TypedServices.Resolve<ICalculator>(registration.Descriptor.ServiceId);
        await registration.DisposeAsync();
    }

    [Benchmark(Description = "Create a stream + subscribe + dispose both")]
    public async Task Stream()
    {
        var stream = _engine.Host.Streams.Create<int>(new StreamDefinition($"bench.st{_counter++}", "St", new Version(1, 0)));
        var subscription = _engine.Host.Streams.Subscribe<int>(stream.Descriptor.StreamId)!;
        await subscription.DisposeAsync();
        await stream.DisposeAsync();
    }

    private sealed class Calculator : ICalculator
    {
        public int Add(int a, int b) => a + b;

        public Task<int> AddAsync(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a + b);

        public ValueTask<int> AddValueAsync(int a, int b) => new(a + b);

        public Summary Describe(Summary summary) => summary;

        public Bag Keep(Bag bag) => bag;

        public object Pass(object value) => value;
    }
}
