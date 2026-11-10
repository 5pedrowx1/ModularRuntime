using System.Diagnostics;
using System.Text;

namespace ModularRuntime.DevTools;

/// <summary>Result of a <c>dotnet</c> invocation.</summary>
internal sealed record DotnetResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs the .NET SDK (<c>dotnet build</c>, <c>dotnet msbuild -getProperty</c>, …). The SDK is the
/// only build mechanism: the tool never compiles code itself.
/// </summary>
internal static class DotnetRunner
{
    /// <summary>The dotnet host: the one running this tool when available, otherwise "dotnet" on PATH.</summary>
    public static string HostPath { get; } = ResolveHost();

    public static async Task<DotnetResult> RunAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(HostPath)
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

        // A child build must evaluate with its own SDK resolution, not with MSBuild state inherited
        // from a parent build or test host.
        foreach (var name in startInfo.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase) || k.StartsWith("MSBUILD", StringComparison.Ordinal)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(e.Data);
            }

            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (error)
                {
                    error.AppendLine(e.Data);
                }
            }
        };

        if (!process.Start())
        {
            throw new DevToolsException($"Could not start '{HostPath}'.");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user canceled: stop the build we started (and its MSBuild worker processes).
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
            }

            throw;
        }

        process.WaitForExit(); // flush asynchronous output
        lock (output)
        {
            lock (error)
            {
                return new DotnetResult(process.ExitCode, output.ToString(), error.ToString());
            }
        }
    }

    private static string ResolveHost()
    {
        var fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromCli) && File.Exists(fromCli))
        {
            return fromCli;
        }

        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return "dotnet";
    }
}

/// <summary>An expected failure reported to the user without a stack trace (exit code 1).</summary>
internal sealed class DevToolsException : Exception
{
    public DevToolsException()
    {
    }

    public DevToolsException(string message)
        : base(message)
    {
    }

    public DevToolsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
