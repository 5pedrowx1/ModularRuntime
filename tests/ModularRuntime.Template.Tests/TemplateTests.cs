using System.Text.Json.Nodes;

namespace ModularRuntime.Template.Tests;

/// <summary>The installed <c>modularruntime-plugin</c> template, used as an external developer would.</summary>
[Collection(ExternalWorkspaceGroup.Name)]
public sealed class TemplateTests(ExternalWorkspace workspace)
{
    public static TheoryData<string> Kinds { get; } = ["basic", "event-publisher", "event-subscriber", "service-provider", "service-consumer", "extension"];

    private static JsonObject Manifest(string project) => JsonNode.Parse(File.ReadAllText(Path.Combine(project, "plugin.json")))!.AsObject();

    [Fact]
    public async Task Template_IsInstalledFromTheLocalPackage()
    {
        var list = await workspace.DotnetAsync(workspace.Work, "new", "list", "modularruntime", "--debug:custom-hive", workspace.Hive);

        list.AssertSuccess("dotnet new list");
        Assert.Contains("modularruntime-plugin", list.Output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryKind_GeneratesAProjectThatBuildsInDebugAndRelease_AndValidates(string kind)
    {
        var name = "Kind" + string.Concat(kind.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        var project = await workspace.NewPluginAsync(name, kind, Path.Combine(workspace.Work, "kinds"));

        Assert.Equal(
            // packages.lock.json is written by the restore that runs after generation (v0.6: the
            // template enables RestorePackagesWithLockFile; official review requires the lock file).
            [".gitignore", name + ".cs", name + ".csproj", "README.md", "packages.lock.json", "plugin.json"],
            Directory.GetFiles(project).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("\"ModularRuntime.Sdk\"", await File.ReadAllTextAsync(Path.Combine(project, "packages.lock.json")), StringComparison.Ordinal);
        var csproj = await File.ReadAllTextAsync(Path.Combine(project, name + ".csproj"));
        Assert.Contains($"<PackageReference Include=\"ModularRuntime.Sdk\" Version=\"{ExternalWorkspace.Version}\" />", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("ModularRuntime.Engine\"", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", csproj, StringComparison.Ordinal);
        Assert.Contains($"public sealed class {name} : IPlugin", await File.ReadAllTextAsync(Path.Combine(project, name + ".cs")), StringComparison.Ordinal);
        var manifest = Manifest(project);
        Assert.Equal("com.example." + name.ToLowerInvariant(), manifest["id"]!.GetValue<string>());
        Assert.Equal($"{name}.{name}", manifest["entryType"]!.GetValue<string>());
        Assert.Equal(name + ".dll", manifest["entryAssembly"]!.GetValue<string>());

        (await workspace.DotnetAsync(project, "build", "-c", "Debug", "-nologo")).AssertSuccess("Debug build");
        (await workspace.DotnetAsync(project, "build", "-c", "Release", "-nologo")).AssertSuccess("Release build");
        var validate = await workspace.ToolAsync(project, "validate", "-c", "Release");
        validate.AssertSuccess("validate");
        Assert.Contains("Valid:", validate.Output, StringComparison.Ordinal);
        Assert.Contains("0 warning(s)", validate.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamesWithSpaces_InPathsWithUnicode_ProduceValidIdentifiers()
    {
        var project = await workspace.NewPluginAsync("My Plugin", "basic", Path.Combine(workspace.Work, "Pasta ção"));

        Assert.True(File.Exists(Path.Combine(project, "My Plugin.csproj")));
        var manifest = Manifest(project);
        Assert.Equal("com.example.my-plugin", manifest["id"]!.GetValue<string>());
        Assert.Equal("My_Plugin.My_Plugin", manifest["entryType"]!.GetValue<string>());
        Assert.Equal("My Plugin", manifest["name"]!.GetValue<string>());

        var validate = await workspace.ToolAsync(project, "validate", "--build");
        validate.AssertSuccess("validate --build");
    }

    [Fact]
    public async Task ExplicitOptions_AreSubstituted()
    {
        var project = await workspace.NewPluginAsync(
            "Reports", "service-provider", Path.Combine(workspace.Work, "options"),
            "--plugin-id", "com.contoso.reports", "--plugin-name", "Contoso Reports", "--plugin-author", "Contoso", "--service-id", "contoso.reports");

        var manifest = Manifest(project);
        Assert.Equal("com.contoso.reports", manifest["id"]!.GetValue<string>());
        Assert.Equal("Contoso Reports", manifest["name"]!.GetValue<string>());
        Assert.Equal("Contoso", manifest["author"]!.GetValue<string>());
        Assert.Contains("ServiceId = \"contoso.reports\"", await File.ReadAllTextAsync(Path.Combine(project, "Reports.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidExplicitPluginId_IsReportedByValidate_WithoutBuilding()
    {
        var project = await workspace.NewPluginAsync("BadId", "basic", Path.Combine(workspace.Work, "bad"), "--plugin-id", "Bad Id!");

        var validate = await workspace.ToolAsync(project, "validate");

        Assert.Equal(1, validate.ExitCode);
        Assert.Contains("[manifest_invalid]", validate.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(project, "bin")));
    }
}
