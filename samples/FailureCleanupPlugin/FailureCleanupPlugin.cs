using ModularRuntime.Contracts;

namespace FailureCleanupPlugin;

/// <summary>
/// Creates resources (a subscription, a service, and a tracked object) and then fails during
/// initialization on purpose. The runtime rolls the instance back and releases every resource
/// it created, without any cleanup code in the plugin.
/// </summary>
public sealed class FailureCleanupPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Events.Subscribe("example.greetings", (_, _) => ValueTask.CompletedTask);
        context.Services.Register(
            new PluginServiceDefinition("example.cleanup.probe", "Cleanup probe", new Version(1, 0), ["ping"]),
            (_, _) => ValueTask.FromResult(PluginResponse.Success()));
        context.Resources.Track("cleanup-probe", new CleanupProbe(context.Logger));

        context.Logger.LogInformation(
            $"FailureCleanupPlugin created {context.Resources.GetResources().Count} resources and will now fail intentionally.");
        throw new InvalidOperationException("FailureCleanupPlugin fails intentionally after creating resources.");
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class CleanupProbe(IPluginLogger logger) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            logger.LogInformation("FailureCleanupPlugin tracked resource was disposed by the runtime.");
            return ValueTask.CompletedTask;
        }
    }
}
