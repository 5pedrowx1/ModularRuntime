using System.Buffers;

namespace ModularRuntime.Contracts;

/// <summary>
/// Handler for binary request/response invocations.
/// </summary>
/// <remarks>
/// <para>
/// Buffer ownership is explicit. <see cref="BinaryRequest.Payload"/> is <b>borrowed</b>: it is
/// valid only until the returned <see cref="ValueTask{TResult}"/> completes and must be copied by
/// the handler if it is kept beyond that point. The runtime does not copy it; the caller must not
/// modify it while the call is in flight.
/// </para>
/// <para>
/// The response <b>transfers ownership</b> of its bytes to the caller: a handler either returns
/// memory it will never touch again (a fresh array, an immutable buffer) or an
/// <see cref="IMemoryOwner{T}"/> that the caller disposes through
/// <see cref="BinaryInvocationResult.Dispose"/>. A rented buffer must never be returned to its
/// pool by the handler after it has been handed to the runtime.
/// </para>
/// <para>
/// The token is canceled when the caller cancels or when the handler's owner begins to shut down.
/// Handlers run without any runtime lock held and may call back into the runtime.
/// </para>
/// </remarks>
public delegate ValueTask<BinaryResponse> BinaryInvocationHandler(
    BinaryRequest request,
    CancellationToken cancellationToken);

/// <summary>Request delivered to a binary service handler.</summary>
/// <param name="Operation">Operation name requested by the caller.</param>
/// <param name="Payload">Borrowed request bytes; see <see cref="BinaryInvocationHandler"/>.</param>
/// <param name="Caller">Owner identity of the caller (host or plugin instance).</param>
/// <param name="CorrelationId">Optional caller-supplied correlation identifier.</param>
public readonly record struct BinaryRequest(
    string Operation,
    ReadOnlyMemory<byte> Payload,
    ResourceOwner Caller,
    string? CorrelationId);

/// <summary>Response produced by a binary handler. See <see cref="BinaryInvocationHandler"/> for ownership.</summary>
public sealed class BinaryResponse
{
    private BinaryResponse(bool isSuccess, ReadOnlyMemory<byte> payload, IMemoryOwner<byte>? owner, string? errorCode, string? message)
    {
        IsSuccess = isSuccess;
        Payload = payload;
        Owner = owner;
        ErrorCode = errorCode;
        Message = message;
    }

    /// <summary>A successful response without bytes.</summary>
    public static BinaryResponse Empty { get; } = new(true, ReadOnlyMemory<byte>.Empty, null, null, null);

    public bool IsSuccess { get; }

    /// <summary>Response bytes. The runtime hands them, together with <see cref="Owner"/>, to the caller.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Optional owner of <see cref="Payload"/>, disposed by the caller when it is done with the result.</summary>
    public IMemoryOwner<byte>? Owner { get; }

    public string? ErrorCode { get; }

    public string? Message { get; }

    /// <summary>Successful response whose bytes the handler will never modify or reuse.</summary>
    public static BinaryResponse Success(ReadOnlyMemory<byte> payload) =>
        payload.IsEmpty ? Empty : new(true, payload, null, null, null);

    /// <summary>
    /// Successful response that transfers <paramref name="owner"/> to the caller. The first
    /// <paramref name="length"/> bytes of <see cref="IMemoryOwner{T}.Memory"/> are the payload.
    /// </summary>
    public static BinaryResponse Success(IMemoryOwner<byte> owner, int length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var memory = owner.Memory;
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, memory.Length);
        return new(true, memory[..length], owner, null, null);
    }

    /// <summary>Handler-reported failure with a stable error code, for example <c>invalid_payload</c>.</summary>
    public static BinaryResponse Failure(string errorCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentNullException.ThrowIfNull(message);
        return new(false, ReadOnlyMemory<byte>.Empty, null, errorCode, message);
    }
}

/// <summary>
/// Outcome of a binary invocation as seen by the caller. Dispose it when the payload is no longer
/// needed; disposal returns a provider-owned buffer to its pool and is otherwise a no-op.
/// </summary>
/// <remarks>
/// Status semantics follow <see cref="PluginInvocationResult"/>. After <see cref="Dispose"/>,
/// <see cref="Payload"/> must not be read.
/// </remarks>
public sealed class BinaryInvocationResult : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private ReadOnlyMemory<byte> _payload;

    public BinaryInvocationResult(
        string targetId,
        string operation,
        PluginOperationStatus status,
        ReadOnlyMemory<byte> payload,
        IMemoryOwner<byte>? owner,
        string? errorCode,
        string? message,
        PluginDiagnostic? diagnostic,
        ResourceOwner? provider)
    {
        TargetId = targetId;
        Operation = operation;
        Status = status;
        _payload = payload;
        _owner = owner;
        ErrorCode = errorCode;
        Message = message;
        Diagnostic = diagnostic;
        Provider = provider;
    }

    public string TargetId { get; }

    public string Operation { get; }

    public PluginOperationStatus Status { get; }

    /// <summary>Response bytes of a successful call; empty otherwise. Valid until <see cref="Dispose"/>.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    public string? ErrorCode { get; }

    public string? Message { get; }

    public PluginDiagnostic? Diagnostic { get; }

    public ResourceOwner? Provider { get; }

    public bool IsSuccess => Status == PluginOperationStatus.Succeeded;

    /// <summary>Copies the payload into a new array (useful when the result must outlive its buffer).</summary>
    public byte[] ToArray() => _payload.ToArray();

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        _payload = ReadOnlyMemory<byte>.Empty;
        owner?.Dispose();
    }
}
