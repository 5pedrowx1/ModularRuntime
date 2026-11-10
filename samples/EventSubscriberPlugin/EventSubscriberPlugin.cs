using ModularRuntime.Contracts;

namespace EventSubscriberPlugin;

/// <summary>
/// Subscribes to <c>example.greetings</c> and logs every event it receives. The subscription
/// is never disposed manually: the runtime removes it when the plugin stops.
/// </summary>
public sealed class EventSubscriberPlugin : IPlugin
{
    public const string Topic = "example.greetings";

    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        var logger = context.Logger;
        context.Events.Subscribe(Topic, (pluginEvent, _) =>
        {
            var text = pluginEvent.Payload.GetString("text") ?? pluginEvent.Payload.Json;
            logger.LogInformation($"EventSubscriberPlugin received '{pluginEvent.Topic}' from {pluginEvent.Source}: {text}");
            return ValueTask.CompletedTask;
        });

        logger.LogInformation($"EventSubscriberPlugin subscribed to '{Topic}'.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("EventSubscriberPlugin shut down; the runtime removes its subscription.");
        return Task.CompletedTask;
    }
}
