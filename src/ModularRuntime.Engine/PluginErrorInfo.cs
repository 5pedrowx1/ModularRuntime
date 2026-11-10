using ModularRuntime.Contracts;

namespace ModularRuntime.Engine;

/// <summary>Error information retained by the engine without holding references to plugin objects.</summary>
public sealed record PluginErrorInfo(
    string Code,
    string Operation,
    string Message,
    string? ExceptionType,
    string? DiagnosticDetail,
    DateTimeOffset OccurredAt)
{
    /// <summary>Structured text-only diagnostic captured with the error.</summary>
    public PluginDiagnostic? Diagnostic { get; init; }

    internal static PluginErrorInfo From(
        string code,
        string operation,
        string message,
        Exception? exception) =>
        From(code, operation, message, PluginDiagnostic.FromExceptionOrNull(exception));

    internal static PluginErrorInfo From(
        string code,
        string operation,
        string message,
        PluginDiagnostic? diagnostic) =>
        new(
            code,
            operation,
            message,
            diagnostic?.ExceptionType,
            diagnostic?.Detail,
            DateTimeOffset.UtcNow)
        {
            Diagnostic = diagnostic,
        };
}
