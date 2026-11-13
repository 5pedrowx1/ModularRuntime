using ModularRuntime.Desktop.Services;
using ModularRuntime.Desktop.ViewModels;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), "ModularRuntime.Tests", "desktop-" + Guid.NewGuid().ToString("N"));
    private readonly FakeConnection _connection = new();
    private readonly InlineDispatcher _dispatcher = new();
    private readonly FakeDialogs _dialogs = new();

    private MainViewModel CreateMain() =>
        new(_connection, _dispatcher, _dialogs, new SettingsService(_settingsDirectory), new FakeTheme());

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    [Fact]
    public void Navigation_ActivatesOnlyTheSelectedPage()
    {
        var main = CreateMain();

        main.SelectedPage = main.Plugins;

        Assert.Equal(9, main.Pages.Count);
        Assert.True(main.Plugins.IsActive);
        Assert.Single(main.Pages, p => p.IsActive);

        main.SelectedPage = main.Store;
        Assert.True(main.Store.IsActive);
        Assert.False(main.Plugins.IsActive);
        Assert.Single(main.Pages, p => p.IsActive);
    }

    [Fact]
    public void Sidebar_ListsStoreBeforePlugins_AsIndependentPages()
    {
        var main = CreateMain();

        Assert.Equal(["Dashboard", "Store", "Plugins", "Events", "Services", "Extensions", "Streams", "Logs", "Settings"], main.Pages.Select(p => p.Title));
        Assert.Same(main.Store.Operations, main.Plugins.Operations); // one operation tracker, no duplicated package manager
    }

    [Fact]
    public void ConnectionIndicator_AndStartDaemonCommand_FollowTheConnection()
    {
        var main = CreateMain();
        Assert.Equal(ConnectionState.Disconnected, main.ConnectionState);
        Assert.True(main.StartDaemonCommand.CanExecute(null));

        _connection.Connect(new FakeClient());

        Assert.Equal(ConnectionState.Connected, main.ConnectionState);
        Assert.True(main.IsConnected);
        Assert.False(main.StartDaemonCommand.CanExecute(null));

        _connection.Disconnect(ConnectionState.Error);
        Assert.Equal(ConnectionState.Error, main.ConnectionState);
        Assert.True(main.StartDaemonCommand.CanExecute(null));

        main.RetryCommand.Execute(null);
        main.StartDaemonCommand.Execute(null);
        Assert.Equal(1, _connection.RetryCount);
        Assert.Equal(1, _connection.DaemonStartRequests);
    }

    [Fact]
    public async Task CloseAsync_DisconnectsOnly_AndNeverStopsTheDaemon()
    {
        var main = CreateMain();
        var client = new FakeClient { RunningPluginCount = 2 };
        _connection.Connect(client);

        await main.CloseAsync();

        Assert.True(_connection.Disposed);
        Assert.Equal(0, _connection.StopDaemonCalls);
        Assert.Equal(0, client.ShutdownCalls);
    }

    [Fact]
    public async Task StopDaemon_RequiresConfirmation_AndMentionsRunningPlugins()
    {
        var main = CreateMain();
        var client = new FakeClient { RunningPluginCount = 3 };
        _connection.Connect(client);

        _dialogs.ConfirmResult = false;
        await main.Settings.StopDaemonCommand.ExecuteAsync();
        Assert.Equal(0, client.ShutdownCalls);
        Assert.Contains("3 running plugin(s)", _dialogs.Confirmations.Single(), StringComparison.Ordinal);

        _dialogs.ConfirmResult = true;
        await main.Settings.StopDaemonCommand.ExecuteAsync();
        Assert.Equal(1, client.ShutdownCalls);
    }

    [Fact]
    public void StopDaemon_IsUnavailableWhileDisconnected()
    {
        var main = CreateMain();

        Assert.False(main.Settings.StopDaemonCommand.CanExecute(null));
    }
}

public sealed class PluginsViewModelTests : IAsyncDisposable
{
    private readonly FakeConnection _connection = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly PluginsViewModel _plugins;
    private readonly FakeClient _client = new();

