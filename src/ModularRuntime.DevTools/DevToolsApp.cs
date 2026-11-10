using System.Reflection;
using ModularRuntime.Contracts;
using ModularRuntime.Packaging;

namespace ModularRuntime.DevTools;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Canceled = 130;
}

internal static class ToolInfo
{
    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}

/// <summary>
/// <c>dotnet modularruntime</c>: the developer tool for plugin authors. Independent of the daemon,
/// the desktop app, and the administrative CLI. It never signs packages and has no access to keys.
/// </summary>
internal static class DevToolsApp
{
    public const string Usage = """
        ModularRuntime developer tools {VERSION} (plugin API {API})

        Usage: dotnet modularruntime <command> [options]

        Commands:
          validate              Check the plugin project and plugin.json (static; no plugin code runs).
                                With --build, also builds and checks the build output.
          pack                  Build with the .NET SDK and create a local, unsigned package (.zip)
                                that the Plugin Manager can install as "Local, unverified".
          inspect <package.zip> Verify a package like the daemon does and list its contents,
                                hashes, signature state, and compatibility. Nothing is executed.
          prepare-submission    Collect the source (without build output, secrets, or keys), a
                                local package, submission.json, and source-integrity.json for
                                official review.
          registry-check <registry.json>
                                Verify a signed catalog revision: signature by a trusted registry
                                key, validity, revision and immutability against --previous,
                                revocations, and local package files. No private key is needed.
          doctor                Check the .NET SDK and the project's ModularRuntime references.

        Options:
          --project <path>        Project file or directory (default: the current directory).
          -c, --configuration <c> Build configuration (default: Release for pack and
                                  prepare-submission, Debug for validate).
          -o, --output <path>     pack: .zip file or directory. prepare-submission: folder.
          --no-build              pack: use the existing build output instead of building.
          --build                 validate: build first, then validate the build output.
          --include-symbols       pack: include .pdb files.
          --no-package            prepare-submission: do not build or include a package.
          --source-root <dir>     prepare-submission: folder to collect (default: project folder).
          --license <spdx>        prepare-submission: license (default: PackageLicenseExpression).
          --source-repository <u> prepare-submission: https URL of the source repository
                                  (default: RepositoryUrl).
          --source-commit <hex>   prepare-submission: the commit the submitted source is taken from.
          --test-trust-store <f>  inspect, registry-check: also trust the test keys in this file.
          --trust <file>          registry-check: trust store (default: the official keys).
          --signature <file>      registry-check: catalog signature (default: registry.sig next to it).
          --previous <file>       registry-check: the previous signed revision (rollback and
                                  immutability checks).
          --packages-root <dir>   registry-check: folder with the catalog's package files.
          --max-validity-days <n> registry-check: longest validity window (default: 90).
          -v, --verbose           Show the full build output.
          -h, --help              Show this help.
          --version               Show the tool version.

        Exit codes: 0 success, 1 validation/build/package failure, 2 usage error, 130 canceled.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        Arguments arguments;
        try
        {
            arguments = Arguments.Parse(args);
        }
        catch (DevToolsException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine("Run 'dotnet modularruntime --help' for usage.");
            return ExitCodes.Usage;
        }

        if (arguments.Flag("version"))
        {
            output.WriteLine(ToolInfo.Version);
            return ExitCodes.Success;
        }

        if (arguments.Command is null || arguments.Flag("help"))
        {
            output.WriteLine(Usage.Replace("{VERSION}", ToolInfo.Version, StringComparison.Ordinal).Replace("{API}", PluginApiVersion.Supported.ToString(2), StringComparison.Ordinal));
            return arguments.Command is null && !arguments.Flag("help") ? ExitCodes.Usage : ExitCodes.Success;
        }

        try
        {
            return arguments.Command switch
            {
                "validate" => await ValidateAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "pack" => await PackAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "inspect" => await InspectAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "prepare-submission" => await SubmissionBuilder.PrepareAsync(
                    new SubmissionOptions(arguments.Value("project"), arguments.Value("source-root"), arguments.Value("output"), arguments.Value("configuration") ?? "Release", !arguments.Flag("no-package"), arguments.Flag("verbose"))
                    {
                        License = arguments.Value("license"),
                        SourceRepository = arguments.Value("source-repository"),
                        SourceCommit = arguments.Value("source-commit"),
                    },
                    output,
                    cancellationToken).ConfigureAwait(false),
                "registry-check" => RegistryCheckCommand.Run(arguments, output),
                "doctor" => await DoctorAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                _ => UnknownCommand(arguments.Command, error),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Canceled.");
            return ExitCodes.Canceled;
        }
        catch (DevToolsException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return ExitCodes.Failed;
        }
        catch (PackageException exception)
        {
            error.WriteLine($"error [{exception.Code}]: {exception.Message}");
            return ExitCodes.Failed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: {exception.Message}");
            return ExitCodes.Failed;
        }
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"Unknown command '{command}'. Run 'dotnet modularruntime --help' for usage.");
        return ExitCodes.Usage;
    }

    private static async Task<int> ValidateAsync(Arguments arguments, TextWriter output, CancellationToken cancellationToken)
    {
        arguments.ExpectPositional(0);
        var project = await PluginProject.EvaluateAsync(PluginProject.Locate(arguments.Value("project")), arguments.Value("configuration") ?? "Debug", cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Validating {project.ProjectPath}");
        var report = new ValidationReport();
        var candidate = await PluginValidator.ValidateProjectAsync(project, report, cancellationToken).ConfigureAwait(false);

        if (!report.HasErrors && arguments.Flag("build"))
        {
            var build = await PluginProject.BuildAsync(project, output, arguments.Flag("verbose"), cancellationToken).ConfigureAwait(false);
            if (!build.Succeeded)
            {
                output.WriteLine($"The build failed (exit code {build.ExitCode}).");
                return ExitCodes.Failed;
            }
        }

        if (!report.HasErrors && File.Exists(project.TargetPath))
        {
            output.WriteLine($"Checking the build output {project.OutputDirectory} (metadata only)");
            await PluginValidator.ValidateDirectoryAsync(project.OutputDirectory, report, sharedAssembliesExcluded: false, cancellationToken).ConfigureAwait(false);
        }
        else if (!report.HasErrors)
        {
            report.Info(DiagnosticCodes.NotBuilt, $"No build output for {project.Configuration}; the entry type and dependencies are checked after a build (use --build).");
        }

        report.WriteTo(output);
        if (report.HasErrors)
        {
            output.WriteLine($"Invalid: {report.ErrorCount} error(s), {report.WarningCount} warning(s).");
            return ExitCodes.Failed;
        }

        output.WriteLine($"Valid: {candidate!.Descriptor.Id} {candidate.Descriptor.Version} (plugin API {candidate.Descriptor.ApiVersion.ToString(2)}), {report.WarningCount} warning(s).");
        return ExitCodes.Success;
    }

    private static async Task<int> PackAsync(Arguments arguments, TextWriter output, CancellationToken cancellationToken)
    {
        arguments.ExpectPositional(0);
        var options = new PackOptions(
            arguments.Value("project"),
            arguments.Value("configuration") ?? "Release",
            arguments.Value("output"),
            arguments.Flag("no-build"),
            arguments.Flag("include-symbols"),
            arguments.Flag("verbose"));
        return await PluginPacker.PackAsync(options, output, cancellationToken).ConfigureAwait(false) is null ? ExitCodes.Failed : ExitCodes.Success;
    }

    private static async Task<int> InspectAsync(Arguments arguments, TextWriter output, CancellationToken cancellationToken)
    {
        if (arguments.Positionals.Count != 1)
        {
            throw new DevToolsException("inspect needs exactly one package path: dotnet modularruntime inspect <package.zip>");
        }

        return await PackageInspector.InspectAsync(arguments.Positionals[0], arguments.Value("test-trust-store"), output, cancellationToken).ConfigureAwait(false)
            ? ExitCodes.Success
            : ExitCodes.Failed;
    }

    private static async Task<int> DoctorAsync(Arguments arguments, TextWriter output, CancellationToken cancellationToken)
    {
        arguments.ExpectPositional(0);
        var ok = true;
        output.WriteLine($"ModularRuntime developer tools {ToolInfo.Version}, plugin API {PluginApiVersion.Supported.ToString(2)}");
        var sdks = await DotnetRunner.RunAsync(["--list-sdks"], Directory.GetCurrentDirectory(), null, cancellationToken).ConfigureAwait(false);
        var versions = sdks.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(' ')[0]).ToList();
        if (versions.Any(v => v.StartsWith("10.", StringComparison.Ordinal) || (int.TryParse(v.Split('.')[0], out var major) && major > 10)))
        {
            output.WriteLine($"  ok       .NET SDK: {string.Join(", ", versions)}");
        }
        else
        {
            ok = false;
            output.WriteLine($"  problem  .NET 10 SDK not found (found: {(versions.Count == 0 ? "none" : string.Join(", ", versions))}). Install it from https://dotnet.microsoft.com/download.");
        }

        string projectPath;
        try
        {
            projectPath = PluginProject.Locate(arguments.Value("project"));
        }
        catch (DevToolsException exception)
        {
            output.WriteLine($"  info     No plugin project checked: {exception.Message}");
            return ok ? ExitCodes.Success : ExitCodes.Failed;
        }

        var project = await PluginProject.EvaluateAsync(projectPath, "Debug", cancellationToken).ConfigureAwait(false);
        output.WriteLine($"  ok       Project: {project.ProjectPath} ({project.TargetFramework ?? project.TargetFrameworks})");
        foreach (var reference in project.Dependencies.Where(d => d.Name.StartsWith("ModularRuntime.", StringComparison.OrdinalIgnoreCase)))
        {
            var versionText = reference.Version ?? "(project reference)";
            var expected = reference.Version is null || reference.Version.StartsWith(ToolInfo.Version, StringComparison.Ordinal);
            output.WriteLine(expected
                ? $"  ok       {reference.Name} {versionText}"
                : $"  warning  {reference.Name} {versionText}; this tool is {ToolInfo.Version} (plugin API {PluginApiVersion.Supported.ToString(2)}). Use matching versions.");
        }

        var report = new ValidationReport();
        await PluginValidator.ValidateProjectAsync(project, report, cancellationToken).ConfigureAwait(false);
        if (report.Items.Count > 0)
        {
            report.WriteTo(output);
        }

        ok &= !report.HasErrors;
        output.WriteLine(ok ? "No problems found." : "Problems found.");
        return ok ? ExitCodes.Success : ExitCodes.Failed;
    }
}

/// <summary>Minimal argument parser: one command, positionals, --name value options, and flags.</summary>
internal sealed class Arguments
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-c"] = "configuration", ["-o"] = "output", ["-v"] = "verbose", ["-h"] = "help", ["-?"] = "help",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "project", "configuration", "output", "source-root", "test-trust-store", "license", "source-repository", "source-commit", "trust", "signature", "previous", "packages-root", "max-validity-days" };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal) { "no-build", "build", "include-symbols", "no-package", "verbose", "help", "version" };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public string? Command { get; private set; }

    public List<string> Positionals { get; } = [];

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.StartsWith('-'))
            {
                var name = Aliases.TryGetValue(arg, out var alias) ? alias : arg.TrimStart('-');
                if (ValueOptions.Contains(name))
                {
                    if (index + 1 >= args.Length)
                    {
                        throw new DevToolsException($"Option '{arg}' needs a value.");
                    }

                    result._values[name] = args[++index];
                }
                else if (FlagOptions.Contains(name))
                {
                    result._flags.Add(name);
                }
                else
                {
                    throw new DevToolsException($"Unknown option '{arg}'.");
                }
            }
            else if (result.Command is null)
            {
                result.Command = arg;
            }
            else
            {
                result.Positionals.Add(arg);
            }
        }

        return result;
    }

    public string? Value(string name) => _values.GetValueOrDefault(name);

    public bool Flag(string name) => _flags.Contains(name);

    public void ExpectPositional(int count)
    {
        if (Positionals.Count != count)
        {
            throw new DevToolsException($"Unexpected argument '{Positionals[count]}' for '{Command}'.");
        }
    }
}
