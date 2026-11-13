using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Buffers;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using ModularRuntime.Engine.Services;

namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Builds the inspection plan of an open type: a function that hands every reference an instance
/// holds to <see cref="ValueScan.Visit"/>. Plans read state only (instance fields, array elements,
/// the entries and comparers of framework collections, delegate targets, exception chains); they
/// never enumerate lazy sequences and never call plugin code.
/// </summary>
/// <remarks>
/// Field plans are generated as <see cref="DynamicMethod"/>s that read the fields directly (no
/// reflection per value, no boxing of reference fields), associated with the module of the
/// inspected type when it is collectible so they are collected with it (<see cref="DynamicCode"/>).
/// Plans are cached per analyzer in the type's <see cref="TypeShape"/> (weak key).
/// </remarks>
internal static class ValueScanPlans
{
    private static readonly MethodInfo VisitMethod = typeof(ValueScan).GetMethod(nameof(ValueScan.Visit))!;
    private static readonly Func<object, ValueScan, bool> Nothing = static (_, _) => true;

    public static Func<object, ValueScan, bool> GetPlan(SharedTypeAnalyzer analyzer, Type type, TypeShape shape)
    {
        if (shape.Plan is { } plan)
        {
            return plan;
        }

        lock (shape)
        {
            return shape.Plan ??= Build(analyzer, type, shape.Kind);
        }
    }

    private static Func<object, ValueScan, bool> Build(SharedTypeAnalyzer analyzer, Type type, ScanKind kind)
    {
        switch (kind)
        {
            case ScanKind.None:
                return Nothing;
            case ScanKind.Fields:
                return EmitFieldsPlan(analyzer, type, null);
            case ScanKind.Array:
                return ScanArray;
            case ScanKind.Delegate:
                return ScanDelegate;
            case ScanKind.Exception:
                var fields = EmitFieldsPlan(analyzer, type, typeof(Exception));
                return ReferenceEquals(fields, Nothing) ? ScanException : (value, scan) => fields(value, scan) && ScanException(value, scan);
            case ScanKind.Reflection:
                return ScanReflection;
            case ScanKind.GCHandle:
                return static (value, scan) => ((GCHandle)value).IsAllocated is false || scan.Visit(((GCHandle)value).Target, "Target");
            case ScanKind.Memory:
                return Generic(typeof(MemoryPlan<>), type.GetGenericArguments()[0]);
            case ScanKind.List:
                return Generic(typeof(ListPlan<>), type.GetGenericArguments()[0]);
            case ScanKind.HashSet:
            case ScanKind.SortedSet:
            case ScanKind.ImmutableHashSet:
            case ScanKind.ImmutableSortedSet:
                return GenericSet(analyzer, kind, type.GetGenericArguments()[0]);
            default:
                return GenericDictionary(analyzer, kind, type.GetGenericArguments()[0], type.GetGenericArguments()[1]);
        }
    }

