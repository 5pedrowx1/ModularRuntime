using System.IO.Compression;
using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;

namespace ModularRuntime.DevTools.Tests;

/// <summary>Result of running the tool in-process.</summary>
internal sealed record ToolRun(int ExitCode, string Output, string Error)
{
    public string All => Output + Error;
}

/// <summary>
/// Writes small, real plugin projects into a temporary directory (outside the repository) that
/// reference the built ModularRuntime.Contracts.dll by path, and runs the tool against them.
/// </summary>
internal sealed class PluginProjectFixture : IDisposable
{
    private readonly TemporaryDirectory _temporary;

    public PluginProjectFixture(string prefix = "devtools")
    {
        _temporary = new TemporaryDirectory(prefix);
        LibDirectory = Path.Combine(Root, "lib");
        Directory.CreateDirectory(LibDirectory);
        foreach (var assembly in new[] { "ModularRuntime.Contracts", "ModularRuntime.Engine" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, assembly + ".dll"), Path.Combine(LibDirectory, assembly + ".dll"));
        }
    }

    public string Root => _temporary.DirectoryPath;

    public string LibDirectory { get; }

    public static async Task<ToolRun> RunAsync(params string[] args) => await RunAsync(CancellationToken.None, args);

    public static async Task<ToolRun> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await DevToolsApp.RunAsync(args, output, error, cancellationToken);
        return new ToolRun(code, output.ToString(), error.ToString());
    }

    /// <summary>Creates a plugin project. <paramref name="source"/> replaces the default entry type source.</summary>
    public string CreatePlugin(
        string name = "SamplePlugin",
        string? directory = null,
        Action<JsonObject>? editManifest = null,
        string? source = null,
        string extraProjectItems = "",
        bool writeManifest = true)
    {
        var projectDirectory = Path.Combine(directory ?? Root, name);
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <EnableDynamicLoading>true</EnableDynamicLoading>
                <Version>1.0.0</Version>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="ModularRuntime.Contracts">
                  <HintPath>{Path.Combine(LibDirectory, "ModularRuntime.Contracts.dll")}</HintPath>
                </Reference>
                {extraProjectItems}
              </ItemGroup>
              <ItemGroup>
                <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDirectory, name + ".cs"), source ?? $$"""
            using ModularRuntime.Contracts;

            namespace {{name}};

            public sealed class {{name}} : IPlugin
            {
                public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
                {
                    context.Logger.LogInformation("{{name}} started.");
                    return Task.CompletedTask;
                }

                public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);
        if (writeManifest)
        {
            var manifest = new JsonObject
            {
                ["id"] = "test." + name.ToLowerInvariant(),
                ["name"] = name,
                ["version"] = "1.0.0",
                ["apiVersion"] = PluginApiVersion.Supported.ToString(2),
                ["entryAssembly"] = name + ".dll",
                ["entryType"] = $"{name}.{name}",
                ["author"] = "Tests",
            };
            editManifest?.Invoke(manifest);
            File.WriteAllText(Path.Combine(projectDirectory, "plugin.json"), manifest.ToJsonString());
        }

        return projectDirectory;
    }

    public static Dictionary<string, byte[]> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        });
    }

    public void Dispose() => _temporary.Dispose();
}
