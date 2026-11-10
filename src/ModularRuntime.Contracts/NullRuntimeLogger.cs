namespace ModularRuntime.Contracts;

/// <summary>Logger that discards every entry. Useful when a host does not want runtime logs.</summary>
public sealed class NullRuntimeLogger : IRuntimeLogger
{
    public static NullRuntimeLogger Instance { get; } = new();

    private NullRuntimeLogger()
    {
    }

    public void Log(LogEntry entry)
    {
    }
}
