using System.Buffers;
using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntimePlugin;

/// <summary>
/// Registers a binary service: bytes in, bytes out, no JSON. The request bytes are borrowed until
/// the handler completes; the response bytes belong to the caller, which disposes the result
/// (returning a pooled buffer to its pool).
/// </summary>
public sealed class ModularRuntimePlugin : IPlugin
{
    public const string ServiceId = "sample.text";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operations = new BinaryOperations()
            // A fresh array: the caller owns it.
            .Add("upper", (request, _) =>
            {
                var upper = new byte[request.Length];
                for (var index = 0; index < upper.Length; index++)
                {
                    var value = request.Span[index];
                    upper[index] = value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;
                }

                return ValueTask.FromResult(upper);
            })
            // A pooled buffer handed to the caller: returned to the pool when the caller disposes the result.
            .Add("reverse", (request, _) =>
            {
                if (request.Payload.IsEmpty)
                {
                    throw new PluginOperationException("empty_request", "Send at least one byte.");
                }

                var owner = MemoryPool<byte>.Shared.Rent(request.Payload.Length);
                request.Payload.Span.CopyTo(owner.Memory.Span);
                owner.Memory.Span[..request.Payload.Length].Reverse();
                return ValueTask.FromResult(BinaryResponse.Success(owner, request.Payload.Length));
            });

        context.Services.RegisterBinary(ServiceId, "Bytes service", new Version(1, 0), operations, maxRequestBytes: 1024 * 1024);
        context.Logger.LogInformation($"Binary service '{ServiceId}' registered with operations: {string.Join(", ", operations.Names)}.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
