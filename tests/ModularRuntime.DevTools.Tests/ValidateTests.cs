using ModularRuntime.Contracts;

namespace ModularRuntime.DevTools.Tests;

/// <summary><c>dotnet modularruntime validate</c> against real projects.</summary>
public sealed class ValidateTests : IDisposable
{
    private readonly PluginProjectFixture _fixture = new("devtools-validate");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ValidProject_IsValid_BeforeAndAfterABuild()
    {
        var project = _fixture.CreatePlugin();

        var before = await PluginProjectFixture.RunAsync("validate", "--project", project);
        Assert.Equal(0, before.ExitCode);
        Assert.Contains("[not_built]", before.Output, StringComparison.Ordinal);

        var after = await PluginProjectFixture.RunAsync("validate", "--project", project, "--build");
        Assert.True(after.ExitCode == 0, after.All);
        Assert.Contains("Valid: test.sampleplugin 1.0.0 (plugin API " + PluginApiVersion.Supported.ToString(2), after.Output, StringComparison.Ordinal);
        Assert.Contains("metadata only", after.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingManifest_IsAnError()
    {
        var project = _fixture.CreatePlugin(writeManifest: false);

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[manifest_missing]", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedManifest_IsReportedClearly()
    {
        var project = _fixture.CreatePlugin();
        File.WriteAllText(Path.Combine(project, "plugin.json"), "{ \"id\": ");

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[manifest_invalid] [manifest_json_invalid]", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bad ID")]
    [InlineData("com..double")]
    [InlineData("-leading")]
    [InlineData("UPPER.case")]
    public async Task InvalidPluginId_IsRejected(string id)
    {
        var project = _fixture.CreatePlugin(editManifest: m => m["id"] = id);

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[manifest_invalid]", run.Output, StringComparison.Ordinal);
        Assert.Contains("id", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompatibleApiVersion_IsRejected()
    {
        var project = _fixture.CreatePlugin(editManifest: m => m["apiVersion"] = "0.1");

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[api_version_incompatible]", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryAssemblyThatTheProjectDoesNotBuild_IsAnError()
    {
        var project = _fixture.CreatePlugin(editManifest: m => m["entryAssembly"] = "Other.dll");

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[entry_assembly_name_mismatch]", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsafeEntryAssemblyPath_IsAnError()
    {
        var project = _fixture.CreatePlugin(editManifest: m => m["entryAssembly"] = "../outside.dll");

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[entry_assembly_path_invalid]", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryTypeThatDoesNotExistOrIsNotAPlugin_IsFoundFromMetadata()
    {
        var missing = _fixture.CreatePlugin("MissingType", editManifest: m => m["entryType"] = "MissingType.Nope");
        var notPlugin = _fixture.CreatePlugin("NotPlugin", source: """
            namespace NotPlugin;
            public sealed class NotPlugin { }
            public static class Uses { public static System.Type T => typeof(ModularRuntime.Contracts.IPlugin); }
            """);

        var first = await PluginProjectFixture.RunAsync("validate", "--project", missing, "--build");
        var second = await PluginProjectFixture.RunAsync("validate", "--project", notPlugin, "--build");

        Assert.Equal(1, first.ExitCode);
        Assert.Contains("[entry_type_not_found]", first.Output, StringComparison.Ordinal);
        Assert.Equal(1, second.ExitCode);
        Assert.Contains("[entry_type_not_a_plugin]", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRuntimeDependency_IsAnError()
    {
        var helper = Path.Combine(_fixture.Root, "Helper");
        Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(helper, "Helper.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(helper, "Text.cs"), "namespace Helper; public static class Text { public static string Hello => \"hello\"; }");
        var project = _fixture.CreatePlugin("WithHelper", extraProjectItems: $"<ProjectReference Include=\"{Path.Combine(helper, "Helper.csproj")}\" />", source: """
            using ModularRuntime.Contracts;
            namespace WithHelper;
            public sealed class WithHelper : IPlugin
            {
                public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) { context.Logger.LogInformation(Helper.Text.Hello); return Task.CompletedTask; }
                public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);
        Assert.Equal(0, (await PluginProjectFixture.RunAsync("validate", "--project", project, "--build")).ExitCode);
        File.Delete(Path.Combine(project, "bin", "Debug", "net10.0", "Helper.dll"));

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[dependency_missing] Runtime dependency 'Helper.dll'", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferencingHostAssemblies_IsAnError()
    {
        var project = _fixture.CreatePlugin(extraProjectItems: $"<Reference Include=\"ModularRuntime.Engine\"><HintPath>{Path.Combine(_fixture.LibDirectory, "ModularRuntime.Engine.dll")}</HintPath></Reference>");

        var run = await PluginProjectFixture.RunAsync("validate", "--project", project);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[host_assembly_referenced]", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationAndInspection_NeverRunPluginCode()
    {
        var marker = Path.Combine(_fixture.Root, "code-ran.txt");
        var project = _fixture.CreatePlugin("Booby", source: $$"""
            using System.Runtime.CompilerServices;
            using ModularRuntime.Contracts;
            namespace Booby;
            internal static class Init
            {
                [ModuleInitializer]
                internal static void Run() => File.WriteAllText(@"{{marker}}", "module");
            }
            public sealed class Booby : IPlugin
            {
                static Booby() => File.WriteAllText(@"{{marker}}", "static");
                public Booby() => File.WriteAllText(@"{{marker}}", "ctor");
                public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;
                public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        Assert.Equal(0, (await PluginProjectFixture.RunAsync("validate", "--project", project, "--build")).ExitCode);
        var pack = await PluginProjectFixture.RunAsync("pack", "--project", project, "-o", Path.Combine(_fixture.Root, "out"));
        Assert.True(pack.ExitCode == 0, pack.All);
        Assert.Equal(0, (await PluginProjectFixture.RunAsync("inspect", Directory.GetFiles(Path.Combine(_fixture.Root, "out"))[0])).ExitCode);

        Assert.False(File.Exists(marker), "No plugin code may run during validation, packing, or inspection.");
    }

    [Fact]
    public async Task UsageErrors_HaveExitCode2_AndHelpAndVersionWork()
    {
        Assert.Equal(2, (await PluginProjectFixture.RunAsync()).ExitCode);
        var unknown = await PluginProjectFixture.RunAsync("frobnicate");
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("Unknown command 'frobnicate'", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(2, (await PluginProjectFixture.RunAsync("pack", "--bogus")).ExitCode);
        Assert.Equal(2, (await PluginProjectFixture.RunAsync("pack", "--output")).ExitCode);

        var help = await PluginProjectFixture.RunAsync("--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("prepare-submission", help.Output, StringComparison.Ordinal);
        Assert.Equal("0.6.0", (await PluginProjectFixture.RunAsync("--version")).Output.Trim());

        var noProject = await PluginProjectFixture.RunAsync("validate", "--project", Path.Combine(_fixture.Root, "nowhere"));
        Assert.Equal(1, noProject.ExitCode);
        Assert.Contains("does not exist", noProject.Error, StringComparison.Ordinal);
    }
}
