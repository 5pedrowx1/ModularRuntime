using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Chooses where runtime-generated code lives so that it never keeps a collectible load context
/// alive on its own.
/// </summary>
/// <remarks>
/// A <see cref="DynamicMethod"/> associated with a module of a collectible assembly is owned by
/// that assembly's loader allocator and collected with it; one associated with the engine's
/// module is permanent and may only reference non-collectible types. The engine therefore
/// associates generated code with the module of the first collectible type the code references
/// (a shared contract type, or a framework generic instantiated over one), and with its own
/// module otherwise.
/// </remarks>
internal static class DynamicCode
{
    /// <summary>The module to associate generated code with, given the types it references.</summary>
    public static Module ModuleFor(params ReadOnlySpan<Type> types)
    {
        foreach (var type in types)
        {
            if (FindCollectible(type, 0) is { } collectible)
            {
                return collectible.Module;
            }
        }

        return typeof(DynamicCode).Module;
    }

    /// <summary>The load context generated types for <paramref name="type"/> must be created in.</summary>
    public static AssemblyLoadContext ContextFor(Type type) =>
        (FindCollectible(type, 0) is { } collectible ? AssemblyLoadContext.GetLoadContext(collectible.Assembly) : null) ?? AssemblyLoadContext.Default;

    public static DynamicMethod Define(string name, Type returnType, Type[] parameterTypes, Module module) =>
        new(name, returnType, parameterTypes, module, skipVisibility: true);

    /// <summary>True when the identity of <paramref name="type"/> involves a collectible assembly.</summary>
    public static bool IsCollectible(Type type) => FindCollectible(type, 0) is not null;

    private static Type? FindCollectible(Type type, int depth)
    {
        if (depth > 16 || type.IsGenericParameter)
        {
            return null;
        }

        if (type.HasElementType)
        {
            return type.GetElementType() is { } element ? FindCollectible(element, depth + 1) : null;
        }

        if (AssemblyLoadContext.GetLoadContext(type.Assembly) is { IsCollectible: true })
        {
            return type;
        }

        if (type.IsConstructedGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (FindCollectible(argument, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
