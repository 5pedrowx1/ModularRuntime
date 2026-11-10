using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

internal sealed class TestPluginFactory : IPluginInstanceFactory
{
    private readonly Func<CancellationToken, ValueTask<IPlugin>> _create;
    private readonly Func<ValueTask>? _release;
    private int _createCount;
    private int _releaseCount;

    public TestPluginFactory(
        Func<CancellationToken, ValueTask<IPlugin>> create,
        Func<ValueTask>? release = null)
    {
        _create = create;
        _release = release;
    }

    public TestPluginFactory(IPlugin plugin, Func<ValueTask>? release = null)
        : this(_ => ValueTask.FromResult(plugin), release)
    {
    }

    public int CreateCount => Volatile.Read(ref _createCount);

    public int ReleaseCount => Volatile.Read(ref _releaseCount);

    public ValueTask<IPlugin> CreateAsync(
        PluginDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _createCount);
        return _create(cancellationToken);
    }

    public ValueTask ReleaseAsync()
    {
        Interlocked.Increment(ref _releaseCount);
        return _release?.Invoke() ?? ValueTask.CompletedTask;
    }
}
