using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace ModularRuntime.Template.Tests;

public sealed record ProcessRun(int ExitCode, string Output)
{
    public void AssertSuccess(string what) => Assert.True(ExitCode == 0, $"{what} failed ({ExitCode}):{Environment.NewLine}{Output}");
}

/// <summary>
/// A developer machine simulated in a temporary folder whose path contains spaces and non-ASCII
/// characters: a local NuGet feed with the packed SDK, an isolated NuGet cache, the template
/// installed in a private template hive, and the tool installed in a private tool path.
/// Nothing is installed globally for the user.
/// </summary>
public sealed class ExternalWorkspace : IAsyncLifetime
{
#if DEBUG
    public const string Configuration = "Debug";
#else
    public const string Configuration = "Release";
#endif

    private static readonly string[] PackedProjects =
    [
        "ModularRuntime.Contracts", "ModularRuntime.Sdk", "ModularRuntime.Engine", "ModularRuntime.Loader",
        "ModularRuntime.Templates", "ModularRuntime.DevTools",
    ];

    public ExternalWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "ModularRuntime.Tests", $"sdk e2e ção {Guid.NewGuid():N}"[..28]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Feed => Path.Combine(Root, "feed");

    public string Work => Path.Combine(Root, "work área");

    public string Hive => Path.Combine(Root, "template-hive");

    public string Tools => Path.Combine(Root, "tools");

    public string NuGetCache => Path.Combine(Root, "nuget-packages");

    public static string Version { get; } =
        typeof(ExternalWorkspace).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public static string RepositoryRoot { get; } = LocateRepository();

    public static string AppDirectory => Path.Combine(RepositoryRoot, "artifacts", "app", Configuration);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        foreach (var project in PackedProjects)
        {
            (await DotnetAsync(RepositoryRoot, "pack", Path.Combine(RepositoryRoot, "src", project, project + ".csproj"), "-c", Configuration, "--no-build", "-o", Feed, "-nologo"))
                .AssertSuccess("dotnet pack " + project);
        }

        // Generated projects restore from the local feed only.
        await File.WriteAllTextAsync(Path.Combine(Work, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="modularruntime-local" value="{Feed}" />
              </packageSources>
            </configuration>
            """);

        (await DotnetAsync(Work, "new", "install", Path.Combine(Feed, $"ModularRuntime.Templates.{Version}.nupkg"), "--debug:custom-hive", Hive)).AssertSuccess("dotnet new install");
        (await DotnetAsync(Work, "tool", "install", "ModularRuntime.DevTools", "--version", Version, "--tool-path", Tools)).AssertSuccess("dotnet tool install");
    }

    public Task DisposeAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Creates a project from the installed template.</summary>
    public async Task<string> NewPluginAsync(string name, string kind = "basic", string? parent = null, params string[] options)
    {
        var parentDirectory = parent ?? Work;
        Directory.CreateDirectory(parentDirectory);
        var directory = Path.Combine(parentDirectory, name);
        (await DotnetAsync(parentDirectory, ["new", "modularruntime-plugin", "-n", name, "-o", directory, "--kind", kind, .. options, "--debug:custom-hive", Hive]))
            .AssertSuccess($"dotnet new modularruntime-plugin {name}");
        return directory;
    }

    /// <summary>Runs <c>dotnet modularruntime ...</c> exactly as a developer types it.</summary>
    public Task<ProcessRun> ToolAsync(string workingDirectory, params string[] arguments) =>
        DotnetAsync(workingDirectory, ["modularruntime", .. arguments]);

    public Task<ProcessRun> DotnetAsync(string workingDirectory, params string[] arguments) =>
        RunAsync("dotnet", workingDirectory, arguments);

    public async Task<ProcessRun> RunAsync(string fileName, string workingDirectory, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in startInfo.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["NUGET_PACKAGES"] = NuGetCache;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["PATH"] = Tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{fileName} {string.Join(' ', arguments)}' did not finish.");
        }

        return new ProcessRun(process.ExitCode, await output + await error);
    }

    /// <summary>
    /// This project references no product assembly on purpose (it tests the packed packages), so it
    /// keeps its own copy of the root lookup instead of using ModularRuntime.TestSupport.
    /// <c>MODULARRUNTIME_ROOT</c> wins when set (other repositories' suites that reuse this fixture).
    /// </summary>
    private static string LocateRepository()
    {
        if (Environment.GetEnvironmentVariable("MODULARRUNTIME_ROOT") is { Length: > 0 } configured)
        {
            var root = Path.GetFullPath(configured);
            return File.Exists(Path.Combine(root, "ModularRuntime.sln"))
                ? root
                : throw new DirectoryNotFoundException($"MODULARRUNTIME_ROOT ('{root}') does not contain ModularRuntime.sln.");
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ModularRuntime.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

[CollectionDefinition(Name)]
public sealed class ExternalWorkspaceGroup : ICollectionFixture<ExternalWorkspace>
{
    public const string Name = "External SDK workspace";
}
