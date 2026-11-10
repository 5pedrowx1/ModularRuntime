using System.Text.Json;
using System.Text.Json.Serialization;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Daemon;

/// <summary>Daemon settings: defaults, then <c>config/daemon.json</c>, then command-line options.</summary>
internal sealed record DaemonOptions
{
    public required RuntimeProfile Profile { get; init; }

    public required string PluginDirectory { get; init; }

    public int LogBufferCapacity { get; init; } = 5000;

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool AutoStartPlugins { get; init; }

    public int MaxMessageBytes { get; init; } = IpcProtocol.DefaultMaxMessageBytes;

    /// <summary>
    /// The official registry location used when neither the command line nor the configuration
    /// names one. Set by the ModularRuntime team before release (an https URL of the published
    /// registry directory); <see langword="null"/> means "no official registry configured".
    /// </summary>
    public static readonly Uri? DefaultRegistryLocation;

    /// <summary>Registry location (https directory URL or local directory), or <see langword="null"/>.</summary>
    public Uri? RegistryLocation { get; init; } = DefaultRegistryLocation;

    /// <summary>
    /// Development and testing only: an additional trust store whose keys must be marked as test
    /// keys. Never set by configuration files or clients; only by an explicit command-line option.
    /// </summary>
    public string? TestTrustStorePath { get; init; }

    public string PackagesDirectory => Path.Combine(Profile.ProfileDirectory, "packages");

    public const string Usage = """
        ModularRuntime.Daemon — owns the plugin runtime for one user profile.

        Usage: ModularRuntime.Daemon [options]

          --profile <dir>           Profile directory (default: MODULARRUNTIME_PROFILE or %LOCALAPPDATA%\ModularRuntime)
          --plugins <dir>           Plugin root (default: config value or <profile>\plugins)
          --log-buffer <count>      In-memory log entries kept for clients (100-100000, default 5000)
          --shutdown-timeout <sec>  Maximum time to stop plugins on shutdown (1-600, default 30)
          --auto-start              Start plugins after discovery (never local plugins without an official signature)
          --registry <url|dir>      Official registry location (https directory URL, or a local mirror directory)
          --test-trust-store <file> DEVELOPMENT ONLY: also trust the test keys in this file
          --help                    Show this help

        The daemon has no window. Use ModularRuntime.Cli or ModularRuntime.Desktop to control it.
        """;

    /// <summary>An https URL or a directory path; always returned as a directory URI ending with '/'.</summary>
    internal static bool TryParseRegistryLocation(string text, string baseDirectory, out Uri? location)
    {
        location = null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !uri.IsFile)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            location = new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/");
            return true;
        }

        try
        {
            var directory = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(baseDirectory, text));
            location = new Uri(directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>Parses arguments and the profile configuration; returns an error message instead of throwing.</summary>
    public static (DaemonOptions? Options, string? Error, bool ShowHelp) Parse(string[] args)
    {
        string? profile = null, plugins = null, registry = null, testTrustStore = null;
        int? logBuffer = null, shutdownSeconds = null;
        var autoStart = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? Next() => index + 1 < args.Length ? args[++index] : null;
            switch (argument.ToLowerInvariant())
            {
                case "--help" or "-h":
                    return (null, null, true);
                case "--profile":
                    profile = Next();
                    if (profile is null) return (null, "--profile requires a directory.", false);
                    break;
                case "--plugins":
                    plugins = Next();
                    if (plugins is null) return (null, "--plugins requires a directory.", false);
                    break;
                case "--log-buffer":
                    if (!int.TryParse(Next(), out var buffer) || buffer is < 100 or > 100_000)
                        return (null, "--log-buffer requires a number between 100 and 100000.", false);
                    logBuffer = buffer;
                    break;
                case "--shutdown-timeout":
                    if (!int.TryParse(Next(), out var seconds) || seconds is < 1 or > 600)
                        return (null, "--shutdown-timeout requires a number of seconds between 1 and 600.", false);
                    shutdownSeconds = seconds;
                    break;
                case "--auto-start":
                    autoStart = true;
                    break;
                case "--registry":
                    registry = Next();
                    if (registry is null) return (null, "--registry requires a URL or directory.", false);
                    break;
                case "--test-trust-store":
                    testTrustStore = Next();
                    if (testTrustStore is null) return (null, "--test-trust-store requires a file.", false);
                    break;
                default:
                    return (null, $"Unknown argument '{argument}'.", false);
            }
        }

        RuntimeProfile resolved;
        try
        {
            resolved = RuntimeProfile.Resolve(profile);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"Invalid profile directory: {exception.Message}", false);
        }

        DaemonConfigFile config;
        try
        {
            config = DaemonConfigFile.Load(resolved.ConfigFilePath);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, $"Invalid configuration '{resolved.ConfigFilePath}': {exception.Message}", false);
        }

        var pluginDirectory = plugins ?? config.PluginDirectory ?? resolved.DefaultPluginDirectory;
        if (!Path.IsPathRooted(pluginDirectory))
        {
            pluginDirectory = Path.Combine(resolved.ProfileDirectory, pluginDirectory);
        }

        var registryText = registry ?? config.RegistryLocation;
        Uri? registryLocation = DefaultRegistryLocation;
        if (registryText is not null && !TryParseRegistryLocation(registryText, resolved.ProfileDirectory, out registryLocation))
        {
            return (null, $"Invalid registry location '{registryText}': use an https URL or a directory path.", false);
        }

        return (new DaemonOptions
        {
            RegistryLocation = registryLocation,
            TestTrustStorePath = testTrustStore is null ? null : Path.GetFullPath(testTrustStore),
            Profile = resolved,
            PluginDirectory = Path.GetFullPath(pluginDirectory),
            LogBufferCapacity = logBuffer ?? Math.Clamp(config.LogBufferCapacity ?? 5000, 100, 100_000),
            ShutdownTimeout = TimeSpan.FromSeconds(shutdownSeconds ?? Math.Clamp(config.ShutdownTimeoutSeconds ?? 30, 1, 600)),
            AutoStartPlugins = autoStart || (config.AutoStartPlugins ?? false),
            MaxMessageBytes = Math.Clamp(config.MaxMessageBytes ?? IpcProtocol.DefaultMaxMessageBytes, 64 * 1024, IpcProtocol.HardMaxMessageBytes),
        }, null, false);
    }
}

/// <summary>Optional <c>config/daemon.json</c>. Unknown properties are rejected.</summary>
internal sealed record DaemonConfigFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string? PluginDirectory { get; init; }

    public int? LogBufferCapacity { get; init; }

    public int? ShutdownTimeoutSeconds { get; init; }

    public bool? AutoStartPlugins { get; init; }

    public int? MaxMessageBytes { get; init; }

    /// <summary>Official registry location (https URL or directory). Trust keys cannot be configured here.</summary>
    public string? RegistryLocation { get; init; }

    public static DaemonConfigFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DaemonConfigFile();
        }

        var info = new FileInfo(path);
        if (info.Length > 64 * 1024)
        {
            throw new IOException("The configuration file is larger than 64 KiB.");
        }

        return JsonSerializer.Deserialize<DaemonConfigFile>(File.ReadAllText(path), Options) ?? new DaemonConfigFile();
    }
}
