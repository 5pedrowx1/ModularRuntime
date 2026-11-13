using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using ModularRuntime.Engine.Services;

namespace ModularRuntime.Engine.Internal;

/// <summary>What is known about the values of a type with respect to plugin-private references.</summary>
internal enum TypeVerdict
{
    /// <summary>No value of the type can reference a plugin-private object: nothing to inspect.</summary>
    Closed = 0,

    /// <summary>The type does not settle the question; each instance is inspected with a bounded plan.</summary>
    Open = 1,

    /// <summary>The type itself (or a type it is constructed from) is private to a plugin.</summary>
    Private = 2,

    /// <summary>The type cannot be analyzed within the analyzer's bounds; its values are refused in the safe mode.</summary>
    Unverifiable = 3,
}

/// <summary>How an instance of an <see cref="TypeVerdict.Open"/> type is inspected.</summary>
internal enum ScanKind
{
    /// <summary>Nothing of its own (an interface, <c>object</c>, an abstract class): the runtime type of each value decides.</summary>
    None = 0,

    /// <summary>A generated plan reads the instance fields whose static type is not closed.</summary>
    Fields,
    Array,
    List,
    Dictionary,
    HashSet,
    SortedDictionary,
    SortedSet,
    SortedList,
    ConcurrentDictionary,
    ImmutableDictionary,
    ImmutableHashSet,
    ImmutableSortedDictionary,
    ImmutableSortedSet,
    Delegate,
    Exception,
    Reflection,
    Memory,
    GCHandle,
}

/// <summary>Analysis result of one type for one analyzer, cached with a weak key.</summary>
internal sealed class TypeShape
{
    /// <summary>Verdict for a value whose runtime type is exactly this type.</summary>
    public TypeVerdict Exact { get; init; }

    /// <summary>Verdict for a value whose static type is this type (a subclass may be anything).</summary>
    public TypeVerdict Static { get; init; }

    public ScanKind Kind { get; init; }

    /// <summary>Why the type is private or unverifiable.</summary>
    public string? Reason { get; init; }

    /// <summary>Instance inspection plan, built on first use (see <see cref="ValueScanPlans"/>).</summary>
    public Func<object, ValueScan, bool>? Plan;
}

/// <summary>
/// Classifies types by construction: a type is <em>closed</em> when the policy accepts it and
/// every type its values can reference (type arguments, element types, instance fields, all the
/// way down) is closed too. Everything else is <em>open</em> and its instances are inspected with
/// a plan bounded by the runtime's limits, or <em>private</em> when the policy refuses the type
/// or a type it is constructed from.
/// </summary>
/// <remarks>
/// <para>
/// The analysis is a closure over the field graph of a type (static fields excluded), solved as
/// a fixpoint so that recursive types are handled, bounded by <see cref="MaxClosureTypes"/>
/// (beyond it the root type is unverifiable). Results are cached per type with weak keys, so
/// plugin and contract types are never kept alive by the analyzer.
/// </para>
/// <para>
/// Two analyzers exist: one per engine, with that engine's <see cref="ISharedTypePolicy"/>, used
/// for every runtime decision; and <see cref="ByConstruction"/>, policy-free, which treats every
/// collectible type as open and decides at emit time which proxy arguments and results can skip
/// the check altogether.
/// </para>
/// </remarks>
internal sealed class SharedTypeAnalyzer
{
    public const int MaxClosureTypes = 512;

