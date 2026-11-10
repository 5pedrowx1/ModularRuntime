using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntime.Packaging;
using ModularRuntime.Submissions;

namespace ModularRuntime.DevTools;

internal sealed record SubmissionOptions(
    string? Project,
    string? SourceRoot,
    string? Output,
    string Configuration,
    bool IncludePackage,
    bool Verbose)
{
    public string? License { get; init; }

    public string? SourceRepository { get; init; }

    public string? SourceCommit { get; init; }
}

/// <summary>
/// Prepares a source submission for official review: the project's source (without build output,
/// IDE state, secrets, or keys), optionally a locally built package, <c>submission.json</c>, and
/// <c>source-integrity.json</c>. Hashes declared here identify what was submitted; they do not make
/// the plugin trusted. The ModularRuntime team reviews the source, rebuilds it in a controlled
/// environment, and signs only what it built.
/// </summary>
internal static class SubmissionBuilder
{
    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", ".vs", ".idea", ".vscode", "node_modules", "packages", "TestResults", "submission", "artifacts"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> PrepareAsync(SubmissionOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var project = await PluginProject.EvaluateAsync(PluginProject.Locate(options.Project), options.Configuration, cancellationToken).ConfigureAwait(false);
        var report = new ValidationReport();
        var candidate = await PluginValidator.ValidateProjectAsync(project, report, cancellationToken).ConfigureAwait(false);
        if (report.HasErrors || candidate is null)
        {
            output.WriteLine("The project is not valid; fix it before preparing a submission:");
            report.WriteTo(output);
            return ExitCodes.Failed;
        }

        var sourceRoot = Path.GetFullPath(options.SourceRoot ?? project.ProjectDirectory);
        if (!IsSameOrBelow(project.ProjectPath, sourceRoot))
        {
            throw new DevToolsException($"The project '{project.ProjectPath}' is not inside the source root '{sourceRoot}'.");
        }

