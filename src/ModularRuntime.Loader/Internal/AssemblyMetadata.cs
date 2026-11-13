using System.Collections.Immutable;
using System.Reflection;

namespace ModularRuntime.Loader.Internal;

/// <summary>Identity and references of an assembly file, read as metadata only (nothing is loaded).</summary>
internal sealed record AssemblyMetadata(string Name, Version Version, ImmutableArray<AssemblyName> References)
{
    /// <summary>Reads <paramref name="path"/>. Throws <see cref="BadImageFormatException"/> or an I/O exception when it is not a managed assembly.</summary>
    public static AssemblyMetadata Read(string path)
    {
        var (name, version, references) = SharedContractRules.ReadIdentity(path);
        return new AssemblyMetadata(name, version, [.. references]);
    }

    public static AssemblyMetadata Read(Stream stream)
    {
        var (name, version, references) = SharedContractRules.ReadIdentity(stream);
        return new AssemblyMetadata(name, version, [.. references]);
    }

    /// <summary>The version this assembly references <paramref name="assemblyName"/> with, or <see langword="null"/> when it does not reference it.</summary>
    public Version? ReferencedVersion(string assemblyName) =>
        References.FirstOrDefault(reference => string.Equals(reference.Name, assemblyName, StringComparison.OrdinalIgnoreCase))?.Version;
}
