using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

internal sealed class TestPlugin : IPlugin
{
    private readonly Func<IPluginContext, CancellationToken, Task>? _onInitialize;
    private readonly Func<CancellationToken, Task>? _onShutdown;
    private int _initializeCount;
    private int _shutdownCount;

    public TestPlugin(
        Func<IPluginContext, CancellationToken, Task>? onInitialize = null,
        Func<CancellationToken, Task>? onShutdown = null)
    {
        _onInitialize = onInitialize;
        _onShutdown = onShutdown;
    }

    public int InitializeCount => Volatile.Read(ref _initializeCount);

    public int ShutdownCount => Volatile.Read(ref _shutdownCount);

    public IPluginContext? Context { get; private set; }

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _initializeCount);
        Context = context;
        return _onInitialize?.Invoke(context, cancellationToken) ?? Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _shutdownCount);
        return _onShutdown?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }
}
