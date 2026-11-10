using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

/// <summary>
/// Entry type of the plugin. The runtime creates it with the public parameterless constructor,
/// calls <see cref="InitializeAsync"/> when the plugin starts and <see cref="ShutdownAsync"/>
/// when it stops. Every start uses a new instance.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        var descriptor = context.Descriptor;
        context.Logger.LogInformation($"{descriptor.Name} {descriptor.Version} started (plugin '{context.PluginId}', instance {context.InstanceId:N}).");

        // Background work must stop when the plugin is asked to stop: RunInBackground passes
        // context.StoppingToken and the runtime waits for the work to finish during shutdown.
        context.RunInBackground("heartbeat", async stoppingToken =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                context.Logger.LogDebug("Still running.");
            }
        });

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Stopping.");
        return Task.CompletedTask;
    }
}
