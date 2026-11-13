namespace ModularRuntimeTests.Contracts;

/// <summary>
/// Contract shared between the typed provider and consumer fixture plugins. Lives in its own
/// assembly, declared by both plugins in <c>sharedContracts</c>, so the runtime loads it once.
/// </summary>
public interface ICounterService
{
    int Count { get; }

    int Increment(int by);

    Task<int> IncrementAsync(int by, CancellationToken cancellationToken);

    /// <summary>Waits until canceled (provider shutdown or caller).</summary>
    Task WaitAsync(CancellationToken cancellationToken);

    CounterSnapshot Snapshot();

    /// <summary>Returns an object whose type is private to the provider plugin.</summary>
    object Opaque();

    /// <summary>Returns the envelope unchanged: a contract record whose <c>object</c> field is inspected on the way in and out.</summary>
    CounterEnvelope Echo(CounterEnvelope envelope);

    /// <summary>Returns an envelope wrapping an object private to the provider plugin.</summary>
    CounterEnvelope PrivateEnvelope();

    /// <summary>Throws <see cref="CounterException"/>, a contract (shared) exception type.</summary>
    void Fail();

    /// <summary>Throws an exception type private to the provider plugin.</summary>
    void FailPrivately();

    /// <summary>Throws a framework exception (<see cref="InvalidOperationException"/>) from provider code.</summary>
    void FailWithFramework();

    /// <summary>Throws a framework exception whose inner exception is a type private to the provider plugin.</summary>
    void FailWithPrivateInner();

    /// <summary>A delegate the consumer hands to the provider (the provider keeps it until cleared).</summary>
    Action? Callback { get; set; }
}

public sealed record CounterSnapshot(int Count, string Provider);

/// <summary>A contract record with an <c>object</c> field: its content, not its type, decides whether it may cross.</summary>
public sealed record CounterEnvelope(string Label, object? Payload);

public sealed class CounterException(string message) : Exception(message);
