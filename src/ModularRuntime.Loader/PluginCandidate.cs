using ModularRuntime.Contracts;

namespace ModularRuntime.Loader;

/// <summary>A validated plugin installation ready to be registered with the engine.</summary>
public sealed record PluginCandidate(
    PluginDescriptor Descriptor,
    string PluginDirectory,
    string ManifestPath,
    string EntryAssemblyPath)
{
    /// <summary>Optional manifest description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional manifest author.</summary>
    public string? Author { get; init; }
}
