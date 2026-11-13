using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Ipc;

/// <summary>Error returned by the daemon for a request, or a client-side failure with an IPC error code.</summary>
public class IpcClientException(IpcError error) : Exception(error.Message)
{
    public IpcError Error { get; } = error;

    public string Code => Error.Code;
}

/// <summary>The connection to the daemon was lost before the request completed.</summary>
public sealed class IpcConnectionLostException(string message)
    : IpcClientException(new IpcError(IpcErrorCodes.ConnectionLost, message));

/// <summary>A daemon notification.</summary>
public sealed class IpcNotificationEventArgs(string name, JsonElement? payload) : EventArgs
{
    public string Name { get; } = name;

    public JsonElement? Payload { get; } = payload;

    public T? Read<T>() => Payload is { } payload ? payload.Deserialize<T>(IpcProtocol.JsonOptions) : default;
}

/// <summary>
/// One connection to a daemon: handshake, correlated request/response, cancellation, and notifications.
/// </summary>
/// <remarks>
/// Ownership: the client owns one background reader task, started after the handshake and ended
/// by disposal or by the daemon closing the pipe. <see cref="Completion"/> completes when the
/// connection is gone; every pending request then fails with <see cref="IpcConnectionLostException"/>.
/// <see cref="NotificationReceived"/> handlers run on the reader task and must not block.
/// </remarks>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly CancellationTokenSource _disposing = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;
    private long _nextId;
    private int _disposed;

    // Set before pending requests are failed; a request registered afterwards sees it and fails itself.
    private int _closed;

    private IpcClient(NamedPipeClientStream pipe, int maxMessageBytes)
    {
        _pipe = pipe;
        _maxMessageBytes = maxMessageBytes;
    }

    public WelcomePayload Welcome { get; private set; } = null!;

    public Guid ClientId { get; private set; }

    /// <summary>Completes when the connection has closed for any reason.</summary>
    public Task Completion => _completion.Task;

    public bool IsConnected => !_completion.Task.IsCompleted;

    /// <summary>Raised for each notification, on the reader task. Handlers must return quickly.</summary>
    public event EventHandler<IpcNotificationEventArgs>? NotificationReceived;

    /// <summary>Connects and performs the handshake.</summary>
    /// <exception cref="TimeoutException">No daemon accepted the connection in time.</exception>
    /// <exception cref="IpcClientException">The handshake was refused (for example a protocol version mismatch).</exception>
    public static async Task<IpcClient> ConnectAsync(
        string pipeName,
        string clientName,
        TimeSpan timeout,
        int maxMessageBytes = IpcProtocol.DefaultMaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        var pipe = PipeSecurityPolicy.CreateClientStream(pipeName);
        var client = new IpcClient(pipe, maxMessageBytes);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            PipeSecurityPolicy.VerifyServerOwner(pipe);
            await client.HandshakeAsync(clientName, timeout, cancellationToken).ConfigureAwait(false);
            client._reader = Task.Run(client.ReadLoopAsync, CancellationToken.None);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a request and returns the deserialized response payload.</summary>
    /// <exception cref="IpcClientException">The daemon returned an error.</exception>
    /// <exception cref="IpcConnectionLostException">The connection closed first.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled; a cancel message was sent to the daemon.</exception>
    public async Task<TResponse> RequestAsync<TResponse>(string operation, object? payload = null, CancellationToken cancellationToken = default)
    {
        var element = await SendRequestAsync(operation, payload, cancellationToken).ConfigureAwait(false);
        if (element is not { } value)
        {
            throw new IpcClientException(new IpcError(IpcErrorCodes.InvalidMessage, $"The daemon returned no payload for '{operation}'."));
        }

        return value.Deserialize<TResponse>(IpcProtocol.JsonOptions)
            ?? throw new IpcClientException(new IpcError(IpcErrorCodes.InvalidMessage, $"The daemon returned an empty payload for '{operation}'."));
    }

    public async Task<JsonElement?> SendRequestAsync(string operation, object? payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected)
        {
            throw new IpcConnectionLostException("The connection to the daemon is closed.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        if (Volatile.Read(ref _closed) != 0)
        {
            _pending.TryRemove(id, out _);
            throw new IpcConnectionLostException("The connection to the daemon is closed.");
        }

        try
        {
            await WriteAsync(
                new IpcMessage
                {
                    Type = IpcMessageTypes.Request,
                    Id = id,
                    Op = operation,
                    Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), IpcProtocol.JsonOptions),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new IpcConnectionLostException($"The connection to the daemon was lost: {exception.Message}");
        }

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            await TrySendCancelAsync(id).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposing.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null)
        {
            await _reader.ConfigureAwait(false);
        }

        Volatile.Write(ref _closed, 1);
        FailPending("The client was disposed.");
        _completion.TrySetResult();
        _disposing.Dispose();
    }

    private async Task HandshakeAsync(string clientName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(timeout);
        ClientId = Guid.NewGuid();

        IpcMessage? answer;
        try
        {
            await WriteAsync(
                new IpcMessage
                {
                    Type = IpcMessageTypes.Hello,
                    Payload = JsonSerializer.SerializeToElement(new HelloPayload(IpcProtocol.Version, clientName, ClientId), IpcProtocol.JsonOptions),
                },
                handshakeTimeout.Token).ConfigureAwait(false);

            answer = await MessageFraming.ReadAsync(_pipe, _maxMessageBytes, handshakeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The handshake's own deadline elapsed (a daemon that accepted the connection but has
            // not answered yet, for example while it is still starting): the documented outcome
            // is a timeout, not a cancellation, so callers such as DaemonLauncher.IsRunningAsync
            // can treat it as "not running yet".
            throw new TimeoutException($"The daemon did not complete the handshake within {timeout.TotalSeconds:0.##} s.");
        }

        if (answer is null)
        {
            throw new IpcConnectionLostException("The daemon closed the connection during the handshake.");
        }

        if (answer.Type == IpcMessageTypes.Error && answer.Error is { } error)
        {
            throw new IpcClientException(error);
        }

        if (answer.Type != IpcMessageTypes.Welcome || answer.Payload is not { } payload)
        {
            throw new IpcClientException(new IpcError(IpcErrorCodes.InvalidMessage, "Unexpected handshake answer."));
        }

        Welcome = payload.Deserialize<WelcomePayload>(IpcProtocol.JsonOptions)
            ?? throw new IpcClientException(new IpcError(IpcErrorCodes.InvalidMessage, "Invalid welcome payload."));
        if (Welcome.ProtocolVersion != IpcProtocol.Version)
        {
            throw new IpcClientException(new IpcError(IpcErrorCodes.ProtocolVersionMismatch, "The daemon answered with another protocol version."));
        }
    }

    private async Task ReadLoopAsync()
    {
        var reason = "The daemon closed the connection.";
        try
        {
            while (!_disposing.IsCancellationRequested)
            {
                var message = await MessageFraming.ReadAsync(_pipe, _maxMessageBytes, _disposing.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                switch (message.Type)
                {
                    case IpcMessageTypes.Response when message.Id is { } id:
                        if (_pending.TryRemove(id, out var completion))
                        {
                            if (message.Error is { } error)
                            {
                                completion.TrySetException(new IpcClientException(error));
                            }
                            else
                            {
                                completion.TrySetResult(message.Payload);
                            }
                        }

                        break;
                    case IpcMessageTypes.Notification when message.Op is { } name:
                        RaiseNotification(name, message.Payload);
                        break;
                    case IpcMessageTypes.Error:
                        reason = $"The daemon reported a protocol error: [{message.Error?.Code}] {message.Error?.Message}";
                        return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or IpcProtocolException)
        {
            reason = $"The connection to the daemon was lost: {exception.Message}";
        }
        finally
        {
            Volatile.Write(ref _closed, 1);
            FailPending(reason);
            _completion.TrySetResult();
        }
    }

    private void RaiseNotification(string name, JsonElement? payload)
    {
        try
        {
            NotificationReceived?.Invoke(this, new IpcNotificationEventArgs(name, payload));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A faulty subscriber must not break the connection.
        }
    }

    private void FailPending(string reason)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(new IpcConnectionLostException(reason));
            }
        }
    }

    private async Task TrySendCancelAsync(long id)
    {
        try
        {
            await WriteAsync(new IpcMessage { Type = IpcMessageTypes.Cancel, Id = id }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task WriteAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MessageFraming.WriteAsync(_pipe, message, _maxMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
