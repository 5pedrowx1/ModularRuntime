namespace Shared.Contracts;

/// <summary>
/// The typed service contract shared by the provider and its consumers. Use only types from this
/// assembly, the framework, or ModularRuntime.Contracts in signatures, and prefer closed types
/// (sealed records and structs of primitives and strings, typed arrays): the runtime refuses any
/// value that references an object private to a plugin, and never inspects closed values. Hand
/// callbacks over through context.TypedServices.RegisterCallback, not as raw delegates.
/// </summary>
public interface ICounterService
{
    /// <summary>Adds <paramref name="by"/> and returns the new count.</summary>
    int Increment(int by);

    /// <summary>The current count.</summary>
    int Count { get; }

    /// <summary>An asynchronous operation; the token is linked to the provider's lifetime.</summary>
    Task<CounterSnapshot> SnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>A value type of the contract: safe to pass and keep across plugins.</summary>
public readonly record struct CounterSnapshot(int Count, DateTimeOffset At);

/// <summary>An exception type of the contract: it crosses to the caller unchanged.</summary>
public sealed class CounterException(string message) : Exception(message);
