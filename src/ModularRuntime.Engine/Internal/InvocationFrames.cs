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
/// </remarks>
internal static class InvocationFrames
{
    private static readonly AsyncLocal<Frame?> Current = new();

    /// <summary>
    /// Marks the current flow as running code of <paramref name="ownerKey"/>. Must be called
    /// at the start of an <see langword="async"/> method; the change is reverted automatically
    /// when that method completes.
    /// </summary>
    public static void Enter(Guid runtimeId, Guid ownerKey) =>
        Current.Value = new Frame(runtimeId, ownerKey, Current.Value);

    public static bool Contains(Guid runtimeId, Guid ownerKey)
    {
        for (var frame = Current.Value; frame is not null; frame = frame.Parent)
        {
            if (frame.RuntimeId == runtimeId && frame.OwnerKey == ownerKey)
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

        return false;
    }

    private sealed record Frame(Guid RuntimeId, Guid OwnerKey, Frame? Parent);
}
