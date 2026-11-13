using ModularRuntime.Contracts;

namespace StreamSubscriberPlugin;

/// <summary>
/// Subscribes to <c>example.ticks</c> (JSON, with backpressure) and <c>example.ticks.raw</c> (binary,
/// dropping the oldest items when it falls behind) and logs what it reads. The subscription is
/// owned by this instance and completes when the publisher stops or this plugin stops.
/// </summary>
public sealed class StreamSubscriberPlugin : IPlugin
{
    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        var ticks = context.Streams.Subscribe<JsonPayload>("example.ticks");
        var raw = context.Streams.Subscribe<ReadOnlyMemory<byte>>("example.ticks.raw", new StreamSubscribeOptions { Capacity = 4, FullMode = StreamFullMode.DropOldest });
        if (ticks is null || raw is null)
        {
            context.Logger.LogWarning("StreamSubscriberPlugin: the publisher's streams are not available; start the publisher first.");
            return Task.CompletedTask;
        }

        context.Resources.Track("tick-reader", new Reader(ReadTicksAsync(ticks, context.StoppingToken)));
        context.Resources.Track("raw-reader", new Reader(ReadRawAsync(raw, context.StoppingToken)));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReadTicksAsync(IStreamSubscription<JsonPayload> ticks, CancellationToken stopping)
    {
        var count = 0;
        try
        {
            await foreach (var message in ticks.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                count++;
                if (count % 10 == 0)
                {
                    _logger?.LogInformation($"StreamSubscriberPlugin read tick #{message.Sequence}: {message.Item.Json}");
                }
            }

            _logger?.LogInformation($"StreamSubscriberPlugin: '{ticks.StreamId}' completed ({ticks.Completion?.ErrorCode}) after {count} item(s).");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadRawAsync(IStreamSubscription<ReadOnlyMemory<byte>> raw, CancellationToken stopping)
    {
        try
        {
            await foreach (var message in raw.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                // Simulate a slow consumer: the publisher never waits for this subscription (DropOldest).
                await Task.Delay(120, stopping).ConfigureAwait(false);
                _logger?.LogDebug($"StreamSubscriberPlugin raw tick {BitConverter.ToInt64(message.Item.Span)} (dropped so far: {raw.DroppedCount}).");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Reader(Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await task.ConfigureAwait(false);
    }
}
