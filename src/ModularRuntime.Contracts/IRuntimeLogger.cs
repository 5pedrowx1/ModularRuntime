namespace ModularRuntime.Contracts;

/// <summary>
/// Logging abstraction consumed by the engine and implemented by hosts.
/// </summary>
public interface IRuntimeLogger
{
    void Log(LogEntry entry);
}
