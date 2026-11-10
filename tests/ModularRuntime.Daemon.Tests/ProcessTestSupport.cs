using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModularRuntime.Ipc;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Daemon.Tests;

/// <summary>Locates the real executables built into artifacts\app\&lt;Configuration&gt;.</summary>
internal static class AppPaths
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    public static string AppDirectory => Path.Combine(RepositoryRoot, "artifacts", "app", Configuration);

    public static string RepositoryRoot => RepositoryLayout.Root;

    public static string ConfigurationName => Configuration;

    public static string Daemon => Path.Combine(AppDirectory, "ModularRuntime.Daemon.exe");

    public static string Cli => Path.Combine(AppDirectory, "ModularRuntime.Cli.exe");

    public static string Desktop => Path.Combine(AppDirectory, "ModularRuntime.Desktop.exe");
}

/// <summary>
/// An isolated profile (temporary directory, unique pipe and mutex) with staged plugins, plus
/// tracking and cleanup of every process a test starts. Tests never touch the user's real profile.
/// </summary>
internal sealed class TestProfile : IAsyncDisposable
{
    private readonly TemporaryDirectory _temporary;
    private readonly List<Process> _processes = [];

    private TestProfile(TemporaryDirectory temporary)
    {
        _temporary = temporary;
        Profile = RuntimeProfile.Resolve(Path.Combine(temporary.DirectoryPath, "profile"));
        Profile.EnsureDirectories();
    }

    public RuntimeProfile Profile { get; }

    public string ProfileDirectory => Profile.ProfileDirectory;

    public static TestProfile Create(params string[] stagedPlugins)
    {
        var profile = new TestProfile(new TemporaryDirectory("daemon"));
        foreach (var plugin in stagedPlugins)
        {
            profile.AddPlugin(plugin);
        }

        return profile;
    }

    /// <summary>Stages a sample, or a fixture entry type as "ModularRuntime.TestPlugin:EntryType:plugin.id".</summary>
    public void AddPlugin(string spec)
    {
        var parts = spec.Split(':');
        var directory = StagedPlugins.CopyPlugin(Profile.DefaultPluginDirectory, parts[0], parts.Length > 2 ? parts[2] : parts[0]);
        if (parts.Length > 2)
        {
            var manifest = StagedPlugins.ReadManifest(directory);
            manifest["id"] = parts[2];
            manifest["entryType"] = "ModularRuntime.TestPlugin." + parts[1];
            StagedPlugins.WriteManifest(directory, manifest);
        }
    }

    public Process StartDaemon(params string[] arguments) =>
        Track(Process.Start(StartInfo(AppPaths.Daemon, ["--profile", ProfileDirectory, .. arguments]))!);

    public Process StartDesktop() =>
        Track(Process.Start(StartInfo(AppPaths.Desktop, ["--profile", ProfileDirectory, "--no-autostart"]))!);

    public async Task<RuntimeClient> ConnectAsync(string name = "test") =>
        await RuntimeClient.ConnectAsync(Profile, name, TimeSpan.FromSeconds(10));

    /// <summary>Starts the daemon through the CLI (the same path users take) and waits until it serves requests.</summary>
    public async Task StartDaemonViaCliAsync(params string[] extra)
    {
        var (exitCode, output) = await RunCliAsync(["daemon", "start", .. extra]);
        Assert.True(exitCode == 0, $"daemon start failed ({exitCode}): {output}");
        await TrackDaemonFromRuntimeInfoAsync();
    }

    public async Task<(int ExitCode, string Output)> RunCliAsync(params string[] arguments)
    {
        var startInfo = StartInfo(AppPaths.Cli, ["--profile", ProfileDirectory, .. arguments]);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        using var process = Process.Start(startInfo)!;

        // Non-interactive, like a script: confirmations are never answered.
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {description}");
            await Task.Delay(100);
        }
    }

    public Task<bool> IsDaemonRunningAsync() => DaemonLauncher.IsRunningAsync(Profile, TimeSpan.FromMilliseconds(500));

    /// <summary>Reads the daemon log files of this profile.</summary>
    public string ReadLogs()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.GetFiles(Profile.LogDirectory, "*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    public Process Track(Process process)
    {
        lock (_processes)
        {
            _processes.Add(process);
        }

        return process;
    }

    /// <summary>Also tracks a daemon started indirectly (CLI, launcher) through its runtime info file.</summary>
    public async Task TrackDaemonFromRuntimeInfoAsync()
    {
        var path = Path.Combine(Profile.RuntimeDirectory, "daemon.json");
        await WaitUntilAsync(() => Task.FromResult(File.Exists(path)), "daemon runtime info");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        try
        {
            Track(Process.GetProcessById(document.RootElement.GetProperty("processId").GetInt32()));
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup only: graceful paths are what the tests themselves verify.
        List<Process> processes;
        lock (_processes)
        {
            processes = [.. _processes];
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        _temporary.Dispose();
    }

    private static ProcessStartInfo StartInfo(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessTestGroup
{
    public const string Name = "Real processes";
}
