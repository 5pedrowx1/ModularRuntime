using ModularRuntime.Contracts;
using Shared.Contracts;

namespace ModularRuntimePlugin;

/// <summary>
/// Provides the typed service <c>sample.text</c> (<see cref="ICounterService"/>) and the typed stream
/// <c>sample.ticks</c>. Consumers call the interface directly through a runtime proxy: no JSON, no
/// copies, and no reference to this assembly, only to the shared contract assembly.
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin, ICounterService
{
    public const string ServiceId = "sample.text";
    public const string StreamId = "sample.ticks";

    private int _count;
    private IStreamProducer<CounterSnapshot>? _stream;

    public int Count => Volatile.Read(ref _count);

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _stream = context.Streams.Create<CounterSnapshot>(new StreamDefinition(StreamId, "Counter snapshots", new Version(1, 0)) { DefaultFullMode = StreamFullMode.DropOldest });

        // The registration belongs to this instance: when the plugin stops, every proxy held by a
        // consumer fails with TypedServiceException (service_removed) instead of reaching this object.
        context.TypedServices.Register<ICounterService>(new TypedServiceDefinition(ServiceId, "Counter", new Version(1, 0)), this);
        context.Logger.LogInformation($"Typed service '{ServiceId}' 1.0 and stream '{StreamId}' registered.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public int Increment(int by)
    {
        if (by <= 0)
        {
            // A contract exception: the caller catches it as CounterException.
            throw new CounterException("The increment must be positive.");
        }

        var count = Interlocked.Add(ref _count, by);
        _stream?.TryWrite(new CounterSnapshot(count, DateTimeOffset.UtcNow));
        return count;
    }

    public async Task<CounterSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return new CounterSnapshot(Count, DateTimeOffset.UtcNow);
    }
}
