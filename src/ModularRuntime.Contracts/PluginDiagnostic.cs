namespace ModularRuntime.Contracts;

/// <summary>
/// Text-only diagnostic captured from an exception at a runtime boundary.
/// </summary>
/// <remarks>
/// This type deliberately contains no reference to <see cref="Exception"/>.
/// Exception objects created by plugin code can keep the plugin type, assembly,
/// and collectible load context alive. The runtime converts them to strings while
/// the exception is still available and retains only this immutable snapshot.
/// </remarks>
public sealed record PluginDiagnostic(
    string ExceptionType,
    string Message,
    string? StackTrace = null,
    string? Detail = null)
{
    /// <summary>Returns a diagnostic for a non-null exception, or <see langword="null"/>.</summary>
    public static PluginDiagnostic? FromExceptionOrNull(Exception? exception) =>
        exception is null ? null : FromException(exception);

    /// <summary>
    /// Captures type, message, stack trace, and full textual detail without retaining
    /// the exception object.
    /// </summary>
    /// <remarks>
    /// Exception types defined by plugins may override <see cref="Exception.Message"/>,
    /// <see cref="Exception.StackTrace"/>, or <see cref="Exception.ToString"/> with code
    /// that throws. Capture never propagates such failures: each member is read
    /// independently and replaced by a placeholder when it cannot be read.
    /// </remarks>
    public static PluginDiagnostic FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is PluginRuntimeException { Diagnostic: { } diagnostic })
        {
            return diagnostic;
        }

        // CancellationTokenSource wraps callback failures in AggregateException.
        // Expose the single underlying failure as the primary type while preserving
        // the complete aggregate text in Detail.
        var primary = exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;
        var primaryType = primary.GetType();
        var typeName = primaryType.FullName ?? primaryType.Name;

        var message = TryRead(primary, static e => e.Message) ??
            $"<message unavailable: {typeName}.Message threw>";
        var stackTrace = TryRead(primary, static e => e.StackTrace);
        var detail = TryRead(exception, static e => e.ToString()) ??
            (stackTrace is null ? $"{typeName}: {message}" : $"{typeName}: {message}{Environment.NewLine}{stackTrace}");

        return new PluginDiagnostic(typeName, message, stackTrace, detail);
    }

    private static string? TryRead(Exception exception, Func<Exception, string?> read)
    {
        try
        {
            return read(exception);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Diagnostic capture runs inside runtime error handlers and must not fail.
            return null;
        }
    }
}
