using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Ipc;

/// <summary>Raised by request handlers to return a structured error to the client.</summary>
public sealed class IpcRequestException(string code, string message, DiagnosticDto? diagnostic = null) : Exception(message)
{
    public IpcError Error { get; } = new(code, message, diagnostic);
}

/// <summary>Daemon-side request processing. Implementations must be thread-safe.</summary>
public interface IIpcRequestHandler
{
    /// <summary>Handshake data for a new connection.</summary>
    WelcomePayload CreateWelcome(Guid connectionId);

    /// <summary>
    /// Handles one request. Returns the response payload (serialized as JSON) or throws
    /// <see cref="IpcRequestException"/>. The token is canceled when the client cancels the request,
    /// disconnects, or the server stops.
    /// </summary>
    Task<object?> HandleAsync(IpcConnection connection, string operation, JsonElement? payload, CancellationToken cancellationToken);

    void OnConnected(IpcConnection connection);

    /// <summary>Called once per connection after all its requests have finished.</summary>
    void OnDisconnected(IpcConnection connection);
}

public sealed record IpcServerOptions(string PipeName)
{
    public int MaxMessageBytes { get; init; } = IpcProtocol.DefaultMaxMessageBytes;

    public int MaxConnections { get; init; } = 32;

    /// <summary>Notifications buffered per connection before new ones are rejected (slow client).</summary>
    public int NotificationQueueCapacity { get; init; } = 256;

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Named pipe server: accepts clients, performs the handshake, dispatches requests concurrently,
/// and delivers notifications through a bounded per-connection queue.
/// </summary>
/// <remarks>
/// <para>
/// Ownership: the server owns the accept loop and one task per connection; each connection owns
/// its reader loop, its notification pump, and the tasks of its in-flight requests. Stopping the
/// server cancels and awaits all of them. A failing or slow client affects only its own connection:
/// request handlers never wait for a client, and notifications to a full queue are rejected instead
/// of blocking the producer.
/// </para>
/// </remarks>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IpcServerOptions _options;
    private readonly IIpcRequestHandler _handler;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, IpcConnection> _connections = new();
    private readonly ConcurrentDictionary<Task, byte> _connectionTasks = new();
    private NamedPipeServerStream? _firstInstance;
    private Task? _acceptLoop;
    private int _stopped;

    public IpcServer(IpcServerOptions options, IIpcRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        if (options.MaxMessageBytes is <= 0 or > IpcProtocol.HardMaxMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMessageBytes is out of range.");
        }

        _options = options;
        _handler = handler;
    }

    public IReadOnlyCollection<IpcConnection> Connections => [.. _connections.Values];

    /// <summary>
    /// Creates the first pipe instance (failing if the pipe already exists, for example because
    /// another daemon serves this profile) and starts accepting clients.
    /// </summary>
    /// <exception cref="IOException">The pipe name is already in use.</exception>
    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        _firstInstance = PipeSecurityPolicy.CreateServerStream(_options.PipeName, firstInstance: true, _options.MaxConnections);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Stops accepting, disconnects every client, and waits for all connection work to finish.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        await Task.WhenAll(_connectionTasks.Keys).ConfigureAwait(false);
        _firstInstance?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Sends a notification to every connected client (non-blocking; full queues drop it).</summary>
    public void Broadcast(string notification, object payload)
    {
        foreach (var connection in _connections.Values)
        {
            connection.TrySendNotification(notification, payload);
        }
    }

