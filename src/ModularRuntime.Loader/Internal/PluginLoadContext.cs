using System.Reflection;
using System.Runtime.Loader;
using ModularRuntime.Contracts;

namespace ModularRuntime.Loader.Internal;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ContractsAssembly = typeof(IPlugin).Assembly;
    private static readonly AssemblyName ContractsName = ContractsAssembly.GetName();

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(PluginId pluginId, string mainAssemblyPath)
        : base($"ModularRuntime.Plugin.{pluginId}.{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractsName.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Shared contract identity: never load a private copy of ModularRuntime.Contracts.dll.
            return ContractsAssembly;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
