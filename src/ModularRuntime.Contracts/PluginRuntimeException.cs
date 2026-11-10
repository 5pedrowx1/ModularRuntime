namespace ModularRuntime.Contracts;

/// <summary>
/// Exception thrown by the runtime for state and registration errors. Carries a stable
/// <see cref="ErrorCode"/> (see <see cref="PluginErrorCodes"/>) and an optional text-only
/// diagnostic snapshot.
/// </summary>
/// <remarks>
/// The diagnostic is captured before the exception crosses a public boundary.
/// Runtime exceptions that are retained by a host therefore do not keep plugin
/// exception objects or collectible load contexts alive.
/// </remarks>
public class PluginRuntimeException : Exception
{
    public PluginRuntimeException(string errorCode, string message, PluginDiagnostic? diagnostic = null)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
        Diagnostic = diagnostic;
    }

    /// <summary>Stable machine-readable error code.</summary>
    public string ErrorCode { get; }

    /// <summary>Text-only diagnostic captured when the failure occurred.</summary>
    public PluginDiagnostic? Diagnostic { get; }
}
