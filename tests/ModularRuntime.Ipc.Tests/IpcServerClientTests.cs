using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Ipc.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "Disposed by xUnit through IAsyncLifetime.DisposeAsync.")]
public sealed class IpcServerClientTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly string _pipeName = $"ModularRuntime.test.{Guid.NewGuid():N}";
    private readonly TestHandler _handler = new();
    private IpcServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new IpcServer(new IpcServerOptions(_pipeName) { MaxMessageBytes = 64 * 1024, NotificationQueueCapacity = 16 }, _handler);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>
    /// Test operations: status → echo; plugins.get → delayed echo of the id; plugins.start → waits for
    /// cancellation; plugins.stop → structured error; plugins.startAll → unexpected exception.
    /// </summary>
    private sealed class TestHandler : IIpcRequestHandler
    {
        public TaskCompletionSource<bool> BlockingCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BlockingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Events { get; } = new();

        public ConcurrentDictionary<Guid, IpcConnection> Connections { get; } = new();

        public WelcomePayload CreateWelcome(Guid connectionId) =>
            new(IpcProtocol.Version, Guid.NewGuid(), Guid.NewGuid(), "test", connectionId, 64 * 1024);

        public void OnConnected(IpcConnection connection)
        {
            Connections[connection.ConnectionId] = connection;
            Events.Enqueue($"connected:{connection.ClientName}");
        }

        public void OnDisconnected(IpcConnection connection)
        {
            Connections.TryRemove(connection.ConnectionId, out _);
            Events.Enqueue($"disconnected:{connection.ClientName}");
        }

        public async Task<object?> HandleAsync(IpcConnection connection, string operation, JsonElement? payload, CancellationToken cancellationToken)
        {
            switch (operation)
            {
                case IpcOperations.RuntimeStatus:
                    return new { client = connection.ClientName };
                case IpcOperations.PluginsGet:
                    var request = payload!.Value.Deserialize<PluginIdRequest>(IpcProtocol.JsonOptions)!;
                    await Task.Delay(Random.Shared.Next(0, 20), cancellationToken);
                    return new PluginIdRequest(request.PluginId);
                case IpcOperations.PluginsStart:
                    BlockingEntered.TrySetResult();
                    try
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        BlockingCanceled.TrySetResult(true);
                        throw;
                    }

                    return null;
                case IpcOperations.PluginsStop:
                    throw new IpcRequestException(IpcErrorCodes.PluginNotFound, "No such plugin.");
                case IpcOperations.PluginsStartAll:
                    throw new InvalidOperationException("handler bug");
                default:
                    return new EmptyPayload();
            }
        }
    }

    private Task<IpcClient> ConnectAsync(string name = "test") => IpcClient.ConnectAsync(_pipeName, name, Timeout);

    private async Task<NamedPipeClientStream> ConnectRawAsync()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)Timeout.TotalMilliseconds);
        return pipe;
    }

    private static async Task WriteRawAsync(Stream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static string Hello(int version = IpcProtocol.Version) =>
        $$$"""{"type":"hello","payload":{"protocolVersion":{{{version}}},"clientName":"raw","clientId":"{{{Guid.NewGuid()}}}"}}""";

    [Fact]
    public async Task Handshake_ReturnsWelcomeAndRegistersTheClient()
    {
        await using var client = await ConnectAsync("handshake");

        Assert.Equal(IpcProtocol.Version, client.Welcome.ProtocolVersion);
        Assert.NotEqual(Guid.Empty, client.Welcome.ConnectionId);
        var status = await client.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None);
        Assert.Equal("handshake", status!.Value.GetProperty("client").GetString());
        Assert.Contains(_server.Connections, c => c.ClientName == "handshake");
    }

    [Fact]
    public async Task ProtocolVersionMismatch_IsRejectedAndTheConnectionCloses()
    {
        await using var pipe = await ConnectRawAsync();
        await WriteRawAsync(pipe, Hello(version: 99));

        var answer = await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None).AsTask().WaitAsync(Timeout);
        var end = await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None).AsTask().WaitAsync(Timeout);

        Assert.Equal(IpcMessageTypes.Error, answer!.Type);
        Assert.Equal(IpcErrorCodes.ProtocolVersionMismatch, answer.Error!.Code);
        Assert.Null(end);
    }

    [Fact]
    public async Task RequestBeforeHandshake_IsRejected()
    {
        await using var pipe = await ConnectRawAsync();
        await WriteRawAsync(pipe, """{"type":"request","id":1,"op":"runtime.status"}""");

        var answer = await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None).AsTask().WaitAsync(Timeout);

        Assert.Equal(IpcErrorCodes.HandshakeRequired, answer!.Error!.Code);
    }

    [Fact]
    public async Task ConcurrentRequests_AreCorrelatedById()
    {
        await using var client = await ConnectAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(async i =>
        {
            var response = await client.RequestAsync<PluginIdRequest>(IpcOperations.PluginsGet, new PluginIdRequest($"p{i}"));
            return (Expected: $"p{i}", Actual: response.PluginId);
        })).WaitAsync(Timeout);

        Assert.All(results, r => Assert.Equal(r.Expected, r.Actual));
    }

    [Fact]
    public async Task SimultaneousClients_AreServedIndependently()
    {
        var clients = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => ConnectAsync($"client{i}")));
        try
        {
            var answers = await Task.WhenAll(clients.Select(async c =>
                (await c.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None))!.Value.GetProperty("client").GetString())).WaitAsync(Timeout);

            Assert.Equal(Enumerable.Range(0, 5).Select(i => $"client{i}"), answers);
            Assert.Equal(5, _server.Connections.Count);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ErrorsAreStructured_AndTheConnectionStaysUsable()
    {
        await using var client = await ConnectAsync();

        var unknown = await Assert.ThrowsAsync<IpcClientException>(() => client.SendRequestAsync("nope.operation", null, CancellationToken.None));
        var notFound = await Assert.ThrowsAsync<IpcClientException>(() => client.SendRequestAsync(IpcOperations.PluginsStop, null, CancellationToken.None));
        var internalError = await Assert.ThrowsAsync<IpcClientException>(() => client.SendRequestAsync(IpcOperations.PluginsStartAll, null, CancellationToken.None));
        var invalid = await Assert.ThrowsAsync<IpcClientException>(() =>
            client.SendRequestAsync(IpcOperations.PluginsGet, JsonDocument.Parse("""{"pluginId":5}""").RootElement, CancellationToken.None));

        Assert.Equal(IpcErrorCodes.UnknownOperation, unknown.Code);
        Assert.Equal(IpcErrorCodes.PluginNotFound, notFound.Code);
        Assert.Equal(IpcErrorCodes.InternalError, internalError.Code);
        Assert.Equal("System.InvalidOperationException", internalError.Error.Diagnostic!.ExceptionType);
        Assert.Equal(IpcErrorCodes.InvalidRequest, invalid.Code);
        Assert.True(client.IsConnected);
        Assert.NotNull(await client.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None));
    }

    [Fact]
    public async Task ClientCancellation_CancelsTheServerOperation()
    {
        await using var client = await ConnectAsync();
        using var cancellation = new CancellationTokenSource();

        var request = client.SendRequestAsync(IpcOperations.PluginsStart, null, cancellation.Token);
        await _handler.BlockingEntered.Task.WaitAsync(Timeout);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(await _handler.BlockingCanceled.Task.WaitAsync(Timeout));
        Assert.NotNull(await client.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None));
    }

    [Fact]
    public async Task DisconnectDuringOperation_CancelsItAndTheServerKeepsServing()
    {
        var client = await ConnectAsync("leaving");
        var request = client.SendRequestAsync(IpcOperations.PluginsStart, null, CancellationToken.None);
        await _handler.BlockingEntered.Task.WaitAsync(Timeout);

        await client.DisposeAsync();

        await Assert.ThrowsAsync<IpcConnectionLostException>(() => request);
        Assert.True(await _handler.BlockingCanceled.Task.WaitAsync(Timeout));
        await WaitUntilAsync(() => _handler.Events.Contains("disconnected:leaving"));

        await using var next = await ConnectAsync("next");
        Assert.NotNull(await next.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedFrame_ClosesOnlyThatConnection()
    {
        await using var healthy = await ConnectAsync("healthy");
        await using var pipe = await ConnectRawAsync();
        await WriteRawAsync(pipe, Hello());
        Assert.Equal(IpcMessageTypes.Welcome, (await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None))!.Type);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 10 * 1024 * 1024);
        await pipe.WriteAsync(header);
        await pipe.FlushAsync();

        var answer = await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None).AsTask().WaitAsync(Timeout);
        Assert.Equal(IpcErrorCodes.MessageTooLarge, answer!.Error!.Code);
        Assert.Null(await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None).AsTask().WaitAsync(Timeout));
        Assert.NotNull(await healthy.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJson_ClosesTheConnectionWithAnError()
    {
        await using var pipe = await ConnectRawAsync();
        await WriteRawAsync(pipe, Hello());
        await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None);
        await WriteRawAsync(pipe, "{ this is not json");

        var answer = await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None).AsTask().WaitAsync(Timeout);

        Assert.Equal(IpcErrorCodes.InvalidMessage, answer!.Error!.Code);
    }

    [Fact]
    public async Task PartialFrameThenDisconnect_DoesNotAffectTheServer()
    {
        await using (var pipe = await ConnectRawAsync())
        {
            await WriteRawAsync(pipe, Hello());
            await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None);
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 100);
            await pipe.WriteAsync(header);
            await pipe.WriteAsync(new byte[10]);
            await pipe.FlushAsync();
        }

        await using var client = await ConnectAsync();
        Assert.NotNull(await client.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None));
    }

    [Fact]
    public async Task SlowClient_DropsNotificationsWithoutBlockingTheProducer()
    {
        await using var pipe = await ConnectRawAsync();
        await WriteRawAsync(pipe, Hello());
        await MessageFraming.ReadAsync(pipe, 64 * 1024, CancellationToken.None);
        await WaitUntilAsync(() => _handler.Connections.Values.Any(c => c.ClientName == "raw"));
        var connection = _handler.Connections.Values.Single(c => c.ClientName == "raw");

        // The client never reads: the pipe buffer and then the bounded queue fill up.
        var payload = new { text = new string('x', 4000) };
        var stopwatch = Stopwatch.StartNew();
        var accepted = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (connection.TrySendNotification(IpcNotifications.LogEntries, payload))
            {
                accepted++;
            }
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Producing notifications took {stopwatch.Elapsed}.");
        Assert.True(connection.DroppedNotifications > 0);
        Assert.True(accepted < 2000);
    }

    [Fact]
    public async Task Notifications_AreDeliveredToClients()
    {
        await using var client = await ConnectAsync();
        var received = new TaskCompletionSource<StateChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, n) =>
        {
            if (n.Name == IpcNotifications.StateChanged)
            {
                received.TrySetResult(n.Read<StateChangedNotification>()!);
            }
        };

        _server.Broadcast(IpcNotifications.StateChanged, new StateChangedNotification(42));

        Assert.Equal(42, (await received.Task.WaitAsync(Timeout)).StateVersion);
    }

    [Fact]
    public async Task Reconnection_AfterDisconnect_Works()
    {
        var first = await ConnectAsync("first");
        await first.DisposeAsync();
        await WaitUntilAsync(() => _handler.Events.Contains("disconnected:first"));

        await using var second = await ConnectAsync("second");

        Assert.NotNull(await second.SendRequestAsync(IpcOperations.RuntimeStatus, null, CancellationToken.None));
        Assert.DoesNotContain(_server.Connections, c => c.ClientName == "first");
    }

    [Fact]
    public async Task TooManyConcurrentRequests_AreRejected()
    {
        await using var client = await ConnectAsync();
        var requests = Enumerable.Range(0, IpcProtocol.MaxConcurrentRequestsPerConnection + 8)
            .Select(_ => client.SendRequestAsync(IpcOperations.PluginsStart, null, CancellationToken.None))
            .ToList();

        await WaitUntilAsync(() => requests.Count(r => r.IsFaulted) >= 8);

        Assert.All(
            requests.Where(r => r.IsFaulted),
            r => Assert.Equal(IpcErrorCodes.TooManyRequests, ((IpcClientException)r.Exception!.InnerException!).Code));
    }

    [Fact]
    public async Task ServerStop_FailsPendingRequestsWithConnectionLost()
    {
        await using var client = await ConnectAsync();
        var request = client.SendRequestAsync(IpcOperations.PluginsStart, null, CancellationToken.None);
        await _handler.BlockingEntered.Task.WaitAsync(Timeout);

        await _server.StopAsync().WaitAsync(Timeout);

        await Assert.ThrowsAnyAsync<IpcClientException>(() => request.WaitAsync(Timeout));
        await client.Completion.WaitAsync(Timeout);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void SecondServerOnTheSamePipe_IsRefused()
    {
        var second = new IpcServer(new IpcServerOptions(_pipeName), _handler);

        var error = Record.Exception(second.Start);
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected: {error}");
    }

    [Fact]
    public void PipeAcl_GrantsOnlyTheCurrentUserAndDeniesNetworkLogons()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var pipe = PipeSecurityPolicy.CreateServerStream($"ModularRuntime.acl.{Guid.NewGuid():N}", firstInstance: true, maxInstances: 1);
        var security = pipe.GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier))); // never Administrators, even when elevated
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(identity.User));
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny &&
                                    r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
        var others = rules.Where(r => r.AccessControlType == AccessControlType.Allow && !r.IdentityReference.Equals(identity.User)).ToList();
        Assert.Empty(others);
        Assert.True(security.AreAccessRulesProtected, "Inherited (broader) entries must not apply.");
    }

    [Fact]
    public async Task ConnectingToAMissingPipe_TimesOut()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            IpcClient.ConnectAsync($"ModularRuntime.missing.{Guid.NewGuid():N}", "test", TimeSpan.FromMilliseconds(300)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(10);
        }
    }
}
