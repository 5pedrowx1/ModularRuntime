using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Tests;

public sealed class PluginSlotCancellationTests
{
    [Fact]
    public void CancelLifetime_WithoutCallbacks_CancelsToken()
    {
        var slot = CreateLoadedSlot(out _, out var lifetime);

        Assert.Null(slot.CancelLifetime());
        Assert.True(lifetime.IsCancellationRequested);

        slot.DetachInstance();
    }

    [Fact]
    public void CancelLifetime_InvokesSynchronousCallbacksOnce()
    {
        var slot = CreateLoadedSlot(out _, out var lifetime);
        var callbackCount = 0;
        lifetime.Token.Register(() => Interlocked.Increment(ref callbackCount));

        Assert.Null(slot.CancelLifetime());

        Assert.Equal(1, Volatile.Read(ref callbackCount));
        slot.DetachInstance();
    }

    [Fact]
    public void CancelLifetime_CallbackCanQueryStateReentrantly()
    {
        var slot = CreateLoadedSlot(out _, out var lifetime);
        var observed = PluginState.Discovered;
        lifetime.Token.Register(() => observed = slot.State);

        Assert.Null(slot.CancelLifetime());

        Assert.Equal(PluginState.Loaded, observed);
        slot.DetachInstance();
    }

    [Fact]
    public async Task CancelLifetime_CallbackCanReadStateFromAnotherThread()
    {
        var slot = CreateLoadedSlot(out _, out var lifetime);
        var observed = (PluginState)(-1);
        lifetime.Token.Register(() =>
        {
            var read = new TaskCompletionSource<PluginState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => read.TrySetResult(slot.State))
            {
                IsBackground = true,
                Name = "PluginSlot state reader",
            };
            thread.Start();

            observed = read.Task.Wait(TimeSpan.FromSeconds(10))
                ? read.Task.Result
                : (PluginState)(-1);
        });

        var cancelTask = Task.Run(() => slot.CancelLifetime());
        var completed = await Task.WhenAny(cancelTask, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.Same(cancelTask, completed);
        Assert.Null(await cancelTask);
        Assert.Equal(PluginState.Loaded, observed);
        slot.DetachInstance();
    }

    [Fact]
    public async Task DetachInstance_DefersLifetimeDisposalUntilCallbackCompletes()
    {
        var slot = CreateLoadedSlot(out var plugin, out var lifetime);
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });

        var cancelTask = Task.Run(() => slot.CancelLifetime());
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var detachTask = Task.Run(() => slot.DetachInstance());
        var detachCompleted = false;
        try
        {
            detachCompleted = ReferenceEquals(
                await Task.WhenAny(detachTask, Task.Delay(TimeSpan.FromSeconds(10))),
                detachTask);
        }
        finally
        {
            releaseCallback.TrySetResult();
        }

        Assert.True(detachCompleted, "DetachInstance must not wait for cancellation callbacks.");
        Assert.Same(plugin, await detachTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await cancelTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
    }

    [Fact]
    public async Task MultipleConcurrentCancels_InvokeCallbackOnce()
    {
        var slot = CreateLoadedSlot(out _, out var lifetime);
        var callbackCount = 0;
        lifetime.Token.Register(() => Interlocked.Increment(ref callbackCount));

        var cancels = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => slot.CancelLifetime()))
            .ToArray();
        var results = await Task.WhenAll(cancels);

        foreach (var result in results)
        {
            Assert.Null(result);
        }

        Assert.Equal(1, Volatile.Read(ref callbackCount));
        Assert.True(lifetime.IsCancellationRequested);
        slot.DetachInstance();
    }

    [Fact]
    public void CancelLifetime_CallbackFailure_IsReportedAndCleanupCompletes()
    {
        var slot = CreateLoadedSlot(out var plugin, out var lifetime);
        lifetime.Token.Register(() =>
            throw new InvalidOperationException("intentional cancellation callback failure"));

        var error = slot.CancelLifetime();

        Assert.NotNull(error);
        Assert.Equal(PluginErrorCodes.CancellationCallbackFailed, error.Code);
        Assert.Equal("System.InvalidOperationException", error.ExceptionType);
        Assert.Contains(
            "intentional cancellation callback failure",
            error.DiagnosticDetail,
            StringComparison.Ordinal);
        Assert.True(lifetime.IsCancellationRequested);

        Assert.Same(plugin, slot.DetachInstance());
        Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
    }

    [Fact]
    public void CancelLifetime_AfterDetach_IsNoOp()
    {
        var slot = CreateLoadedSlot(out var plugin, out var lifetime);

        Assert.Same(plugin, slot.DetachInstance());
        Assert.Null(slot.DetachInstance());
        Assert.Null(slot.CancelLifetime());
        Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
    }

    private static PluginSlot CreateLoadedSlot(
        out TestPlugin plugin,
        out CancellationTokenSource lifetime)
    {
        var descriptor = TestDescriptors.Create($"com.test.slot-{Guid.NewGuid():N}");
        plugin = new TestPlugin();
        lifetime = new CancellationTokenSource();
        var slot = new PluginSlot(descriptor, new TestPluginFactory(plugin));

        Assert.True(slot.TryMarkValidated());
        Assert.True(slot.TryTransition(PluginState.Validated, PluginState.Loaded));
        var instanceId = Guid.NewGuid();
        var scope = new OwnershipScope(
            new ResourceOwner(Guid.NewGuid(), ResourceOwnerKind.Plugin, descriptor.Id, instanceId),
            instanceId);
        slot.AttachInstance(plugin, instanceId, lifetime, scope);
        return slot;
    }
}
