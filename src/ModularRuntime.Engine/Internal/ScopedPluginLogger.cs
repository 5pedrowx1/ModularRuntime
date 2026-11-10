using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Internal;

internal sealed class ScopedPluginLogger : IPluginLogger
{
    private readonly IRuntimeLogger _runtimeLogger;

    public ScopedPluginLogger(IRuntimeLogger runtimeLogger, PluginId pluginId, Guid instanceId)
    {
        _runtimeLogger = runtimeLogger;
        PluginId = pluginId;
        InstanceId = instanceId;
    }

    public PluginId PluginId { get; }

    public Guid InstanceId { get; }

    /// <remarks>
    /// The exception supplied by the plugin is converted to a text-only
    /// <see cref="PluginDiagnostic"/> before it reaches the host logger, so a host that
    /// retains log entries never retains plugin exception objects. A failing host logger
    /// is isolated from plugin code: logging never throws into the plugin.
    /// </remarks>
    public void Log(LogLevel level, string message, Exception? exception = null) =>
        SafeLogger.Log(
            _runtimeLogger,
            new LogEntry(
                level,
                message,
                Category: "Plugin",
                PluginId: PluginId,
                InstanceId: InstanceId,
                Diagnostic: PluginDiagnostic.FromExceptionOrNull(exception)));
}