    private async Task AcceptLoopAsync()
    {
        var token = _stopping.Token;
        var next = _firstInstance;
        while (!token.IsCancellationRequested)
        {
            var pipe = next ?? PipeSecurityPolicy.CreateServerStream(_options.PipeName, firstInstance: false, _options.MaxConnections);
            next = null;
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                // The client vanished between connect and accept; keep serving others.
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (ReferenceEquals(pipe, _firstInstance))
            {
                _firstInstance = null;
            }

            var connection = new IpcConnection(pipe, _options, _handler, _stopping.Token);
            var task = RunConnectionAsync(connection);
            _connectionTasks.TryAdd(task, 0);
            _ = task.ContinueWith(
                completed => _connectionTasks.TryRemove(completed, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunConnectionAsync(IpcConnection connection)
    {
        await Task.Yield();
        var registered = false;
        try
        {
            if (!await connection.HandshakeAsync().ConfigureAwait(false))
            {
                return;
            }

            _connections.TryAdd(connection.ConnectionId, connection);
            registered = true;
            _handler.OnConnected(connection);
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or IpcProtocolException)
        {
            // Connection-level failure: affects only this client.
        }
        finally
        {
            await connection.ShutdownAsync().ConfigureAwait(false);
            if (registered)
            {
                _connections.TryRemove(connection.ConnectionId, out _);
                _handler.OnDisconnected(connection);
            }
        }
    }
}

/// <summary>One accepted client connection (daemon side).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The owning IpcServer disposes every connection exactly once through ShutdownAsync, after all of its tasks completed.")]
public sealed class IpcConnection
{
    private readonly NamedPipeServerStream _pipe;
    private readonly IpcServerOptions _options;
    private readonly IIpcRequestHandler _handler;
    private readonly CancellationTokenSource _closing;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<IpcMessage> _notifications;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _requests = new();
    private readonly ConcurrentDictionary<Task, byte> _requestTasks = new();
    private Task? _pump;
    private long _droppedNotifications;

    internal IpcConnection(NamedPipeServerStream pipe, IpcServerOptions options, IIpcRequestHandler handler, CancellationToken serverStopping)
    {
        _pipe = pipe;
        _options = options;
        _handler = handler;
        _closing = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
        _notifications = Channel.CreateBounded<IpcMessage>(new BoundedChannelOptions(options.NotificationQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public Guid ConnectionId { get; } = Guid.NewGuid();

    public Guid ClientId { get; private set; }

    public string ClientName { get; private set; } = string.Empty;

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Canceled when the connection closes; per-connection subscriptions observe it.</summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>Notifications rejected because this client did not keep up.</summary>
    public long DroppedNotifications => Interlocked.Read(ref _droppedNotifications);

    /// <summary>
    /// Queues a notification without blocking. Returns <see langword="false"/> (and counts a drop)
    /// when the client's queue is full or the connection is closing.
    /// </summary>
    public bool TrySendNotification(string notification, object payload)
    {
        if (_closing.IsCancellationRequested)
        {
            return false;
        }

        var message = new IpcMessage
        {
            Type = IpcMessageTypes.Notification,
            Op = notification,
            Payload = JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions),
        };
        if (_notifications.Writer.TryWrite(message))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedNotifications);
        return false;
    }

    internal async Task<bool> HandshakeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var hello = await MessageFraming.ReadAsync(_pipe, _options.MaxMessageBytes, timeout.Token).ConfigureAwait(false);
        if (hello is null)
        {
            return false;
        }

        if (hello.Type != IpcMessageTypes.Hello || hello.Payload is not { } payload)
        {
            await SendErrorAsync(IpcErrorCodes.HandshakeRequired, "The first message must be a hello handshake.", timeout.Token).ConfigureAwait(false);
            return false;
        }

        HelloPayload? request;
        try
        {
            request = payload.Deserialize<HelloPayload>(IpcProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || request.ClientName is not { Length: > 0 and <= IpcProtocol.MaxIdentifierLength })
        {
            await SendErrorAsync(IpcErrorCodes.InvalidMessage, "Invalid hello payload.", timeout.Token).ConfigureAwait(false);
            return false;
        }

        if (request.ProtocolVersion != IpcProtocol.Version)
        {
            await SendErrorAsync(
                IpcErrorCodes.ProtocolVersionMismatch,
                $"The daemon speaks protocol version {IpcProtocol.Version}; the client speaks {request.ProtocolVersion}.",
                timeout.Token).ConfigureAwait(false);
            return false;
        }

        ClientId = request.ClientId;
        ClientName = request.ClientName;
        await WriteAsync(
            new IpcMessage
            {
                Type = IpcMessageTypes.Welcome,
                Payload = JsonSerializer.SerializeToElement(_handler.CreateWelcome(ConnectionId), IpcProtocol.JsonOptions),
            },
            timeout.Token).ConfigureAwait(false);
        return true;
    }

    internal async Task RunAsync()
    {
        _pump = Task.Run(PumpNotificationsAsync);
        var token = _closing.Token;
        while (!token.IsCancellationRequested)
        {
            IpcMessage? message;
            try
            {
                message = await MessageFraming.ReadAsync(_pipe, _options.MaxMessageBytes, token).ConfigureAwait(false);
            }
            catch (IpcProtocolException exception)
            {
                // Framing is lost; tell the client why and close.
                await TrySendErrorAsync(exception.Code, exception.Message).ConfigureAwait(false);
                return;
            }

            if (message is null)
            {
                return;
            }

            switch (message.Type)
            {
                case IpcMessageTypes.Request:
                    await AcceptRequestAsync(message).ConfigureAwait(false);
                    break;
                case IpcMessageTypes.Cancel when message.Id is { } id:
                    if (_requests.TryGetValue(id, out var cancellation))
                    {
                        await cancellation.CancelAsync().ConfigureAwait(false);
                    }

                    break;
                default:
                    await TrySendErrorAsync(IpcErrorCodes.InvalidMessage, $"Unexpected message type '{message.Type}'.").ConfigureAwait(false);
                    return;
            }
        }
    }

    internal async Task ShutdownAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        _notifications.Writer.TryComplete();
        await Task.WhenAll(_requestTasks.Keys).ConfigureAwait(false);
        if (_pump is not null)
        {
            await _pump.ConfigureAwait(false);
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _closing.Dispose();
    }

    private async Task AcceptRequestAsync(IpcMessage message)
    {
        if (message.Id is not { } id || string.IsNullOrEmpty(message.Op) || message.Op.Length > IpcProtocol.MaxIdentifierLength)
        {
            await TrySendResponseAsync(message.Id, null, new IpcError(IpcErrorCodes.InvalidRequest, "A request needs an id and an operation.")).ConfigureAwait(false);
            return;
        }

        if (_requests.Count >= IpcProtocol.MaxConcurrentRequestsPerConnection)
        {
            await TrySendResponseAsync(id, null, new IpcError(IpcErrorCodes.TooManyRequests, "Too many concurrent requests on this connection.")).ConfigureAwait(false);
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        if (!_requests.TryAdd(id, cancellation))
        {
            cancellation.Dispose();
            await TrySendResponseAsync(id, null, new IpcError(IpcErrorCodes.InvalidRequest, $"Request id {id} is already in flight.")).ConfigureAwait(false);
            return;
        }

        var task = Task.Run(() => ProcessRequestAsync(id, message.Op, message.Payload, cancellation));
        _requestTasks.TryAdd(task, 0);
        _ = task.ContinueWith(
            completed => _requestTasks.TryRemove(completed, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ProcessRequestAsync(long id, string operation, JsonElement? payload, CancellationTokenSource cancellation)
    {
        object? result = null;
        IpcError? error = null;
        try
        {
            if (!IpcOperations.All.Contains(operation))
            {
                error = new IpcError(IpcErrorCodes.UnknownOperation, $"Unknown operation '{operation}'.");
            }
            else
            {
                result = await _handler.HandleAsync(this, operation, payload, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (IpcRequestException exception)
        {
            error = exception.Error;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            error = new IpcError(IpcErrorCodes.Canceled, "The request was canceled.");
        }
        catch (JsonException exception)
        {
            error = new IpcError(IpcErrorCodes.InvalidRequest, $"Invalid request payload: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = new IpcError(
                IpcErrorCodes.InternalError,
                "The daemon failed to process the request.",
                new DiagnosticDto(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.StackTrace, null));
        }
        finally
        {
            _requests.TryRemove(id, out _);
            cancellation.Dispose();
        }

        await TrySendResponseAsync(id, result, error).ConfigureAwait(false);
    }

    private async Task PumpNotificationsAsync()
    {
        try
        {
            await foreach (var message in _notifications.Reader.ReadAllAsync(_closing.Token).ConfigureAwait(false))
            {
                await WriteAsync(message, _closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or IpcProtocolException)
        {
            // The connection is closing; pending notifications are discarded.
        }
    }

    private async Task TrySendResponseAsync(long? id, object? result, IpcError? error)
    {
        try
        {
            await WriteAsync(
                new IpcMessage
                {
                    Type = IpcMessageTypes.Response,
                    Id = id,
                    Payload = error is null && result is not null ? JsonSerializer.SerializeToElement(result, result.GetType(), IpcProtocol.JsonOptions) : null,
                    Error = error,
                },
                _closing.Token).ConfigureAwait(false);
        }
        catch (IpcProtocolException exception) when (exception.Code == IpcErrorCodes.MessageTooLarge)
        {
            await TrySendResponseAsync(id, null, new IpcError(IpcErrorCodes.MessageTooLarge, "The response exceeds the maximum message size; narrow the request.")).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client is gone; nothing to deliver.
        }
    }

    private Task SendErrorAsync(string code, string message, CancellationToken cancellationToken) =>
        WriteAsync(new IpcMessage { Type = IpcMessageTypes.Error, Error = new IpcError(code, message) }, cancellationToken);

    private async Task TrySendErrorAsync(string code, string message)
    {
        try
        {
            await SendErrorAsync(code, message, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task WriteAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MessageFraming.WriteAsync(_pipe, message, _options.MaxMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
