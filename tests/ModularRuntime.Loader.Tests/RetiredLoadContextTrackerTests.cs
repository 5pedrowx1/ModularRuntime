using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.Loader.Internal;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Loader.Tests;

/// <summary>
/// Deterministic tests of unload tracking. Liveness is controlled with ordinary objects: a field
/// keeps one alive; collection is triggered by the test (never by the code under test, except in
/// <see cref="PluginUnloadWait.InduceCollection"/> mode).
/// Runs without parallel test classes so that collection counts are not disturbed.
/// </summary>
[Collection(GcSensitiveTests.Name)]
public sealed class RetiredLoadContextTrackerTests
{
    private static readonly PluginId First = PluginId.Parse("test.first");
    private static readonly PluginId Second = PluginId.Parse("test.second");

    private readonly RetiredLoadContextTracker _tracker = new();
    private object? _alive = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DeadReference()
    {
        var reference = new WeakReference(new object());
        return reference;
    }

    private static WeakReference CollectedReference()
    {
        var reference = DeadReference();
        Assert.True(GcDiagnostics.WaitForCollection(reference));
        return reference;
    }

    private Task<bool> Wait(PluginId id, TimeSpan timeout, PluginUnloadWait mode = PluginUnloadWait.Observe, CancellationToken token = default) =>
        _tracker.WaitAsync(id, timeout, mode, TimeProvider.System, token);

    [Fact]
    public async Task NothingTracked_IsUnloaded()
    {
        Assert.True(await Wait(First, TimeSpan.Zero));
        Assert.Equal(0, _tracker.GetPendingCount(First));
    }

    [Fact]
    public async Task Timeout_ReturnsFalse_AndKeepsTheLiveReference_SoTheWaitCanBeRepeated()
    {
        _tracker.Track(First, new WeakReference(_alive));

        Assert.False(await Wait(First, TimeSpan.Zero));
        Assert.False(await Wait(First, TimeSpan.FromMilliseconds(120)));
        Assert.Equal(1, _tracker.GetPendingCount(First));

        _alive = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.True(await Wait(First, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _tracker.GetPendingCount(First));
    }

    [Fact]
    public async Task Cancellation_Throws_AndKeepsTheLiveReference()
    {
        _tracker.Track(First, new WeakReference(_alive));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(First, TimeSpan.FromMinutes(1), token: canceled.Token));
        Assert.Equal(1, _tracker.GetPendingCount(First));

        using var later = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(First, TimeSpan.FromMinutes(1), token: later.Token));
        Assert.Equal(1, _tracker.GetPendingCount(First));
        GC.KeepAlive(_alive);
    }

    [Fact]
    public async Task ObserveMode_NeverForcesAGarbageCollection()
    {
        _tracker.Track(First, new WeakReference(_alive));
        var before = GC.CollectionCount(2);

        Assert.False(await Wait(First, TimeSpan.FromMilliseconds(200), PluginUnloadWait.Observe));

        Assert.Equal(before, GC.CollectionCount(2));
        GC.KeepAlive(_alive);
    }

    [Fact]
    public async Task InduceCollectionMode_CollectsUnreachableContexts()
    {
        _tracker.Track(First, DeadReference());

        Assert.True(await Wait(First, TimeSpan.FromSeconds(5), PluginUnloadWait.InduceCollection));
    }

    [Fact]
    public void CollectedReferences_AreNotAccumulated()
    {
        for (var index = 0; index < 200; index++)
        {
            _tracker.Track(First, CollectedReference());
        }

        _tracker.Track(Second, new WeakReference(_alive));
        var weakDead = DeadReference();
        _tracker.Track(First, weakDead);
        GcDiagnostics.WaitForCollection(weakDead);

        Assert.Equal(1, _tracker.TrackedCount);
        Assert.Equal(0, _tracker.GetPendingCount(First));
        Assert.Equal(1, _tracker.GetPendingCount(Second));
        GC.KeepAlive(_alive);
    }

    [Fact]
    public async Task Plugins_AreTrackedIndependently()
    {
        _tracker.Track(First, new WeakReference(_alive));
        _tracker.Track(Second, CollectedReference());

        Assert.True(await Wait(Second, TimeSpan.Zero));
        Assert.False(await Wait(First, TimeSpan.Zero));
        GC.KeepAlive(_alive);
    }

    [Fact]
    public async Task ConcurrentTrackingQueriesAndWaits_AreSafe()
    {
        var keepAlive = Enumerable.Range(0, 64).Select(_ => new object()).ToList();
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var index = 0; index < 200; index++)
            {
                var id = index % 2 == 0 ? First : Second;
                _tracker.Track(id, index % 3 == 0 ? new WeakReference(keepAlive[(worker * 8 + index) % keepAlive.Count]) : DeadReference());
                _ = _tracker.GetPendingCount(id);
                _ = _tracker.TrackedCount;
                if (index % 50 == 0)
                {
                    _ = await Wait(id, TimeSpan.FromMilliseconds(1));
                }
            }
        })).ToList();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(_tracker.GetPendingCount(First) + _tracker.GetPendingCount(Second) > 0);
        keepAlive.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.True(await Wait(First, TimeSpan.FromSeconds(5)));
        Assert.True(await Wait(Second, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _tracker.TrackedCount);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GcSensitiveTests
{
    public const string Name = "GC-sensitive";
}
