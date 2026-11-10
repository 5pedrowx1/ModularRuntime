using System.Runtime.CompilerServices;

namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Awaitable that always resumes on a fresh thread-pool work item, ignoring any
/// synchronization context.
/// </summary>
/// <remarks>
/// <para>
/// When an awaited plugin method completes asynchronously, the runtime code awaiting it resumes
/// <em>on top of</em> the plugin method's frame, and anything that then completes synchronously
/// (for example the caller of <c>StopAsync</c> or <c>InvokeAsync</c>) also runs on that stack.
/// While such frames exist they keep the plugin's collectible load context alive, and the
/// caller's code runs inside the plugin's call stack.
/// </para>
/// <para>
/// The runtime awaits this hop after plugin code has run asynchronously (after teardown, and after
/// asynchronously completed handlers), so that callers always continue on a stack that contains
/// no plugin frames. Found by the v0.2 unload tests (scenario E): without the hop a host that
/// checked unload right after a stop could still observe the plugin's context alive.
/// </para>
/// </remarks>
internal readonly struct ThreadPoolHop : ICriticalNotifyCompletion
{
    public bool IsCompleted => false;

    public ThreadPoolHop GetAwaiter() => this;

    public void GetResult()
    {
    }

    public void OnCompleted(Action continuation) =>
        ThreadPool.QueueUserWorkItem(static state => state(), continuation, preferLocal: false);

    public void UnsafeOnCompleted(Action continuation) =>
        ThreadPool.UnsafeQueueUserWorkItem(static state => state(), continuation, preferLocal: false);
}
