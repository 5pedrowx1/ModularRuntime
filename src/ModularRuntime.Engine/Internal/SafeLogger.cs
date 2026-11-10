using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Writes to a host-supplied logger without letting logger failures escape.
/// </summary>
/// <remarks>
/// Logging happens between lifecycle transitions. If a host logger exception escaped
/// at that point, a plugin could be left in a transitional state (for example
/// <see cref="PluginState.Stopping"/>) with its instance still attached. Logging is
/// therefore best-effort: non-fatal logger failures are discarded.
/// </remarks>
internal static class SafeLogger
{
    public static void Log(IRuntimeLogger logger, LogEntry entry)
    {
        try
        {
            logger.Log(entry);
        }
        catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
        {
            // Intentionally ignored; see type remarks.
        }
    }
}
