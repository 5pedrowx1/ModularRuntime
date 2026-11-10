using ModularRuntime.Contracts;

namespace ModularRuntime.Loader.Internal;

/// <summary>
/// Writes to a host-supplied logger without letting logger failures escape loader
/// operations (discovery, assembly loading, factory release).
/// </summary>
internal static class SafeLogger
{
    public static void Log(IRuntimeLogger logger, LogEntry entry)
    {
        try
        {
            logger.Log(entry);
        }
        catch (Exception exception) when (!LoaderExceptionClassifier.IsFatal(exception))
        {
            // Logging is best-effort; a broken host logger must not fail loading or
            // leave a load context without an owner.
        }
    }
}
