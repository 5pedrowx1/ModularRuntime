namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Tracks, per asynchronous flow, which owners' code is currently executing on behalf of
/// the runtime (lifecycle methods and event/service/contribution handlers).
/// </summary>
/// <remarks>
/// <para>
/// Stopping a plugin waits for its handlers to drain and for <c>ShutdownAsync</c>. If that
/// stop were requested from code that is itself running inside one of the plugin's handlers
/// or lifecycle methods (directly, or through a chain such as plugin handler → event →
/// host handler → stop), the stop would wait for its own caller and deadlock. The engine
/// uses these frames to detect that situation and reject the operation with
/// <c>reentrant_lifecycle_operation</c>.
/// </para>
/// <para>
/// Frames hold only GUIDs, never owner objects, so the execution context that flows into
/// tasks started by plugins cannot keep plugin objects alive. Because frames flow with the
/// execution context, background work started from a handler or lifecycle method is
/// considered part of that owner's flow.
/// </para>
/// <para>
/// Synchronous typed-service calls use a second, thread-local stack (<see cref="PushSynchronous"/>)
/// that costs no allocation: the frame is popped on the same thread when the call returns, and
/// does not flow into work the provider starts in the background. <see cref="Contains"/> consults
/// both stacks.
/// </para>
/// </remarks>
internal static class InvocationFrames
{
    private static readonly AsyncLocal<Frame?> Current = new();

    [ThreadStatic]
    private static SynchronousFrame[]? t_synchronous;

    [ThreadStatic]
    private static int t_synchronousDepth;

    /// <summary>
    /// Marks the current flow as running code of <paramref name="ownerKey"/>. Must be called
    /// at the start of an <see langword="async"/> method; the change is reverted automatically
    /// when that method completes.
    /// </summary>
    public static void Enter(Guid runtimeId, Guid ownerKey) =>
        Current.Value = new Frame(runtimeId, ownerKey, Current.Value);

    /// <summary>
    /// Pushes a frame that flows with the execution context and returns the previous frame, so
    /// the caller can restore it with <see cref="Restore"/> from a non-async method.
    /// </summary>
    public static Frame? EnterFlowing(Guid runtimeId, Guid ownerKey)
    {
        var previous = Current.Value;
        Current.Value = new Frame(runtimeId, ownerKey, previous);
        return previous;
    }

    /// <summary>Restores the frame returned by <see cref="EnterFlowing"/> on the current flow.</summary>
    public static void Restore(Frame? previous) => Current.Value = previous;

    /// <summary>Pushes a thread-local frame for a synchronous call; pair with <see cref="PopSynchronous"/> on the same thread.</summary>
    public static void PushSynchronous(Guid runtimeId, Guid ownerKey)
    {
        var stack = t_synchronous ??= new SynchronousFrame[8];
        var depth = t_synchronousDepth;
        if (depth == stack.Length)
        {
            Array.Resize(ref stack, depth * 2);
            t_synchronous = stack;
        }

        stack[depth] = new SynchronousFrame(runtimeId, ownerKey);
        t_synchronousDepth = depth + 1;
    }

    public static void PopSynchronous()
    {
        var depth = t_synchronousDepth - 1;
        if (depth < 0)
        {
            throw new InvalidOperationException("Synchronous invocation frames are unbalanced.");
        }

        t_synchronous![depth] = default;
        t_synchronousDepth = depth;
    }

    public static bool Contains(Guid runtimeId, Guid ownerKey)
    {
        for (var frame = Current.Value; frame is not null; frame = frame.Parent)
        {
            if (frame.RuntimeId == runtimeId && frame.OwnerKey == ownerKey)
            {
                return true;
            }
        }

        var stack = t_synchronous;
        for (var index = t_synchronousDepth - 1; index >= 0; index--)
        {
            if (stack![index].RuntimeId == runtimeId && stack[index].OwnerKey == ownerKey)
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsRuntime(Guid runtimeId)
    {
        for (var frame = Current.Value; frame is not null; frame = frame.Parent)
        {
            if (frame.RuntimeId == runtimeId)
            {
                return true;
            }
        }

        var stack = t_synchronous;
        for (var index = t_synchronousDepth - 1; index >= 0; index--)
        {
            if (stack![index].RuntimeId == runtimeId)
            {
                return true;
            }
        }

        return false;
    }

    internal sealed record Frame(Guid RuntimeId, Guid OwnerKey, Frame? Parent);

    private readonly record struct SynchronousFrame(Guid RuntimeId, Guid OwnerKey);
}
