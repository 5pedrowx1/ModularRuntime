using ModularRuntime.Packaging;

namespace ModularRuntime.DevTools;

internal sealed record PackOptions(
    string? Project,
    string Configuration,
    string? Output,
    bool NoBuild,
    bool IncludeSymbols,
    bool Verbose);

internal sealed record PackResult(string PackagePath, string Sha256, long Size, IntegrityManifest Manifest);

/// <summary>
/// Builds (with the .NET SDK) and packs a plugin into a local package: the plugin's files, an
/// integrity manifest (<c>integrity.json</c>) declared by the packager, and no signature. The ZIP
/// is deterministic for the same build output (fixed entry order and timestamps).
/// </summary>
internal static class PluginPacker
{
    private static readonly string[] ExcludedFileNames = ["Thumbs.db", ".DS_Store", "desktop.ini"];

    public static async Task<PackResult?> PackAsync(PackOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var project = await PluginProject.EvaluateAsync(PluginProject.Locate(options.Project), options.Configuration, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Project:  {project.ProjectPath}");

        var report = new ValidationReport();
        var candidate = await PluginValidator.ValidateProjectAsync(project, report, cancellationToken).ConfigureAwait(false);
        if (report.HasErrors || candidate is null)
        {
            output.WriteLine("The project is not valid; nothing was built or packed:");
            report.WriteTo(output);
            return null;
        }

        if (!options.NoBuild)
        {
            var build = await PluginProject.BuildAsync(project, output, options.Verbose, cancellationToken).ConfigureAwait(false);
            if (!build.Succeeded)
            {
                output.WriteLine($"The build failed (exit code {build.ExitCode}); nothing was packed.");
                return null;
            }
        }
        else if (!File.Exists(project.TargetPath))
        {
            output.WriteLine($"--no-build was given, but '{project.TargetPath}' does not exist. Build the project first.");
            return null;
        }

        var staging = Path.Combine(Path.GetTempPath(), "modularruntime-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var included = Stage(project, staging, options.IncludeSymbols, cancellationToken);
            output.WriteLine($"Staged {included} file(s) from {project.OutputDirectory}");

            var contentReport = new ValidationReport();
            await PluginValidator.ValidateDirectoryAsync(staging, contentReport, sharedAssembliesExcluded: true, cancellationToken).ConfigureAwait(false);
            if (contentReport.Items.Count > 0)
            {
                contentReport.WriteTo(output);
            }

            if (contentReport.HasErrors)
            {
                output.WriteLine("The plugin files are not valid; no package was written.");
                return null;
            }

            var manifest = await PackageBuilder.CreateManifestAsync(staging, cancellationToken: cancellationToken).ConfigureAwait(false);
            var packagePath = ResolveOutput(options.Output, project, manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
            var temporary = packagePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                // Unsigned on purpose: only the official publishing process signs packages.
                PackageBuilder.WriteZip(staging, manifest, manifest.Serialize(), signatureBytes: null, temporary);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, packagePath, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            var sha256 = await Sha256Hex.ComputeFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(packagePath).Length;
            output.WriteLine();
            output.WriteLine($"Package:  {packagePath}");
            output.WriteLine($"Plugin:   {manifest.PluginId} {manifest.Version} (plugin API {candidate.Descriptor.ApiVersion.ToString(2)})");
            output.WriteLine($"Files:    {manifest.Files.Count} ({manifest.Files.Sum(f => f.Size)} bytes), listed with their SHA-256 in integrity.json");
            output.WriteLine($"Size:     {size} bytes");
            output.WriteLine($"SHA-256:  {sha256}");
            output.WriteLine("Trust:    LOCAL, UNSIGNED package. It installs as \"Local, unverified\" after explicit acceptance;");
            output.WriteLine("          the hashes in integrity.json are declared by this tool, not an official approval.");
            return new PackResult(packagePath, sha256, size, manifest);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Copies the files the plugin needs at run time into the staging directory.</summary>
    private static int Stage(PluginProject project, string staging, bool includeSymbols, CancellationToken cancellationToken)
    {
        var source = project.OutputDirectory;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (!ShouldInclude(relative, source, includeSymbols))
            {
                continue;
            }

            var target = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            count++;
        }

        // The project's plugin.json is the source of truth (the output copy may be stale or absent).
        Directory.CreateDirectory(staging);
        File.Copy(project.ManifestPath, Path.Combine(staging, "plugin.json"), overwrite: true);
        return count;
    }

    internal static bool ShouldInclude(string relativePath, string outputDirectory, bool includeSymbols)
    {
        var fileName = Path.GetFileName(relativePath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        if (PluginValidator.SharedAssemblies.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return false; // shared with the host
        }

        if (ExcludedFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase) ||
            fileName.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase) ||
            PackagePaths.IsMetadataFile(relativePath.Replace('\\', '/')))
        {
            return false;
        }

        if (!includeSymbols && extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // XML documentation next to an assembly is for IDEs, not for the runtime.
        return !(extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                 File.Exists(Path.Combine(outputDirectory, Path.ChangeExtension(relativePath, ".dll"))));
    }

    private static string ResolveOutput(string? output, PluginProject project, IntegrityManifest manifest)
    {
        var fileName = $"{manifest.PluginId}-{manifest.Version}.zip";
        if (string.IsNullOrWhiteSpace(output))
        {
            return Path.Combine(project.ProjectDirectory, "bin", project.Configuration, "modularruntime", fileName);
        }

        var full = Path.GetFullPath(output);
        return full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, fileName);
    }

    internal static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
