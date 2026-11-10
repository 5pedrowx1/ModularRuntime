using System.Runtime.InteropServices;
using ModularRuntime.Contracts;
using ModularRuntime.Ipc;

namespace ModularRuntime.Daemon;

/// <summary>Process entry: arguments, single-instance lock, run, graceful termination.</summary>
internal static partial class DaemonEntryPoint
{
    public static int Run(string[] args)
    {
        var (options, error, showHelp) = DaemonOptions.Parse(args);
        if (showHelp || error is not null)
        {
            // WinExe has no console of its own; reuse the parent's console when there is one.
            AttachConsole(AttachParentProcess);
            if (error is not null)
            {
                Console.Error.WriteLine(error);
            }

            Console.WriteLine(DaemonOptions.Usage);
            return showHelp ? DaemonExitCodes.Success : DaemonExitCodes.InvalidArguments;
        }

        options!.Profile.EnsureDirectories();
        var logs = new LogStore(options.LogBufferCapacity, options.Profile.LogDirectory);
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, options.Profile.MutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing it; ownership passed to us.
                acquired = true;
                logs.Write(LogLevel.Warning, "Daemon", "The previous daemon for this profile terminated abnormally.");
            }

            if (!acquired)
            {
                logs.Write(LogLevel.Information, "Daemon", $"Another daemon already serves profile '{options.Profile.ProfileDirectory}'; exiting (pid {Environment.ProcessId}).");
                return DaemonExitCodes.AlreadyRunning;
            }

            try
            {
                return RunAsync(options, logs).GetAwaiter().GetResult();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        finally
        {
            logs.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task<int> RunAsync(DaemonOptions options, LogStore logs)
    {
        await using var host = new DaemonHost(options, logs);

        // Termination signals (console close, logoff, shutdown, Ctrl+C when a console is attached)
        // and process exit request a graceful shutdown and wait for it, bounded by the OS.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);
        using var sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        try
        {
            try
            {
                await host.StartAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logs.Write(LogLevel.Error, "Daemon", $"The IPC pipe is already in use ({exception.Message}); another daemon serves this profile.");
                return DaemonExitCodes.AlreadyRunning;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logs.Log(new LogEntry(LogLevel.Critical, $"Daemon startup failed: {exception.Message}", "Daemon", Diagnostic: PluginDiagnostic.FromException(exception)));
                await host.StopAsync().ConfigureAwait(false);
                return DaemonExitCodes.StartupFailed;
            }

            await host.ShutdownRequested.ConfigureAwait(false);
            return await host.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }

        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            host.RequestShutdown($"signal {context.Signal}");
        }

        void OnProcessExit(object? sender, EventArgs e)
        {
            host.RequestShutdown("process exit");
            host.WaitForStopped(TimeSpan.FromSeconds(5));
        }
    }

    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
