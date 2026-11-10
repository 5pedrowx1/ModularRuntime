namespace ModularRuntime.Contracts;

/// <summary>
/// Convenience methods for plugin loggers.
/// </summary>
public static class PluginLoggerExtensions
{
    public static void LogTrace(this IPluginLogger logger, string message) =>
        logger.Log(LogLevel.Trace, message);

    public static void LogDebug(this IPluginLogger logger, string message) =>
        logger.Log(LogLevel.Debug, message);

    public static void LogInformation(this IPluginLogger logger, string message) =>
        logger.Log(LogLevel.Information, message);

    public static void LogWarning(this IPluginLogger logger, string message) =>
        logger.Log(LogLevel.Warning, message);

    public static void LogWarning(this IPluginLogger logger, string message, Exception exception) =>
        logger.Log(LogLevel.Warning, message, exception);

    public static void LogError(this IPluginLogger logger, string message) =>
        logger.Log(LogLevel.Error, message);

    public static void LogError(this IPluginLogger logger, string message, Exception exception) =>
        logger.Log(LogLevel.Error, message, exception);

    public static void LogCritical(this IPluginLogger logger, string message, Exception exception) =>
        logger.Log(LogLevel.Critical, message, exception);
}
