using ModularRuntime.Contracts;
using ModularRuntime.Loader.Internal;

namespace ModularRuntime.Loader;

/// <summary>
/// Discovers and validates plugin installations under a root directory.
/// </summary>
/// <remarks>
/// Each immediate subdirectory containing a <c>plugin.json</c> file is considered a
/// plugin installation. Nested plugin directories are not scanned.
/// </remarks>
public sealed class PluginLoader
{
    private static readonly StringComparer DirectoryComparer = StringComparer.OrdinalIgnoreCase;

    private readonly IRuntimeLogger _logger;

    public PluginLoader(IRuntimeLogger? logger = null)
    {
        _logger = logger ?? NullRuntimeLogger.Instance;
    }

    public async Task<PluginDiscoveryResult> DiscoverAsync(
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            throw new ArgumentException(
                "A plugin root directory is required.",
                nameof(pluginDirectory));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(pluginDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(
                PluginDiscoveryErrorCodes.PluginDirectoryInvalid,
                $"Plugin directory '{pluginDirectory}' is invalid: {exception.Message}",
                pluginDirectory,
                exception);
        }

        if (!Directory.Exists(fullRoot))
        {
            return Failure(
                PluginDiscoveryErrorCodes.PluginDirectoryNotFound,
                $"Plugin directory '{fullRoot}' does not exist.",
                fullRoot);
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(fullRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                PluginDiscoveryErrorCodes.PluginDirectoryAccessFailed,
                $"Plugin directory '{fullRoot}' could not be enumerated: {exception.Message}",
                fullRoot,
                exception);
        }

        Array.Sort(directories, DirectoryComparer);

        var candidates = new List<PluginCandidate>();
        var errors = new List<PluginDiscoveryError>();
        var firstManifestById = new Dictionary<PluginId, string>();

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var outcome = await PluginManifestReader
                .ReadAsync(directory, manifestPath, cancellationToken)
                .ConfigureAwait(false);

            errors.AddRange(outcome.Errors);

            if (outcome.Candidate is null)
            {
                continue;
            }

            var candidate = outcome.Candidate;
            if (firstManifestById.TryGetValue(candidate.Descriptor.Id, out var firstManifest))
            {
                errors.Add(new PluginDiscoveryError(
                    PluginDiscoveryErrorCodes.DuplicatePluginId,
                    $"Plugin ID '{candidate.Descriptor.Id}' is declared by both " +
                    $"'{firstManifest}' and '{candidate.ManifestPath}'. The duplicate was ignored.",
                    candidate.PluginDirectory,
                    candidate.ManifestPath,
                    candidate.Descriptor.Id.Value));
                continue;
            }

            firstManifestById.Add(candidate.Descriptor.Id, candidate.ManifestPath);
            candidates.Add(candidate);
        }

        foreach (var error in errors)
        {
            Log(
                LogLevel.Warning,
                error.Message,
                error.PluginId is null ? null : PluginId.Parse(error.PluginId),
                error.Diagnostic);
        }

        Log(
            LogLevel.Information,
            $"Discovery under '{fullRoot}' found {candidates.Count} valid plugin(s) and " +
            $"{errors.Count} error(s).");

        return new PluginDiscoveryResult(candidates, errors);
    }

    /// <summary>
    /// Reads and validates one plugin installation directory (its <c>plugin.json</c> and entry
    /// assembly path) with the same rules as discovery. Nothing is loaded.
    /// </summary>
    public async Task<PluginDiscoveryResult> ReadInstallationAsync(
        string installationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        var directory = Path.GetFullPath(installationDirectory);
        var outcome = await PluginManifestReader
            .ReadAsync(directory, Path.Combine(directory, "plugin.json"), cancellationToken)
            .ConfigureAwait(false);
        foreach (var error in outcome.Errors)
        {
            Log(LogLevel.Warning, error.Message, diagnostic: error.Diagnostic);
        }

        return new PluginDiscoveryResult(
            outcome.Candidate is null ? [] : [outcome.Candidate],
            outcome.Errors);
    }

    /// <summary>
    /// Validates a <c>plugin.json</c> with the same rules as discovery, without requiring the entry
    /// assembly to exist yet (for example in a plugin's source project before it is built). The
    /// entry assembly path must still be a safe path relative to <paramref name="pluginDirectory"/>.
    /// Nothing is loaded.
    /// </summary>
    public static async Task<PluginDiscoveryResult> ValidateManifestAsync(
        string pluginDirectory,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var outcome = await PluginManifestReader
            .ReadAsync(Path.GetFullPath(pluginDirectory), Path.GetFullPath(manifestPath), cancellationToken, requireEntryAssemblyFile: false)
            .ConfigureAwait(false);
        return new PluginDiscoveryResult(outcome.Candidate is null ? [] : [outcome.Candidate], outcome.Errors);
    }

    /// <summary>Creates the engine factory responsible for one plugin installation.</summary>
    public AssemblyPluginInstanceFactory CreateInstanceFactory(
        PluginCandidate candidate,
        IPluginLoadVerifier? verifier = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new AssemblyPluginInstanceFactory(candidate, _logger, verifier);
    }

    private PluginDiscoveryResult Failure(
        string code,
        string message,
        string pluginDirectory,
        Exception? exception = null)
    {
        var diagnostic = PluginDiagnostic.FromExceptionOrNull(exception);
        Log(LogLevel.Error, message, diagnostic: diagnostic);
        return new PluginDiscoveryResult(
            [],
            [new PluginDiscoveryError(code, message, pluginDirectory, Diagnostic: diagnostic)]);
    }

    private void Log(
        LogLevel level,
        string message,
        PluginId? pluginId = null,
        PluginDiagnostic? diagnostic = null) =>
        SafeLogger.Log(
            _logger,
            new LogEntry(
                level,
                message,
                Category: "Loader",
                PluginId: pluginId,
                Diagnostic: diagnostic));
}
