using System.Text;
using System.Text.Json;

namespace ModularRuntime.Template.Tests;

/// <summary>A real daemon on a temporary profile, driven through the administrative CLI.</summary>
internal sealed class DaemonSession(ExternalWorkspace workspace, string profile) : IAsyncDisposable
{
    public string Profile { get; } = profile;

    private static string Cli => Path.Combine(ExternalWorkspace.AppDirectory, "ModularRuntime.Cli.exe");

    public static async Task<DaemonSession> StartAsync(ExternalWorkspace workspace, string name)
    {
        var session = new DaemonSession(workspace, Path.Combine(workspace.Root, "profiles", name));
        (await session.CliAsync("daemon", "start")).AssertSuccess("daemon start");
        return session;
    }

    public Task<ProcessRun> CliAsync(params string[] arguments) =>
        workspace.RunAsync(Cli, workspace.Root, ["--profile", Profile, .. arguments]);

    public async Task<string> CliOkAsync(params string[] arguments)
    {
        var run = await CliAsync(arguments);
        run.AssertSuccess("modularruntime " + string.Join(' ', arguments));
        return run.Output;
    }

    public string ReadLogs()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.GetFiles(Path.Combine(Profile, "logs"), "*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    public async Task WaitForLogAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!ReadLogs().Contains(text, StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for '{text}' in the daemon log:{Environment.NewLine}{ReadLogs()}");
            await Task.Delay(250);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var stop = await CliAsync("daemon", "stop");
        if (stop.ExitCode != 0)
        {
            // Cleanup only (the tests assert the graceful paths).
            var info = Path.Combine(Profile, "runtime", "daemon.json");
            if (File.Exists(info))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(info));
                try
                {
                    System.Diagnostics.Process.GetProcessById(document.RootElement.GetProperty("processId").GetInt32()).Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                }
            }
        }
    }
}
