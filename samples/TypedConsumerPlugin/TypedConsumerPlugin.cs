using Example.Telemetry;
using ModularRuntime.Contracts;

namespace TypedConsumerPlugin;

/// <summary>
/// Uses several mechanisms at once: calls the typed service <c>example.telemetry</c> through a
/// proxy, subscribes to the typed stream <c>example.telemetry.samples</c>, and calls the binary
/// service <c>example.checksum</c>. It never references the provider plugins' assemblies: only the
/// shared contract assembly and <c>ModularRuntime.Contracts</c>.
/// </summary>
public sealed class TypedConsumerPlugin : IPlugin
{
    private IPluginLogger? _logger;
    private ITypedServiceClient<ITelemetryService>? _telemetry;
    private int _streamed;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;

        // Typed service: the proxy keeps working as long as the provider runs; afterwards its
        // calls throw TypedServiceException (service_removed) instead of reaching a stopped plugin.
        _telemetry = context.TypedServices.GetClient<ITelemetryService>(TypedProviderPluginIds.ServiceId, new Version(1, 0));
        if (_telemetry is null)
        {
            context.Logger.LogWarning($"TypedConsumerPlugin: typed service '{TypedProviderPluginIds.ServiceId}' 1.x is not available.");
        }

        // Typed stream: items are the shared record struct, delivered without serialization.
        var subscription = context.Streams.Subscribe<TelemetrySample>(TypedProviderPluginIds.StreamId, new StreamSubscribeOptions { Capacity = 16 });
        if (subscription is not null)
        {
            context.Resources.Track("sample-reader", new ReaderTask(ReadAsync(subscription, context.StoppingToken)));
        }

        if (_telemetry is not null)
        {
            try
            {
                var service = _telemetry.Service;
                var now = DateTimeOffset.UtcNow;
                service.Record(new TelemetrySample("cpu", 0.42, now));
                service.Record(new TelemetrySample("cpu", 0.58, now.AddSeconds(1)));
                var total = service.Record(new TelemetrySample("memory", 512, now.AddSeconds(2)));
                var summary = service.Summary();
                await service.FlushAsync(cancellationToken).ConfigureAwait(false);
                context.Logger.LogInformation(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"TypedConsumerPlugin recorded {total} sample(s); summary: count={summary.Count}, min={summary.Minimum}, max={summary.Maximum}."));

                try
                {
                    service.Record(new TelemetrySample("", double.NaN, now));
                }
                catch (TelemetryException exception)
                {
                    // A contract exception type: it crosses unchanged.
                    context.Logger.LogInformation($"TypedConsumerPlugin: provider refused a sample as expected ({exception.Message}).");
                }
            }
            catch (TypedServiceException exception)
            {
                context.Logger.LogWarning($"TypedConsumerPlugin: typed call failed [{exception.ErrorCode}] {exception.Message}");
            }
        }

        // Binary service: bytes in, bytes out; the result is disposed when the bytes are no longer needed.
        using var checksum = await context.Services.InvokeBinaryAsync(BinaryProviderPluginIds.ServiceId, "crc32", "hello"u8.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (checksum.IsSuccess)
        {
            context.Logger.LogInformation($"TypedConsumerPlugin: crc32(\"hello\") = {Convert.ToHexString(checksum.Payload.Span)}.");
        }
        else
        {
            context.Logger.LogWarning($"TypedConsumerPlugin: binary call failed [{checksum.ErrorCode}] {checksum.Message}");
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation($"TypedConsumerPlugin shut down after streaming {Volatile.Read(ref _streamed)} sample(s).");
        return Task.CompletedTask;
    }

    private async Task ReadAsync(IStreamSubscription<TelemetrySample> subscription, CancellationToken stopping)
    {
        try
        {
            await foreach (var message in subscription.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _streamed);
                _logger?.LogDebug($"TypedConsumerPlugin streamed #{message.Sequence}: {message.Item.Metric}={message.Item.Value}.");
            }

            _logger?.LogInformation($"TypedConsumerPlugin: stream '{subscription.StreamId}' completed ({subscription.Completion?.ErrorCode}), dropped={subscription.DroppedCount}.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ReaderTask(Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await task.ConfigureAwait(false);
    }
}

/// <summary>IDs published by the provider samples (constants only; no assembly reference).</summary>
internal static class TypedProviderPluginIds
{
    public const string ServiceId = "example.telemetry";
    public const string StreamId = "example.telemetry.samples";
}

internal static class BinaryProviderPluginIds
{
    public const string ServiceId = "example.checksum";
}
