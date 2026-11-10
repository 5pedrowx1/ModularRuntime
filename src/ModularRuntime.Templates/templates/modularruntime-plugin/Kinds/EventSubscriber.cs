using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

/// <summary>
/// This plugin's own view of the event data. It does not reference the publisher's assembly:
/// the JSON payload is deserialized into this type.
/// </summary>
public sealed record GreetingMessage(string From, string Text, int Sequence);

/// <summary>Subscribes to the topic and logs every greeting it receives.</summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string Topic = "sample.greetings";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The subscription belongs to this plugin instance and is removed when it stops.
        context.Events.Subscribe<GreetingMessage>(Topic, (message, pluginEvent, eventCancellation) =>
        {
            // A payload that does not match GreetingMessage fails this delivery only; the
            // publisher sees it in its EventPublishResult.
            context.Logger.LogInformation($"Received greeting #{message?.Sequence} from '{message?.From}': {message?.Text} (event {pluginEvent.EventId:N}).");
            return ValueTask.CompletedTask;
        });
        context.Logger.LogInformation($"Subscribed to '{Topic}'.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