    public PluginsViewModelTests()
    {
        _plugins = new PluginsViewModel(_connection, new InlineDispatcher(), _dialogs, new PackageOperationsTracker(_connection, new InlineDispatcher())) { IsActive = true };
        _client.Plugins = [FakeClient.Plugin("com.a", "Validated"), FakeClient.Plugin("com.b", "Running"), FakeClient.Plugin("com.c", "Failed")];
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        _connection.Connect(_client);
        await _plugins.RefreshCommand.Execution;
    }

    [Fact]
    public async Task Connecting_LoadsTheListFromTheDaemon()
    {
        Assert.Equal(PageState.Disconnected, _plugins.State);

        await ConnectAsync();

        Assert.Equal(PageState.Ready, _plugins.State);
        Assert.Equal(["com.a", "com.b", "com.c"], _plugins.Items.Select(i => i.Id));
        Assert.Equal(1, _plugins.RunningCount);
        Assert.Equal(1, _plugins.FailedCount);
    }

    [Fact]
    public async Task EmptyList_ShowsTheEmptyState()
    {
        _client.Plugins = [];

        await ConnectAsync();

        Assert.Equal(PageState.Empty, _plugins.State);
        Assert.False(_plugins.StartAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_DoesNotChangeStateOptimistically_AndPreventsDuplicateClicks()
    {
        await ConnectAsync();
        var item = _plugins.Items.Single(i => i.Id == "com.a");
        var pending = new TaskCompletionSource<OperationResultDto>();
        _client.OnStart = _ => pending.Task;

        item.StartCommand.Execute(null);

        Assert.True(item.IsBusy);
        Assert.Equal("Validated", item.State);
        Assert.False(item.StartCommand.CanExecute(null));
        item.StartCommand.Execute(null);
        Assert.Single(_client.Calls, c => c == "start com.a");

        _client.Plugins[0] = FakeClient.Plugin("com.a", "Running");
        pending.SetResult(FakeClient.Succeeded("com.a", "Start", "Running"));
        await item.StartCommand.Execution;

        Assert.False(item.IsBusy);
        Assert.Equal("Running", item.State);
        Assert.Same(item, _plugins.Items.Single(i => i.Id == "com.a"));
        Assert.False(_plugins.OperationFailed);
    }

    [Fact]
    public async Task FailedOperation_IsReported_AndTheStateComesFromTheDaemon()
    {
        await ConnectAsync();
        var item = _plugins.Items.Single(i => i.Id == "com.a");
        _client.OnStart = id => Task.FromResult(new OperationResultDto(id, "Start", "Failed", "Failed", "start_failed", "boom", null));
        _client.Plugins[0] = FakeClient.Plugin("com.a", "Failed");

        await item.StartCommand.ExecuteAsync();

        Assert.True(_plugins.OperationFailed);
        Assert.Contains("[start_failed] boom", _plugins.OperationMessage, StringComparison.Ordinal);
        Assert.Equal("Failed", item.State);
    }

    [Fact]
    public async Task IpcError_DuringOperation_IsReportedWithoutCrashing()
    {
        await ConnectAsync();
        var item = _plugins.Items.Single(i => i.Id == "com.b");
        _client.OnStop = _ => Task.FromException<OperationResultDto>(new IpcClientException(new IpcError(IpcErrorCodes.DaemonStopping, "The daemon is shutting down.")));

        await item.StopCommand.ExecuteAsync();

        Assert.True(_plugins.OperationFailed);
        Assert.Contains("daemon_stopping", _plugins.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadError_ShowsTheErrorState()
    {
        _client.OnList = () => Task.FromException<PluginListResponse>(new IpcClientException(new IpcError(IpcErrorCodes.InternalError, "kaput")));

        await ConnectAsync();

        Assert.Equal(PageState.Error, _plugins.State);
        Assert.Contains("kaput", _plugins.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disconnection_ClearsDataAndMessages_SoNothingStaleIsShown()
    {
        await ConnectAsync();
        await _plugins.Items[0].StartCommand.ExecuteAsync();
        Assert.NotNull(_plugins.OperationMessage);

        _connection.Disconnect();

        Assert.Equal(PageState.Disconnected, _plugins.State);
        Assert.Empty(_plugins.Items);
        Assert.Null(_plugins.OperationMessage);
        Assert.False(_plugins.StartAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task StoppedPlugin_CanBeStartedAgain_StartStopStart()
    {
        await ConnectAsync();
        var item = _plugins.Items.Single(i => i.Id == "com.b");

        for (var cycle = 0; cycle < 3; cycle++)
        {
            _client.Plugins[1] = FakeClient.Plugin("com.b", "Stopped");
            await item.StopCommand.ExecuteAsync();
            Assert.Equal("Stopped", item.State);
            Assert.True(item.CanBeStarted);
            Assert.True(item.StartCommand.CanExecute(null));
            Assert.False(item.StopCommand.CanExecute(null));

            _client.Plugins[1] = FakeClient.Plugin("com.b", "Running");
            await item.StartCommand.ExecuteAsync();
            Assert.Equal("Running", item.State);
            Assert.True(item.StopCommand.CanExecute(null));
        }

        Assert.Equal(3, _client.Calls.Count(c => c == "start com.b"));
        Assert.False(_plugins.OperationFailed);
    }

    [Fact]
    public async Task Restart_IsAvailableForRunningPlugins_AndCallsTheDaemon()
    {
        await ConnectAsync();
        var running = _plugins.Items.Single(i => i.Id == "com.b");
        _plugins.Selected = running;

        Assert.True(running.RestartCommand.CanExecute(null));
        Assert.True(_plugins.RestartSelectedCommand.CanExecute(null));
        Assert.False(_plugins.Items.Single(i => i.Id == "com.a").RestartCommand.CanExecute(null));

        await _plugins.RestartSelectedCommand.ExecuteAsync();

        Assert.Contains("restart com.b", _client.Calls);
        Assert.False(_plugins.OperationFailed);
    }

    [Fact]
    public async Task FailedRestart_ShowsTheDaemonsDiagnostic()
    {
        await ConnectAsync();
        _client.OnRestart = id => Task.FromResult(new OperationResultDto(id, "restart", "Failed", "Stopped", "factory_failed", "Plugin was not loaded: integrity check failed.", null));

        await _plugins.Items.Single(i => i.Id == "com.b").RestartCommand.ExecuteAsync();

        Assert.True(_plugins.OperationFailed);
        Assert.Contains("factory_failed", _plugins.OperationMessage, StringComparison.Ordinal);
        Assert.Contains("integrity check failed", _plugins.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedPlugin_CanBeRetried()
    {
        await ConnectAsync();

        Assert.True(_plugins.Items.Single(i => i.Id == "com.c").StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reconnection_ShowsTheDaemonsCurrentState()
    {
        await ConnectAsync();
        _connection.Disconnect();
        Assert.Empty(_plugins.Items);

        // While the GUI was away, the daemon kept running and the plugin was stopped and started again.
        _client.Plugins[0] = FakeClient.Plugin("com.a", "Running");
        _client.Plugins[1] = FakeClient.Plugin("com.b", "Stopped");
        await ConnectAsync();

        Assert.Equal("Running", _plugins.Items.Single(i => i.Id == "com.a").State);
        Assert.Equal("Stopped", _plugins.Items.Single(i => i.Id == "com.b").State);
        Assert.True(_plugins.Items.Single(i => i.Id == "com.b").StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task StopAll_WithRunningPlugins_AsksForConfirmation()
    {
        await ConnectAsync();

        _dialogs.ConfirmResult = false;
        await _plugins.StopAllCommand.ExecuteAsync();
        Assert.DoesNotContain("stopAll", _client.Calls);

        _dialogs.ConfirmResult = true;
        await _plugins.StopAllCommand.ExecuteAsync();
        Assert.Contains("stopAll", _client.Calls);
        Assert.Equal("Stop all: completed.", _plugins.OperationMessage);
    }

    [Fact]
    public async Task RuntimeStateChange_RefreshesTheList()
    {
        await ConnectAsync();
        _client.Plugins[1] = FakeClient.Plugin("com.b", "Stopped");

        _connection.RaiseRuntimeChanged(2);
        await _plugins.RefreshCommand.Execution;

        Assert.Equal("Stopped", _plugins.Items.Single(i => i.Id == "com.b").State);
    }

    [Fact]
    public async Task Filters_BySearchTextAndState()
    {
        await ConnectAsync();

        _plugins.StateFilter = PluginStateFilter.Running;
        Assert.Equal(["com.b"], _plugins.Items.Select(i => i.Id));

        _plugins.StateFilter = PluginStateFilter.All;
        _plugins.SearchText = "C";
        Assert.Equal(["com.a", "com.b", "com.c"], _plugins.Items.Select(i => i.Id));
        _plugins.SearchText = "com.c";
        Assert.Equal(["com.c"], _plugins.Items.Select(i => i.Id));
    }
}

public sealed class LogsViewModelTests : IAsyncDisposable
{
    private readonly FakeConnection _connection = new();
    private readonly InlineDispatcher _dispatcher = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly LogsViewModel _logs;

    public LogsViewModelTests()
    {
        _logs = new LogsViewModel(_connection, _dispatcher, _dialogs, autoScroll: true);
    }

    public async ValueTask DisposeAsync()
    {
        _logs.Detach();
        await _connection.DisposeAsync();
    }

    private static LogEntryDto Entry(long sequence, string message = "m", LogLevelDto level = LogLevelDto.Information, string? plugin = null) =>
        new(sequence, DateTimeOffset.UtcNow, level, "Test", message, plugin, null, null);

    private async Task ConnectAsync(FakeClient client)
    {
        _connection.Connect(client);
        await _logs.RefreshCommand.Execution;
    }

    [Fact]
    public async Task SubscribesBeforeQuerying_AndStreamsInTheBackgroundWhenNotVisible()
    {
        var client = new FakeClient { OnQueryLogs = _ => new LogQueryResponse([Entry(1), Entry(2)], 2, false) };

        await ConnectAsync(client);

        Assert.False(_logs.IsActive);
        Assert.Equal(["subscribeLogs", "queryLogs after="], client.Calls);
        Assert.Equal([1L, 2L], _logs.Entries.Select(e => e.Sequence));
    }

    [Fact]
    public async Task StreamedEntries_AreBatchedOnTheTimer_AndDeduplicatedBySequence()
    {
        var client = new FakeClient { OnQueryLogs = _ => new LogQueryResponse([Entry(1), Entry(2)], 2, false) };
        await ConnectAsync(client);

        _connection.RaiseLogs(new LogBatchNotification(client.LogSubscriptionId, [Entry(2), Entry(3), Entry(4)], 0));
        Assert.Equal(2, _logs.Entries.Count); // nothing touches the collection until the UI timer ticks
        _dispatcher.Tick();

        Assert.Equal([1L, 2L, 3L, 4L], _logs.Entries.Select(e => e.Sequence));
    }

    [Fact]
    public async Task BatchesOfAnotherSubscription_AreIgnored()
    {
        var client = new FakeClient();
        await ConnectAsync(client);

        _connection.RaiseLogs(new LogBatchNotification(Guid.NewGuid(), [Entry(1)], 0));
        _dispatcher.Tick();

        Assert.Empty(_logs.Entries);
    }

    [Fact]
    public async Task DroppedEntries_AreMarked()
    {
        var client = new FakeClient();
        await ConnectAsync(client);

        _connection.RaiseLogs(new LogBatchNotification(client.LogSubscriptionId, [Entry(10)], 7));
        _dispatcher.Tick();

        Assert.Contains(_logs.Entries, e => e.IsMarker && e.Message.StartsWith("7 log entries were dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pause_HoldsNewEntries_AndResumeShowsThem()
    {
        var client = new FakeClient();
        await ConnectAsync(client);

        _logs.IsPaused = true;
        _connection.RaiseLogs(new LogBatchNotification(client.LogSubscriptionId, [Entry(1), Entry(2)], 0));
        _dispatcher.Tick();
        Assert.Empty(_logs.Entries);
        Assert.Equal(2, _logs.PendingCount);

        _logs.IsPaused = false;

        Assert.Equal(2, _logs.Entries.Count);
        Assert.Equal(0, _logs.PendingCount);
    }

    [Fact]
    public async Task Filters_AndClear_AffectOnlyTheLocalView()
    {
        var client = new FakeClient
        {
            OnQueryLogs = _ => new LogQueryResponse([Entry(1, "debug noise", LogLevelDto.Debug), Entry(2, "hello", plugin: "com.a"), Entry(3, "world", LogLevelDto.Error)], 3, false),
        };
        await ConnectAsync(client);

        Assert.Equal(2, _logs.Entries.Count); // Information and above by default
        _logs.PluginFilter = "com.a";
        Assert.Equal(["hello"], _logs.Entries.Select(e => e.Message));
        _logs.PluginFilter = LogsViewModel.AllPlugins;
        _logs.SearchText = "WORLD";
        Assert.Equal(["world"], _logs.Entries.Select(e => e.Message));

        _logs.ClearCommand.Execute(null);

        Assert.Empty(_logs.Entries);
        Assert.Equal(0, client.ShutdownCalls);
        Assert.DoesNotContain(client.Calls, c => c.Contains("clear", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reconnection_QueriesOnlyMissedEntries_AndMarksTheGap()
    {
        var instance = Guid.NewGuid();
        await ConnectAsync(new FakeClient(instance) { OnQueryLogs = _ => new LogQueryResponse([Entry(1), Entry(2)], 2, false) });

        _connection.Disconnect();
        Assert.Contains(_logs.Entries, e => e.IsMarker && e.Message.StartsWith("Disconnected", StringComparison.Ordinal));
        Assert.Equal(PageState.Disconnected, _logs.State);

        var again = new FakeClient(instance) { OnQueryLogs = r => new LogQueryResponse([Entry(5)], 5, Truncated: r.AfterSequence == 2) };
        await ConnectAsync(again);

        Assert.Contains("queryLogs after=2", again.Calls);
        Assert.Contains(_logs.Entries, e => e.IsMarker && e.Message.StartsWith("Some log entries were missed", StringComparison.Ordinal));
        Assert.Equal(5, _logs.Entries.Last().Sequence);
    }

    [Fact]
    public async Task DaemonRestart_IsMarked_AndSequencesRestart()
    {
        await ConnectAsync(new FakeClient(Guid.NewGuid()) { OnQueryLogs = _ => new LogQueryResponse([Entry(41), Entry(42)], 42, false) });
        _connection.Disconnect();

        var restarted = new FakeClient(Guid.NewGuid()) { OnQueryLogs = _ => new LogQueryResponse([Entry(1, "fresh")], 1, false) };
        await ConnectAsync(restarted);

        Assert.Contains("queryLogs after=", restarted.Calls);
        Assert.Contains(_logs.Entries, e => e.IsMarker && e.Message.StartsWith("The daemon was restarted", StringComparison.Ordinal));
        Assert.Equal("fresh", _logs.Entries.Last().Message);
    }

    [Fact]
    public async Task LocalView_IsBounded()
    {
        var client = new FakeClient();
        await ConnectAsync(client);

        _connection.RaiseLogs(new LogBatchNotification(client.LogSubscriptionId, [.. Enumerable.Range(1, LogsViewModel.MaxEntries + 100).Select(i => Entry(i))], 0));
        for (var i = 0; i < 5; i++)
        {
            _dispatcher.Tick();
        }

        Assert.Equal(LogsViewModel.MaxEntries, _logs.TotalCount);
        Assert.Equal(101, _logs.Entries[0].Sequence);
    }

    [Fact]
    public async Task Copy_PutsTheFullEntryOnTheClipboard()
    {
        var client = new FakeClient { OnQueryLogs = _ => new LogQueryResponse([Entry(1, "copy me", plugin: "com.a")], 1, false) };
        await ConnectAsync(client);

        _logs.Selected = _logs.Entries[0];
        _logs.CopyCommand.Execute(null);

        Assert.Contains("INFO [Test] [com.a] copy me", _dialogs.Clipboard, StringComparison.Ordinal);
    }
}
