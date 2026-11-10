using ModularRuntime.Contracts;

namespace ModularRuntime.Engine;

/// <summary>
/// Formal lifecycle transition policy.
/// </summary>
/// <remarks>
/// Stopped and Failed are terminal for a plugin registration. Restarting requires a new
/// registration, instance, and loading context.
/// </remarks>
public static class PluginLifecycle
{
    private static readonly HashSet<(PluginState From, PluginState To)> Allowed =
    [
        (PluginState.Discovered, PluginState.Validated),
        (PluginState.Discovered, PluginState.Failed),
        (PluginState.Validated, PluginState.Loaded),
        (PluginState.Validated, PluginState.Failed),
        (PluginState.Loaded, PluginState.Running),
        (PluginState.Loaded, PluginState.Failed),
        (PluginState.Running, PluginState.Stopping),
        (PluginState.Running, PluginState.Failed),
        (PluginState.Stopping, PluginState.Stopped),
        (PluginState.Stopping, PluginState.Failed),
    ];

    public static bool CanTransition(PluginState from, PluginState to) => Allowed.Contains((from, to));
}
