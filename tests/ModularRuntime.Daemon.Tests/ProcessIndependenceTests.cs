using System.Diagnostics;
using System.IO.Pipes;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Daemon.Tests;

/// <summary>
/// Real-process tests of the daemon/client separation. Every test uses its own temporary profile
/// (unique pipe and mutex) and starts the real executables from artifacts\app.
/// </summary>
/// <remarks>Tests B, C, and D start the WPF desktop app and need an interactive Windows session.</remarks>
[Collection(ProcessTestGroup.Name)]
public sealed class ProcessIndependenceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // A — a client disconnects; the daemon and its plugins keep running.
    [Fact]
    public async Task A_ClientDisconnect_LeavesDaemonAndPluginsRunning()
    {
        await using var profile = TestProfile.Create("HelloPlugin");
        await profile.StartDaemonViaCliAsync();

        await using (var client = await profile.ConnectAsync("first"))
        {
            Assert.True((await client.StartPluginAsync("com.example.hello")).IsSuccess);
        }

        await using var second = await profile.ConnectAsync("second");
        var status = await second.GetStatusAsync();
        Assert.Equal(DaemonState.Running, status.State);
        Assert.Equal(1, status.RunningPluginCount);
        Assert.DoesNotContain(status.Clients, c => c.ClientName == "first");
    }

    // B + C — the desktop app is closed, then reopened.
    [Fact]
    public async Task B_C_DesktopClosedThenReopened_DaemonAndPluginKeepRunning_AndStateIsRecovered()
    {
        await using var profile = TestProfile.Create("HelloPlugin");
        await profile.StartDaemonViaCliAsync();
        await using var observer = await profile.ConnectAsync("observer");
        var daemonPid = (await observer.GetStatusAsync()).ProcessId;

        var desktop = profile.StartDesktop();
        await TestProfile.WaitUntilAsync(async () => (await observer.GetStatusAsync()).Clients.Any(c => c.ClientName == "desktop"), "desktop connected");
        Assert.True((await observer.StartPluginAsync("com.example.hello")).IsSuccess);

        // Close the window the way the X button does (WM_CLOSE).
        await WaitForMainWindowAsync(desktop);
        Assert.True(desktop.CloseMainWindow());
        await desktop.WaitForExitAsync().WaitAsync(Timeout);

        var afterClose = await observer.GetStatusAsync();
        Assert.Equal(DaemonState.Running, afterClose.State);
        Assert.Equal(daemonPid, afterClose.ProcessId);
        Assert.Equal("Running", (await observer.GetPluginAsync("com.example.hello")).Summary.State);
        Assert.DoesNotContain(afterClose.Clients, c => c.ClientName == "desktop");

        // C — reopening connects to the same daemon; no second daemon is created.
        var reopened = profile.StartDesktop();
        await TestProfile.WaitUntilAsync(async () => (await observer.GetStatusAsync()).Clients.Any(c => c.ClientName == "desktop"), "desktop reconnected");
        Assert.Equal(daemonPid, (await observer.GetStatusAsync()).ProcessId);
        Assert.Single(DaemonProcessesFor(profile));
        await WaitForMainWindowAsync(reopened);
        reopened.CloseMainWindow();
        await reopened.WaitForExitAsync().WaitAsync(Timeout);
    }

    // D — the desktop app and the CLI query the same daemon at the same time.
    [Fact]
    public async Task D_DesktopAndCli_UseTheSameDaemonSimultaneously()
    {
        await using var profile = TestProfile.Create("HelloPlugin", "FailingPlugin");
        await profile.StartDaemonViaCliAsync();
        await using var observer = await profile.ConnectAsync("observer");
        var desktop = profile.StartDesktop();
        await TestProfile.WaitUntilAsync(async () => (await observer.GetStatusAsync()).Clients.Any(c => c.ClientName == "desktop"), "desktop connected");

        var (exitCode, output) = await profile.RunCliAsync("--json", "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"desktop\"", output, StringComparison.Ordinal);
        Assert.Contains("\"cli\"", output, StringComparison.Ordinal);
        var list = await profile.RunCliAsync("plugins", "list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("com.example.failing", list.Output, StringComparison.Ordinal);
        await WaitForMainWindowAsync(desktop);
        desktop.CloseMainWindow();
        await desktop.WaitForExitAsync().WaitAsync(Timeout);
    }

    // E — concurrent starts never create two daemons for one profile.
    [Fact]
    public async Task E_ConcurrentStarts_CreateExactlyOneDaemon()
    {
        await using var profile = TestProfile.Create("HelloPlugin");

        var processes = Enumerable.Range(0, 4).Select(_ => profile.StartDaemon()).ToList();
        var launches = Enumerable.Range(0, 3).Select(_ => DaemonLauncher.EnsureRunningAsync(profile.Profile, AppPaths.Daemon)).ToList();
        await Task.WhenAll(launches).WaitAsync(Timeout);
        await profile.TrackDaemonFromRuntimeInfoAsync();

        await TestProfile.WaitUntilAsync(() => Task.FromResult(processes.Count(p => p.HasExited) >= 3), "losing daemons exited");
        var exited = processes.Where(p => p.HasExited).ToList();
        Assert.All(exited, p => Assert.Equal(DaemonExitCodes.AlreadyRunning, p.ExitCode));
        Assert.Single(DaemonProcessesFor(profile));
        await using var client = await profile.ConnectAsync();
        Assert.Equal(DaemonState.Running, (await client.GetStatusAsync()).State);
    }

    // F — client failures (killed CLI, abandoned partial frame, crashed desktop) do not stop the engine.
    [Fact]
    public async Task F_ClientFailures_DoNotStopTheEngine()
    {
        await using var profile = TestProfile.Create("HelloPlugin");
        await profile.StartDaemonViaCliAsync();
        await using var observer = await profile.ConnectAsync("observer");
        Assert.True((await observer.StartPluginAsync("com.example.hello")).IsSuccess);

        // A CLI streaming logs is killed mid-stream.
        var startInfo = new ProcessStartInfo(AppPaths.Cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--profile", profile.ProfileDirectory, "logs", "--follow" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var follower = profile.Track(Process.Start(startInfo)!);
        await TestProfile.WaitUntilAsync(async () => (await observer.GetStatusAsync()).Clients.Any(c => c.ClientName == "cli"), "cli connected");
        follower.Kill();
        await follower.WaitForExitAsync().WaitAsync(Timeout);

        // A raw client sends half a frame and disappears.
        await using (var pipe = new NamedPipeClientStream(".", profile.Profile.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(5000);
            await pipe.WriteAsync(new byte[] { 200, 0, 0, 0, 1, 2, 3 });
        }

        // The desktop app crashes (killed).
        var desktop = profile.StartDesktop();
        await TestProfile.WaitUntilAsync(async () => (await observer.GetStatusAsync()).Clients.Any(c => c.ClientName == "desktop"), "desktop connected");
        desktop.Kill();
        await desktop.WaitForExitAsync().WaitAsync(Timeout);

        await TestProfile.WaitUntilAsync(async () => (await observer.GetStatusAsync()).Clients.All(c => c.ClientName == "observer"), "failed clients removed");
        var status = await observer.GetStatusAsync();
        Assert.Equal(DaemonState.Running, status.State);
        Assert.Equal(1, status.RunningPluginCount);
    }

    // G — the daemon dies; clients detect the loss and the desktop app survives it.
    [Fact]
    public async Task G_DaemonFailure_IsDetectedByClients()
    {
        await using var profile = TestProfile.Create("HelloPlugin");
        var daemon = profile.StartDaemon();
        await TestProfile.WaitUntilAsync(profile.IsDaemonRunningAsync, "daemon ready");
        var client = await profile.ConnectAsync("watcher");
        var desktop = profile.StartDesktop();
        await TestProfile.WaitUntilAsync(async () => (await client.GetStatusAsync()).Clients.Any(c => c.ClientName == "desktop"), "desktop connected");

        daemon.Kill();
        await daemon.WaitForExitAsync().WaitAsync(Timeout);

        await client.Completion.WaitAsync(Timeout);
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<IpcConnectionLostException>(() => client.GetStatusAsync());
        await client.DisposeAsync();

        await Task.Delay(1500);
        Assert.False(desktop.HasExited, "The desktop app must survive the loss of the daemon.");
        Assert.False(await profile.IsDaemonRunningAsync(), "The desktop app must not restart the daemon silently.");
        await WaitForMainWindowAsync(desktop);
        desktop.CloseMainWindow();
        await desktop.WaitForExitAsync().WaitAsync(Timeout);
    }

    // H — an authorized shutdown request stops plugins and ends the daemon gracefully.
    [Fact]
    public async Task H_ExplicitShutdown_IsGraceful()
    {
        await using var profile = TestProfile.Create("HelloPlugin", "ServiceProviderPlugin");
        var daemon = profile.StartDaemon();
        await TestProfile.WaitUntilAsync(profile.IsDaemonRunningAsync, "daemon ready");
        await using (var client = await profile.ConnectAsync())
        {
            await client.StartAllAsync();
        }

        var (exitCode, output) = await profile.RunCliAsync("daemon", "stop");

        Assert.Equal(0, exitCode);
        Assert.Contains("Daemon stopped.", output, StringComparison.Ordinal);
        await daemon.WaitForExitAsync().WaitAsync(Timeout);
        Assert.Equal(DaemonExitCodes.Success, daemon.ExitCode);
        var logs = profile.ReadLogs();
        Assert.Contains("HelloPlugin shut down.", logs, StringComparison.Ordinal);
        Assert.Contains("Service 'example.text.transform' removed.", logs, StringComparison.Ordinal);
        Assert.Contains("Plugin engine shutdown completed.", logs, StringComparison.Ordinal);
        Assert.Contains("Daemon stopped (exit code 0).", logs, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(profile.Profile.RuntimeDirectory, "daemon.json")));
    }

    // Plugin restart — Running → Stopped → Running repeatedly in the same daemon, through IPC and the
    // CLI, while the desktop app is connected; closing the app leaves the daemon and plugin running.
    [Fact]
    public async Task PluginStartStopStart_AndRestart_WorkRepeatedlyInTheSameDaemon()
    {
        await using var profile = TestProfile.Create("HelloPlugin");
        await profile.StartDaemonViaCliAsync();
        await using var observer = await profile.ConnectAsync("observer");
        var daemonPid = (await observer.GetStatusAsync()).ProcessId;
        var desktop = profile.StartDesktop();
        await TestProfile.WaitUntilAsync(async () => (await observer.GetStatusAsync()).Clients.Any(c => c.ClientName == "desktop"), "desktop connected");
        const string Id = "com.example.hello";
        var instances = new List<Guid?>();

        for (var cycle = 0; cycle < 4; cycle++)
        {
            var start = await observer.StartPluginAsync(Id);
            Assert.True(start.IsSuccess, $"start #{cycle + 1}: [{start.ErrorCode}] {start.Message}");
            var running = await observer.GetPluginAsync(Id);
            Assert.Equal("Running", running.Summary.State);
            instances.Add(running.Summary.InstanceId);

            Assert.True((await observer.StopPluginAsync(Id)).IsSuccess, $"stop #{cycle + 1}");
            Assert.Equal("Stopped", (await observer.GetPluginAsync(Id)).Summary.State);
        }

        // The CLI uses the same path: start, restart (a real stop + start), stop, start.
        Assert.Equal(0, (await profile.RunCliAsync("plugins", "start", Id)).ExitCode);
        var beforeRestart = (await observer.GetPluginAsync(Id)).Summary.InstanceId;
        var (restartCode, restartOutput) = await profile.RunCliAsync("plugins", "restart", Id);
        Assert.True(restartCode == 0, restartOutput);
        var afterRestart = (await observer.GetPluginAsync(Id)).Summary;
        Assert.Equal("Running", afterRestart.State);
        Assert.NotEqual(beforeRestart, afterRestart.InstanceId);
        Assert.Equal(0, (await profile.RunCliAsync("plugins", "stop", Id)).ExitCode);
        Assert.Equal(0, (await profile.RunCliAsync("plugins", "start", Id)).ExitCode);

        instances.Add(beforeRestart);
        instances.Add(afterRestart.InstanceId);
        Assert.Equal(instances.Count, instances.Distinct().Count()); // a new instance every time
        Assert.Equal(daemonPid, (await observer.GetStatusAsync()).ProcessId); // never restarted the daemon
        var logs = profile.ReadLogs();
        Assert.True(CountOf(logs, "HelloPlugin initialized") >= 7, logs);
        Assert.Contains("registered again for a new start (new instance and load context)", logs, StringComparison.Ordinal);

        // Closing the desktop app leaves the daemon and the restarted plugin running.
        await WaitForMainWindowAsync(desktop);
        Assert.True(desktop.CloseMainWindow());
        await desktop.WaitForExitAsync().WaitAsync(Timeout);
        var afterClose = await observer.GetStatusAsync();
        Assert.Equal(DaemonState.Running, afterClose.State);
        Assert.Equal(daemonPid, afterClose.ProcessId);
        Assert.Equal("Running", (await observer.GetPluginAsync(Id)).Summary.State);

        static int CountOf(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }

    // Restart — a new daemon serves the profile after the previous one stopped.
    [Fact]
    public async Task DaemonRestart_ClientsReconnectToTheNewInstance()
    {
        await using var profile = TestProfile.Create("HelloPlugin");
        await profile.StartDaemonViaCliAsync();
        var first = await profile.ConnectAsync();
        var firstInstance = first.Welcome.DaemonInstanceId;

        Assert.Equal(0, (await profile.RunCliAsync("daemon", "stop")).ExitCode);
        await first.Completion.WaitAsync(Timeout);
        await first.DisposeAsync();
        await profile.StartDaemonViaCliAsync();

        await using var second = await profile.ConnectAsync();
        Assert.NotEqual(firstInstance, second.Welcome.DaemonInstanceId);
        Assert.Equal(DaemonState.Running, (await second.GetStatusAsync()).State);
    }

    // Shutdown policy — a plugin that ignores cancellation cannot keep the daemon alive forever.
    [Fact]
    public async Task ShutdownTimeout_IsReportedAndTheDaemonExitsWithItsCode()
    {
        await using var profile = TestProfile.Create("ModularRuntime.TestPlugin:StubbornShutdownPlugin:com.test.stubborn");
        var daemon = profile.StartDaemon("--shutdown-timeout", "2", "--auto-start");
        await TestProfile.WaitUntilAsync(profile.IsDaemonRunningAsync, "daemon ready");

        var (exitCode, output) = await profile.RunCliAsync("daemon", "stop");

        Assert.Equal(1, exitCode);
        Assert.Contains("shutdown timeout", output, StringComparison.OrdinalIgnoreCase);
        await daemon.WaitForExitAsync().WaitAsync(Timeout);
        Assert.Equal(DaemonExitCodes.ShutdownTimedOut, daemon.ExitCode);
        Assert.Contains("did not complete within 2s", profile.ReadLogs(), StringComparison.Ordinal);
    }

    // Lifecycle operations are rejected while the daemon is stopping.
    [Fact]
    public async Task OperationsDuringShutdown_AreRejectedWithDaemonStopping()
    {
        await using var profile = TestProfile.Create("ModularRuntime.TestPlugin:StubbornShutdownPlugin:com.test.stubborn", "HelloPlugin");
        var daemon = profile.StartDaemon("--shutdown-timeout", "5", "--auto-start");
        await TestProfile.WaitUntilAsync(profile.IsDaemonRunningAsync, "daemon ready");
        await using var admin = await profile.ConnectAsync("admin");
        await using var other = await profile.ConnectAsync("other");
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.NotificationReceived += (_, n) =>
        {
            if (n.Name == IpcNotifications.DaemonStopping)
            {
                stopping.TrySetResult();
            }
        };

        Assert.True((await admin.ShutdownAsync("test")).Accepted);
        await stopping.Task.WaitAsync(Timeout);

        var rejected = await Assert.ThrowsAsync<IpcClientException>(() => other.StartPluginAsync("com.example.hello"));
        Assert.Equal(IpcErrorCodes.DaemonStopping, rejected.Code);
        Assert.Equal(DaemonState.Stopping, (await other.GetStatusAsync()).State);
        await daemon.WaitForExitAsync().WaitAsync(Timeout);
    }

    // Live logs and state notifications over a real daemon.
    [Fact]
    public async Task LogsAndStateNotifications_StreamFromTheDaemon()
    {
        await using var profile = TestProfile.Create("HelloPlugin");
        await profile.StartDaemonViaCliAsync();
        await using var watcher = await profile.ConnectAsync("watcher");
        await using var actor = await profile.ConnectAsync("actor");
        var helloLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateChanged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.NotificationReceived += (_, n) =>
        {
            if (n.Name == IpcNotifications.LogEntries && n.Read<LogBatchNotification>()!.Entries.Any(e => e.Message.Contains("HelloPlugin initialized", StringComparison.Ordinal)))
            {
                helloLogged.TrySetResult();
            }
            else if (n.Name == IpcNotifications.StateChanged)
            {
                stateChanged.TrySetResult(n.Read<StateChangedNotification>()!.StateVersion);
            }
        };
        var logSubscription = await watcher.SubscribeLogsAsync(new LogFilterDto(PluginId: "com.example.hello"));
        var stateSubscription = await watcher.SubscribeStateAsync();

        await actor.StartPluginAsync("com.example.hello");

        await helloLogged.Task.WaitAsync(Timeout);
        Assert.True(await stateChanged.Task.WaitAsync(Timeout) > stateSubscription.StateVersion);
        await watcher.UnsubscribeLogsAsync(logSubscription.SubscriptionId);
        var again = await Assert.ThrowsAsync<IpcClientException>(() => watcher.UnsubscribeLogsAsync(logSubscription.SubscriptionId));
        Assert.Equal(IpcErrorCodes.SubscriptionNotFound, again.Code);

        var history = await watcher.QueryLogsAsync(new LogQueryRequest(new LogFilterDto(MinimumLevel: LogLevelDto.Information, Category: "Plugin")));
        Assert.Contains(history.Entries, e => e.PluginId == "com.example.hello");
    }

    private static IEnumerable<Process> DaemonProcessesFor(TestProfile profile) =>
        Process.GetProcessesByName("ModularRuntime.Daemon").Where(p =>
        {
            try
            {
                return !p.HasExited && IsServing(p, profile);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });

    private static bool IsServing(Process process, TestProfile profile)
    {
        var info = Path.Combine(profile.Profile.RuntimeDirectory, "daemon.json");
        return File.Exists(info) && File.ReadAllText(info).Contains($"\"processId\":{process.Id},", StringComparison.Ordinal);
    }

    private static async Task WaitForMainWindowAsync(Process process)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (process.MainWindowHandle == IntPtr.Zero)
        {
            Assert.True(DateTime.UtcNow < deadline, "The desktop window did not appear.");
            await Task.Delay(100);
            process.Refresh();
        }
    }
}
