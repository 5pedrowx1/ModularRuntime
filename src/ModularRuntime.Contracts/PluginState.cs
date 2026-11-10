namespace ModularRuntime.Contracts;

/// <summary>
/// Lifecycle states of a registered plugin instance.
/// </summary>
public enum PluginState
{
    /// <summary>The plugin metadata was registered but not validated by the runtime.</summary>
    Discovered = 0,

    /// <summary>Metadata, paths, and API compatibility were validated. The plugin may be started.</summary>
    Validated = 1,

    /// <summary>The plugin instance was created. Initialization has not completed yet.</summary>
    Loaded = 2,

    /// <summary>Initialization completed successfully and the plugin is active.</summary>
    Running = 3,

    /// <summary>Shutdown was requested and is in progress.</summary>
    Stopping = 4,

    /// <summary>Shutdown completed successfully. A stopped plugin is not restarted on the same instance.</summary>
    Stopped = 5,

    /// <summary>A load, initialization, or shutdown operation failed. This state is terminal for the instance.</summary>
    Failed = 6,
}
