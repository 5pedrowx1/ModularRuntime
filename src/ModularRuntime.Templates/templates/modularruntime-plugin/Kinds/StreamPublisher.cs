using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

/// <summary>A JSON item of the stream; subscribers deserialize it into their own type.</summary>
public sealed record Tick(int Sequence, DateTimeOffset At);

/// <summary>
/// Creates the stream <c>sample.ticks</c> and writes one JSON item per second while it runs. Each
/// subscriber has its own bounded buffer: in Wait mode a slow subscriber slows this producer down;
/// in a drop mode it loses items instead and sees gaps in the sequence numbers.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string StreamId = "sample.ticks";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stream = context.Streams.Create<JsonPayload>(new StreamDefinition(StreamId, "Ticks", new Version(1, 0)) { DefaultCapacity = 32 });
        context.RunInBackground("publisher", async stoppingToken =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var sequence = 0;
            do
            {
                sequence++;
                if (!await stream.WriteJsonAsync(new Tick(sequence, DateTimeOffset.UtcNow), cancellationToken: stoppingToken))
                {
                    break; // completed or removed
                }

                context.Logger.LogInformation($"Wrote tick #{sequence} to {stream.SubscriberCount} subscriber(s).");
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        });
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
