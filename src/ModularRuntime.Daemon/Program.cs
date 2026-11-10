using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

namespace ModularRuntime.Daemon;

internal static class Program
{
    // Synchronous Main: the single-instance mutex is thread-affine and must be acquired and
    // released on the same thread.
    public static int Main(string[] args) => DaemonEntryPoint.Run(args);
}
