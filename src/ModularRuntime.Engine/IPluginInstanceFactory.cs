using ModularRuntime.Contracts;

namespace ModularRuntime.Engine;

/// <summary>
/// Creates and releases one plugin instance. This is the boundary that keeps the engine
/// independent from <c>AssemblyLoadContext</c> and other loading mechanisms.
/// </summary>
/// <remarks>
/// A factory belongs to a single registered plugin. <see cref="ReleaseAsync"/> may be called
/// after a failed start, after shutdown, or during engine disposal; implementations must be
/// idempotent and must release all resources owned by the factory.
/// </remarks>
public interface IPluginInstanceFactory
{
    ValueTask<IPlugin> CreateAsync(PluginDescriptor descriptor, CancellationToken cancellationToken);

    ValueTask ReleaseAsync();
}
