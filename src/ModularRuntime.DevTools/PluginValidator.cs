using System.Text.Json;
using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.Packaging;

namespace ModularRuntime.DevTools;

/// <summary>
/// Static validation of a plugin project and of a plugin directory (build output or package
/// content). Uses the Loader's own manifest rules and the Packaging path rules; reads assemblies
/// only as metadata. Nothing is loaded or executed.
/// </summary>
internal static class PluginValidator
{
    /// <summary>Assemblies shared with the host: never packaged with a plugin.</summary>
    internal static readonly string[] SharedAssemblies = ["ModularRuntime.Contracts"];

    /// <summary>Checks the project file and plugin.json (before or without a build).</summary>
    public static async Task<PluginCandidate?> ValidateProjectAsync(PluginProject project, ValidationReport report, CancellationToken cancellationToken)
    {
        if (!File.Exists(project.ManifestPath))
        {
            report.Error(DiagnosticCodes.ManifestMissing, "plugin.json was not found next to the project file.", project.ManifestPath);
        }

        PluginCandidate? candidate = null;
        if (File.Exists(project.ManifestPath))
        {
            var manifest = await PluginLoader.ValidateManifestAsync(project.ProjectDirectory, project.ManifestPath, cancellationToken).ConfigureAwait(false);
            foreach (var error in manifest.Errors)
            {
                report.Error(DiagnosticCodes.ManifestInvalid, $"[{error.Code}]{(error.Field is null ? string.Empty : $" {error.Field}:")} {StripPath(error.Message, project.ProjectDirectory)}", "plugin.json");
            }

            candidate = manifest.Plugins.Count > 0 ? manifest.Plugins[0] : null;
        }

        if (candidate is not null)
        {
            var expected = project.AssemblyName + ".dll";
            if (!string.Equals(candidate.Descriptor.EntryAssembly.Replace('\\', '/'), expected, StringComparison.OrdinalIgnoreCase))
            {
                report.Error(DiagnosticCodes.EntryAssemblyNameMismatch, $"plugin.json declares entryAssembly '{candidate.Descriptor.EntryAssembly}', but the project builds '{expected}'.", "plugin.json");
            }

            if (project.Version is { } projectVersion && PluginVersions.TryParse(projectVersion, out var version) && PluginVersions.Compare(version, candidate.Descriptor.Version) != 0)
            {
                report.Warning(DiagnosticCodes.VersionMismatch, $"The project version ({projectVersion}) differs from the plugin.json version ({candidate.Descriptor.Version}); the package uses plugin.json.");
            }
        }

        if (project.TargetFrameworks is not null && project.TargetFramework is null)
        {
            report.Error(DiagnosticCodes.TargetFrameworkUnsupported, $"The project multi-targets ({project.TargetFrameworks}); a plugin targets exactly net10.0.");
        }
        else if (!string.Equals(project.TargetFramework, "net10.0", StringComparison.OrdinalIgnoreCase))
        {
            report.Error(DiagnosticCodes.TargetFrameworkUnsupported, $"The project targets '{project.TargetFramework ?? "(none)"}'; ModularRuntime plugins target net10.0.");
        }

        if (!project.Dependencies.Any(d => d.Name is "ModularRuntime.Contracts" or "ModularRuntime.Sdk"))
        {
            report.Error(DiagnosticCodes.ContractsReferenceMissing, "The project references neither ModularRuntime.Sdk nor ModularRuntime.Contracts.");
        }

        foreach (var host in project.Dependencies.Where(d => AssemblyMetadataInspector.HostAssemblies.Contains(d.Name, StringComparer.OrdinalIgnoreCase)))
        {
            report.Error(DiagnosticCodes.HostAssemblyReferenced, $"The project references {host.Kind} '{host.Name}', a host-side ModularRuntime component. Plugins use only ModularRuntime.Sdk and ModularRuntime.Contracts.");
        }

        if (!project.EnableDynamicLoading)
        {
            report.Warning(DiagnosticCodes.DynamicLoadingDisabled, "<EnableDynamicLoading>true</EnableDynamicLoading> is not set: package dependencies may not be copied to the build output.");
        }

        if (!project.CopiesManifestToOutput)
        {
            report.Warning(DiagnosticCodes.ManifestNotCopied, "plugin.json is not copied to the build output (CopyToOutputDirectory); pack still includes it from the project directory.");
        }

        return candidate;
    }

