namespace ModularRuntime.Contracts;

/// <summary>
/// Logger available to plugin code through <see cref="IPluginContext.Logger"/>.
/// </summary>
public interface IPluginLogger
{
    PluginId PluginId { get; }

    Guid InstanceId { get; }

    void Log(LogLevel level, string message, Exception? exception = null);
}
