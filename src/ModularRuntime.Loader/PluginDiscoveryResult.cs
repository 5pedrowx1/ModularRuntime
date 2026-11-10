namespace ModularRuntime.Loader;

/// <summary>Result of scanning a plugin root.</summary>
public sealed class PluginDiscoveryResult
{
    public PluginDiscoveryResult(
        IEnumerable<PluginCandidate> plugins,
        IEnumerable<PluginDiscoveryError> errors)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(errors);

        Plugins = Array.AsReadOnly(plugins.ToArray());
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public IReadOnlyList<PluginCandidate> Plugins { get; }

    public IReadOnlyList<PluginDiscoveryError> Errors { get; }

    public bool HasErrors => Errors.Count > 0;
}
