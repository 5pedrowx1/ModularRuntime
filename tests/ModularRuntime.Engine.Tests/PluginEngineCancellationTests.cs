using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Engine.Tests;

public sealed class PluginEngineCancellationTests
{
    [Fact]
    public async Task StopAsync_CancelsStoppingTokenBeforeShutdown()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.cancellation-token");
        var plugin = new TestPlugin();
        engine.Register(descriptor, new TestPluginFactory(plugin));
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var stoppingToken = plugin.Context!.StoppingToken;
        Assert.False(stoppingToken.IsCancellationRequested);

        var result = await engine.StopAsync(descriptor.Id);

        Assert.True(result.IsSuccess);
        Assert.True(stoppingToken.IsCancellationRequested);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task StopAsync_CancellationCallbackFailure_IsLoggedAndShutdownCompletes()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger);
        var descriptor = TestDescriptors.Create("com.test.cancellation-callback-failure");
        var plugin = new TestPlugin(
            onInitialize: (context, _) =>
            {
                context.StoppingToken.Register(() =>
                    throw new InvalidOperationException("intentional cancellation callback failure"));
                return Task.CompletedTask;
            });
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var result = await engine.StopAsync(descriptor.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginState.Stopped, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(1, plugin.ShutdownCount);
        Assert.Equal(1, factory.ReleaseCount);

        var entry = logger.Find(LogLevel.Warning, "cancellation callbacks");
        Assert.NotNull(entry);
        Assert.NotNull(entry.Diagnostic);
        Assert.Contains(
            "InvalidOperationException",
            entry.Diagnostic.ExceptionType,
            StringComparison.Ordinal);
    }
}
