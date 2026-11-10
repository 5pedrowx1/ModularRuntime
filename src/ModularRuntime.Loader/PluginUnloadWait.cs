namespace ModularRuntime.Loader;

/// <summary>How <see cref="PluginRuntime.WaitForUnloadAsync(Contracts.PluginId, TimeSpan, PluginUnloadWait, CancellationToken)"/> waits.</summary>
public enum PluginUnloadWait
{
    /// <summary>Only observes collection; the runtime never forces a garbage collection.</summary>
    Observe,

    /// <summary>
    /// Also induces garbage collections while waiting. Meant for explicit maintenance operations
    /// that need the plugin's files released (update, removal) and for diagnostics.
    /// </summary>
    InduceCollection,
}
