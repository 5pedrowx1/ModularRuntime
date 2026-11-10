using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace EventPublisherPlugin;

/// <summary>Publishes a JSON greeting on <c>example.greetings</c> when it starts.</summary>
public sealed class EventPublisherPlugin : IPlugin
{
    public const string Topic = "example.greetings";

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var payload = JsonPayload.FromNode(new JsonObject
        {
            ["text"] = "Hello from EventPublisherPlugin",
            ["sequence"] = 1,
        });

        var result = await context.Events.PublishAsync(
            Topic,
            payload,
            new EventPublishOptions { CorrelationId = $"greeting-{context.InstanceId:N}" },
            cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation(
            $"EventPublisherPlugin published '{Topic}': status={result.Status}, " +
            $"delivered={result.SucceededCount}, failed={result.FailedCount}.");
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
