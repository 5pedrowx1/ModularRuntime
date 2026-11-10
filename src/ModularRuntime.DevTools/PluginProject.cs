using System.Text.Json;

namespace ModularRuntime.DevTools;

/// <summary>A package or project reference declared by the plugin project.</summary>
internal sealed record ProjectDependency(string Kind, string Name, string? Version);

/// <summary>
/// A plugin project as evaluated by MSBuild (<c>dotnet msbuild -getProperty/-getItem</c>):
/// evaluation only, no target is run and no plugin code is executed.
/// </summary>
internal sealed record PluginProject(
    string ProjectPath,
    string Configuration,
    string AssemblyName,
    string? TargetFramework,
    string? TargetFrameworks,
    bool EnableDynamicLoading,
    string? Version,
    string TargetPath,
    IReadOnlyList<ProjectDependency> Dependencies,
    bool CopiesManifestToOutput,
    string? PackageLicenseExpression = null,
    string? RepositoryUrl = null)
{
    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    public string ManifestPath => Path.Combine(ProjectDirectory, "plugin.json");

    public string OutputDirectory => Path.GetDirectoryName(TargetPath)!;

    public string Name => Path.GetFileNameWithoutExtension(ProjectPath);

    /// <summary>Finds the project file: an explicit .csproj, or the single .csproj in a directory.</summary>
    public static string Locate(string? path)
    {
        var target = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : path);
        if (File.Exists(target))
        {
            return target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? target
                : throw new DevToolsException($"'{target}' is not a C# project file (.csproj).");
        }

        if (!Directory.Exists(target))
        {
            throw new DevToolsException($"'{target}' does not exist.");
        }

        var projects = Directory.GetFiles(target, "*.csproj", SearchOption.TopDirectoryOnly);
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new DevToolsException($"No .csproj file in '{target}'. Run the command in the plugin project directory or pass --project <path>."),
            _ => throw new DevToolsException($"'{target}' contains several projects; choose one with --project <path>."),
        };
    }

    public static async Task<PluginProject> EvaluateAsync(string projectPath, string configuration, CancellationToken cancellationToken)
    {
        string[] properties = ["AssemblyName", "TargetFramework", "TargetFrameworks", "EnableDynamicLoading", "Version", "TargetPath", "PackageLicenseExpression", "RepositoryUrl"];
        var arguments = new List<string> { "msbuild", projectPath, "-nologo", $"-p:Configuration={configuration}" };
        arguments.AddRange(properties.Select(p => $"-getProperty:{p}"));
        arguments.AddRange(["-getItem:PackageReference", "-getItem:ProjectReference", "-getItem:Reference", "-getItem:None", "-getItem:Content"]);
        var result = await DotnetRunner.RunAsync(arguments, Path.GetDirectoryName(projectPath)!, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new DevToolsException($"MSBuild could not evaluate '{projectPath}':{Environment.NewLine}{(result.Output + result.Error).Trim()}");
        }

        try
        {
            var start = result.Output.IndexOf('{', StringComparison.Ordinal);
            using var document = JsonDocument.Parse(start >= 0 ? result.Output[start..] : result.Output);
            var values = document.RootElement.GetProperty("Properties");
            string? Property(string name) =>
                values.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text : null;

            var items = document.RootElement.TryGetProperty("Items", out var itemsElement) ? itemsElement : default;
            var dependencies = new List<ProjectDependency>();
            var copiesManifest = false;
            if (items.ValueKind == JsonValueKind.Object)
            {
                foreach (var (kind, label) in new[] { ("PackageReference", "package"), ("ProjectReference", "project"), ("Reference", "assembly") })
                {
                    foreach (var item in Items(items, kind))
                    {
                        var identity = item.GetProperty("Identity").GetString() ?? string.Empty;
                        var name = kind switch
                        {
                            "ProjectReference" => Path.GetFileNameWithoutExtension(identity),
                            "Reference" when identity.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) => Path.GetFileNameWithoutExtension(identity),
                            "Reference" => identity.Split(',')[0].Trim(),
                            _ => identity,
                        };
                        var version = item.TryGetProperty(nameof(Version), out var v) ? v.GetString() : null;
                        if (kind == "Reference" && IsFrameworkReference(item))
                        {
                            continue;
                        }

                        dependencies.Add(new ProjectDependency(label, name, string.IsNullOrEmpty(version) ? null : version));
                    }
                }

                copiesManifest = Items(items, "None").Concat(Items(items, "Content")).Any(item =>
                    string.Equals(Path.GetFileName(item.GetProperty("Identity").GetString()), "plugin.json", StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("CopyToOutputDirectory", out var copy) &&
                    copy.GetString() is "PreserveNewest" or "Always" or "IfDifferent");
            }

            var targetPath = Property("TargetPath") ?? throw new DevToolsException($"MSBuild did not report a TargetPath for '{projectPath}'.");
            return new PluginProject(
                projectPath,
                configuration,
                Property("AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath),
                Property("TargetFramework"),
                Property("TargetFrameworks"),
                string.Equals(Property("EnableDynamicLoading"), "true", StringComparison.OrdinalIgnoreCase),
                Property("Version"),
                targetPath,
                dependencies,
                copiesManifest,
                Property("PackageLicenseExpression"),
                Property("RepositoryUrl"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DevToolsException($"Unexpected MSBuild output while evaluating '{projectPath}': {exception.Message}", exception);
        }
    }

    public static async Task<DotnetResult> BuildAsync(PluginProject project, TextWriter output, bool verbose, CancellationToken cancellationToken)
    {
        output.WriteLine($"Building {Path.GetFileName(project.ProjectPath)} ({project.Configuration}) with the .NET SDK…");
        var result = await DotnetRunner.RunAsync(
            ["build", project.ProjectPath, "-c", project.Configuration, "-nologo", "-nodeReuse:false", "-v:minimal", "-clp:NoSummary"],
            project.ProjectDirectory,
            verbose ? output.WriteLine : null,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !verbose)
        {
            foreach (var line in (result.Output + result.Error).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Contains(" error ", StringComparison.Ordinal) || l.Contains("error MSB", StringComparison.Ordinal) || l.Contains("error NU", StringComparison.Ordinal)).Distinct().Take(30))
            {
                output.WriteLine("  " + line);
            }
        }

        return result;
    }

    private static JsonElement[] Items(JsonElement items, string kind) =>
        items.TryGetProperty(kind, out var list) && list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray()] : [];

    private static bool IsFrameworkReference(JsonElement item) =>
        !item.TryGetProperty("HintPath", out var hint) || string.IsNullOrEmpty(hint.GetString());
}
