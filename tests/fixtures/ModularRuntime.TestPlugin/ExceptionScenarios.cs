using ModularRuntime.Contracts;

namespace ModularRuntime.TestPlugin;

/// <summary>Throws a plugin-defined exception during initialization.</summary>
public sealed class InitializationFailurePlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) =>
        throw new PluginFailureException("intentional plugin initialization failure");

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Succeeds during initialization and throws a plugin-defined exception during shutdown.</summary>
public sealed class ShutdownFailurePlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("ShutdownFailurePlugin initialized.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) =>
        throw new PluginFailureException("intentional plugin shutdown failure");
}

/// <summary>Logs an exception defined in its own assembly through the plugin logger.</summary>
public sealed class LoggingFailurePlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogError(
            "LoggingFailurePlugin logged an intentional plugin exception.",
            new PluginFailureException("plugin-defined exception supplied to the logger"));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Throws a plugin-defined exception from its constructor.</summary>
public sealed class ConstructorFailurePlugin : IPlugin
{
    public ConstructorFailurePlugin() =>
        throw new PluginFailureException("intentional plugin constructor failure");

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Throws a plugin-defined exception whose diagnostic members also throw.</summary>
public sealed class HostileExceptionPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) =>
        throw new HostilePluginException();

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Registers a stopping-token callback that throws a plugin-defined exception.</summary>
public sealed class CancellationCallbackFailurePlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.StoppingToken.Register(() =>
            throw new PluginFailureException("intentional plugin cancellation callback failure"));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Blocks initialization until its token is canceled, as a slow plugin might.</summary>
public sealed class BlockingInitializationPlugin : IPlugin
{
    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("BlockingInitializationPlugin is waiting for cancellation.");
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
