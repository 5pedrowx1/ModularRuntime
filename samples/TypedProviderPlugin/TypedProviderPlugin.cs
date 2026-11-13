using Example.Telemetry;
using ModularRuntime.Contracts;

namespace TypedProviderPlugin;

/// <summary>
/// Provides <c>example.telemetry</c> 1.0 as a typed service (<see cref="ITelemetryService"/>) and
/// publishes every recorded sample on the typed stream <c>example.telemetry.samples</c>.
/// </summary>
/// <remarks>
/// The runtime hands consumers a proxy, never this object. When this plugin stops, the proxy's
/// calls fail with <c>service_removed</c> and the stream completes with <c>stream_removed</c>.
/// </remarks>
public sealed class TypedProviderPlugin : IPlugin, ITelemetryService
{
    public const string ServiceId = "example.telemetry";
    public const string StreamId = "example.telemetry.samples";

    private readonly object _sync = new();
    private readonly List<TelemetrySample> _samples = [];
    private IPluginLogger? _logger;
    private IStreamProducer<TelemetrySample>? _stream;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        _stream = context.Streams.Create<TelemetrySample>(
            new StreamDefinition(StreamId, "Telemetry samples", new Version(1, 0)) { DefaultCapacity = 64, DefaultFullMode = StreamFullMode.DropOldest });
        context.TypedServices.Register<ITelemetryService>(
            new TypedServiceDefinition(ServiceId, "Telemetry", new Version(1, 0)) { Metadata = new Dictionary<string, string> { ["description"] = "Records numeric samples." } },
            this);
        context.Logger.LogInformation($"TypedProviderPlugin registered typed service '{ServiceId}' 1.0 and stream '{StreamId}'.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("TypedProviderPlugin shut down; the runtime removes its service and stream.");
        return Task.CompletedTask;
    }

    public long Record(TelemetrySample sample)
    {
        if (string.IsNullOrWhiteSpace(sample.Metric) || double.IsNaN(sample.Value))
        {
            throw new TelemetryException("A sample needs a metric name and a numeric value.");
        }

        long count;
        lock (_sync)
        {
            _samples.Add(sample);
            count = _samples.Count;
        }

        // Fan out to subscribers without waiting (DropOldest keeps the producer responsive).
        _stream?.TryWrite(sample);
        return count;
    }

    public TelemetrySummary Summary()
    {
        lock (_sync)
        {
            return _samples.Count == 0
                ? new TelemetrySummary(0, 0, 0, 0)
                : new TelemetrySummary(_samples.Count, _samples.Min(s => s.Value), _samples.Max(s => s.Value), _samples.Average(s => s.Value));
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation($"TypedProviderPlugin flushed {Summary().Count} sample(s).");
    }
}