        var destination = Path.GetFullPath(options.Output ?? Path.Combine(project.ProjectDirectory, "submission"));
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new DevToolsException($"'{destination}' already exists and is not empty; choose another --output or remove it.");
        }

        var sourceOut = Path.Combine(destination, "source");
        var artifactsOut = Path.Combine(destination, "artifacts");
        Directory.CreateDirectory(sourceOut);

        var excluded = new List<(string Path, string Reason)>();
        var sourceFiles = new List<IntegrityFile>();
        foreach (var (full, relative) in Enumerate(sourceRoot, destination, excluded))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ExclusionReason(full, relative) is { } reason)
            {
                excluded.Add((relative, reason));
                continue;
            }

            var target = Path.Combine(sourceOut, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(full, target);
            sourceFiles.Add(new IntegrityFile(relative.Replace('\\', '/'), new FileInfo(target).Length, await Sha256Hex.ComputeFileAsync(target, cancellationToken).ConfigureAwait(false)));
        }

        var artifactFiles = new List<IntegrityFile>();
        if (options.IncludePackage)
        {
            var packed = await PluginPacker.PackAsync(new PackOptions(project.ProjectPath, options.Configuration, artifactsOut, NoBuild: false, IncludeSymbols: false, options.Verbose), output, cancellationToken).ConfigureAwait(false);
            if (packed is null)
            {
                output.WriteLine("Packing failed; the submission was not completed.");
                return ExitCodes.Failed;
            }

            artifactFiles.Add(new IntegrityFile("artifacts/" + Path.GetFileName(packed.PackagePath), packed.Size, packed.Sha256));
        }

        var sdkVersion = (await DotnetRunner.RunAsync(["--version"], project.ProjectDirectory, null, cancellationToken).ConfigureAwait(false)).Output.Trim();
        var license = options.License ?? project.PackageLicenseExpression;
        var repository = options.SourceRepository ?? project.RepositoryUrl;
        var lockFiles = sourceFiles.Where(f => Path.GetFileName(f.Path).Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToList();
        var licenseFile = sourceFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Path).Equals("LICENSE", StringComparison.OrdinalIgnoreCase) && !f.Path.Contains('/', StringComparison.Ordinal))?.Path;
        var projectRelative = Path.GetRelativePath(sourceRoot, project.ProjectPath).Replace('\\', '/');
        var solutions = sourceFiles.Where(f => f.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToList();
        var descriptor = candidate.Descriptor;
        var submission = new JsonObject
        {
            ["schemaVersion"] = SubmissionManifest.CurrentSchemaVersion,
            ["pluginId"] = descriptor.Id.Value,
            ["name"] = descriptor.Name,
            ["version"] = descriptor.Version.ToString(),
            ["apiVersion"] = descriptor.ApiVersion.ToString(2),
            ["author"] = candidate.Author,
            ["description"] = candidate.Description,
            ["license"] = license,
            ["licenseFile"] = licenseFile,
            ["sourceRepository"] = repository,
            ["sourceCommit"] = options.SourceCommit,
            ["project"] = projectRelative,
            ["solutions"] = new JsonArray([.. solutions.Select(s => (JsonNode)s)]),
            ["lockFiles"] = new JsonArray([.. lockFiles.Select(s => (JsonNode)s)]),
            ["targetFramework"] = project.TargetFramework,
            ["dependencies"] = new JsonArray([.. project.Dependencies.Select(d => (JsonNode)new JsonObject { ["kind"] = d.Kind, ["name"] = d.Name, ["version"] = d.Version })]),
            ["build"] = new JsonObject
            {
                ["instructions"] = $"dotnet build \"source/{projectRelative}\" -c Release, then dotnet modularruntime pack --project \"source/{projectRelative}\" --configuration Release",
                ["dotnetSdkVersion"] = sdkVersion,
                ["devToolsVersion"] = ToolInfo.Version,
                ["pluginApi"] = PluginApiVersion.Supported.ToString(2),
            },
            ["artifacts"] = new JsonArray([.. artifactFiles.Select(f => (JsonNode)f.Path)]),
            ["excluded"] = new JsonArray([.. excluded.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path.Replace('\\', '/'), ["reason"] = e.Reason })]),
            ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["notice"] = "Hashes in this submission are declared by the author to identify the delivered files. They do not prove the plugin is safe. The ModularRuntime team reviews the source, rebuilds it in a controlled environment, computes the final hashes, and signs only what it built.",
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "submission.json"), submission.ToJsonString(JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var integrity = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["hashAlgorithm"] = "SHA-256",
            ["files"] = new JsonArray([.. sourceFiles.Select(f => (JsonNode)new JsonObject { ["path"] = "source/" + f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256 })
                .Concat(artifactFiles.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256 }))]),
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "source-integrity.json"), integrity.ToJsonString(JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        output.WriteLine();
        output.WriteLine($"Submission: {destination}");
        output.WriteLine($"  source/                {sourceFiles.Count} file(s) from {sourceRoot}");
        output.WriteLine($"  artifacts/             {artifactFiles.Count} file(s)");
        output.WriteLine("  submission.json        plugin identity, project, dependencies, build instructions, tool versions");
        output.WriteLine("  source-integrity.json  SHA-256 of every delivered file");
        if (excluded.Count > 0)
        {
            output.WriteLine($"Excluded {excluded.Count} item(s) (build output, IDE state, secrets, keys):");
            foreach (var (path, reason) in excluded.Take(50))
            {
                output.WriteLine($"  {path}  ({reason})");
            }
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(license))
        {
            missing.Add("a license (--license, or PackageLicenseExpression in the project)");
        }

        if (string.IsNullOrWhiteSpace(repository))
        {
            missing.Add("the source repository (--source-repository, or RepositoryUrl in the project)");
        }

        if (string.IsNullOrWhiteSpace(options.SourceCommit))
        {
            missing.Add("the source commit (--source-commit)");
        }

        if (project.Dependencies.Any(d => d.Kind == "package") && lockFiles.Count == 0)
        {
            missing.Add("a NuGet lock file (set RestorePackagesWithLockFile=true and restore)");
        }

        if (missing.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Warning: the official review requires " + string.Join("; ", missing) + ". The submission will be rejected without them.");
        }

        output.WriteLine("Review the folder before sending it. The hashes identify the delivery; they are not an approval.");
        return ExitCodes.Success;
    }

    private static IEnumerable<(string Full, string Relative)> Enumerate(string root, string destination, List<(string, string)> excluded)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    excluded.Add((relative, "symbolic link or junction (not followed)"));
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    if (IsSameOrBelow(destination, entry.FullName) && string.Equals(Path.GetFullPath(entry.FullName), destination, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (ExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        excluded.Add((relative + "/", "build output, IDE or tool state"));
                        continue;
                    }

                    pending.Push(entry.FullName);
                }
                else
                {
                    yield return (entry.FullName, relative);
                }
            }
        }
    }

    internal static string? ExclusionReason(string fullPath, string relativePath)
    {
        string? text = null;
        var info = new FileInfo(fullPath);
        if (info.Length <= SourceClassifier.MaxScannedTextBytes)
        {
            try
            {
                text = File.ReadAllText(fullPath);
            }
            catch (IOException)
            {
                return "unreadable";
            }
        }

        return SourceClassifier.ExclusionReason(relativePath, text);
    }

    private static bool IsSameOrBelow(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (fullPath + Path.DirectorySeparatorChar).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
