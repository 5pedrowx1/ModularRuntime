using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Engine.Tests;

// Types that play the role of shared contract types (they live in the test assembly, which the
// engine's default policy treats as shared) and of plugin-private types (excluded by policy).
public interface IShape
{
    string Name { get; }
}

public sealed class SharedShape : IShape
{
    public string Name => "shared";
}

/// <summary>Marked private through the policy.</summary>
public sealed class PrivateShape : IShape
{
    public string Name => "private";
}

/// <summary>A contract record whose <c>object</c> field only an inspection of the instance can judge.</summary>
public sealed record Box(string Label, object? Value);

/// <summary>A mutable graph node; cycles are expected.</summary>
public sealed class GraphNode
{
    public GraphNode? Next { get; set; }

    public object? Payload { get; set; }
}

public sealed record Tagged(string Name, IReadOnlyList<string> Tags);

public sealed record Keyed(IReadOnlyDictionary<string, object> Values);

public readonly record struct Pair(string Key, object? Value);

/// <summary>A shared class with a field whose static type is private: judged by its content.</summary>
public sealed class Holder
{
    public PrivateValue? Value { get; init; }
}

/// <summary>A contract exception type with a field of its own.</summary>
public sealed class CarrierException(string message) : Exception(message)
{
    public object? Payload { get; init; }
}

/// <summary>Marked private through the policy.</summary>
public sealed class PrivateComparer : IEqualityComparer<string>, IComparer<string>
{
    public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(string obj) => obj.ToUpperInvariant().GetHashCode(StringComparison.Ordinal);