    /// <summary>
    /// Checks a plugin directory laid out as it will be installed (build output or package
    /// content): manifest and entry assembly with the Loader's rules, entry type and references
    /// from metadata, runtime dependencies from deps.json, and package path rules.
    /// </summary>
    public static async Task<PluginCandidate?> ValidateDirectoryAsync(string directory, ValidationReport report, bool sharedAssembliesExcluded, CancellationToken cancellationToken)
    {
        var installation = await new PluginLoader().ReadInstallationAsync(directory, cancellationToken).ConfigureAwait(false);
        foreach (var error in installation.Errors)
        {
            var code = error.Code == PluginDiscoveryErrorCodes.EntryAssemblyMissing ? DiagnosticCodes.EntryAssemblyMissing : DiagnosticCodes.ManifestInvalid;
            report.Error(code, $"[{error.Code}] {StripPath(error.Message, directory)}");
        }

        var candidate = installation.Plugins.Count > 0 ? installation.Plugins[0] : null;
        if (candidate is null)
        {
            return null;
        }

        AssemblyMetadataInspector.Inspect(candidate.EntryAssemblyPath, candidate.Descriptor.EntryType, report);
        CheckDependencies(directory, Path.GetFileNameWithoutExtension(candidate.EntryAssemblyPath), report, sharedAssembliesExcluded);
        CheckPaths(directory, report);
        return candidate;
    }

    private static void CheckDependencies(string directory, string assemblyName, ValidationReport report, bool sharedAssembliesExcluded)
    {
        var depsPath = Path.Combine(directory, assemblyName + ".deps.json");
        if (!File.Exists(depsPath))
        {
            report.Warning(DiagnosticCodes.DependencyMissing, $"{assemblyName}.deps.json is missing; the loader resolves dependencies from it.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
            if (!document.RootElement.TryGetProperty("targets", out var targets))
            {
                return;
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    var libraryName = library.Name.Split('/')[0];
                    if (SharedAssemblies.Contains(libraryName, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (library.Value.TryGetProperty("runtime", out var runtime))
                    {
                        foreach (var asset in runtime.EnumerateObject())
                        {
                            var fileName = Path.GetFileName(asset.Name);
                            if (!File.Exists(Path.Combine(directory, fileName)))
                            {
                                report.Error(DiagnosticCodes.DependencyMissing, $"Runtime dependency '{fileName}' of '{library.Name}' is not in the plugin directory.");
                            }
                        }
                    }

                    if (library.Value.TryGetProperty("runtimeTargets", out var runtimeTargets))
                    {
                        foreach (var asset in runtimeTargets.EnumerateObject())
                        {
                            if (!File.Exists(Path.Combine(directory, asset.Name.Replace('/', Path.DirectorySeparatorChar))))
                            {
                                report.Warning(DiagnosticCodes.DependencyMissing, $"Runtime-specific asset '{asset.Name}' of '{library.Name}' is not in the plugin directory.");
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            report.Error(DiagnosticCodes.DependencyMissing, $"{assemblyName}.deps.json is not valid JSON: {exception.Message}");
        }

        if (sharedAssembliesExcluded)
        {
            foreach (var shared in SharedAssemblies.Where(s => File.Exists(Path.Combine(directory, s + ".dll"))))
            {
                report.Error(DiagnosticCodes.HostAssemblyReferenced, $"{shared}.dll is shared with the host and must not be packaged.");
            }
        }
    }

    private static void CheckPaths(string directory, ValidationReport report)
    {
        var limits = PackageLimits.Default;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var path = PackagePaths.ToPackagePath(Path.GetFullPath(directory), file);
            if (!PackagePaths.TryValidate(path, limits, out var reason))
            {
                report.Error(DiagnosticCodes.UnsafePath, $"'{path}' cannot be packaged: {reason}");
            }

            if (keys.TryGetValue(PackagePaths.ComparisonKey(path), out var other))
            {
                report.Error(DiagnosticCodes.PathCollision, $"'{path}' and '{other}' differ only by case.");
            }
            else
            {
                keys[PackagePaths.ComparisonKey(path)] = path;
            }

            total += new FileInfo(file).Length;
            count++;
        }

        if (total > limits.MaxTotalBytes || count > limits.MaxEntries)
        {
            report.Error(DiagnosticCodes.PackageLimits, $"The plugin has {count} files and {total} bytes; packages are limited to {limits.MaxEntries} files and {limits.MaxTotalBytes} bytes.");
        }
    }

    private static string StripPath(string message, string directory) =>
        message.Replace(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, string.Empty, StringComparison.OrdinalIgnoreCase);
}
