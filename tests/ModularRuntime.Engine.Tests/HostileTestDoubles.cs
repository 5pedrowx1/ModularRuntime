using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

/// <summary>
/// Exception whose diagnostic members throw, as a hostile or buggy plugin exception might.
/// </summary>
internal sealed class HostileException : Exception
{
    public override string Message => throw new InvalidOperationException("Message getter failed.");

    public override string? StackTrace => throw new InvalidOperationException("StackTrace getter failed.");

    public override string ToString() => throw new InvalidOperationException("ToString failed.");
}

/// <summary>Host logger that fails on every call.</summary>
internal sealed class ThrowingRuntimeLogger : IRuntimeLogger
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Log(LogEntry entry)
    {
        Interlocked.Increment(ref _calls);
        throw new InvalidOperationException("intentional host logger failure");
    }
}
