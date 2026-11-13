namespace Example.Telemetry;

/// <summary>
/// Contract of the <c>example.telemetry</c> typed service. Provider and consumer plugins both
/// declare <c>Example.Telemetry.Contracts</c> in <c>plugin.json</c> and call this interface
/// directly: no JSON, no copies.
/// </summary>
public interface ITelemetryService
{
    /// <summary>Records one sample and returns the running total of samples.</summary>
    long Record(TelemetrySample sample);

    /// <summary>Snapshot of what was recorded so far.</summary>
    TelemetrySummary Summary();

    /// <summary>Persists the recorded samples; cancellation is honored.</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>A sample; records and primitives only, so nothing private to a plugin crosses the boundary.</summary>
public readonly record struct TelemetrySample(string Metric, double Value, DateTimeOffset At);

public sealed record TelemetrySummary(long Count, double Minimum, double Maximum, double Average);

/// <summary>Raised by the provider for invalid samples; shared, so consumers can catch it as such.</summary>
public sealed class TelemetryException(string message) : Exception(message);
