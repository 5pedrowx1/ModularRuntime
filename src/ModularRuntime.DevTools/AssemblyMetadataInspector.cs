using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ModularRuntime.Contracts;

namespace ModularRuntime.DevTools;

/// <summary>
/// Checks a built plugin assembly by reading its metadata tables with
/// <see cref="System.Reflection.Metadata"/>. The assembly is never loaded into the process, so no
/// type initializer, constructor, or other plugin code runs.
/// </summary>
internal static class AssemblyMetadataInspector
{
    private const string ContractsAssemblyName = "ModularRuntime.Contracts";
    private const string PluginInterface = "ModularRuntime.Contracts.IPlugin";

    /// <summary>Host-side assemblies a plugin must never reference: they are not part of the plugin API.</summary>
    internal static readonly string[] HostAssemblies =
    [
        "ModularRuntime.Engine", "ModularRuntime.Loader", "ModularRuntime.Daemon", "ModularRuntime.Ipc",
        "ModularRuntime.Ipc.Contracts", "ModularRuntime.Packaging", "ModularRuntime.Registry",
        "ModularRuntime.Desktop", "ModularRuntime.Cli", "ModularRuntime.Publisher", "ModularRuntime.DevTools",
    ];

    public static void Inspect(string assemblyPath, string entryType, ValidationReport report)
    {
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                report.Error(DiagnosticCodes.EntryAssemblyInvalid, "The entry assembly is not a .NET assembly.", Path.GetFileName(assemblyPath));
                return;
            }

            var metadata = pe.GetMetadataReader();
            CheckReferences(metadata, report);
            CheckEntryType(metadata, entryType, report);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or InvalidOperationException)
        {
            report.Error(DiagnosticCodes.EntryAssemblyInvalid, $"The entry assembly could not be read: {exception.Message}", Path.GetFileName(assemblyPath));
        }
    }

    private static void CheckReferences(MetadataReader metadata, ValidationReport report)
    {
        var referencesContracts = false;
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            if (string.Equals(name, ContractsAssemblyName, StringComparison.Ordinal))
            {
                referencesContracts = true;
                var version = reference.Version;
                if (version.Major != PluginApiVersion.Supported.Major || version.Minor != PluginApiVersion.Supported.Minor)
                {
                    report.Error(
                        DiagnosticCodes.ApiVersionMismatch,
                        $"The assembly is built against ModularRuntime.Contracts {version.Major}.{version.Minor} (plugin API {version.Major}.{version.Minor}); this SDK targets plugin API {PluginApiVersion.Supported.ToString(2)}. Update the ModularRuntime.Sdk/Contracts package reference.");
                }
            }
            else if (HostAssemblies.Contains(name, StringComparer.Ordinal))
            {
                report.Error(DiagnosticCodes.HostAssemblyReferenced, $"The assembly references '{name}', a host-side ModularRuntime assembly. Plugins may only use ModularRuntime.Contracts (and ModularRuntime.Sdk).");
            }
        }

        if (!referencesContracts)
        {
            report.Error(DiagnosticCodes.ContractsReferenceMissing, "The assembly does not reference ModularRuntime.Contracts, so it cannot implement IPlugin.");
        }
    }

    private static void CheckEntryType(MetadataReader metadata, string entryType, ValidationReport report)
    {
        var definition = Find(metadata, entryType);
        if (definition is not { } type)
        {
            report.Error(DiagnosticCodes.EntryTypeNotFound, $"Entry type '{entryType}' is not defined in the entry assembly.");
            return;
        }

        var attributes = type.Attributes;
        if ((attributes & TypeAttributes.VisibilityMask) is not (TypeAttributes.Public or TypeAttributes.NestedPublic))
        {
            report.Error(DiagnosticCodes.EntryTypeNotInstantiable, $"Entry type '{entryType}' must be public.");
        }

        if ((attributes & TypeAttributes.Interface) != 0 || (attributes & TypeAttributes.Abstract) != 0)
        {
            report.Error(DiagnosticCodes.EntryTypeNotInstantiable, $"Entry type '{entryType}' must be a concrete (non-abstract) class.");
        }

        switch (ImplementsPlugin(metadata, type, depth: 0))
        {
            case false:
                report.Error(DiagnosticCodes.EntryTypeNotAPlugin, $"Entry type '{entryType}' does not implement {PluginInterface}.");
                break;
            case null:
                report.Warning(DiagnosticCodes.EntryTypeNotAPlugin, $"Entry type '{entryType}' derives from a type in another assembly; whether it implements IPlugin is checked when the plugin loads.");
                break;
        }

        var constructors = type.GetMethods().Select(metadata.GetMethodDefinition).Where(m => metadata.GetString(m.Name) == ".ctor").ToList();
        if (constructors.Count > 0 && !constructors.Any(c => (c.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public && ParameterCount(metadata, c) == 0))
        {
            report.Error(DiagnosticCodes.EntryTypeNotInstantiable, $"Entry type '{entryType}' needs a public parameterless constructor.");
        }
    }

    private static TypeDefinition? Find(MetadataReader metadata, string fullName)
    {
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (string.Equals(FullName(metadata, type), fullName, StringComparison.Ordinal))
            {
                return type;
            }
        }

        return null;
    }

    private static string FullName(MetadataReader metadata, TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return FullName(metadata, metadata.GetTypeDefinition(declaring)) + "+" + name;
        }

        var ns = metadata.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>true / false when decidable from this assembly; null when a base type lives elsewhere.</summary>
    private static bool? ImplementsPlugin(MetadataReader metadata, TypeDefinition type, int depth)
    {
        if (depth > 32)
        {
            return null;
        }

        foreach (var implementation in type.GetInterfaceImplementations().Select(metadata.GetInterfaceImplementation))
        {
            if (TypeName(metadata, implementation.Interface) == PluginInterface)
            {
                return true;
            }
        }

        var baseType = type.BaseType;
        if (baseType.IsNil)
        {
            return false;
        }

        if (baseType.Kind == HandleKind.TypeDefinition)
        {
            return ImplementsPlugin(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)baseType), depth + 1);
        }

        return TypeName(metadata, baseType) is "System.Object" ? false : null;
    }

    private static string? TypeName(MetadataReader metadata, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => metadata.GetTypeReference((TypeReferenceHandle)handle) is var reference
            ? metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name)
            : null,
        HandleKind.TypeDefinition => FullName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)handle)),
        _ => null,
    };

    private static int ParameterCount(MetadataReader metadata, MethodDefinition method)
    {
        var reader = metadata.GetBlobReader(method.Signature);
        _ = reader.ReadSignatureHeader();
        return reader.ReadCompressedInteger();
    }
}