    public int Compare(string? x, string? y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Marked private through the policy.</summary>
public sealed class PrivateMemoryManager : MemoryManager<byte>
{
    private readonly byte[] _buffer = new byte[16];

    public override Span<byte> GetSpan() => _buffer;

    public override MemoryHandle Pin(int elementIndex = 0) => default;

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>Marked private through the policy; its methods serve as delegate targets.</summary>
public sealed class PrivateTargets
{
    public int Calls { get; private set; }

    public void Handle() => Calls++;

    public static int Compute() => 42;
}

public interface IEcho
{
    object? Echo(object? value);

    Task<object?> EchoAsync(object? value);

    Box Wrap(Box box);

    /// <summary>Returns the token the callee received.</summary>
    CancellationToken Observe(CancellationToken cancellationToken);
}

public interface IPrivateSignature
{
    void Take(PrivateValue value);
}

public interface IPrivateResult
{
    Task<IReadOnlyList<PrivateValue>> ListAsync();
}

public interface IExcludedContract
{
    void Run();
}

public sealed class SharedValueCheckTests
{
    private static readonly Type[] PrivateTypes = [typeof(PrivateValue), typeof(PrivateShape), typeof(PrivateComparer), typeof(PrivateMemoryManager), typeof(PrivateTargets), typeof(PrivateException)];

    private sealed class EchoService : IEcho
    {
        public Func<object?, object?>? Behavior { get; set; }

        public object? Echo(object? value) => Behavior is null ? value : Behavior(value);

        public async Task<object?> EchoAsync(object? value)
        {
            await Task.Yield();
            return value;
        }

        public Box Wrap(Box box) => box;

        /// <summary>The last token received by <see cref="Observe"/>: a callee that keeps the token it was given.</summary>
        public CancellationToken Kept { get; private set; }

        public Action<CancellationToken>? OnObserve { get; set; }

        public CancellationToken Observe(CancellationToken cancellationToken)
        {
            Kept = cancellationToken;
            OnObserve?.Invoke(cancellationToken);
            return cancellationToken;
        }
    }

    private static PluginEngine CreateEngine(RuntimeLimits? limits = null, IRuntimeLogger? logger = null, params Type[] morePrivate) =>
        new(logger, limits, new ExcludingPolicy([.. PrivateTypes, .. morePrivate]));

    private static IEcho RegisterEcho(PluginEngine engine, EchoService? service = null)
    {
        engine.Host.TypedServices.Register<IEcho>(new TypedServiceDefinition("test.echo", "Echo", new Version(1, 0)), service ?? new EchoService());
        return engine.Host.TypedServices.Resolve<IEcho>("test.echo")!;
    }

    private static TypedServiceException Rejected(IEcho echo, object? value)
    {
        var exception = Assert.Throws<TypedServiceException>(() => echo.Echo(value));
        Assert.Equal(PluginErrorCodes.ValueNotShared, exception.ErrorCode);
        return exception;
    }

    private static TypedServiceException NotVerifiable(IEcho echo, object? value)
    {
        var exception = Assert.Throws<TypedServiceException>(() => echo.Echo(value));
        Assert.Equal(PluginErrorCodes.ValueNotVerifiable, exception.ErrorCode);
        return exception;
    }

    [Fact]
    public async Task Analyzer_ClassifiesTypesByConstruction()
    {
        await using var engine = CreateEngine();
        var analyzer = engine.SharedTypeAnalyzer;

        // Closed: nothing a value of the type references can be private.
        foreach (var closed in new[] { typeof(string), typeof(int), typeof(Guid), typeof(DateTimeOffset), typeof(decimal), typeof(string[]), typeof(string[,]), typeof(ImmutableArray<string>), typeof(KeyValuePair<string, string>), typeof((string, int)), typeof(JsonPayload), typeof(CounterLike), typeof(Version), typeof(Uri), typeof(List<string>), typeof(int?), typeof(WeakReference<string>) })
        {
            Assert.Equal(TypeVerdict.Closed, analyzer.GetShape(closed).Exact);
        }

        // Sealed closed types are closed statically too; non-sealed ones are open statically (a subclass may be anything).
        Assert.Equal(TypeVerdict.Closed, analyzer.GetShape(typeof(string)).Static);
        Assert.Equal(TypeVerdict.Closed, analyzer.GetShape(typeof(CounterLike)).Static);
        Assert.Equal(TypeVerdict.Open, analyzer.GetShape(typeof(List<string>)).Static);
        Assert.Equal(TypeVerdict.Open, analyzer.GetShape(typeof(Uri)).Static);

        // Open: the instance decides.
        foreach (var open in new[] { typeof(object), typeof(IShape), typeof(Box), typeof(Holder), typeof(Tagged), typeof(Keyed), typeof(Pair), typeof(Action), typeof(Exception), typeof(CarrierException), typeof(Dictionary<string, int>), typeof(HashSet<string>), typeof(ReadOnlyMemory<byte>), typeof(object[]), typeof(IShape[]), typeof(List<object>), typeof(GraphNode), typeof(Type), typeof(GCHandle), typeof(Lazy<string>) })
        {
            Assert.Equal(TypeVerdict.Open, analyzer.GetShape(open).Static);
        }

        Assert.Equal(TypeVerdict.Closed, analyzer.GetShape(typeof(object)).Exact);
        Assert.Equal(TypeVerdict.Closed, analyzer.GetShape(typeof(List<string>)).Exact);
        Assert.Equal(TypeVerdict.Open, analyzer.GetShape(typeof(Box)).Exact);

        // Private: the type, or a type it is constructed from, is refused by the policy.
        foreach (var @private in new[] { typeof(PrivateValue), typeof(List<PrivateValue>), typeof(PrivateValue[]), typeof(Dictionary<string, PrivateValue[]>), typeof(Action<PrivateValue>), typeof(KeyValuePair<string, PrivateShape>), typeof(PrivateValue[,]), typeof(WeakReference<PrivateValue>) })
        {
            var shape = analyzer.GetShape(@private);
            Assert.Equal(TypeVerdict.Private, shape.Static);
            Assert.NotNull(shape.Reason);
        }

        // The policy-free analyzer never says private; it treats collectible types as open.
        Assert.Equal(TypeVerdict.Closed, SharedTypeAnalyzer.ByConstruction.GetShape(typeof(PrivateValue)).Static);
        Assert.True(Services.InvocationEmitter.ShouldCheck(typeof(object)));
        Assert.True(Services.InvocationEmitter.ShouldCheck(typeof(Box)));
        Assert.False(Services.InvocationEmitter.ShouldCheck(typeof(string)));
        Assert.False(Services.InvocationEmitter.ShouldCheck(typeof(CounterLike)));
        Assert.False(Services.InvocationEmitter.ShouldCheck(typeof(int)));
    }

    public sealed record CounterLike(int Count, string Provider);

    /// <summary>
    /// A <c>CancellationToken</c> parameter is never inspected: the callee receives a token owned
    /// by the call, which follows the caller's token while the call runs and is detached when it
    /// ends. So a caller whose source carries plugin callbacks is not refused, and the inspection
    /// cost (one scan per asynchronous call, the whole source and its registration nodes for a live
    /// token) is not paid.
    /// </summary>
    [Fact]
    public async Task CancellationTokens_AreNotInspected_BecauseTheCalleeReceivesACallOwnedToken()
    {
        await using var engine = CreateEngine();
        var service = new EchoService();
        var echo = RegisterEcho(engine, service);
        var targets = new PrivateTargets();

        // During the call the callee's token follows the caller's, even though the caller's source carries a private callback.
        using var first = new CancellationTokenSource();
        using var firstRegistration = first.Token.Register(targets.Handle);
        service.OnObserve = token =>
        {
            Assert.False(token.IsCancellationRequested);
            first.Cancel();
            Assert.True(token.IsCancellationRequested);
        };
        var received = echo.Observe(first.Token);
        Assert.NotEqual(first.Token, received);
        Assert.True(received.IsCancellationRequested);
        Assert.Equal(1, targets.Calls);

        // After the call the callee's token is detached from the caller's source.
        service.OnObserve = null;
        using var second = new CancellationTokenSource();
        using var secondRegistration = second.Token.Register(targets.Handle);
        received = echo.Observe(second.Token);
        Assert.NotEqual(second.Token, received);
        second.Cancel();
        Assert.False(received.IsCancellationRequested);
        Assert.Equal(2, targets.Calls);

        // A token that cannot be canceled is replaced by the callee's scope token.
        Assert.NotEqual(CancellationToken.None, echo.Observe(CancellationToken.None));
    }

    /// <summary>
    /// A callee that keeps the token it received keeps nothing of the caller: not the caller's
    /// source, not the callbacks registered on it (a framework linked source would keep both
    /// through its registrations, even after being disposed).
    /// </summary>
    [Fact]
    public async Task CalleeKeepingTheToken_DoesNotRetainTheCallerSourceOrItsCallbacks()
    {
        await using var engine = CreateEngine();
        var service = new EchoService();
        var echo = RegisterEcho(engine, service);
        var (source, callbackState) = CallWithARegisteredCallback(echo);
        Assert.True(service.Kept.CanBeCanceled);

        for (var attempt = 0; attempt < 10 && (source.IsAlive || callbackState.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(callbackState.IsAlive);
        Assert.False(source.IsAlive);
        GC.KeepAlive(service);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Source, WeakReference CallbackState) CallWithARegisteredCallback(IEcho echo)
    {
        var source = new CancellationTokenSource();
        var state = new SharedShape();
        source.Token.Register(static s => GC.KeepAlive(s), state); // the caller never unregisters it
        echo.Observe(source.Token);
        return (new WeakReference(source), new WeakReference(state));
    }

    /// <summary>
    /// Mirrors the three record/list rows of <c>benchmarks/ModularRuntime.Benchmarks/ServiceCallBenchmarks.cs</c>
    /// (a closed record; an open record whose interface-typed field holds a <c>string[]</c>; an
    /// <c>object</c> carrying a <c>List&lt;object&gt;</c> of eight strings) and pins what each row
    /// exercises. The number of references a scan visits is observed through the reference limit:
    /// the value passes at that limit and is refused as not verifiable one below it.
    /// </summary>
    [Fact]
    public async Task BenchmarkShapes_ExerciseTheChecksTheirDescriptionsClaim()
    {
        // Closed record: no check is emitted for the parameter or the result, in any mode.
        Assert.Equal(TypeVerdict.Closed, SharedTypeAnalyzer.ByConstruction.GetShape(typeof(CounterLike)).Static);
        Assert.False(Services.InvocationEmitter.ShouldCheck(typeof(CounterLike)));

        // Open record: checked; the scan reads the one field and stops at the string[] (closed): the record and the array.
        Assert.True(Services.InvocationEmitter.ShouldCheck(typeof(Tagged)));
        Assert.Equal(TypeVerdict.Closed, SharedTypeAnalyzer.ByConstruction.GetShape(typeof(string[])).Exact);
        string[] tags = ["a", "b", "c", "d"];
        await AssertVisits(new Tagged("bench", tags), references: 2);

        // object carrying a List<object> of eight strings: the list and every element.
        Assert.True(Services.InvocationEmitter.ShouldCheck(typeof(object)));
        await AssertVisits(new List<object> { "a", "b", "c", "d", "e", "f", "g", "h" }, references: 9);

        static async Task AssertVisits(object value, int references)
        {
            await using var enough = CreateEngine(new RuntimeLimits { MaxValueScanReferences = references });
            Assert.Same(value, RegisterEcho(enough).Echo(value));
            await using var tooFew = CreateEngine(new RuntimeLimits { MaxValueScanReferences = references - 1 });
            NotVerifiable(RegisterEcho(tooFew), value);
        }
    }

    [Fact]
    public async Task ClosedValues_Pass_WithoutInspection_AndWithoutAllocating()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);
        var box = new Box("flat", "shared");
        var tagged = new Tagged("t", ["a", "b"]);

        // Legitimate values of every common shape pass.
        object?[] values =
        [
            "text", 42, 3.5m, Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), new Version(1, 2), new Uri("https://example.test/"),
            new[] { "a", "b" }, new string[2, 2], new List<string> { "a" }, ImmutableArray.Create("a"), ImmutableList.Create("a"),
            new Dictionary<string, int> { ["a"] = 1 }, new Dictionary<string, object> { ["a"] = "b" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" },
            ImmutableDictionary<string, object>.Empty.Add("a", 1), new KeyValuePair<string, string>("k", "v"), ("a", 1), (int?)5,
            JsonPayload.Parse("""{"a":1}"""), new CounterLike(1, "p"), box, tagged, new Keyed(new Dictionary<string, object> { ["n"] = 1 }), new Pair("k", "v"),
            new GraphNode { Payload = "p" }, new Holder(), new SharedShape(), new List<IShape> { new SharedShape() }, new Lazy<string>(static () => "lazy"),
            (Func<int>)PrivateTargetsFree.Compute, (Action)(static () => { }), typeof(string), typeof(List<string>), typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes),
            new ReadOnlyMemory<byte>([1, 2, 3]), "text".AsMemory(), CancellationToken.None, Task.FromResult("done"), new object(), new Cell<string>("x"),
            new CarrierException("shared") { Payload = "text" }, new InvalidOperationException("plain", new ArgumentException("inner")),
        ];
        foreach (var value in values)
        {
            Assert.Same(value, echo.Echo(value));
            Assert.Same(value, await echo.EchoAsync(value));
        }

        // Values whose type is closed, and open values whose content is flat, cost no allocation.
        for (var warmup = 0; warmup < 100; warmup++)
        {
            echo.Echo("x");
            echo.Echo(tagged);
            echo.Echo(box);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 1000; round++)
        {
            echo.Echo("x");
            echo.Echo(tagged);
            echo.Echo(box);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    public static class PrivateTargetsFree
    {
        public static int Compute() => 7;
    }

    public sealed class Cell<T>(T value)
    {
        public T Value { get; } = value;
    }

    [Fact]
    public async Task PrivateObjects_HiddenInSharedObjects_AreRejected_WithThePath()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);

        var inBox = Rejected(echo, new Box("b", new PrivateValue()));
        Assert.Contains("Typed service 'test.echo', argument 'value' → field '<Value>k__BackingField'", inBox.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PrivateValue), inBox.Message, StringComparison.Ordinal);
        Rejected(echo, new Holder { Value = new PrivateValue() });
        Rejected(echo, new Pair("k", new PrivateValue()));
        Rejected(echo, new Keyed(new Dictionary<string, object> { ["ok"] = "s", ["bad"] = new PrivateShape() }));
        Rejected(echo, new List<IShape> { new SharedShape(), new PrivateShape() });
        Rejected(echo, new Cell<object>(new PrivateValue()));
        Rejected(echo, new Box("nested", new Box("inner", new Pair("k", new List<object> { new PrivateValue() }))));
        Rejected(echo, new PrivateValue());
        var captured = new PrivateValue();
        Rejected(echo, new Lazy<string>(() => captured.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var typed = Assert.Throws<TypedServiceException>(() => echo.Wrap(new Box("w", new PrivateValue())));
        Assert.Equal(PluginErrorCodes.ValueNotShared, typed.ErrorCode);
        Assert.Contains("argument 'box'", typed.Message, StringComparison.Ordinal);
        Assert.Equal(PluginErrorCodes.ValueNotShared, (await Assert.ThrowsAsync<TypedServiceException>(() => echo.EchoAsync(new Box("b", new PrivateValue())))).ErrorCode);

        // The same shapes with shared content pass.
        Assert.NotNull(echo.Echo(new Box("b", "s")));
        Assert.NotNull(echo.Echo(new Holder()));
        Assert.NotNull(echo.Echo(new Keyed(new Dictionary<string, object> { ["ok"] = new SharedShape() })));
    }

    [Fact]
    public async Task CyclicGraphs_Terminate_AndEachObjectIsInspectedOnce()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);

        var a = new GraphNode { Payload = "a" };
        var b = new GraphNode { Payload = "b", Next = a };
        a.Next = b;
        Assert.Same(a, echo.Echo(a));

        b.Payload = new PrivateValue();
        var failure = Rejected(echo, a);
        Assert.Contains("field '<Next>k__BackingField' → field '<Payload>k__BackingField'", failure.Message, StringComparison.Ordinal);

        // A cycle within the depth limit is entered once per node and terminates.
        var head = new GraphNode { Payload = "0" };
        var tail = head;
        for (var index = 1; index < RuntimeLimits.DefaultMaxValueScanDepth; index++)
        {
            tail.Next = new GraphNode { Payload = index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            tail = tail.Next;
        }

        tail.Next = head;
        Assert.Same(head, echo.Echo(head));

        // A chain longer than the depth limit is refused as not verifiable (and terminates).
        tail.Next = new GraphNode { Payload = "beyond" };
        Assert.Contains("MaxValueScanDepth", NotVerifiable(echo, head).Message, StringComparison.Ordinal);

        // A diamond (one object reachable twice) is inspected once as well.
        var shared = new Box("shared", "x");
        Assert.NotNull(echo.Echo(new List<object> { shared, shared, new Box("outer", shared) }));
    }

    [Fact]
    public async Task MultidimensionalAndJaggedArrays_AreInspected()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);

        Assert.NotNull(echo.Echo(new object[2, 2] { { "a", 1 }, { "b", 2 } }));
        Assert.NotNull(echo.Echo(new object[][] { ["a"], ["b", 1] }));
        Assert.NotNull(echo.Echo(new IShape[] { new SharedShape() }));
        Assert.NotNull(echo.Echo(new Pair[] { new("k", "v") }));
        Rejected(echo, new object[2, 2] { { "a", 1 }, { "b", new PrivateValue() } });
        Rejected(echo, new object[][] { ["a"], ["b", new PrivateValue()] });
        Rejected(echo, new IShape[] { new SharedShape(), new PrivateShape() });
        Rejected(echo, new Pair[] { new("k", new PrivateValue()) });
        Rejected(echo, new PrivateValue[0, 0]);
        Rejected(echo, Array.Empty<PrivateValue>());
    }

