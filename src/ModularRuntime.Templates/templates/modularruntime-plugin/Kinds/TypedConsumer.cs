using ModularRuntime.Contracts;
using ModularRuntime.Sdk;
using Shared.Contracts;

namespace ModularRuntimePlugin;

/// <summary>
/// Calls the typed service <c>sample.text</c> through the shared contract (<see cref="ICounterService"/>)
/// every few seconds and reads its stream <c>sample.ticks</c>. The provider is another plugin that
/// declares the same contract assembly; it may start later or stop: a missing or stopped provider
/// is reported, never a stale reference into its load context.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string ServiceId = "sample.text";
    public const string StreamId = "sample.ticks";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RunInBackground("consumer", async stoppingToken =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            ITypedServiceClient<ICounterService>? client = null;
            do
            {
                try
                {
                    // Resolve once; the proxy stays bound to that provider instance.
                    client ??= context.TypedServices.GetClient<ICounterService>(ServiceId, new Version(1, 0));
                    if (client is null)
                    {
                        context.Logger.LogWarning($"Typed service '{ServiceId}' is not available (is the provider running, and does it declare the same contract assembly?).");
                        continue;
                    }

                    var count = client.Service.Increment(1);
                    var snapshot = await client.Service.SnapshotAsync(stoppingToken);
                    context.Logger.LogInformation($"Typed service '{ServiceId}' answered: {count} (snapshot {snapshot.Count} at {snapshot.At:O})");
                }
                catch (TypedServiceException exception)
                {
                    // service_removed / service_unavailable: the provider stopped; resolve again next time.
                    context.Logger.LogWarning($"Typed service '{ServiceId}' call failed [{exception.ErrorCode}]: {exception.Message}");
                    client = null;
                }
                catch (CounterException exception)
                {
                    context.Logger.LogWarning($"The provider refused the call: {exception.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        });

        // The stream completes when the provider stops; this reader stops with the plugin.
        if (context.Consume<CounterSnapshot>(StreamId, (message, _) =>
            {
                context.Logger.LogDebug($"Stream '{StreamId}' #{message.Sequence}: count {message.Item.Count}.");
                return ValueTask.CompletedTask;
            }) is null)
        {
            context.Logger.LogWarning($"Stream '{StreamId}' is not available yet.");
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