    private static Func<object, ValueScan, bool> Generic(Type planDefinition, params Type[] arguments) =>
        planDefinition.MakeGenericType(arguments).GetMethod("Scan", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Func<object, ValueScan, bool>>();

    private static Func<object, ValueScan, bool> GenericSet(SharedTypeAnalyzer analyzer, ScanKind kind, Type item)
    {
        var items = analyzer.GetShape(item).Static != TypeVerdict.Closed;
        var scan = typeof(SetPlan<>).MakeGenericType(item).GetMethod(kind.ToString(), BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<object, ValueScan, bool, bool>>();
        return (value, state) => scan(value, state, items);
    }

    private static Func<object, ValueScan, bool> GenericDictionary(SharedTypeAnalyzer analyzer, ScanKind kind, Type key, Type value)
    {
        var entries = analyzer.GetShape(key).Static != TypeVerdict.Closed || analyzer.GetShape(value).Static != TypeVerdict.Closed;
        var scan = typeof(DictionaryPlan<,>).MakeGenericType(key, value).GetMethod(kind.ToString(), BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<object, ValueScan, bool, bool>>();
        return (instance, state) => scan(instance, state, entries);
    }

    /// <summary>
    /// A plan that reads the instance fields of <paramref name="type"/> (declared below
    /// <paramref name="stopAt"/>) whose static type is not closed, nested value-type fields inline.
    /// </summary>
    private static Func<object, ValueScan, bool> EmitFieldsPlan(SharedTypeAnalyzer analyzer, Type type, Type? stopAt)
    {
        var open = new List<(FieldInfo[] Path, string Label)>();
        CollectOpenFields(analyzer, type, stopAt, [], null, open, 0);
        if (open.Count == 0)
        {
            return Nothing;
        }

        var method = DynamicCode.Define($"ModularRuntime.Scan.{type.Name}", typeof(bool), [typeof(object), typeof(ValueScan)], DynamicCode.ModuleFor(type));
        var il = method.GetILGenerator();
        LocalBuilder instance;
        if (type.IsValueType)
        {
            instance = il.DeclareLocal(type.MakeByRefType());
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Unbox, type);
        }
        else
        {
            instance = il.DeclareLocal(type);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
        }

        il.Emit(OpCodes.Stloc, instance);
        var failed = il.DefineLabel();
        foreach (var (path, label) in open)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, instance);
            for (var step = 0; step < path.Length - 1; step++)
            {
                il.Emit(OpCodes.Ldflda, path[step]);
            }

            var field = path[^1];
            il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType.IsValueType)
            {
                il.Emit(OpCodes.Box, field.FieldType);
            }

            il.Emit(OpCodes.Ldstr, label);
            il.Emit(OpCodes.Callvirt, VisitMethod);
            il.Emit(OpCodes.Brfalse, failed);
        }

        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(failed);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object, ValueScan, bool>>();
    }

    private static void CollectOpenFields(SharedTypeAnalyzer analyzer, Type type, Type? stopAt, FieldInfo[] prefix, string? labelPrefix, List<(FieldInfo[], string)> open, int nesting)
    {
        foreach (var field in SharedTypeAnalyzer.InstanceFields(type, stopAt))
        {
            var fieldType = field.FieldType;
            if (fieldType.IsPointer || fieldType.IsFunctionPointer || fieldType.IsByRef)
            {
                continue;
            }

            var shape = analyzer.GetShape(fieldType);
            if (shape.Static == TypeVerdict.Closed)
            {
                continue;
            }

            var path = new FieldInfo[prefix.Length + 1];
            prefix.CopyTo(path, 0);
            path[^1] = field;
            var label = labelPrefix is null ? $"field '{field.Name}'" : $"{labelPrefix}.{field.Name}";
            if (fieldType.IsValueType && shape.Kind == ScanKind.Fields && nesting < 8)
            {
                // A struct field is read in place (no boxing); its own open fields are listed under it.
                CollectOpenFields(analyzer, fieldType, null, path, label, open, nesting + 1);
            }
            else
            {
                open.Add((path, label));
            }
        }
    }

    private static bool ScanArray(object value, ValueScan scan)
    {
        if (value is object?[] references)
        {
            for (var index = 0; index < references.Length; index++)
            {
                if (!scan.VisitAt(references[index], "element", index))
                {
                    return false;
                }
            }

            return true;
        }

        var position = 0;
        foreach (var element in (Array)value)
        {
            if (!scan.VisitAt(element, "element", position++))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ScanDelegate(object value, ValueScan scan)
    {
        var handler = (Delegate)value;
        if (handler.HasSingleTarget)
        {
            return ScanDelegateTarget(handler, scan);
        }

        foreach (var target in Delegate.EnumerateInvocationList(handler))
        {
            if (!ScanDelegateTarget(target, scan))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ScanDelegateTarget(Delegate target, ValueScan scan)
    {
        var method = target.Method;
        if (method.DeclaringType is { } declaring && !scan.VisitType(declaring, "target method", $"the target method '{method.Name}' of {SharedTypeAnalyzer.Describe(declaring)}"))
        {
            return false;
        }

        if (method.IsGenericMethod)
        {
            foreach (var argument in method.GetGenericArguments())
            {
                if (!scan.VisitType(argument, "target method", $"the target method '{method.Name}' instantiated over {SharedTypeAnalyzer.Describe(argument)}"))
                {
                    return false;
                }
            }
        }

        return target.Target is not { } instance || scan.Visit(instance, "target object");
    }

    private static bool ScanException(object value, ValueScan scan)
    {
        var exception = (Exception)value;
        if (exception is AggregateException aggregate)
        {
            var position = 0;
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (!scan.VisitAt(inner, "InnerExceptions", position++))
                {
                    return false;
                }
            }
        }
        else if (exception.InnerException is { } inner && !scan.Visit(inner, "InnerException"))
        {
            return false;
        }

        var data = exception.Data;
        if (data.Count == 0)
        {
            return true;
        }

        foreach (DictionaryEntry entry in data)
        {
            if (!scan.Visit(entry.Key, "Data key") || !scan.Visit(entry.Value, "Data value"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ScanReflection(object value, ValueScan scan)
    {
        switch (value)
        {
            case Type type:
                return scan.VisitType(type, "Type");
            case MethodBase method:
                if (method.DeclaringType is { } declaring && !scan.VisitType(declaring, "DeclaringType"))
                {
                    return false;
                }

                if (method.IsGenericMethod)
                {
                    foreach (var argument in method.GetGenericArguments())
                    {
                        if (!scan.VisitType(argument, "generic argument"))
                        {
                            return false;
                        }
                    }
                }

                return true;
            case MemberInfo member:
                return member.DeclaringType is not { } owner || scan.VisitType(owner, "DeclaringType");
            case Assembly assembly:
                return scan.VisitAssembly(assembly, "Assembly");
            case Module module:
                return scan.VisitAssembly(module.Assembly, "Module");
            case RuntimeTypeHandle typeHandle:
                return typeHandle.Value == IntPtr.Zero || scan.VisitType(Type.GetTypeFromHandle(typeHandle)!, "RuntimeTypeHandle");
            case RuntimeMethodHandle methodHandle:
                return methodHandle.Value == IntPtr.Zero || ScanReflection(MethodBase.GetMethodFromHandle(methodHandle)!, scan);
            case RuntimeFieldHandle fieldHandle:
                return fieldHandle.Value == IntPtr.Zero || ScanReflection(FieldInfo.GetFieldFromHandle(fieldHandle), scan);
            default:
                return true;
        }
    }

    private static class MemoryPlan<T>
    {
        public static bool Scan(object value, ValueScan scan)
        {
            var memory = value is Memory<T> mutable ? mutable : (ReadOnlyMemory<T>)value;
            if (MemoryMarshal.TryGetMemoryManager<T, MemoryManager<T>>(memory, out var manager))
            {
                return scan.Visit(manager, "MemoryManager");
            }

            return !MemoryMarshal.TryGetArray(memory, out var segment) || scan.Visit(segment.Array, "array");
        }
    }

    private static class ListPlan<T>
    {
        public static bool Scan(object value, ValueScan scan)
        {
            var list = (List<T>)value;
            for (var index = 0; index < list.Count; index++)
            {
                if (!scan.VisitAt(list[index], "element", index))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static class SetPlan<T>
    {
        public static bool HashSet(object value, ValueScan scan, bool items)
        {
            var set = (HashSet<T>)value;
            return scan.Visit(set.Comparer, "Comparer") && (!items || Items(set, scan));
        }

        public static bool SortedSet(object value, ValueScan scan, bool items)
        {
            var set = (SortedSet<T>)value;
            return scan.Visit(set.Comparer, "Comparer") && (!items || Items(set, scan));
        }

        public static bool ImmutableHashSet(object value, ValueScan scan, bool items)
        {
            var set = (ImmutableHashSet<T>)value;
            return scan.Visit(set.KeyComparer, "KeyComparer") && (!items || Items(set, scan));
        }

        public static bool ImmutableSortedSet(object value, ValueScan scan, bool items)
        {
            var set = (ImmutableSortedSet<T>)value;
            return scan.Visit(set.KeyComparer, "KeyComparer") && (!items || Items(set, scan));
        }

        private static bool Items(IEnumerable<T> items, ValueScan scan)
        {
            var index = 0;
            foreach (var item in items)
            {
                if (!scan.VisitAt(item, "element", index++))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static class DictionaryPlan<TKey, TValue>
        where TKey : notnull
    {
        public static bool Dictionary(object value, ValueScan scan, bool entries)
        {
            var dictionary = (Dictionary<TKey, TValue>)value;
            return scan.Visit(dictionary.Comparer, "Comparer") && (!entries || Entries(dictionary, scan));
        }

        public static bool SortedDictionary(object value, ValueScan scan, bool entries)
        {
            var dictionary = (SortedDictionary<TKey, TValue>)value;
            return scan.Visit(dictionary.Comparer, "Comparer") && (!entries || Entries(dictionary, scan));
        }

        public static bool SortedList(object value, ValueScan scan, bool entries)
        {
            var list = (SortedList<TKey, TValue>)value;
            return scan.Visit(list.Comparer, "Comparer") && (!entries || Entries(list, scan));
        }

        public static bool ConcurrentDictionary(object value, ValueScan scan, bool entries)
        {
            var dictionary = (ConcurrentDictionary<TKey, TValue>)value;
            return scan.Visit(dictionary.Comparer, "Comparer") && (!entries || Entries(dictionary, scan));
        }

        public static bool ImmutableDictionary(object value, ValueScan scan, bool entries)
        {
            var dictionary = (ImmutableDictionary<TKey, TValue>)value;
            return scan.Visit(dictionary.KeyComparer, "KeyComparer") && scan.Visit(dictionary.ValueComparer, "ValueComparer") && (!entries || Entries(dictionary, scan));
        }

        public static bool ImmutableSortedDictionary(object value, ValueScan scan, bool entries)
        {
            var dictionary = (ImmutableSortedDictionary<TKey, TValue>)value;
            return scan.Visit(dictionary.KeyComparer, "KeyComparer") && scan.Visit(dictionary.ValueComparer, "ValueComparer") && (!entries || Entries(dictionary, scan));
        }

        private static bool Entries(IEnumerable<KeyValuePair<TKey, TValue>> entries, ValueScan scan)
        {
            var index = 0;
            foreach (var entry in entries)
            {
                if (!scan.VisitAt(entry.Key, "key", index) || !scan.VisitAt(entry.Value, "value", index))
                {
                    return false;
                }

                index++;
            }

            return true;
        }
    }
}
