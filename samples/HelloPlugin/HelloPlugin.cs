using ModularRuntime.Contracts;

namespace HelloPlugin;

/// <summary>Minimal plugin that demonstrates context access and lifecycle logging.</summary>
public sealed class HelloPlugin : IPlugin
{
    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        context.Logger.LogInformation(
            $"HelloPlugin initialized. PluginId='{context.PluginId}', InstanceId='{context.InstanceId}', " +
            $"StopRequested={context.StoppingToken.IsCancellationRequested}.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("HelloPlugin shut down.");
        return Task.CompletedTask;
    }
}
