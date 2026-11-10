namespace ModularRuntime.Contracts;

/// <summary>
/// Runtime services exposed to a single plugin instance.
/// </summary>
/// <remarks>
/// The context is the plugin's entry point to the platform: logging, cooperative shutdown,
/// the event bus, the service registry, extension points, and resource ownership. All of
/// them are scoped to this instance and are cleaned up automatically when it stops or fails.
/// </remarks>
public interface IPluginContext : IRuntimeScope
{
    /// <summary>Unique identifier of this plugin instance.</summary>
    Guid InstanceId { get; }

    /// <summary>Stable identifier of the plugin.</summary>
    PluginId PluginId { get; }

    /// <summary>
    /// The plugin's validated manifest: name, version, API version, entry assembly and type, and
    /// start order. Added in plugin API 0.3.
    /// </summary>
    PluginDescriptor Descriptor { get; }

    /// <summary>Logger scoped to this plugin instance.</summary>
    IPluginLogger Logger { get; }

    /// <summary>
    /// Canceled when the runtime requests cooperative shutdown of this instance.
    /// Background work started by the plugin should observe this token.
    /// </summary>
    CancellationToken StoppingToken { get; }
}
