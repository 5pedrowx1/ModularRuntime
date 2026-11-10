namespace ModularRuntime.Contracts;

/// <summary>
/// Handler for request/response invocations. Used by services and by extension
/// contributions, which share one invocation engine.
/// </summary>
/// <remarks>
/// The token is canceled when the caller cancels or when the handler's owner begins to
/// shut down. Handlers run without any runtime lock held and may call back into the
/// runtime (publish events, invoke other services).
/// </remarks>
public delegate ValueTask<PluginResponse> PluginInvocationHandler(
    PluginRequest request,
    CancellationToken cancellationToken);

/// <summary>Request delivered to a service or contribution handler.</summary>
/// <param name="Operation">Operation name requested by the caller.</param>
/// <param name="Payload">Validated JSON payload.</param>
/// <param name="Caller">Owner identity of the caller (host or plugin instance).</param>
/// <param name="CorrelationId">Optional caller-supplied correlation identifier.</param>
public sealed record PluginRequest(
    string Operation,
    JsonPayload Payload,
    ResourceOwner Caller,
    string? CorrelationId);

/// <summary>Response produced by a handler.</summary>
public sealed record PluginResponse
{
    private PluginResponse(bool isSuccess, JsonPayload payload, string? errorCode, string? message)
    {
        IsSuccess = isSuccess;
        Payload = payload;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool IsSuccess { get; }

    public JsonPayload Payload { get; }

    public string? ErrorCode { get; }

    public string? Message { get; }

    /// <summary>Successful response with an optional payload (defaults to JSON <c>null</c>).</summary>
    public static PluginResponse Success(JsonPayload? payload = null) =>
        new(true, payload ?? JsonPayload.Null, null, null);

    /// <summary>Handler-reported failure with a stable error code, for example <c>invalid_payload</c>.</summary>
    public static PluginResponse Failure(string errorCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentNullException.ThrowIfNull(message);
        return new(false, JsonPayload.Null, errorCode, message);
    }
}

/// <summary>
/// Outcome of a service or contribution invocation as seen by the caller.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="PluginOperationStatus.Succeeded"/>: the handler returned success.</item>
/// <item><see cref="PluginOperationStatus.Failed"/>: the handler reported a failure, threw,
/// or returned an invalid response.</item>
/// <item><see cref="PluginOperationStatus.Rejected"/>: the runtime refused the call before
/// invoking the handler (not found, unavailable, removed, version, operation, payload).</item>
/// <item><see cref="PluginOperationStatus.Canceled"/>: the call was canceled.</item>
/// </list>
/// The result is text-only and never references the provider's objects.
/// </remarks>
public sealed record PluginInvocationResult(
    string TargetId,
    string Operation,
    PluginOperationStatus Status,
    JsonPayload? Payload,
    string? ErrorCode,
    string? Message,
    PluginDiagnostic? Diagnostic,
    ResourceOwner? Provider)
{
    public bool IsSuccess => Status == PluginOperationStatus.Succeeded;
}
