using System.Diagnostics;

namespace ModularRuntime.Ipc;

/// <summary>Exit codes of ModularRuntime.Daemon.</summary>
public static class DaemonExitCodes
{
    public const int Success = 0;
    public const int StartupFailed = 1;

    /// <summary>Another daemon already serves this profile; this process exited without doing anything.</summary>
    public const int AlreadyRunning = 2;

    /// <summary>Shutdown did not complete within the configured timeout; the process exited anyway.</summary>
    public const int ShutdownTimedOut = 3;

    public const int InvalidArguments = 4;
}

public enum DaemonStartOutcome
{
    /// <summary>A daemon was already serving the profile.</summary>
    AlreadyRunning,

    /// <summary>This call started the daemon and it is now accepting connections.</summary>
    Started,
}

/// <summary>
/// Locates and starts ModularRuntime.Daemon for a profile and waits until it accepts connections.
/// </summary>
/// <remarks>
/// <para>
/// The daemon is started directly (no <c>cmd.exe</c> or PowerShell) through <c>CreateProcess</c>
/// without handle inheritance, without a console window, in its own process group and, when the
/// caller's job object allows it, outside that job (see <see cref="DetachedProcess"/>). The started
/// process therefore does not depend on the lifetime or the standard streams of the caller:
/// closing the desktop application or the CLI leaves the daemon running. (A caller that runs
/// inside a Windows job object with kill-on-close semantics, such as some debuggers, can still
/// take child processes down with it; see docs/PROCESS_LIFECYCLE.md.)
/// </para>
/// <para>
/// Concurrent starts are safe: the daemon's single-instance mutex lets only one process serve a
/// profile; the others exit with <see cref="DaemonExitCodes.AlreadyRunning"/>, and every caller
/// keeps polling until it can connect to the winner.
/// </para>
/// </remarks>
public static class DaemonLauncher
{
    public const string DaemonPathEnvironmentVariable = "MODULARRUNTIME_DAEMON_PATH";
    public const string DaemonExecutableName = "ModularRuntime.Daemon.exe";

    /// <summary>
    /// Resolution order: explicit path, <c>MODULARRUNTIME_DAEMON_PATH</c>, then the directory of the
    /// running application (the publish and build layouts place all three executables together).
    /// </summary>
    public static string? ResolveDaemonPath(string? explicitPath = null)
    {
        foreach (var candidate in new[]
                 {
                     explicitPath,
                     Environment.GetEnvironmentVariable(DaemonPathEnvironmentVariable),
                     Path.Combine(AppContext.BaseDirectory, DaemonExecutableName),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>Returns <see langword="true"/> when a daemon accepts a handshake for the profile.</summary>
    public static async Task<bool> IsRunningAsync(RuntimeProfile profile, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = await IpcClient.ConnectAsync(profile.PipeName, "probe", timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or IpcClientException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Ensures a daemon serves <paramref name="profile"/>, starting one if needed.</summary>
    /// <exception cref="FileNotFoundException">The daemon executable could not be located.</exception>
    /// <exception cref="TimeoutException">The daemon did not accept connections in time.</exception>
    /// <exception cref="InvalidOperationException">The daemon process exited during startup.</exception>
    public static async Task<DaemonStartOutcome> EnsureRunningAsync(
        RuntimeProfile profile,
        string? daemonPath = null,
        TimeSpan? startTimeout = null,
        IReadOnlyList<string>? extraArguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (await IsRunningAsync(profile, TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false))
        {
            return DaemonStartOutcome.AlreadyRunning;
        }

        var executable = ResolveDaemonPath(daemonPath)
            ?? throw new FileNotFoundException(
                $"{DaemonExecutableName} was not found next to the application. Set {DaemonPathEnvironmentVariable} or pass its path.");

        List<string> arguments = ["--profile", profile.ProfileDirectory, .. extraArguments ?? []];
        using var process = DetachedProcess.Start(executable, arguments, Path.GetDirectoryName(executable)!);

        var deadline = DateTime.UtcNow + (startTimeout ?? TimeSpan.FromSeconds(20));
        var delay = TimeSpan.FromMilliseconds(100);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsRunningAsync(profile, TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false))
            {
                return DaemonStartOutcome.Started;
            }

            if (DetachedProcess.TryGetExitCode(process) is { } code && code != DaemonExitCodes.AlreadyRunning)
            {
                throw new InvalidOperationException($"The daemon exited during startup with code {code}. See the daemon log in '{profile.LogDirectory}'.");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
        }

        throw new TimeoutException($"The daemon did not accept connections on '{profile.PipeName}' in time.");
    }
}
