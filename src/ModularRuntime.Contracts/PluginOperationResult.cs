namespace ModularRuntime.Contracts;

/// <summary>
/// Result of an engine operation on one plugin.
/// </summary>
/// <remarks>
/// The result contains the state observed at the end of the operation. A rejected or
/// canceled operation normally leaves the state unchanged. <see cref="Diagnostic"/>
/// holds a text-only snapshot of the failure; the result never retains the original
/// exception object, which could keep a plugin assembly and load context alive.
/// </remarks>
public sealed record PluginOperationResult(
    PluginId PluginId,
    string Operation,
    PluginOperationStatus Status,
    PluginState? State,
    string? ErrorCode = null,
    string? Message = null,
    PluginDiagnostic? Diagnostic = null)
{
    public bool IsSuccess => Status == PluginOperationStatus.Succeeded;
}
