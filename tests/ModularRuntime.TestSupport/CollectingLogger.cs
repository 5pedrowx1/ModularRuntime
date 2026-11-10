using System.Collections.Concurrent;
using ModularRuntime.Contracts;

namespace ModularRuntime.TestSupport;

/// <summary>Thread-safe logger used to assert observable runtime behavior.</summary>
public sealed class CollectingLogger : IRuntimeLogger
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public void Log(LogEntry entry) => _entries.Enqueue(entry);

    public bool HasMessageContaining(string text) =>
        _entries.Any(entry => entry.Message.Contains(text, StringComparison.Ordinal));

    public LogEntry? Find(LogLevel level, string messagePart) =>
        _entries.FirstOrDefault(entry =>
            entry.Level == level &&
            entry.Message.Contains(messagePart, StringComparison.Ordinal));
}
