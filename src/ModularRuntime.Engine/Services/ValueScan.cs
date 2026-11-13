using System.ComponentModel;
using System.Reflection;
using ModularRuntime.Engine.Internal;

namespace ModularRuntime.Engine.Services;

/// <summary>
/// State of one bounded inspection of a value crossing a typed boundary. Infrastructure for
/// generated inspection plans: plans call <see cref="Visit"/> for every reference a value holds.
/// </summary>
/// <remarks>
/// <para>
/// The scan is iterative (a worklist, breadth first), never recursive: a plan reports the
/// references of one object and open objects are queued, so no graph can exhaust the stack. It
/// examines at most <see cref="RuntimeLimits.MaxValueScanReferences"/> references, enters each
/// object once (cyclic graphs terminate), refuses objects farther than
/// <see cref="RuntimeLimits.MaxValueScanDepth"/> hops from the root, never enumerates lazy
/// sequences, and never calls plugin code. Reaching a limit, or a plan that throws, ends the
/// scan as <em>not verifiable</em>: the runtime fails closed.
/// </para>
/// <para>
/// One instance per thread is reused and cleared after every scan, so it never retains what it
/// inspected and a scan of a flat open value allocates nothing.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ValueScan
{
    private const int SmallVisitedCapacity = 8;

    [ThreadStatic]
    private static ValueScan? t_cached;

    private SharedTypeAnalyzer _analyzer = null!;
    private Entry[] _entries = new Entry[16];
    private int _count;
    private int _current;
    private readonly object?[] _smallVisited = new object?[SmallVisitedCapacity];
    private int _smallVisitedCount;
    private HashSet<object>? _visited;
    private int _remaining;
    private int _maxDepth;
    private ValueBoundary _boundary;
    private int _failParent = -1;
    private string? _failLabel;
    private int _failIndex;

    private ValueScan()
    {
    }

    /// <summary>The private type found, when the scan failed on one.</summary>
    internal Type? Offending { get; private set; }

    /// <summary>What the offending reference is ("a value of type …", "the target method …"), when the scan failed on a private type.</summary>
    internal string? OffendingDescription { get; private set; }

    /// <summary>Why the value could not be verified, when the scan failed without finding a private type.</summary>
    internal string? LimitReached { get; private set; }

    internal bool Failed => _failParent >= 0 || _failLabel is not null;

    /// <summary>The path from the boundary to the failing reference, such as <c>argument 'items' → field 'Tags' → element [3]</c>.</summary>
    internal string Path
    {
        get
        {
            if (!Failed)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            for (var entry = _failParent; entry >= 0; entry = _entries[entry].Parent)
            {
                parts.Add(Describe(_entries[entry].Label, _entries[entry].Index, entry == 0));
            }

            parts.Reverse();
            if (_failLabel is not null)
            {
                parts.Add(Describe(_failLabel, _failIndex, parts.Count == 0));
            }

            return string.Join(" → ", parts);
        }
    }

    internal static ValueScan Rent(SharedTypeAnalyzer analyzer, int maxReferences, int maxDepth)
    {
        var scan = t_cached ?? new ValueScan();
        t_cached = null;
        scan._analyzer = analyzer;
        scan._remaining = maxReferences;
        scan._maxDepth = maxDepth;
        scan._count = 0;
        scan._current = -1;
        scan._failParent = -1;
        scan._failLabel = null;
        scan._failIndex = -1;
        scan.Offending = null;
        scan.OffendingDescription = null;
        scan.LimitReached = null;
        return scan;
    }

    /// <summary>Clears every reference the scan held (it must not retain plugin objects) and caches the instance for the thread.</summary>
    internal void Return()
    {
        Array.Clear(_entries, 0, _count);
        _count = 0;
        Array.Clear(_smallVisited);
        _smallVisitedCount = 0;
        _visited?.Clear();
        _analyzer = null!;
        Offending = null;
        t_cached = this;
    }

    /// <summary>Inspects the value crossing <paramref name="boundary"/> (an open value; closed ones are settled by the caller).</summary>
    internal bool Scan(object value, in ValueBoundary boundary)
    {
        _boundary = boundary;
        _current = -1;
        if (!VisitCore(value, string.Empty, -1))
        {
            return false;
        }

        // Breadth-first over the queued open objects; each plan reports the object's references.
        for (_current = 0; _current < _count; _current++)
        {
            var entry = _entries[_current];
            try
            {
                if (!ValueScanPlans.GetPlan(_analyzer, entry.Value.GetType(), entry.Shape)(entry.Value, this))
                {
                    return false;
                }
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                // A plan that fails (a collection modified concurrently, a member that throws) is not
                // evidence either way: the value is not verifiable.
                _failParent = entry.Parent;
                _failLabel = entry.Label;
                _failIndex = entry.Index;
                LimitReached = $"the inspection of {SharedTypeAnalyzer.Describe(entry.Value.GetType())} failed with {exception.GetType().Name}";
                return false;
            }
        }

        return true;
    }

    /// <summary>Inspects one reference. Returns <see langword="false"/> when the scan has failed and must stop.</summary>
    public bool Visit(object? value, string label) => VisitCore(value, label, -1);

    /// <summary>Inspects one element of a collection. Returns <see langword="false"/> when the scan has failed and must stop.</summary>
    public bool VisitAt(object? value, string label, int index) => VisitCore(value, label, index);

    /// <summary>Inspects a type reached through reflection (a delegate's target method, a <see cref="Type"/> value).</summary>
    internal bool VisitType(Type type, string label, string? description = null)
    {
        var shape = _analyzer.GetShape(type);
        switch (shape.Static)
        {
            case TypeVerdict.Private:
                FailPrivate(type, description ?? $"a reference to the type {SharedTypeAnalyzer.Describe(type)}", label, -1);
                return false;
            case TypeVerdict.Unverifiable:
                FailLimit(shape.Reason ?? $"the type {SharedTypeAnalyzer.Describe(type)} cannot be analyzed", label, -1);
                return false;
            default:
                return true;
        }
    }

    internal bool VisitAssembly(Assembly assembly, string label)
    {
        if (_analyzer.IsSharedAssembly(assembly))
        {
            return true;
        }

        FailPrivate(null, $"a reference to assembly '{assembly.GetName().Name}', which is not shared", label, -1);
        return false;
    }

    private bool VisitCore(object? value, string label, int index)
    {
        if (value is null)
        {
            return true;
        }

        if (--_remaining < 0)
        {
            FailLimit("more references than RuntimeLimits.MaxValueScanReferences allows", label, index);
            return false;
        }

        var type = value.GetType();
        var shape = _analyzer.GetShape(type);
        switch (shape.Exact)
        {
            case TypeVerdict.Closed:
                return true;
            case TypeVerdict.Private:
                FailPrivate(type, $"a value of type {SharedTypeAnalyzer.Describe(type)}", label, index);
                return false;
            case TypeVerdict.Unverifiable:
                FailLimit(shape.Reason ?? $"the type {SharedTypeAnalyzer.Describe(type)} cannot be analyzed", label, index);
                return false;
        }

        if (!type.IsValueType && !MarkVisited(value))
        {
            return true;
        }

        var depth = _current < 0 ? 0 : _entries[_current].Depth + 1;
        if (depth >= _maxDepth)
        {
            FailLimit("deeper nesting than RuntimeLimits.MaxValueScanDepth allows", label, index);
            return false;
        }

        if (_count == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }

        _entries[_count++] = new Entry(value, shape, label, index, _current, depth);
        return true;
    }

    /// <summary>Records an object as entered; false when it was entered already (a cycle or a shared sub-object).</summary>
    private bool MarkVisited(object value)
    {
        if (_visited is null)
        {
            for (var slot = 0; slot < _smallVisitedCount; slot++)
            {
                if (ReferenceEquals(_smallVisited[slot], value))
                {
                    return false;
                }
            }

            if (_smallVisitedCount < SmallVisitedCapacity)
            {
                _smallVisited[_smallVisitedCount++] = value;
                return true;
            }

            _visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var seen in _smallVisited)
            {
                _visited.Add(seen!);
            }
        }

        return _visited.Add(value);
    }

    private void FailPrivate(Type? type, string description, string label, int index)
    {
        _failParent = _current;
        _failLabel = label;
        _failIndex = index;
        Offending = type;
        OffendingDescription = description;
    }

    private void FailLimit(string reason, string label, int index)
    {
        _failParent = _current;
        _failLabel = label;
        _failIndex = index;
        LimitReached = reason;
    }

    private string Describe(string label, int index, bool root)
    {
        var text = root ? _boundary.ToString() : label;
        return index >= 0 ? $"{text} [{index}]" : text;
    }

    private readonly record struct Entry(object Value, TypeShape Shape, string Label, int Index, int Parent, int Depth);
}
