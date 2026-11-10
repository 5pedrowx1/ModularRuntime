using ModularRuntime.Contracts;

namespace ModularRuntime.Loader;

/// <summary>Error raised when a validated plugin cannot be loaded or instantiated.</summary>
/// <remarks>
/// The exception intentionally does not expose an inner <see cref="Exception"/>.
/// <see cref="Diagnostic"/> preserves the type, message, stack trace, and full detail
/// as text so a retained load error cannot keep a collectible plugin load context alive.
/// </remarks>
public sealed class PluginLoadException : PluginRuntimeException
{
    public PluginLoadException(
        string code,
        PluginId pluginId,
        string message,
        PluginDiagnostic? diagnostic = null)
        : base(code, message, diagnostic)
    {
        PluginId = pluginId;
    }

    public PluginId PluginId { get; }
}