    [Fact]
    public async Task PolymorphicCollections_AndTheirComparers_AreInspected()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);

        Assert.NotNull(echo.Echo(new Dictionary<string, IShape> { ["s"] = new SharedShape() }));
        Rejected(echo, new Dictionary<string, IShape> { ["s"] = new SharedShape(), ["p"] = new PrivateShape() });
        Rejected(echo, new Dictionary<IShape, string> { [new PrivateShape()] = "key" });
        Rejected(echo, new SortedDictionary<string, object>(StringComparer.Ordinal) { ["p"] = new PrivateValue() });
        Rejected(echo, ImmutableDictionary<string, object>.Empty.Add("p", new PrivateValue()));
        Rejected(echo, ImmutableList.Create<object>("a", new PrivateValue()));
        Rejected(echo, new Queue<object>([new PrivateValue()]));
        Rejected(echo, new LinkedList<object>([new PrivateValue()]));
        Rejected(echo, new Stack<IShape>([new PrivateShape()]));

        // A comparer object supplied to a framework collection is part of its content.
        var comparer = Rejected(echo, new Dictionary<string, int>(new PrivateComparer()) { ["a"] = 1 });
        Assert.Contains("Comparer", comparer.Message, StringComparison.Ordinal);
        Rejected(echo, new HashSet<string>(new PrivateComparer()));
        Rejected(echo, new SortedSet<string>(new PrivateComparer()));
        Rejected(echo, new SortedDictionary<string, int>(new PrivateComparer()));
        Rejected(echo, new SortedList<string, int>(new PrivateComparer()));
        Rejected(echo, ImmutableHashSet.Create<string>(new PrivateComparer()));
        Rejected(echo, ImmutableDictionary.Create<string, int>(new PrivateComparer()));
        Rejected(echo, new System.Collections.Concurrent.ConcurrentDictionary<string, int>(new PrivateComparer()));
        Assert.NotNull(echo.Echo(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 }));
        Assert.NotNull(echo.Echo(new HashSet<string>(StringComparer.Ordinal) { "a" }));
        Assert.NotNull(echo.Echo(new SortedDictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1 }));
        Assert.NotNull(echo.Echo(new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task DelegatesAndClosures_AreInspectedThroughTheirTargets()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);
        var targets = new PrivateTargets();

        // A closure over a private object: the closure class is shared, its field is not.
        var closure = Rejected(echo, CaptureAPrivateValue());
        Assert.Contains("target object → field 'captured'", closure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PrivateValue), closure.Message, StringComparison.Ordinal);

        // Methods of a private type, static or instance; multicast with one private target.
        Rejected(echo, (Action)targets.Handle);
        Rejected(echo, (Func<int>)PrivateTargets.Compute);
        Rejected(echo, (Action)(static () => { }) + targets.Handle);
        Rejected(echo, new Box("cb", (Action)targets.Handle));

        // Shared delegates: static lambdas, closures over shared values, methods of shared objects.
        Assert.NotNull(echo.Echo((Action)(static () => { })));
        Assert.NotNull(echo.Echo(CaptureAString()));
        Assert.NotNull(echo.Echo((Func<string>)new SharedShape().Name.ToUpperInvariant));
        Assert.NotNull(echo.Echo((Func<int>)PrivateTargetsFree.Compute));
        Assert.Equal(0, targets.Calls);

        // C# hoists every captured local of a scope into one closure object: a lambda that captures
        // only shared values is still refused when a sibling lambda of the same scope captured a
        // private one (the closure object is what crosses).
        Rejected(echo, SiblingClosures().Shared);
    }

    private static Func<int> CaptureAPrivateValue()
    {
        var captured = new PrivateValue();
        return () => captured.Number;
    }

    private static Func<string> CaptureAString()
    {
        var text = "shared";
        return () => text;
    }

    private static (Func<int> Private, Func<string> Shared) SiblingClosures()
    {
        var captured = new PrivateValue();
        var text = "shared";
        return (() => captured.Number, () => text);
    }

    [Fact]
    public async Task ReflectionObjects_AreJudgedByWhatTheyRepresent()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);

        Assert.NotNull(echo.Echo(typeof(string)));
        Assert.NotNull(echo.Echo(typeof(List<string>)));
        Assert.NotNull(echo.Echo(typeof(SharedShape).GetProperty(nameof(SharedShape.Name))));
        Assert.NotNull(echo.Echo(typeof(string).Assembly));
        Assert.NotNull(echo.Echo(typeof(string).TypeHandle));
        Rejected(echo, typeof(PrivateValue));
        Rejected(echo, typeof(List<PrivateValue>));
        Rejected(echo, typeof(PrivateTargets).GetMethod(nameof(PrivateTargets.Handle)));
        Rejected(echo, typeof(PrivateValue).GetProperty(nameof(PrivateValue.Number)));
        Rejected(echo, typeof(PrivateValue).TypeHandle);
        Rejected(echo, typeof(PrivateTargets).GetMethod(nameof(PrivateTargets.Compute))!.MethodHandle);
        Rejected(echo, new Box("t", typeof(PrivateValue)));
    }

    [Fact]
    public async Task MemoryManagers_AndGcHandles_AreInspected()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);

        using var manager = new PrivateMemoryManager();
        var privateMemory = manager.Memory;
        Rejected(echo, (ReadOnlyMemory<byte>)privateMemory);
        Rejected(echo, privateMemory);
        Rejected(echo, new Box("m", (ReadOnlyMemory<byte>)privateMemory));
        Assert.NotNull(echo.Echo(new ReadOnlyMemory<byte>([1, 2])));
        Assert.NotNull(echo.Echo(new Memory<byte>(new byte[4])));
        Assert.NotNull(echo.Echo("abc".AsMemory()));

        var handle = GCHandle.Alloc(new PrivateValue());
        try
        {
            Rejected(echo, handle);
            Rejected(echo, new Box("h", handle));
        }
        finally
        {
            handle.Free();
        }

        var shared = GCHandle.Alloc("text");
        try
        {
            Assert.NotNull(echo.Echo(shared));
        }
        finally
        {
            shared.Free();
        }
    }

    [Fact]
    public async Task ExceptionsCarryingPrivateObjects_InTheirOwnFields_AreConverted()
    {
        await using var engine = CreateEngine();
        var service = new EchoService();
        var echo = RegisterEcho(engine, service);

        service.Behavior = _ => throw new CarrierException("carrier") { Payload = new PrivateValue() };
        var converted = Assert.Throws<TypedServiceException>(() => echo.Echo("x"));
        Assert.Equal(PluginErrorCodes.HandlerFailed, converted.ErrorCode);
        Assert.Equal(typeof(CarrierException).FullName, converted.Diagnostic!.ExceptionType);
        Assert.Contains("<Payload>k__BackingField", converted.Message, StringComparison.Ordinal);

        service.Behavior = _ => throw new CarrierException("carrier") { Payload = "text" };
        Assert.Equal("carrier", Assert.Throws<CarrierException>(() => echo.Echo("x")).Message);

        service.Behavior = _ =>
        {
            var exception = new CarrierException("data");
            exception.Data["box"] = new Box("b", new PrivateShape());
            throw exception;
        };
        var data = Assert.Throws<TypedServiceException>(() => echo.Echo("x"));
        Assert.Equal(PluginErrorCodes.HandlerFailed, data.ErrorCode);
        Assert.Contains("Data value", data.Message, StringComparison.Ordinal);

        // An exception passed as a value (not thrown) follows the same rule.
        service.Behavior = null;
        Rejected(echo, new CarrierException("v") { Payload = new PrivateValue() });
        Rejected(echo, new InvalidOperationException("outer", new PrivateException("inner")));
        Assert.NotNull(echo.Echo(new InvalidOperationException("outer", new CarrierException("inner"))));
    }

    [Fact]
    public async Task ScanLimits_FailClosed_Predictably()
    {
        var logger = new CollectingLogger();
        await using var engine = CreateEngine(new RuntimeLimits { MaxValueScanReferences = 100, MaxValueScanDepth = 4 }, logger);
        var echo = RegisterEcho(engine);

        // More references than the budget: refused as not verifiable, whatever the content.
        var wide = NotVerifiable(echo, new List<object>(Enumerable.Repeat<object>("x", 150)));
        Assert.Contains("MaxValueScanReferences", wide.Message, StringComparison.Ordinal);
        Assert.Contains("element [99]", wide.Message, StringComparison.Ordinal);

        // The same elements in a closed collection type need no scan at all.
        Assert.NotNull(echo.Echo(new List<string>(Enumerable.Repeat("x", 150))));
        Assert.NotNull(echo.Echo(Enumerable.Repeat("x", 100_000).ToArray()));

        // Deeper nesting than allowed: refused as not verifiable; a private object within the depth is found first.
        object nested = "leaf";
        for (var level = 0; level < 6; level++)
        {
            nested = new List<object> { nested };
        }

        Assert.Contains("MaxValueScanDepth", NotVerifiable(echo, nested).Message, StringComparison.Ordinal);
        Rejected(echo, new List<object> { new List<object> { new PrivateValue() } });

        // A cycle longer than the limits is not verifiable (and terminates).
        var head = new GraphNode();
        var tail = head;
        for (var index = 0; index < 200; index++)
        {
            tail.Next = new GraphNode();
            tail = tail.Next;
        }

        tail.Next = head;
        NotVerifiable(echo, head);
        var wideCycle = new List<object>();
        for (var index = 0; index < 150; index++)
        {
            wideCycle.Add(new GraphNode { Next = head });
        }

        NotVerifiable(echo, wideCycle);

        // Default limits: a huge array is refused quickly rather than scanned to the end.
        await using var defaults = CreateEngine();
        var strict = RegisterEcho(defaults);
        var huge = new object[1_000_000];
        Array.Fill(huge, "x");
        var stopwatch = Stopwatch.StartNew();
        NotVerifiable(strict, huge);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"refusal took {stopwatch.ElapsedMilliseconds} ms");
        huge[^1] = new PrivateValue();
        NotVerifiable(strict, huge);

        // Warn mode: logged once per root type, the value passes.
        await using var warning = CreateEngine(new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn, MaxValueScanReferences = 10 }, logger);
        var lenient = RegisterEcho(warning);
        Assert.NotNull(lenient.Echo(new List<object>(Enumerable.Repeat<object>("x", 20))));
        Assert.NotNull(lenient.Echo(new List<object>(Enumerable.Repeat<object>("y", 20))));
        Assert.Single(logger.Entries, e => e.Message.Contains("MaxValueScanReferences", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContractSignatures_AreValidatedAtRegistration()
    {
        await using var strict = CreateEngine();
        var definition = new TypedServiceDefinition("test.sig", "Signature", new Version(1, 0));

        var parameter = Assert.Throws<PluginRuntimeException>(() => strict.Host.TypedServices.Register<IPrivateSignature>(definition, new PrivateSignature()));
        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, parameter.ErrorCode);
        Assert.Contains("parameter 'value' of 'IPrivateSignature.Take'", parameter.Message, StringComparison.Ordinal);
        var result = Assert.Throws<PluginRuntimeException>(() => strict.Host.TypedServices.Register<IPrivateResult>(definition, new PrivateResult()));
        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, result.ErrorCode);
        Assert.Contains("result of 'IPrivateResult.ListAsync'", result.Message, StringComparison.Ordinal);
        Assert.Empty(strict.Host.Services.GetServices());

        // A contract whose own type is private: the guidance names sharedContracts.
        await using var excluded = CreateEngine(morePrivate: typeof(IExcludedContract));
        var contract = Assert.Throws<PluginRuntimeException>(() => excluded.Host.TypedServices.Register<IExcludedContract>(definition, new ExcludedContract()));
        Assert.Equal(PluginErrorCodes.TypedServiceInvalidContract, contract.ErrorCode);
        Assert.Contains("sharedContracts", contract.Message, StringComparison.Ordinal);

        // Warn: registered, with a warning; None: registered silently.
        var logger = new CollectingLogger();
        await using var warning = CreateEngine(new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, logger);
        Assert.True(warning.Host.TypedServices.Register<IPrivateSignature>(definition, new PrivateSignature()).IsActive);
        Assert.NotNull(logger.Find(LogLevel.Warning, PluginErrorCodes.TypedServiceInvalidContract));
        await using var none = CreateEngine(new RuntimeLimits { TypedValueCheck = TypedValueCheck.None }, logger);
        Assert.True(none.Host.TypedServices.Register<IPrivateSignature>(definition, new PrivateSignature()).IsActive);
        Assert.Single(logger.Entries, e => e.Message.Contains(PluginErrorCodes.TypedServiceInvalidContract, StringComparison.Ordinal));

        // Generic methods and open shapes are fine: the instances are checked.
        Assert.True(strict.Host.TypedServices.Register<IEcho>(definition, new EchoService()).IsActive);
    }

    private sealed class PrivateSignature : IPrivateSignature
    {
        public void Take(PrivateValue value)
        {
        }
    }

    private sealed class PrivateResult : IPrivateResult
    {
        public Task<IReadOnlyList<PrivateValue>> ListAsync() => Task.FromResult<IReadOnlyList<PrivateValue>>([]);
    }

    private sealed class ExcludedContract : IExcludedContract
    {
        public void Run()
        {
        }
    }

    [Fact]
    public async Task NoneMode_SkipsEveryCheck_ByExplicitChoice()
    {
        await using var engine = CreateEngine(new RuntimeLimits { TypedValueCheck = TypedValueCheck.None });
        var echo = RegisterEcho(engine);
        Assert.IsType<PrivateValue>(echo.Echo(new PrivateValue()));
        Assert.NotNull(echo.Echo(new Box("b", new PrivateValue())));
        Assert.NotNull(echo.Echo(new object[1_000_000]));
        var stream = engine.Host.Streams.Create<object>(new StreamDefinition("test.none", "None", new Version(1, 0)));
        Assert.True(stream.TryWrite(new PrivateValue()));
    }

    [Fact]
    public async Task Streams_FollowTheSameRules_AsTypedServices()
    {
        await using var engine = CreateEngine();
        var streams = engine.Host.Streams;
        static StreamDefinition Definition(string id) => new(id, id, new Version(1, 0)) { DefaultCapacity = 8 };

        // Interface items: the instance decides.
        var shapes = streams.Create<IShape>(Definition("test.shapes"));
        Assert.True(shapes.TryWrite(new SharedShape()));
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => shapes.TryWrite(new PrivateShape())).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, (await Assert.ThrowsAsync<PluginRuntimeException>(async () => await shapes.WriteAsync(new PrivateShape()))).ErrorCode);
        Assert.Equal(1, shapes.Sequence);

        // A sealed record with an object field: inspected per item; its content decides.
        var boxes = streams.Create<Box>(Definition("test.boxes"));
        Assert.True(boxes.TryWrite(new Box("b", "s")));
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => boxes.TryWrite(new Box("b", new PrivateValue()))).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => boxes.TryWrite(new Box("b", new List<object> { new Box("n", new PrivateShape()) }))).ErrorCode);

        // Closed item types are never inspected; a non-sealed framework collection is checked per item (cheaply).
        Assert.True(streams.Create<CounterLike>(Definition("test.closed")).TryWrite(new CounterLike(1, "p")));
        Assert.True(streams.Create<List<string>>(Definition("test.lists")).TryWrite(["a"]));
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => streams.Create<object>(Definition("test.objects")).TryWrite(new List<object> { new PrivateValue() })).ErrorCode);

        // Binary items: copied on write by default (fresh array, nothing to check); shared memory is inspected.
        using var manager = new PrivateMemoryManager();
        Assert.True(streams.Create<ReadOnlyMemory<byte>>(Definition("test.bytes.copy")).TryWrite(manager.Memory));
        var sharedBytes = streams.Create<ReadOnlyMemory<byte>>(Definition("test.bytes.shared") with { CopyBinaryItems = false });
        Assert.True(sharedBytes.TryWrite(new byte[] { 1 }));
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => sharedBytes.TryWrite(manager.Memory)).ErrorCode);

        // Private item types are refused at creation; unverifiable values at write.
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => streams.Create<PrivateShape>(Definition("test.private"))).ErrorCode);
        Assert.Equal(PluginErrorCodes.ValueNotShared, Assert.Throws<PluginRuntimeException>(() => streams.Create<List<PrivateValue>>(Definition("test.private.list"))).ErrorCode);
        await using var small = CreateEngine(new RuntimeLimits { MaxValueScanReferences = 10 });
        var tight = small.Host.Streams.Create<object>(Definition("test.tight"));
        Assert.Equal(PluginErrorCodes.ValueNotVerifiable, Assert.Throws<PluginRuntimeException>(() => tight.TryWrite(new List<object>(Enumerable.Repeat<object>("x", 50)))).ErrorCode);

        // Warn mode: delivered, logged once.
        var logger = new CollectingLogger();
        await using var warning = CreateEngine(new RuntimeLimits { TypedValueCheck = TypedValueCheck.Warn }, logger);
        var lenient = warning.Host.Streams.Create<Box>(Definition("test.lenient"));
        var subscription = warning.Host.Streams.Subscribe<Box>("test.lenient")!;
        Assert.True(lenient.TryWrite(new Box("b", new PrivateValue())));
        Assert.True(lenient.TryWrite(new Box("b", new PrivateValue())));
        Assert.True(subscription.Reader.TryRead(out var delivered));
        Assert.IsType<PrivateValue>(delivered.Item.Value);
        Assert.Single(logger.Entries, e => e.Message.Contains(PluginErrorCodes.ValueNotShared, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentChecks_AreIndependent_AndNeverRetainValues()
    {
        await using var engine = CreateEngine();
        var echo = RegisterEcho(engine);

        // Many threads scanning open graphs at once: every outcome is the expected one.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            var passes = 0;
            var rejections = 0;
            for (var round = 0; round < 200; round++)
            {
                var payload = (round + index) % 3 == 0 ? new PrivateValue() : (object)"text";
                var value = new Box("b", new List<object> { new Box("inner", payload), new Pair("k", "v") });
                try
                {
                    echo.Echo(value);
                    passes++;
                }
                catch (TypedServiceException exception) when (exception.ErrorCode == PluginErrorCodes.ValueNotShared)
                {
                    rejections++;
                }
            }

            return (passes, rejections);
        })));
        Assert.All(outcomes, outcome => Assert.Equal(200, outcome.passes + outcome.rejections));
        Assert.True(outcomes.Sum(o => o.rejections) > 0);

        // The scan state keeps no reference to what it inspected.
        var reference = ScanAndForget(echo);
        Assert.True(GcDiagnostics.WaitForCollection(reference), "a scanned value must be collectible once the caller drops it");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ScanAndForget(IEcho echo)
    {
        var value = new Box("b", new List<object> { new GraphNode { Payload = "p" }, new Box("inner", new PrivateValue()) });
        Assert.Throws<TypedServiceException>(() => echo.Echo(value));
        return new WeakReference(value);
    }
}
