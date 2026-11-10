namespace ModularRuntime.Contracts;

/// <summary>
/// Structured log entry used by the runtime and by plugin-scoped loggers.
/// </summary>
/// <remarks>
/// <see cref="Diagnostic"/> is a text-only snapshot. A logger may retain entries in
/// memory without retaining exception objects created by plugin assemblies.
/// </remarks>
public sealed record LogEntry(
    LogLevel Level,
    string Message,
    string? Category = null,
    PluginId? PluginId = null,
    Guid? InstanceId = null,
    PluginDiagnostic? Diagnostic = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
