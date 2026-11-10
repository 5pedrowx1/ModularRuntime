using System.Collections.Concurrent;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Tests;

public sealed class RuntimeConnectionTests
{
    private static readonly RuntimeProfile Profile = RuntimeProfile.Resolve(Path.Combine(Path.GetTempPath(), "ModularRuntime.Tests", "connection-unused"));

    /// <summary>Connect attempts succeed only while <see cref="DaemonUp"/> is set.</summary>
    private sealed class FakeFactory : IRuntimeClientFactory
    {
        public volatile bool DaemonUp;

        public int Attempts;

        public ConcurrentQueue<FakeClient> Clients { get; } = new();

        public Task<IRuntimeClient> ConnectAsync(RuntimeProfile profile, string clientName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (!DaemonUp)
            {
                return Task.FromException<IRuntimeClient>(new TimeoutException("no daemon"));
            }

            var client = new FakeClient();
            Clients.Enqueue(client);
            return Task.FromResult<IRuntimeClient>(client);
        }
    }

    private sealed class FakeDaemon(FakeFactory factory, bool startsSuccessfully = true) : IDaemonController
    {
        public int Starts;

        public Task<DaemonStartOutcome> EnsureRunningAsync(RuntimeProfile profile, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            if (!startsSuccessfully)
            {
                return Task.FromException<DaemonStartOutcome>(new InvalidOperationException("The daemon exited during startup with code 1."));
            }

            factory.DaemonUp = true;
            return Task.FromResult(DaemonStartOutcome.Started);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {description}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task AutoStart_StartsTheDaemonOnce_ThenConnectsAndSubscribesToState()
    {
        var factory = new FakeFactory();
        var daemon = new FakeDaemon(factory);
        await using var connection = new RuntimeConnection(Profile, factory, daemon);

        connection.Start(autoStartDaemon: true);
        await WaitForAsync(() => connection.State == ConnectionState.Connected, "connected");

        Assert.Equal(1, daemon.Starts);
        Assert.Same(factory.Clients.Single(), connection.Client);
        Assert.Contains("subscribeState", factory.Clients.Single().Calls);
    }

    [Fact]
    public async Task WithoutAutoStart_TheDaemonIsNeverStartedImplicitly()
    {
        var factory = new FakeFactory();
        var daemon = new FakeDaemon(factory);
        await using var connection = new RuntimeConnection(Profile, factory, daemon);

        connection.Start(autoStartDaemon: false);
        await WaitForAsync(() => connection.State == ConnectionState.Disconnected && factory.Attempts >= 1, "first attempt");
        connection.RetryNow();
        await WaitForAsync(() => factory.Attempts >= 2 && connection.State == ConnectionState.Disconnected, "retry failed again");

        Assert.Equal(0, daemon.Starts);
        Assert.Equal("The daemon is not running.", connection.StatusMessage);
    }

    [Fact]
    public async Task FailedDaemonStart_ShowsTheError_AndAutoStartIsNotRepeated()
    {
        var factory = new FakeFactory();
        var daemon = new FakeDaemon(factory, startsSuccessfully: false);
        await using var connection = new RuntimeConnection(Profile, factory, daemon);

        connection.Start(autoStartDaemon: true);
        await WaitForAsync(() => connection.State == ConnectionState.Error, "error state");
        Assert.Contains("could not be started", connection.StatusMessage, StringComparison.Ordinal);

        connection.RetryNow();
        await WaitForAsync(() => factory.Attempts >= 2 && connection.State is ConnectionState.Disconnected or ConnectionState.Error, "retry failed again");
        Assert.Equal(1, daemon.Starts);

        connection.RequestDaemonStart();
        await WaitForAsync(() => daemon.Starts == 2, "explicit start");
    }

    [Fact]
    public async Task LostConnection_IsDetected_AndReconnects()
    {
        var factory = new FakeFactory { DaemonUp = true };
        await using var connection = new RuntimeConnection(Profile, factory, new FakeDaemon(factory));
        var states = new ConcurrentQueue<ConnectionState>();
        connection.StateChanged += (_, _) => states.Enqueue(connection.State);
        connection.Start(autoStartDaemon: false);
        await WaitForAsync(() => connection.State == ConnectionState.Connected, "connected");
        var first = factory.Clients.Single();

        first.Disconnect();

        await WaitForAsync(() => factory.Clients.Count == 2 && connection.State == ConnectionState.Connected, "reconnected");
        Assert.Contains(ConnectionState.Disconnected, states);
        Assert.True(first.Disposed);
        Assert.NotSame(first, connection.Client);
    }

    [Fact]
    public async Task ExplicitStop_WaitsForTheUser_InsteadOfReconnecting()
    {
        var factory = new FakeFactory { DaemonUp = true };
        await using var connection = new RuntimeConnection(Profile, factory, new FakeDaemon(factory));
        connection.Start(autoStartDaemon: false);
        await WaitForAsync(() => connection.State == ConnectionState.Connected, "connected");
        var client = factory.Clients.Single();

        var response = await connection.StopDaemonAsync();
        Assert.True(response.Accepted);
        Assert.Equal(1, client.ShutdownCalls);
        factory.DaemonUp = false;
        client.Disconnect(); // the daemon closes the pipe when it exits

        await WaitForAsync(() => connection.State == ConnectionState.Disconnected, "disconnected");
        Assert.Equal("The daemon was stopped.", connection.StatusMessage);
        var attempts = factory.Attempts;
        await Task.Delay(1500);
        Assert.Equal(attempts, factory.Attempts);

        factory.DaemonUp = true;
        connection.RetryNow();
        await WaitForAsync(() => connection.State == ConnectionState.Connected, "reconnected after retry");
    }

    [Fact]
    public async Task Notifications_AreRoutedToEvents()
    {
        var factory = new FakeFactory { DaemonUp = true };
        await using var connection = new RuntimeConnection(Profile, factory, new FakeDaemon(factory));
        var versions = new ConcurrentQueue<long>();
        var batches = new ConcurrentQueue<LogBatchNotification>();
        connection.RuntimeStateChanged += (_, v) => versions.Enqueue(v);
        connection.LogBatchReceived += (_, b) => batches.Enqueue(b);
        connection.Start(autoStartDaemon: false);
        await WaitForAsync(() => connection.State == ConnectionState.Connected, "connected");
        var client = factory.Clients.Single();

        client.Notify(IpcNotifications.StateChanged, new StateChangedNotification(9));
        client.Notify(IpcNotifications.LogEntries, new LogBatchNotification(Guid.NewGuid(), [], 3));
        client.Notify(IpcNotifications.DaemonStopping, new DaemonStoppingNotification("test"));

        Assert.Equal([9L], versions);
        Assert.Equal(3, batches.Single().DroppedCount);
        Assert.Equal(ConnectionState.Stopping, connection.State);
    }

    [Fact]
    public async Task Dispose_DisconnectsTheClient_AndNeverStopsTheDaemon()
    {
        var factory = new FakeFactory { DaemonUp = true };
        var connection = new RuntimeConnection(Profile, factory, new FakeDaemon(factory));
        connection.Start(autoStartDaemon: false);
        await WaitForAsync(() => connection.State == ConnectionState.Connected, "connected");
        var client = factory.Clients.Single();

        await connection.DisposeAsync();

        Assert.True(client.Disposed);
        Assert.Equal(0, client.ShutdownCalls);
        Assert.True(connection.Supervisor.IsCompleted);
        Assert.Null(connection.Client);
    }
}