    private static readonly HashSet<Type> ClosedLeaves =
    [
        typeof(string), typeof(Uri), typeof(Version), typeof(Guid), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset),
        typeof(TimeSpan), typeof(DateOnly), typeof(TimeOnly), typeof(CultureInfo), typeof(RegionInfo), typeof(TimeZoneInfo),
        typeof(Regex), typeof(WeakReference), typeof(nint), typeof(nuint), typeof(System.Numerics.BigInteger),
    ];

    private static readonly Dictionary<Type, ScanKind> CollectionKinds = new()
    {
        [typeof(List<>)] = ScanKind.List,
        [typeof(Dictionary<,>)] = ScanKind.Dictionary,
        [typeof(HashSet<>)] = ScanKind.HashSet,
        [typeof(SortedDictionary<,>)] = ScanKind.SortedDictionary,
        [typeof(SortedSet<>)] = ScanKind.SortedSet,
        [typeof(SortedList<,>)] = ScanKind.SortedList,
        [typeof(ConcurrentDictionary<,>)] = ScanKind.ConcurrentDictionary,
        [typeof(ImmutableDictionary<,>)] = ScanKind.ImmutableDictionary,
        [typeof(ImmutableHashSet<>)] = ScanKind.ImmutableHashSet,
        [typeof(ImmutableSortedDictionary<,>)] = ScanKind.ImmutableSortedDictionary,
        [typeof(ImmutableSortedSet<>)] = ScanKind.ImmutableSortedSet,
    };

    private readonly ISharedTypePolicy? _policy;
    private readonly ConditionalWeakTable<Type, TypeShape> _shapes = new();

    public SharedTypeAnalyzer(ISharedTypePolicy? policy)
    {
        _policy = policy;
    }

    /// <summary>The policy-free analyzer: collectible types are open (their verdict is the engine's at run time).</summary>
    public static SharedTypeAnalyzer ByConstruction { get; } = new(null);

    public TypeShape GetShape(Type type) => _shapes.TryGetValue(type, out var shape) ? shape : Analyze(type);

    /// <summary>True when <paramref name="type"/> and the types it is constructed from are accepted by the policy.</summary>
    public bool IsSharedType(Type type) => GetShape(type).Static != TypeVerdict.Private;

    public bool IsSharedAssembly(Assembly assembly) =>
        _policy?.IsSharedAssembly(assembly) ?? (AssemblyLoadContext.GetLoadContext(assembly) is null or { IsCollectible: false });

    /// <summary>Instance fields of <paramref name="type"/> and its base classes up to (excluding) <paramref name="stopAt"/>.</summary>
    public static IEnumerable<FieldInfo> InstanceFields(Type type, Type? stopAt = null)
    {
        for (var current = type; current is not null && current != typeof(object) && current != stopAt; current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }

    public static string Describe(Type type) => $"'{type.FullName ?? type.Name}' from assembly '{type.Assembly.GetName().Name}'";

    private TypeShape Analyze(Type root)
    {
        var nodes = new Dictionary<Type, Node>();
        var pending = new Stack<Type>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (nodes.ContainsKey(type))
            {
                continue;
            }

            if (_shapes.TryGetValue(type, out var known))
            {
                nodes.Add(type, Node.Known(type, known));
                continue;
            }

            if (nodes.Count >= MaxClosureTypes)
            {
                return Store(root, new TypeShape
                {
                    Exact = TypeVerdict.Unverifiable,
                    Static = TypeVerdict.Unverifiable,
                    Reason = $"the type graph of {Describe(root)} exceeds {MaxClosureTypes} types",
                });
            }

            Node node;
            try
            {
                node = Classify(type);
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                node = Node.Leaf(type, TypeVerdict.Unverifiable, TypeVerdict.Unverifiable, ScanKind.None, $"{Describe(type)} could not be analyzed: {exception.GetType().Name}: {exception.Message}");
            }

            nodes.Add(type, node);
            foreach (var dependency in node.Components)
            {
                pending.Push(dependency);
            }

            foreach (var dependency in node.Fields)
            {
                pending.Push(dependency);
            }
        }

        Solve(nodes);
        TypeShape? result = null;
        foreach (var node in nodes.Values)
        {
            if (node.IsKnown)
            {
                continue;
            }

            var shape = Store(node.Type, node.ToShape());
            if (node.Type == root)
            {
                result = shape;
            }
        }

        return result ?? GetShape(root);
    }

    private TypeShape Store(Type type, TypeShape shape) => _shapes.GetValue(type, _ => shape);

    private static void Solve(Dictionary<Type, Node> nodes)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var node in nodes.Values)
            {
                if (node.IsKnown || node.Exact == TypeVerdict.Private || node.Exact == TypeVerdict.Unverifiable)
                {
                    continue;
                }

                foreach (var component in node.Components)
                {
                    var verdict = nodes[component].Static ?? TypeVerdict.Closed;
                    if (verdict is TypeVerdict.Private or TypeVerdict.Unverifiable)
                    {
                        node.Exact = verdict;
                        node.Static = verdict;
                        node.Reason = verdict == TypeVerdict.Private
                            ? $"{Describe(node.Type)} is constructed over {Describe(component)}, which is not a shared type"
                            : nodes[component].Reason;
                        changed = true;
                        break;
                    }
                }

                if (node.Exact is not null)
                {
                    continue;
                }

                foreach (var field in node.Fields)
                {
                    var verdict = nodes[field].Static ?? nodes[field].Exact ?? TypeVerdict.Closed;
                    if (verdict != TypeVerdict.Closed)
                    {
                        node.Exact = TypeVerdict.Open;
                        if (node.SealedLike)
                        {
                            node.Static = TypeVerdict.Open;
                        }

                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        foreach (var node in nodes.Values)
        {
            if (node.IsKnown)
            {
                continue;
            }

            node.Exact ??= TypeVerdict.Closed;
            node.Static ??= node.SealedLike ? node.Exact : TypeVerdict.Open;
        }
    }

    private Node Classify(Type type)
    {
        if (type.IsPointer || type.IsFunctionPointer || type.IsByRef)
        {
            // Unmanaged references: they retain nothing and cannot be boxed.
            return Node.Leaf(type, TypeVerdict.Closed, TypeVerdict.Closed, ScanKind.None);
        }

        if (type.IsGenericParameter || type.ContainsGenericParameters)
        {
            return Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.None);
        }

        if (!IsOwnAssemblyShared(type))
        {
            return _policy is null
                ? Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.None)
                : Node.Leaf(type, TypeVerdict.Private, TypeVerdict.Private, ScanKind.None, $"{Describe(type)} is not a shared type");
        }

        if (type.IsPrimitive || type.IsEnum)
        {
            return Node.Leaf(type, TypeVerdict.Closed, TypeVerdict.Closed, ScanKind.None);
        }

        if (type.Assembly == typeof(SharedTypeAnalyzer).Assembly)
        {
            // Engine objects (proxies, call contexts, client handles, registrations) hold identifiers
            // and runtime state only; they drop every owner-supplied object on release by design and
            // are never inspected (an inspection would walk the registries).
            return Node.Leaf(type, TypeVerdict.Closed, TypeVerdict.Closed, ScanKind.None);
        }

        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            return new Node(type, ScanKind.Array, true) { Components = [element], Fields = [element] };
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.Delegate) with { Components = GenericArguments(type) };
        }

        if (typeof(Exception).IsAssignableFrom(type))
        {
            return Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.Exception) with { Components = GenericArguments(type) };
        }

        if (typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type) || typeof(Module).IsAssignableFrom(type)
            || type == typeof(RuntimeTypeHandle) || type == typeof(RuntimeMethodHandle) || type == typeof(RuntimeFieldHandle))
        {
            return Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.Reflection);
        }

        if (type == typeof(GCHandle))
        {
            return Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.GCHandle);
        }

        if (type == typeof(object))
        {
            return Node.Leaf(type, TypeVerdict.Closed, TypeVerdict.Open, ScanKind.None);
        }

        if (type.IsInterface || type.IsAbstract)
        {
            return Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.None) with { Components = GenericArguments(type) };
        }

        var sealedLike = type.IsSealed || type.IsValueType;
        if (ClosedLeaves.Contains(type) || (type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(WeakReference<>)))
        {
            return Node.Leaf(type, TypeVerdict.Closed, sealedLike ? TypeVerdict.Closed : TypeVerdict.Open, ScanKind.None) with { Components = GenericArguments(type) };
        }

        if (type.IsConstructedGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Memory<>) || definition == typeof(ReadOnlyMemory<>))
            {
                return Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, ScanKind.Memory) with { Components = GenericArguments(type) };
            }

            if (CollectionKinds.TryGetValue(definition, out var kind))
            {
                var arguments = GenericArguments(type);
                return kind == ScanKind.List
                    ? Composite(type, kind, sealedLike, arguments, arguments)
                    : Node.Leaf(type, TypeVerdict.Open, TypeVerdict.Open, kind) with { Components = arguments };
            }
        }

        var fields = new List<Type>();
        foreach (var field in InstanceFields(type))
        {
            fields.Add(field.FieldType);
        }

        return Composite(type, ScanKind.Fields, sealedLike, GenericArguments(type), [.. fields]);
    }

    /// <summary>A node whose exact verdict follows from its fields; a non-sealed class is statically open whatever its fields.</summary>
    private static Node Composite(Type type, ScanKind kind, bool sealedLike, Type[] components, Type[] fields)
    {
        var node = new Node(type, kind, sealedLike) { Components = components, Fields = fields };
        if (!sealedLike)
        {
            node.Static = TypeVerdict.Open;
        }

        return node;
    }

    private bool IsOwnAssemblyShared(Type type)
    {
        if (_policy is null)
        {
            return AssemblyLoadContext.GetLoadContext(type.Assembly) is null or { IsCollectible: false };
        }

        return _policy.IsShared(type);
    }

    private static Type[] GenericArguments(Type type) => type.IsConstructedGenericType ? type.GetGenericArguments() : [];

    private sealed record Node(Type Type, ScanKind Kind, bool SealedLike)
    {
        public TypeVerdict? Exact { get; set; }

        public TypeVerdict? Static { get; set; }

        public string? Reason { get; set; }

        public bool IsKnown { get; init; }

        public Type[] Components { get; init; } = [];

        public Type[] Fields { get; init; } = [];

        public static Node Leaf(Type type, TypeVerdict exact, TypeVerdict @static, ScanKind kind, string? reason = null) =>
            new(type, kind, type.IsSealed || type.IsValueType) { Exact = exact, Static = @static, Reason = reason };

        public static Node Known(Type type, TypeShape shape) =>
            new(type, shape.Kind, type.IsSealed || type.IsValueType) { Exact = shape.Exact, Static = shape.Static, Reason = shape.Reason, IsKnown = true };

        public TypeShape ToShape() => new() { Exact = Exact ?? TypeVerdict.Closed, Static = Static ?? TypeVerdict.Open, Kind = Kind, Reason = Reason };
    }
}
