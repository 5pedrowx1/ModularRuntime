using ModularRuntime.Contracts;

namespace FailingPlugin;

/// <summary>Plugin that intentionally fails during initialization to demonstrate isolation.</summary>
public sealed class FailingPlugin : IPlugin
{
    private IPluginLogger? _logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        context.Logger.LogInformation("FailingPlugin initialization started; failure is intentional.");
        throw new InvalidOperationException(
            "FailingPlugin intentionally fails during initialization.");
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("FailingPlugin rollback shutdown executed.");
        return Task.CompletedTask;
    }
}
