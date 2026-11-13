using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace StreamPublisherPlugin;

/// <summary>
/// Publishes a JSON stream <c>example.ticks</c> (one <see cref="JsonPayload"/> per tick) and a binary
/// stream <c>example.ticks.raw</c> (8 bytes per tick) while it runs. Subscribers in
/// <see cref="StreamFullMode.Wait"/> mode apply backpressure to this producer; subscribers that
/// choose a drop mode never slow it down.
/// </summary>
public sealed class StreamPublisherPlugin : IPlugin
{
    public const string JsonStreamId = "example.ticks";
    public const string BinaryStreamId = "example.ticks.raw";

    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        var ticks = context.Streams.Create<JsonPayload>(new StreamDefinition(JsonStreamId, "Ticks", new Version(1, 0)) { DefaultCapacity = 32 });
        var raw = context.Streams.Create<ReadOnlyMemory<byte>>(new StreamDefinition(BinaryStreamId, "Raw ticks", new Version(1, 0)) { DefaultCapacity = 32 });
        context.Resources.Track("tick-loop", new Loop(RunAsync(ticks, raw, context.StoppingToken)));
        context.Logger.LogInformation($"StreamPublisherPlugin created streams '{JsonStreamId}' and '{BinaryStreamId}'.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(IStreamProducer<JsonPayload> ticks, IStreamProducer<ReadOnlyMemory<byte>> raw, CancellationToken stopping)
    {
        var buffer = new byte[8];
        try
        {
            for (var tick = 1; !stopping.IsCancellationRequested; tick++)
            {
                var written = await ticks.WriteAsync(JsonPayload.FromNode(new JsonObject { ["tick"] = tick, ["at"] = DateTimeOffset.UtcNow }), stopping).ConfigureAwait(false);
                BitConverter.TryWriteBytes(buffer, (long)tick);
                // Binary items are copied on write by default, so this buffer can be reused.
                await raw.WriteAsync(buffer, stopping).ConfigureAwait(false);
                if (!written)
                {
                    break;
                }

                if (tick % 10 == 0)
                {
                    _logger?.LogInformation($"StreamPublisherPlugin wrote tick #{tick} to {ticks.SubscriberCount} subscriber(s).");
                }

                await Task.Delay(50, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Loop(Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await task.ConfigureAwait(false);
    }
}
