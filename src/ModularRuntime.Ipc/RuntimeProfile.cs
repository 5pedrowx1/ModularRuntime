using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ModularRuntime.Ipc;

/// <summary>
/// A per-user runtime profile: its data directories and the names of the kernel objects that
/// identify the daemon serving it.
/// </summary>
/// <remarks>
/// <para>
/// One daemon serves one profile. The pipe name and the single-instance mutex name are derived
/// from the current user's SID and the normalized profile directory, so different users and
/// different profiles never collide, and tests can run isolated daemons by using a temporary
/// profile directory.
/// </para>
/// <para>
/// Resolution order of the profile directory: explicit argument, the
/// <c>MODULARRUNTIME_PROFILE</c> environment variable, then
/// <c>%LOCALAPPDATA%\ModularRuntime</c>.
/// </para>
/// </remarks>
public sealed class RuntimeProfile
{
    public const string ProfileEnvironmentVariable = "MODULARRUNTIME_PROFILE";

    private RuntimeProfile(string profileDirectory)
    {
        ProfileDirectory = profileDirectory;
        var key = ComputeKey(profileDirectory);
        PipeName = $"ModularRuntime.ipc{Contracts.IpcProtocol.Version}.{key}";
        MutexName = $@"Local\ModularRuntime.Daemon.{key}";
    }

    public string ProfileDirectory { get; }

    /// <summary>Daemon configuration (<c>daemon.json</c>).</summary>
    public string ConfigDirectory => Path.Combine(ProfileDirectory, "config");

    public string LogDirectory => Path.Combine(ProfileDirectory, "logs");

    /// <summary>Default plugin root; the daemon configuration may point elsewhere.</summary>
    public string DefaultPluginDirectory => Path.Combine(ProfileDirectory, "plugins");

    /// <summary>Runtime state of the daemon (for example its last-known process information).</summary>
    public string RuntimeDirectory => Path.Combine(ProfileDirectory, "runtime");

    public string ConfigFilePath => Path.Combine(ConfigDirectory, "daemon.json");

    /// <summary>Local named pipe served by this profile's daemon.</summary>
    public string PipeName { get; }

    /// <summary>Session-local mutex owned by the daemon serving this profile.</summary>
    public string MutexName { get; }

    public static string DefaultProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularRuntime");

    public static RuntimeProfile Resolve(string? profileDirectory = null)
    {
        var directory = profileDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = DefaultProfileDirectory;
        }

        return new RuntimeProfile(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(DefaultPluginDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }

    private static string ComputeKey(string profileDirectory)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var material = Encoding.UTF8.GetBytes($"{sid}|{profileDirectory.ToUpperInvariant()}");
        return Convert.ToHexString(SHA256.HashData(material))[..24].ToLowerInvariant();
    }
}
