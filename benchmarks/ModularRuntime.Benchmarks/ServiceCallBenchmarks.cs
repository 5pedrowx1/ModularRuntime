using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Sdk;

namespace ModularRuntime.Benchmarks;

public interface ICalculator
{
    int Add(int a, int b);

    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken);

    ValueTask<int> AddValueAsync(int a, int b);

    /// <summary>A closed contract record (sealed, fields of closed types): the proxy emits no check for it.</summary>
    Summary Describe(Summary summary);

    /// <summary>An open contract record (an interface-typed field): the instance is inspected, one hop.</summary>
    Bag Keep(Bag bag);

    /// <summary>An <c>object</c> parameter carrying a list of eight strings: the list is inspected element by element.</summary>
    object Pass(object value);
}

public sealed record Summary(long Count, string Provider);

public sealed record Bag(string Name, IReadOnlyList<string> Tags);

/// <summary>
/// One request/response through each mechanism, host to host inside one engine (no load
/// contexts involved: the costs measured are the runtime's own). "Direct" is the floor: the same
/// interface call on the same provider object without the runtime.
/// </summary>
/// <remarks>
/// <para>
/// Methodology. The provider's methods are marked <see cref="MethodImplOptions.NoInlining"/> and
/// the operands come from fields, so the direct call is a real call and the provider's work is
/// identical on every path: the proxies call the same methods through the same interface. The
/// direct call is still below what the harness can resolve (about 2 ns of workload loop against
/// 1.2 to 1.8 ns of empty loop, which BenchmarkDotNet subtracts and reports as 0 to 0.7 ns with
/// a <c>ZeroMeasurement</c> warning), so the baseline for the <c>Ratio</c> column is the
/// synchronous proxy call: ratios read as "times a bare proxy call", and the direct row is an
/// order of magnitude, not a number.
/// </para>
/// <para>
/// The two <see cref="ValueCheck"/> values do the same functional work: the proxy code does not
/// depend on the mode (which parameters and results carry a check is decided from the static
/// types alone), the same instances cross the boundary, and in <c>None</c> the check returns
/// after one mode test. The pair of each method runs back to back (<see cref="PairedOrderer"/>),
/// so that a change of the machine's state during the run disturbs one pair rather than one
/// whole column. What each of the three record/list rows exercises is asserted by
/// <c>SharedValueCheckTests.BenchmarkShapes_ExerciseTheChecksTheirDescriptionsClaim</c> in
/// the engine tests (the shapes are mirrored there).
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(PairedConfig))]
public class ServiceCallBenchmarks
{
    private sealed class Calculator : ICalculator
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Add(int a, int b) => a + b;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task<int> AddAsync(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a + b);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ValueTask<int> AddValueAsync(int a, int b) => new(a + b);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Summary Describe(Summary summary) => summary;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Bag Keep(Bag bag) => bag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object Pass(object value) => value;
    }

    /// <summary>Runs the <c>None</c> and <c>Reject</c> case of each method consecutively; the summary keeps the default grouping by parameter.</summary>
    private sealed class PairedOrderer : DefaultOrderer
    {
        public override IEnumerable<BenchmarkCase> GetExecutionOrder(ImmutableArray<BenchmarkCase> benchmarksCase, IEnumerable<BenchmarkLogicalGroupRule>? order = null) =>
            benchmarksCase
                .OrderBy(benchmark => benchmark.Descriptor.MethodIndex)
                .ThenBy(benchmark => benchmark.Parameters.DisplayInfo, StringComparer.Ordinal);
    }

    private sealed class PairedConfig : ManualConfig
    {
        public PairedConfig() => WithOrderer(new PairedOrderer());
    }

    public sealed record AddRequest(int A, int B);

    public sealed record AddResponse(int Sum);

    private PluginEngine _engine = null!;
    private ICalculator _direct = null!;
    private ICalculator _proxy = null!;
    private IPluginServiceClient _jsonClient = null!;
    private IPluginServiceClient _binaryClient = null!;
    private JsonPayload _request = JsonPayload.Null;
    private byte[] _requestBytes = [];
    private int _a;
    private int _b;
    private readonly CancellationToken _cancellable = new CancellationTokenSource().Token;
    private readonly Summary _summary = new(1, "bench");
    private readonly Bag _bag = new("bench", new[] { "a", "b", "c", "d" }); // a string[] (closed): the scan stops at the field
    private readonly List<object> _list = ["a", "b", "c", "d", "e", "f", "g", "h"];

    [Params(TypedValueCheck.Reject, TypedValueCheck.None)]
    public TypedValueCheck ValueCheck { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _a = 2;
        _b = 3;
        _engine = new PluginEngine(limits: new RuntimeLimits { TypedValueCheck = ValueCheck });
        _direct = new Calculator();
        _engine.Host.TypedServices.Register<ICalculator>(new TypedServiceDefinition("bench.typed", "Typed", new Version(1, 0)), _direct);
        _proxy = _engine.Host.TypedServices.Resolve<ICalculator>("bench.typed")!;

        _engine.Host.Services.Register(
            "bench.json",
            "Json",
            new Version(1, 0),
            new PluginOperations().Add<AddRequest, AddResponse>("add", (request, _, _) => ValueTask.FromResult(new AddResponse(request.A + request.B))));
        _jsonClient = _engine.Host.Services.GetClient("bench.json")!;
        _request = PluginJson.ToPayload(new AddRequest(_a, _b));

        _engine.Host.Services.RegisterBinary(
            new BinaryServiceDefinition("bench.binary", "Binary", new Version(1, 0), ["add"]),
            (request, _) =>
            {
                var sum = BitConverter.ToInt32(request.Payload.Span) + BitConverter.ToInt32(request.Payload.Span[4..]);
                return ValueTask.FromResult(BinaryResponse.Success(BitConverter.GetBytes(sum)));
            });
        _binaryClient = _engine.Host.Services.GetClient("bench.binary")!;
        _requestBytes = [.. BitConverter.GetBytes(_a), .. BitConverter.GetBytes(_b)];

        // The run is meaningless if the floor or the proxies do not compute what the rows claim.
        if (_direct.Add(_a, _b) != 5 || _proxy.Add(_a, _b) != 5 || !ReferenceEquals(_proxy.Pass(_list), _list) || !ReferenceEquals(_proxy.Keep(_bag), _bag))
        {
            throw new InvalidOperationException("The benchmark fixture does not compute the expected results.");
        }
    }

    [GlobalCleanup]
    public Task Cleanup() => _engine.DisposeAsync().AsTask();

    [Benchmark(Description = "Direct interface call (no runtime)")]
    public int Direct() => _direct.Add(_a, _b);

    [Benchmark(Baseline = true, Description = "Typed service: sync call through proxy")]
    public int TypedSync() => _proxy.Add(_a, _b);

    [Benchmark(Description = "Typed service: Task<int> call through proxy")]
    public Task<int> TypedAsync() => _proxy.AddAsync(_a, _b, CancellationToken.None);

    [Benchmark(Description = "Typed service: Task<int> with a cancellable token")]
    public Task<int> TypedAsyncLinked() => _proxy.AddAsync(_a, _b, _cancellable);

    [Benchmark(Description = "Typed service: ValueTask<int> call through proxy")]
    public ValueTask<int> TypedValueTask() => _proxy.AddValueAsync(_a, _b);

    [Benchmark(Description = "Typed service: closed record argument and result (no inspection)")]
    public Summary TypedClosedRecord() => _proxy.Describe(_summary);

    [Benchmark(Description = "Typed service: open record (interface field) argument and result")]
    public Bag TypedOpenRecord() => _proxy.Keep(_bag);

    [Benchmark(Description = "Typed service: object argument carrying List<object> of 8 strings")]
    public object TypedListOfObject() => _proxy.Pass(_list);

    [Benchmark(Description = "Binary service: InvokeBinaryAsync (8 bytes)")]
    public async ValueTask<int> Binary()
    {
        using var result = await _binaryClient.InvokeBinaryAsync("add", _requestBytes);
        return BitConverter.ToInt32(result.Payload.Span);
    }

    [Benchmark(Description = "JSON service: InvokeAsync (raw payload)")]
    public async ValueTask<int> JsonRaw()
    {
        var result = await _jsonClient.InvokeAsync("add", _request);
        return result.Payload!.ByteCount;
    }

    [Benchmark(Description = "JSON service: SDK InvokeAsync<TRequest, TResponse>")]
    public async ValueTask<int> JsonTyped()
    {
        var result = await _engine.Host.Services.InvokeAsync<AddRequest, AddResponse>("bench.json", "add", new AddRequest(_a, _b));
        return result.Value!.Sum;
    }
}
