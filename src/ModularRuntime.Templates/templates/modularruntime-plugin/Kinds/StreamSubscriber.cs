using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

/// <summary>The subscriber's own type for the stream's JSON items (it does not reference the publisher).</summary>
public sealed record Tick(int Sequence, DateTimeOffset At);

/// <summary>
/// Subscribes to <c>sample.ticks</c> and reads it in the background with a small buffer. With
/// <see cref="StreamFullMode.Wait"/> the publisher waits when this buffer is full; choose
/// <see cref="StreamFullMode.DropOldest"/> to never slow the publisher down and count the loss.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string StreamId = "sample.ticks";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var subscription = context.ConsumeJson<Tick>(
            StreamId,
            (tick, message, _) =>
            {
                context.Logger.LogInformation($"Received tick #{message.Sequence} from '{StreamId}': {tick?.Sequence} at {tick?.At:O}");
                return ValueTask.CompletedTask;
            },
            new StreamSubscribeOptions { Capacity = 8, FullMode = StreamFullMode.Wait });

        if (subscription is null)
        {
            // The publisher is not running (yet): start it, then restart this plugin.
            context.Logger.LogWarning($"Stream '{StreamId}' is not available; start the publisher first.");
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
