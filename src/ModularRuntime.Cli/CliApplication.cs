using System.Diagnostics;
using System.Text.Json;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Cli;

/// <summary>Exit codes of ModularRuntime.Cli.</summary>
internal static class CliExitCodes
{
    public const int Success = 0;

    /// <summary>The daemon processed the command but the operation failed (for example a plugin failed to start).</summary>
    public const int OperationFailed = 1;

    public const int Usage = 2;

    /// <summary>No daemon serves the profile, or it could not be started or reached.</summary>
    public const int DaemonUnavailable = 3;

    /// <summary>The daemon rejected the request or a protocol error occurred.</summary>
    public const int RequestFailed = 4;

    public const int Canceled = 130;
}

/// <summary>Parses the command line and runs one command against the daemon over IPC.</summary>
internal static class CliApplication
{
    public const string Usage = """
        ModularRuntime CLI — administers the ModularRuntime daemon for the current user.

        Usage: modularruntime [global options] <command>

        Commands:
          status                          Daemon state, version, uptime, and counts
          daemon start [--auto-start]     Start the daemon if it is not running
          daemon stop                     Stop the daemon gracefully (stops all plugins)
          plugins list                    Registered plugins and their lifecycle state
          plugins info <id>               Details, resources, and last error
          plugins start <id>
          plugins stop <id>
          plugins restart <id>            Stop and start again (new instance; integrity re-verified)
          plugins start-all
          plugins stop-all
          plugins packages                Installed packages with source and trust state
          plugins install <id> [--version <v>]
                                          Install an official plugin from the registry
          plugins install-zip <file.zip> [--allow-unverified] [--stop-if-running]
                                          Install a local package (unsigned packages need confirmation
                                          or, non-interactively, --allow-unverified)
          plugins verify <id>             Re-check the signature and every file's SHA-256
          plugins update <id> [--stop-if-running]
          plugins remove <id> [--stop-if-running] [--yes]
          plugins updates                 Installed packages with newer registry versions
          registry status | refresh | list
          registry info <id>
          services list
          extensions list
          streams list
          events subscriptions
          logs [--follow] [--level <level>] [--plugin <id>] [--category <name>] [--search <text>] [--limit <n>]

        Global options:
          --profile <dir>    Profile directory (default: MODULARRUNTIME_PROFILE or %LOCALAPPDATA%\ModularRuntime)
          --json             Write machine-readable JSON
          --timeout <sec>    Connection timeout (default 5)
          -h, --help         Show this help

        Exit codes: 0 success, 1 operation failed, 2 usage, 3 daemon unavailable, 4 request failed, 130 canceled.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var arguments = new List<string>(args);
        string? profileDirectory = null;
        var json = false;
        var timeout = TimeSpan.FromSeconds(5);

        try
        {
            if (arguments.Count == 0)
            {
                output.WriteLine(Usage);
                return CliExitCodes.Usage;
            }

            if (TakeFlag(arguments, "--help") | TakeFlag(arguments, "-h"))
            {
                output.WriteLine(Usage);
                return CliExitCodes.Success;
            }

            profileDirectory = TakeValue(arguments, "--profile");
            json = TakeFlag(arguments, "--json");
            if (TakeValue(arguments, "--timeout") is { } seconds)
            {
                timeout = int.TryParse(seconds, out var value) && value is > 0 and <= 300
                    ? TimeSpan.FromSeconds(value)
                    : throw new UsageException("--timeout requires a number of seconds between 1 and 300.");
            }
        }
        catch (UsageException exception)
        {
            error.WriteLine(exception.Message);
            return CliExitCodes.Usage;
        }

        RuntimeProfile profile;
        try
        {
            profile = RuntimeProfile.Resolve(profileDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error.WriteLine($"Invalid profile directory: {exception.Message}");
            return CliExitCodes.Usage;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var context = new CliContext(profile, timeout, json, output, error, cancellation.Token);
            return await DispatchAsync(context, arguments).ConfigureAwait(false);
        }
        catch (UsageException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine("Run 'modularruntime --help' for usage.");
            return CliExitCodes.Usage;
        }
        catch (TimeoutException)
        {
            error.WriteLine($"The daemon for profile '{profile.ProfileDirectory}' is not running (or did not answer). Start it with 'modularruntime daemon start'.");
            return CliExitCodes.DaemonUnavailable;
        }
        catch (IpcConnectionLostException exception)
        {
            error.WriteLine($"Lost the connection to the daemon: {exception.Message}");
            return CliExitCodes.DaemonUnavailable;
        }
        catch (IpcClientException exception)
        {
            error.WriteLine($"[{exception.Code}] {exception.Message}");
            if (exception.Error.Diagnostic is { } diagnostic)
            {
                error.WriteLine($"  {diagnostic.ExceptionType}: {diagnostic.Message}");
            }

            return CliExitCodes.RequestFailed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            error.WriteLine("Canceled.");
            return CliExitCodes.Canceled;
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(exception.Message);
            return CliExitCodes.DaemonUnavailable;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> DispatchAsync(CliContext context, List<string> arguments)
    {
        var command = string.Join(' ', arguments.Take(2)).ToLowerInvariant();
        switch (arguments[0].ToLowerInvariant())
        {
            case "status":
                Expect(arguments, 1);
                return await WithClientAsync(context, CliCommands.StatusAsync).ConfigureAwait(false);

            case "daemon" when arguments.Count >= 2 && arguments[1].Equals("start", StringComparison.OrdinalIgnoreCase):
            {
                var autoStart = TakeFlag(arguments, "--auto-start");
                Expect(arguments, 2);
                return await StartDaemonAsync(context, autoStart).ConfigureAwait(false);
            }

            case "daemon" when arguments.Count == 2 && arguments[1].Equals("stop", StringComparison.OrdinalIgnoreCase):
                return await StopDaemonAsync(context).ConfigureAwait(false);

            case "plugins" when arguments.Count >= 2:
                return await (arguments[1].ToLowerInvariant() switch
                {
                    "list" => Expect(arguments, 2, () => WithClientAsync(context, CliCommands.ListPluginsAsync)),
                    "info" => Expect(arguments, 3, () => WithClientAsync(context, (c, client) => CliCommands.PluginInfoAsync(c, client, arguments[2]))),
                    "start" => Expect(arguments, 3, () => WithClientAsync(context, (c, client) => CliCommands.StartPluginAsync(c, client, arguments[2]))),
                    "stop" => Expect(arguments, 3, () => WithClientAsync(context, (c, client) => CliCommands.StopPluginAsync(c, client, arguments[2]))),
                    "restart" => Expect(arguments, 3, () => WithClientAsync(context, (c, client) => CliCommands.RestartPluginAsync(c, client, arguments[2]))),
                    "start-all" => Expect(arguments, 2, () => WithClientAsync(context, CliCommands.StartAllAsync)),
                    "stop-all" => Expect(arguments, 2, () => WithClientAsync(context, CliCommands.StopAllAsync)),
                    "packages" => Expect(arguments, 2, () => WithClientAsync(context, CliPackageCommands.PackagesAsync)),
                    "updates" => Expect(arguments, 2, () => WithClientAsync(context, CliPackageCommands.UpdatesAsync)),
                    "verify" => Expect(arguments, 3, () => WithClientAsync(context, (c, client) => CliPackageCommands.VerifyAsync(c, client, arguments[2]))),
                    "install" => InstallOfficial(context, arguments),
                    "install-zip" => InstallZip(context, arguments),
                    "update" => ChangePackage(context, arguments, remove: false),
                    "remove" => ChangePackage(context, arguments, remove: true),
                    _ => throw new UsageException($"Unknown command 'plugins {arguments[1]}'."),
                }).ConfigureAwait(false);

            case "registry" when arguments.Count >= 2:
                return await (arguments[1].ToLowerInvariant() switch
                {
                    "status" => Expect(arguments, 2, () => WithClientAsync(context, CliPackageCommands.RegistryStatusAsync)),
                    "refresh" => Expect(arguments, 2, () => WithClientAsync(context, CliPackageCommands.RegistryRefreshAsync)),
                    "list" => Expect(arguments, 2, () => WithClientAsync(context, CliPackageCommands.RegistryListAsync)),
                    "info" => Expect(arguments, 3, () => WithClientAsync(context, (c, client) => CliPackageCommands.RegistryInfoAsync(c, client, arguments[2]))),
                    _ => throw new UsageException($"Unknown command 'registry {arguments[1]}'."),
                }).ConfigureAwait(false);

            case "services" when command == "services list":
                Expect(arguments, 2);
                return await WithClientAsync(context, CliCommands.ListServicesAsync).ConfigureAwait(false);

            case "extensions" when command == "extensions list":
                Expect(arguments, 2);
                return await WithClientAsync(context, CliCommands.ListExtensionsAsync).ConfigureAwait(false);

            case "streams" when command == "streams list":
                Expect(arguments, 2);
                return await WithClientAsync(context, CliCommands.ListStreamsAsync).ConfigureAwait(false);

            case "events" when command == "events subscriptions":
                Expect(arguments, 2);
                return await WithClientAsync(context, CliCommands.ListSubscriptionsAsync).ConfigureAwait(false);

            case "logs":
            {
                var follow = TakeFlag(arguments, "--follow") | TakeFlag(arguments, "-f");
                var filter = new LogFilterDto(
                    MinimumLevel: TakeValue(arguments, "--level") is { } level
                        ? Enum.TryParse<LogLevelDto>(level, ignoreCase: true, out var parsed) ? parsed : throw new UsageException($"Unknown level '{level}'. Use trace, debug, information, warning, error, or critical.")
                        : null,
                    PluginId: TakeValue(arguments, "--plugin"),
                    Category: TakeValue(arguments, "--category"),
                    Text: TakeValue(arguments, "--search"));
                var limit = TakeValue(arguments, "--limit") is { } text
                    ? int.TryParse(text, out var n) && n is >= 1 and <= 5000 ? n : throw new UsageException("--limit requires a number between 1 and 5000.")
                    : 100;
                Expect(arguments, 1);
                return await WithClientAsync(context, (c, client) => CliCommands.LogsAsync(c, client, filter, limit, follow)).ConfigureAwait(false);
            }

            default:
                throw new UsageException($"Unknown command '{string.Join(' ', arguments)}'.");
        }
    }

    private static Task<int> InstallOfficial(CliContext context, List<string> arguments)
    {
        var version = TakeValue(arguments, "--version");
        Expect(arguments, 3);
        return WithClientAsync(context, (c, client) => CliPackageCommands.InstallOfficialAsync(c, client, arguments[2], version));
    }

    private static Task<int> InstallZip(CliContext context, List<string> arguments)
    {
        var allowUnverified = TakeFlag(arguments, "--allow-unverified");
        var stop = TakeFlag(arguments, "--stop-if-running");
        Expect(arguments, 3);
        var path = Path.GetFullPath(arguments[2]);
        return WithClientAsync(context, (c, client) => CliPackageCommands.InstallZipAsync(c, client, path, allowUnverified, stop));
    }

    private static Task<int> ChangePackage(CliContext context, List<string> arguments, bool remove)
    {
        var stop = TakeFlag(arguments, "--stop-if-running");
        var yes = remove && TakeFlag(arguments, "--yes");
        Expect(arguments, 3);
        return WithClientAsync(context, (c, client) => remove
            ? CliPackageCommands.RemoveAsync(c, client, arguments[2], stop, yes)
            : CliPackageCommands.UpdateAsync(c, client, arguments[2], stop));
    }

    private static async Task<int> StartDaemonAsync(CliContext context, bool autoStart)
    {
        var outcome = await DaemonLauncher.EnsureRunningAsync(
            context.Profile,
            extraArguments: autoStart ? ["--auto-start"] : null,
            cancellationToken: context.Cancellation).ConfigureAwait(false);
        await using var client = await RuntimeClient.ConnectAsync(context.Profile, "cli", context.Timeout, context.Cancellation).ConfigureAwait(false);
        var status = await client.GetStatusAsync(context.Cancellation).ConfigureAwait(false);
        context.Write(
            new { outcome = outcome.ToString(), status },
            () => context.Output.WriteLine(outcome == DaemonStartOutcome.Started
                ? $"Daemon started (pid {status.ProcessId}, version {status.DaemonVersion})."
                : $"Daemon already running (pid {status.ProcessId}, version {status.DaemonVersion})."));
        return CliExitCodes.Success;
    }

    /// <summary>
    /// Requests shutdown and reports success only after the daemon process has actually exited.
    /// </summary>
    private static async Task<int> StopDaemonAsync(CliContext context)
    {
        await using var client = await RuntimeClient.ConnectAsync(context.Profile, "cli", context.Timeout, context.Cancellation).ConfigureAwait(false);
        var status = await client.GetStatusAsync(context.Cancellation).ConfigureAwait(false);

        // Open the process before requesting shutdown so its exit code stays readable.
        using var process = TryOpenProcess(status.ProcessId);
        var response = await client.ShutdownAsync("cli daemon stop", context.Cancellation).ConfigureAwait(false);
        if (!context.Json)
        {
            context.Output.WriteLine($"Shutdown requested; stopping {response.RunningPluginCount} running plugin(s)...");
        }

        int? exitCode = null;
        if (process is not null)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
            wait.CancelAfter(TimeSpan.FromMinutes(11));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        else
        {
            await client.Completion.WaitAsync(TimeSpan.FromMinutes(11), context.Cancellation).ConfigureAwait(false);
        }

        var stopped = exitCode is null or DaemonExitCodes.Success;
        context.Write(
            new { stopped, exitCode },
            () => context.Output.WriteLine(exitCode switch
            {
                DaemonExitCodes.Success => "Daemon stopped.",
                DaemonExitCodes.ShutdownTimedOut => "Daemon exited after its shutdown timeout: a plugin did not stop in time (see the daemon log).",
                null => "Daemon disconnected.",
                _ => $"Daemon exited with code {exitCode}.",
            }));
        return stopped ? CliExitCodes.Success : CliExitCodes.OperationFailed;
    }

    private static Process? TryOpenProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            _ = process.Handle;
            return process;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static async Task<int> WithClientAsync(CliContext context, Func<CliContext, IRuntimeClient, Task<int>> command)
    {
        await using var client = await RuntimeClient.ConnectAsync(context.Profile, "cli", context.Timeout, context.Cancellation).ConfigureAwait(false);
        return await command(context, client).ConfigureAwait(false);
    }

    private static void Expect(List<string> arguments, int count)
    {
        if (arguments.Count != count)
        {
            throw new UsageException($"Unexpected arguments: '{string.Join(' ', arguments)}'.");
        }
    }

    private static Task<int> Expect(List<string> arguments, int count, Func<Task<int>> run)
    {
        Expect(arguments, count);
        return run();
    }

    private static bool TakeFlag(List<string> arguments, string flag)
    {
        var index = arguments.FindIndex(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        arguments.RemoveAt(index);
        return true;
    }

    private static string? TakeValue(List<string> arguments, string option)
    {
        var index = arguments.FindIndex(a => a.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= arguments.Count)
        {
            throw new UsageException($"{option} requires a value.");
        }

        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        return value;
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed record CliContext(
    RuntimeProfile Profile,
    TimeSpan Timeout,
    bool Json,
    TextWriter Output,
    TextWriter Error,
    CancellationToken Cancellation)
{
    private static readonly JsonSerializerOptions JsonOutput = new(IpcProtocol.JsonOptions) { WriteIndented = true };

    /// <summary>Writes <paramref name="value"/> as JSON with --json, otherwise runs the text renderer.</summary>
    public void Write(object value, Action text)
    {
        if (Json)
        {
            Output.WriteLine(JsonSerializer.Serialize(value, value.GetType(), JsonOutput));
        }
        else
        {
            text();
        }
    }
}
