using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ModularRuntime.Ipc;

/// <summary>
/// Starts a process that shares nothing with its launcher: no inherited handles, no console, and,
/// when the launcher's job object allows it, outside that job.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> always calls
/// <c>CreateProcess</c> with <c>bInheritHandles = TRUE</c>, so the child inherits every inheritable
/// handle of the launcher, including redirected standard streams. A daemon started that way keeps
/// the launcher's stdout pipe open for its whole life, and whoever reads that pipe (a script running
/// <c>ModularRuntime.Cli daemon start</c>) never sees end-of-file.
/// </remarks>
internal static partial class DetachedProcess
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ErrorAccessDenied = 5;
    private const int StillActive = 259;

    /// <summary>Starts <paramref name="executable"/> and returns a handle to the new process.</summary>
    public static SafeProcessHandle Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var commandLine = new StringBuilder(QuoteArgument(executable));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(QuoteArgument(argument));
        }

        const uint baseFlags = CreateNoWindow | CreateUnicodeEnvironment | CreateNewProcessGroup;
        if (TryCreate(executable, commandLine.ToString(), workingDirectory, baseFlags | CreateBreakawayFromJob, out var handle, out var error))
        {
            return handle;
        }

        // The launcher's job does not allow breakaway; start inside it instead.
        if (error == ErrorAccessDenied && TryCreate(executable, commandLine.ToString(), workingDirectory, baseFlags, out handle, out error))
        {
            return handle;
        }

        throw new Win32Exception(error, $"Could not start '{executable}'.");
    }

    /// <summary>Returns the exit code, or <see langword="null"/> while the process is running.</summary>
    public static int? TryGetExitCode(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return GetExitCodeProcess(process, out var code) && code != StillActive ? code : null;
    }

    /// <summary>Quotes one argument following the rules of <c>CommandLineToArgvW</c>.</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }

            backslashes = 0;
            quoted.Append(character);
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static unsafe bool TryCreate(string executable, string commandLine, string workingDirectory, uint flags, out SafeProcessHandle handle, out int error)
    {
        var startup = new StartupInfo { Cb = sizeof(StartupInfo) };
        var buffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, buffer, 0, commandLine.Length);
        fixed (char* application = executable)
        fixed (char* command = buffer)
        fixed (char* directory = workingDirectory)
        {
            if (!CreateProcessW(application, command, IntPtr.Zero, IntPtr.Zero, inheritHandles: false, flags, IntPtr.Zero, directory, &startup, out var information))
            {
                error = Marshal.GetLastPInvokeError();
                handle = new SafeProcessHandle();
                return false;
            }

            CloseHandle(information.Thread);
            handle = new SafeProcessHandle(information.Process, ownsHandle: true);
            error = 0;
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        char* applicationName,
        char* commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        char* currentDirectory,
        StartupInfo* startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
