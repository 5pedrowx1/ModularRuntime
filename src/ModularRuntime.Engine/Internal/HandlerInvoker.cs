using ModularRuntime.Contracts;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Internal;

/// <summary>A resource that exposes a request/response handler (service or contribution).</summary>
internal abstract class InvocableEntry : OwnedResource
{
    private PluginInvocationHandler? _handler;

    protected InvocableEntry(OwnershipScope scope, ResourceKind kind, string name, PluginInvocationHandler handler)
        : base(scope, kind, name)
    {
        _handler = handler;
    }

    /// <summary>The owner's handler, or <see langword="null"/> after release.</summary>
    public PluginInvocationHandler? Handler => Volatile.Read(ref _handler);

    /// <summary>Drops the reference to the owner's delegate (and therefore its target object).</summary>
    protected void ClearHandler() => Volatile.Write(ref _handler, null);
}

/// <summary>
/// Single invocation engine used by services and extension contributions.
/// </summary>
/// <remarks>
/// Invariants:
/// <list type="bullet">
/// <item>No runtime lock is held while the handler runs.</item>
/// <item>The call is admitted through the provider's scope; once the provider begins to shut
/// down, new calls are rejected and in-flight calls are counted for draining.</item>
/// <item>The handler token is linked to the caller's token and the provider's scope token.</item>
/// <item>Exceptions are converted to text immediately; results never reference provider objects.</item>
/// </list>
/// </remarks>
internal static class HandlerInvoker
{
    public static async ValueTask<PluginInvocationResult> InvokeAsync(
        RuntimeCore core,
        InvocableEntry entry,
        string targetId,
        string category,
        string unavailableCode,
        string removedCode,
        PluginRequest request,
        CancellationToken cancellationToken)
    {
        var provider = entry.Owner;
        if (cancellationToken.IsCancellationRequested)
        {
            return Result(targetId, request, PluginOperationStatus.Canceled, provider, PluginErrorCodes.OperationCanceled, "The invocation was canceled before it started.");
        }

        if (!entry.Scope.TryEnterInvocation(out var scopeToken))
        {
            return Result(targetId, request, PluginOperationStatus.Rejected, provider, unavailableCode, $"'{targetId}' is unavailable because its owner is shutting down.");
        }

        var completedAsynchronously = false;
        ValueTask<PluginResponse> pending = default;
        try
        {
            // Re-check after admission: a removal that completed before this point must
            // prevent the invocation.
            var handler = entry.Handler;
            if (handler is null || entry.IsReleased)
            {
                return Result(targetId, request, PluginOperationStatus.Rejected, provider, removedCode, $"'{targetId}' was removed.");
            }

            InvocationFrames.Enter(core.RuntimeId, entry.Scope.OwnerKey);
            using var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scopeToken)
                : null;
            var token = linked?.Token ?? scopeToken;

            try
            {
                pending = handler(request, token);
                handler = null;
                completedAsynchronously = !pending.IsCompleted;
                var response = await pending.ConfigureAwait(false);

                // The pending task is the provider's async state machine box, whose type belongs to
                // the provider's assembly: never keep it beyond this point.
                pending = default;
                if (response is null)
                {
                    return Result(targetId, request, PluginOperationStatus.Failed, provider, PluginErrorCodes.InvalidResponse, "The handler returned a null response.");
                }

                if (!response.IsSuccess)
                {
                    return Result(targetId, request, PluginOperationStatus.Failed, provider, response.ErrorCode, response.Message);
                }

                if (!core.IsPayloadWithinLimit(response.Payload))
                {
                    return Result(targetId, request, PluginOperationStatus.Failed, provider, PluginErrorCodes.PayloadTooLarge, "Response: " + core.PayloadTooLargeMessage(response.Payload));
                }

                return new PluginInvocationResult(targetId, request.Operation, PluginOperationStatus.Succeeded, response.Payload, null, null, null, provider);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                var message = cancellationToken.IsCancellationRequested
                    ? "The invocation was canceled by the caller."
                    : "The invocation was canceled because the provider is shutting down.";
                return Result(targetId, request, PluginOperationStatus.Canceled, provider, PluginErrorCodes.OperationCanceled, message);
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                var diagnostic = PluginDiagnostic.FromException(exception);
                exception = null!;
                var message = $"Handler for '{targetId}' operation '{request.Operation}' failed: {diagnostic.Message}";
                core.Log(LogLevel.Warning, category, message, provider, diagnostic);
                return new PluginInvocationResult(targetId, request.Operation, PluginOperationStatus.Failed, null, PluginErrorCodes.HandlerFailed, message, diagnostic, provider);
            }
        }
        finally
        {
            // The pending task may be faulted with a provider exception; drop it on every path.
            pending = default;
            entry.Scope.ExitInvocation();
            if (completedAsynchronously)
            {
                // Do not return to the caller on top of the provider's frames; see ThreadPoolHop.
                await new ThreadPoolHop();
            }
        }
    }

    public static PluginInvocationResult Result(
        string targetId,
        PluginRequest request,
        PluginOperationStatus status,
        ResourceOwner? provider,
        string? errorCode,
        string? message) =>
        new(targetId, request.Operation, status, null, errorCode, message, null, provider);

    public static PluginInvocationResult Rejected(string targetId, string operation, string errorCode, string message, ResourceOwner? provider = null) =>
        new(targetId, operation, PluginOperationStatus.Rejected, null, errorCode, message, null, provider);
}
