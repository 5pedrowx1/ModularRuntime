using System.Reflection;
using System.Runtime.Loader;
using ModularRuntime.Contracts;

namespace ModularRuntime.Loader.Internal;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ContractsAssembly = typeof(IPlugin).Assembly;
    private static readonly AssemblyName ContractsName = ContractsAssembly.GetName();

    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, Assembly> _sharedContracts;

    public PluginLoadContext(PluginId pluginId, string mainAssemblyPath, IReadOnlyDictionary<string, Assembly>? sharedContracts = null)
        : base($"ModularRuntime.Plugin.{pluginId}.{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _sharedContracts = sharedContracts ?? new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractsName.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Shared contract identity: never load a private copy of ModularRuntime.Contracts.dll.
            return ContractsAssembly;
        }

        if (assemblyName.Name is not null && _sharedContracts.TryGetValue(assemblyName.Name, out var shared))
        {
            // A contract assembly declared in the manifest: the shared copy, never the private file.
            return shared;
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
