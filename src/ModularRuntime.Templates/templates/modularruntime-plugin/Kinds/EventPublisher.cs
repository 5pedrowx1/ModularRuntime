using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

/// <summary>The data published on the topic. Only its JSON form crosses to other plugins.</summary>
public sealed record GreetingMessage(string From, string Text, int Sequence);

/// <summary>Publishes a <see cref="GreetingMessage"/> on the topic every few seconds while running.</summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string Topic = "sample.greetings";

    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        context.RunInBackground("publisher", async stoppingToken =>
        {
            var sequence = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                var message = new GreetingMessage(context.PluginId.Value, $"Hello from {context.Descriptor.Name}", ++sequence);
                var result = await context.Events.PublishAsync(Topic, message, cancellationToken: stoppingToken);

                // A publish succeeds even without subscribers; failed deliveries are reported per subscriber.
                context.Logger.LogInformation($"Published greeting #{sequence} on '{Topic}' to {result.Deliveries.Count} subscriber(s), {result.FailedCount} failed.");
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        });
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Publisher stopping.");
        return Task.CompletedTask;
    }
}
