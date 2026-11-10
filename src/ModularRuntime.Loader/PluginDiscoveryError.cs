using ModularRuntime.Contracts;

namespace ModularRuntime.Loader;

/// <summary>
/// Describes one rejected plugin installation discovered under the plugin root.
/// </summary>
/// <remarks>
/// <see cref="Diagnostic"/> is a text-only snapshot; discovery reports never retain
/// exception objects from plugin assemblies.
/// </remarks>
public sealed record PluginDiscoveryError(
    string Code,
    string Message,
    string PluginDirectory,
    string? ManifestPath = null,
    string? PluginId = null,
    string? Field = null,
    PluginDiagnostic? Diagnostic = null);
