using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ModularRuntime.Contracts;

namespace ModularRuntime.Loader;

/// <summary>
/// Rules a shared contract assembly (manifest <c>sharedContracts</c>) must follow, shared by the
/// loader (enforced before loading) and the developer tool (reported at validation).
/// </summary>
public static class SharedContractRules
{
    private static readonly Lazy<HashSet<string>> Framework = new(LoadFrameworkAssemblyNames);

    /// <summary>
    /// A contract assembly may reference only the framework, <c>ModularRuntime.Contracts</c>, and
    /// the other shared contracts declared by the same plugin: anything else would leak a plugin's
    /// private dependency into every plugin that shares the contract.
    /// </summary>
    public static bool IsAllowedReference(string referencedAssemblyName, IReadOnlyList<string> declaredSharedContracts)
    {
        ArgumentNullException.ThrowIfNull(referencedAssemblyName);
        ArgumentNullException.ThrowIfNull(declaredSharedContracts);
        return Framework.Value.Contains(referencedAssemblyName)
            || string.Equals(referencedAssemblyName, PluginDescriptor.ReservedSharedContractName, StringComparison.OrdinalIgnoreCase)
            || declaredSharedContracts.Contains(referencedAssemblyName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="assemblyName"/> is part of the shared framework of this process.</summary>
    public static bool IsFrameworkAssembly(string assemblyName) => Framework.Value.Contains(assemblyName);

    /// <summary>
    /// Reads the simple name, version, and references of an assembly file as metadata (nothing is
    /// loaded). Throws <see cref="BadImageFormatException"/> or an I/O exception for other files.
    /// </summary>
    public static (string Name, Version Version, IReadOnlyList<AssemblyName> References) ReadIdentity(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadIdentity(stream);
    }

    public static (string Name, Version Version, IReadOnlyList<AssemblyName> References) ReadIdentity(Stream stream)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException("The file has no managed metadata.");
        }

        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            throw new BadImageFormatException("The file is a module, not an assembly.");
        }

        var definition = reader.GetAssemblyDefinition();
        var references = new List<AssemblyName>(reader.AssemblyReferences.Count);
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            references.Add(new AssemblyName(reader.GetString(reference.Name)) { Version = reference.Version });
        }

        return (reader.GetString(definition.Name), definition.Version, references);
    }

    private static HashSet<string> LoadFrameworkAssemblyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
        {
            foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(Path.GetFileNameWithoutExtension(path));
            }
        }

        return names;
    }
}
