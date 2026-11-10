using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine.Ownership;
using ModularRuntime.TestSupport;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

/// <summary>
/// v0.2.1 regression tests for concurrent release of owned resources and for the
/// BeginClose / CloseAsync / cancellation-callback interplay. Coordination uses
/// TaskCompletionSource only; timeouts are safety limits.
/// </summary>
public sealed class OwnershipConcurrencyTests
{
    /// <summary>Disposable whose DisposeAsync blocks until <see cref="Open"/> is called.</summary>
    private sealed class GatedDisposable(bool fail = false) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task Entered => _entered.Task;

        public int DisposeCount => Volatile.Read(ref _count);

        public bool Completed { get; private set; }

        public void Open() => _gate.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _count);
            _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            Completed = true;
            if (fail)
            {
                throw new InvalidOperationException("intentional release failure");
            }
        }
    }

    private static OwnershipScope NewScope()
    {
        var instanceId = Guid.NewGuid();
        return new OwnershipScope(
            new ResourceOwner(Guid.NewGuid(), ResourceOwnerKind.Plugin, PluginId.Parse("com.test.scope"), instanceId),
            instanceId);
    }

    private static TrackedResource Track(OwnershipScope scope, IAsyncDisposable disposable)
    {
        var resource = new TrackedResource(scope, "gated", disposable);
        Assert.True(scope.TryAdd(resource));
        return resource;
    }

    [Fact]
    public async Task TwoConcurrentDisposeAsync_BothObserveTheSameSlowRelease()
    {
        var scope = NewScope();
        var disposable = new GatedDisposable();
        var resource = Track(scope, disposable);

        var first = resource.DisposeAsync().AsTask();
        await disposable.Entered.WaitAsync(TestTimeout);
        var second = resource.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted, "A second DisposeAsync must not report completion while the release is still running.");
        Assert.False(resource.IsActive);

        disposable.Open();
        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        Assert.Equal(1, disposable.DisposeCount);
        Assert.True(disposable.Completed);
        Assert.Empty(scope.GetResources());
    }

    [Fact]
    public async Task CloseAsync_WaitsForAnExplicitReleaseAlreadyInProgress()
    {
        var scope = NewScope();
        var disposable = new GatedDisposable();
        var resource = Track(scope, disposable);

        var explicitRelease = resource.DisposeAsync().AsTask();
        await disposable.Entered.WaitAsync(TestTimeout);
        var close = scope.CloseAsync();

        Assert.False(close.IsCompleted, "CloseAsync must not complete while an owned resource is still being released.");

        disposable.Open();
        await explicitRelease.WaitAsync(TestTimeout);
        var failures = await close.WaitAsync(TestTimeout);

        Assert.Empty(failures);
        Assert.Equal(1, disposable.DisposeCount);
    }

    [Fact]
    public async Task ExplicitDispose_JoinsAReleaseStartedByCloseAsync()
    {
        var scope = NewScope();
        var disposable = new GatedDisposable();
        var resource = Track(scope, disposable);

        var close = scope.CloseAsync();
        await disposable.Entered.WaitAsync(TestTimeout);
        var explicitRelease = resource.DisposeAsync().AsTask();

        Assert.False(explicitRelease.IsCompleted);
        Assert.False(close.IsCompleted);

        disposable.Open();
        await explicitRelease.WaitAsync(TestTimeout);
        Assert.Empty(await close.WaitAsync(TestTimeout));
        Assert.Equal(1, disposable.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentCloseAsyncCalls_ShareOneCloseOperation()
    {
        var scope = NewScope();
        var disposable = new GatedDisposable();
        Track(scope, disposable);

        var first = scope.CloseAsync();
        await disposable.Entered.WaitAsync(TestTimeout);
        var second = scope.CloseAsync();

        Assert.False(second.IsCompleted, "A concurrent CloseAsync must wait for the close in progress.");
        disposable.Open();

        Assert.Same(await first.WaitAsync(TestTimeout), await second.WaitAsync(TestTimeout));
        Assert.Equal(1, disposable.DisposeCount);
    }

    [Fact]
    public async Task FailingSlowRelease_IsObservedByEveryCaller_AndNotHidden()
    {
        var scope = NewScope();
        var disposable = new GatedDisposable(fail: true);
        var resource = Track(scope, disposable);

        var first = resource.DisposeAsync().AsTask();
        await disposable.Entered.WaitAsync(TestTimeout);
        var second = resource.DisposeAsync().AsTask();
        var close = scope.CloseAsync();
        disposable.Open();

        var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TestTimeout));
        var secondError = await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(TestTimeout));
        var failures = await close.WaitAsync(TestTimeout);

        Assert.Same(firstError, secondError);
        Assert.Equal(1, disposable.DisposeCount);
        // The owner started this release and received the exception; the scope does not report
        // it a second time.
        Assert.Empty(failures);
    }

    [Fact]
    public async Task FailingRelease_StartedByCloseAsync_IsReportedOnce()
    {
        var scope = NewScope();
        var disposable = new GatedDisposable(fail: true);
        var resource = Track(scope, disposable);

        var close = scope.CloseAsync();
        await disposable.Entered.WaitAsync(TestTimeout);
        var explicitRelease = resource.DisposeAsync().AsTask();
        disposable.Open();

        var failures = await close.WaitAsync(TestTimeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => explicitRelease.WaitAsync(TestTimeout));

        var failure = Assert.Single(failures);
        Assert.Equal("System.InvalidOperationException", failure.Diagnostic.ExceptionType);
        Assert.Equal(ResourceKind.Custom, failure.Resource!.Kind);
    }

    [Fact]
    public async Task CloseAsync_WaitsForCancellationCallbacksRunningOnAnotherThread()
    {
        var scope = NewScope();
        var disposable = new GatedDisposable();
        disposable.Open();
        Track(scope, disposable);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(scope.TryEnterInvocation(out var token));
        token.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.Wait(TestTimeout);
        });

        var beginClose = Task.Run(scope.BeginClose);
        await callbackEntered.Task.WaitAsync(TestTimeout);
        var close = scope.CloseAsync();

        Assert.False(close.IsCompleted, "CloseAsync must not release resources while cancellation callbacks are running.");
        Assert.Equal(0, disposable.DisposeCount);

        // The in-flight invocation ends while the callback is still running: the token source
        // must not be disposed underneath the running Cancel().
        scope.ExitInvocation();
        releaseCallback.TrySetResult();

        Assert.Null(await beginClose.WaitAsync(TestTimeout));
        Assert.Empty(await close.WaitAsync(TestTimeout));
        Assert.Equal(1, disposable.DisposeCount);
    }

    [Fact]
    public async Task CompletedRelease_KeepsNoReferenceToTheReleasedObject()
    {
        var scope = NewScope();
        var (resource, weak) = await TrackAndReleaseConcurrentlyAsync(scope);

        Assert.True(GcDiagnostics.WaitForCollection(weak), "A released resource handle must not keep the released object alive.");
        Assert.False(resource.IsActive);
        GC.KeepAlive(resource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(TrackedResource, WeakReference)> TrackAndReleaseConcurrentlyAsync(OwnershipScope scope)
    {
        var disposable = new GatedDisposable();
        var resource = Track(scope, disposable);
        var weak = new WeakReference(disposable);

        var explicitRelease = resource.DisposeAsync().AsTask();
        await disposable.Entered.WaitAsync(TestTimeout);
        var close = scope.CloseAsync();
        disposable.Open();
        await explicitRelease.WaitAsync(TestTimeout);
        await close.WaitAsync(TestTimeout);
        return (resource, weak);
    }

    [Fact]
    public async Task DisposeAsyncConcurrentWithStop_FactoryIsReleasedOnlyAfterCleanupCompletes()
    {
        await using var engine = new PluginEngine();
        var disposable = new GatedDisposable();
        IPluginResource? handle = null;
        var factoryReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? cleanupCompletedAtFactoryRelease = null;
        var descriptor = Create("com.test.concurrent-dispose");
        var plugin = new TestPlugin((context, _) =>
        {
            handle = context.Resources.Track("gated", disposable);
            return Task.CompletedTask;
        });
        var factory = new TestPluginFactory(plugin, () =>
        {
            cleanupCompletedAtFactoryRelease ??= disposable.Completed;
            factoryReleased.TrySetResult();
            return ValueTask.CompletedTask;
        });
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        Assert.True((await engine.StartAsync(descriptor.Id)).IsSuccess);

        var explicitRelease = handle!.DisposeAsync().AsTask();
        await disposable.Entered.WaitAsync(TestTimeout);
        var stop = engine.StopAsync(descriptor.Id);

        // With correct ordering the factory cannot be released while the cleanup is blocked, so
        // this bounded wait elapses; it only limits how long a defective engine is given to
        // release the factory early.
        await Task.WhenAny(factoryReleased.Task, Task.Delay(300));
        disposable.Open();

        var result = await stop.WaitAsync(TestTimeout);
        await explicitRelease.WaitAsync(TestTimeout);

        Assert.True(result.IsSuccess);
        Assert.True(cleanupCompletedAtFactoryRelease, "The factory (and unload) must not be released while resource cleanup is pending.");
        Assert.Equal(1, disposable.DisposeCount);
        Assert.Equal(1, factory.ReleaseCount);
    }

    [Fact]
    public async Task CancellationCallbackStillRunning_BlocksResourceReleaseAndUnloadUntilItReturns()
    {
        await using var engine = new PluginEngine();
        var disposable = new GatedDisposable();
        disposable.Open();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = Create("com.test.callback-during-close");
        var plugin = new TestPlugin((context, _) =>
        {
            context.Resources.Track("tracked", disposable);
            context.Services.Register(Service("test.callback-close"), async (_, ct) =>
            {
                ct.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Task.Wait(TestTimeout);
                });
                handlerEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return PluginResponse.Success();
            });
            return Task.CompletedTask;
        });
        var factory = new TestPluginFactory(plugin);
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        Assert.True((await engine.StartAsync(descriptor.Id)).IsSuccess);

        var invocation = engine.Host.Services.InvokeAsync("test.callback-close", "echo").AsTask();
        await handlerEntered.Task.WaitAsync(TestTimeout);
        var stop = Task.Run(() => engine.StopAsync(descriptor.Id));
        await callbackEntered.Task.WaitAsync(TestTimeout);

        // The thread running teardown is inside the callback: nothing may have been released.
        Assert.Equal(0, disposable.DisposeCount);
        Assert.Equal(0, factory.ReleaseCount);

        releaseCallback.TrySetResult();
        Assert.True((await stop.WaitAsync(TestTimeout)).IsSuccess);
        Assert.Equal(PluginOperationStatus.Canceled, (await invocation.WaitAsync(TestTimeout)).Status);
        Assert.Equal(1, disposable.DisposeCount);
        Assert.Equal(1, factory.ReleaseCount);
    }
}
