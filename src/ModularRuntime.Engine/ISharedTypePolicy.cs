using System.Reflection;
using System.Runtime.Loader;

namespace ModularRuntime.Engine;

/// <summary>
/// Decides whether a type is <em>shared</em>: safe to pass across plugin boundaries through typed
/// services, callbacks, and streams without retaining a plugin's private load context. The loader
/// installs a policy that knows its shared contract contexts; the engine alone treats the default
/// load context as shared.
/// </summary>
/// <remarks>
/// The policy answers for a type's own assembly only. The engine applies it structurally: a
/// constructed generic type or an array is shared only when its type arguments and element type
/// are, and a value is shared only when everything it references is (see
/// <see cref="RuntimeLimits.TypedValueCheck"/>).
/// </remarks>
public interface ISharedTypePolicy
{
    /// <summary>True when values of <paramref name="type"/> may cross typed boundaries.</summary>
    bool IsShared(Type type);

    /// <summary>True when the types of <paramref name="assembly"/> may cross typed boundaries (used for reflection objects such as <see cref="Assembly"/>).</summary>
    bool IsSharedAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return AssemblyLoadContext.GetLoadContext(assembly) is null or { IsCollectible: false };
    }
}

/// <summary>Shared types are those whose assembly lives in <see cref="AssemblyLoadContext.Default"/>.</summary>
public sealed class DefaultSharedTypePolicy : ISharedTypePolicy
{
    public static DefaultSharedTypePolicy Instance { get; } = new();

    private DefaultSharedTypePolicy()
    {
    }

    public bool IsShared(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return AssemblyLoadContext.GetLoadContext(type.Assembly) is null or { IsCollectible: false };
    }
}
