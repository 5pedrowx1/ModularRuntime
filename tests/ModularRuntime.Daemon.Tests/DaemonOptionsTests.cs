using ModularRuntime.TestSupport;

namespace ModularRuntime.Daemon.Tests;

public sealed class DaemonOptionsTests
{
    [Fact]
    public void Defaults_UseTheProfileLayout()
    {
        using var directory = new TemporaryDirectory("options");

        var (options, error, help) = DaemonOptions.Parse(["--profile", directory.DirectoryPath]);

        Assert.Null(error);
        Assert.False(help);
        Assert.Equal(Path.Combine(directory.DirectoryPath, "plugins"), options!.PluginDirectory);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ShutdownTimeout);
        Assert.False(options.AutoStartPlugins);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--profile")]
    [InlineData("--log-buffer", "5")]
    [InlineData("--shutdown-timeout", "0")]
    [InlineData("--shutdown-timeout", "abc")]
    public void InvalidArguments_AreRejected(params string[] args)
    {
        var (options, error, _) = DaemonOptions.Parse(args);

        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Fact]
    public void ConfigFile_IsApplied_AndArgumentsOverrideIt()
    {
        using var directory = new TemporaryDirectory("options");
        Directory.CreateDirectory(Path.Combine(directory.DirectoryPath, "config"));
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "config", "daemon.json"),
            """{ "pluginDirectory": "custom", "shutdownTimeoutSeconds": 7, "autoStartPlugins": true, /* comment */ }""");

        var fromFile = DaemonOptions.Parse(["--profile", directory.DirectoryPath]).Options!;
        var overridden = DaemonOptions.Parse(["--profile", directory.DirectoryPath, "--shutdown-timeout", "3"]).Options!;

        Assert.Equal(Path.Combine(directory.DirectoryPath, "custom"), fromFile.PluginDirectory);
        Assert.Equal(TimeSpan.FromSeconds(7), fromFile.ShutdownTimeout);
        Assert.True(fromFile.AutoStartPlugins);
        Assert.Equal(TimeSpan.FromSeconds(3), overridden.ShutdownTimeout);
    }

    [Fact]
    public void ConfigFile_WithUnknownProperties_IsRejected()
    {
        using var directory = new TemporaryDirectory("options");
        Directory.CreateDirectory(Path.Combine(directory.DirectoryPath, "config"));
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "config", "daemon.json"), """{ "execute": "calc.exe" }""");

        var (options, error, _) = DaemonOptions.Parse(["--profile", directory.DirectoryPath]);

        Assert.Null(options);
        Assert.Contains("Invalid configuration", error, StringComparison.Ordinal);
    }
}
