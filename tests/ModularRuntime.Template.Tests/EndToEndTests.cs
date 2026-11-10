using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModularRuntime.Template.Tests;

/// <summary>
/// Scenarios A–H with generated plugins, distributed packages, the real daemon, and an embedded
/// host application generated outside the repository.
/// </summary>
[Collection(ExternalWorkspaceGroup.Name)]
public sealed partial class EndToEndTests(ExternalWorkspace workspace)
{
    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(30);

    private string Scenario(string name) => Path.Combine(workspace.Work, "scenarios", name);

    private async Task<string> CreateAndPackAsync(string parent, string name, string kind, params string[] options)
    {
        var project = await workspace.NewPluginAsync(name, kind, parent, options);
        var pack = await workspace.ToolAsync(project, "pack");
        pack.AssertSuccess("dotnet modularruntime pack " + name);
        return Assert.Single(Directory.GetFiles(Path.Combine(project, "bin", "Release", "modularruntime"), "*.zip"));
    }

    // A (Hello plugin) + E (lifecycle) + F (security) + H (unload in the daemon).
    [Fact]
    public async Task A_E_F_H_GeneratedPlugin_IsPackedInstalledAndRunInTheDaemon_AsALocalUnverifiedPackage()
    {
        var zip = await CreateAndPackAsync(Scenario("A"), "MyFirstPlugin", "basic");
        const string Id = "com.example.myfirstplugin";

        // F — the local package carries no signature and is never presented as official.
        var inspect = await workspace.ToolAsync(Scenario("A"), "inspect", zip);
        inspect.AssertSuccess("inspect");
        Assert.Contains("Signature:   NONE", inspect.Output, StringComparison.Ordinal);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.DoesNotContain(archive.Entries, e => e.FullName == "integrity.sig");
            Assert.DoesNotContain(archive.Entries, e => e.FullName == "ModularRuntime.Contracts.dll");
            Assert.Contains(archive.Entries, e => e.FullName == "ModularRuntime.Sdk.dll");
        }

        await using var daemon = await DaemonSession.StartAsync(workspace, "A");
        var refused = await daemon.CliAsync("plugins", "install-zip", zip);
        Assert.NotEqual(0, refused.ExitCode); // non-interactive and without --allow-unverified: refused
        Assert.Contains("completed", await daemon.CliOkAsync("plugins", "install-zip", zip, "--allow-unverified"), StringComparison.Ordinal);
        var packages = await daemon.CliOkAsync("plugins", "packages");
        Assert.Contains(Id, packages, StringComparison.Ordinal);
        Assert.Contains("local, unverified", packages, StringComparison.Ordinal);
        Assert.DoesNotContain("official", packages, StringComparison.OrdinalIgnoreCase);

        // A + E — start, stop, start, restart, stop in the same daemon.
        foreach (var command in new[] { "start", "stop", "start", "restart", "stop" })
        {
            Assert.Contains("Succeeded", await daemon.CliOkAsync("plugins", command, Id), StringComparison.Ordinal);
        }

        var logs = daemon.ReadLogs();
        var instances = InstanceStarted().Matches(logs).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(3, instances.Count);
        Assert.Equal(3, instances.Distinct().Count());
        Assert.Contains("registered again for a new start (new instance and load context)", logs, StringComparison.Ordinal);

        // H — removal waits for the previous load contexts to be collected before deleting files.
        Assert.Contains("completed", await daemon.CliOkAsync("plugins", "remove", Id, "--yes"), StringComparison.Ordinal);
        Assert.DoesNotContain("has not unloaded", daemon.ReadLogs(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(daemon.Profile, "plugins", Id)));
    }

    // B — two generated plugins communicate through the event bus.
    [Fact]
    public async Task B_GeneratedPublisherAndSubscriber_CommunicateThroughEvents()
    {
        var parent = Scenario("B");
        var publisher = await CreateAndPackAsync(parent, "GreetingPublisher", "event-publisher", "--topic", "e2e.greetings");
        var subscriber = await CreateAndPackAsync(parent, "GreetingSubscriber", "event-subscriber", "--topic", "e2e.greetings");
        await using var daemon = await DaemonSession.StartAsync(workspace, "B");
        await daemon.CliOkAsync("plugins", "install-zip", publisher, "--allow-unverified");
        await daemon.CliOkAsync("plugins", "install-zip", subscriber, "--allow-unverified");

        await daemon.CliOkAsync("plugins", "start", "com.example.greetingsubscriber");
        await daemon.CliOkAsync("plugins", "start", "com.example.greetingpublisher");

        await daemon.WaitForLogAsync("Received greeting #1 from 'com.example.greetingpublisher': Hello from GreetingPublisher", LogTimeout);
        await daemon.WaitForLogAsync("to 1 subscriber(s), 0 failed", LogTimeout);
        await daemon.CliOkAsync("plugins", "stop", "com.example.greetingpublisher");
        await daemon.CliOkAsync("plugins", "stop", "com.example.greetingsubscriber");
    }

    // C — a generated plugin provides a service, another calls it without referencing it.
    [Fact]
    public async Task C_GeneratedServiceProviderAndConsumer_CommunicateThroughTheServiceRegistry()
    {
        var parent = Scenario("C");
        var provider = await CreateAndPackAsync(parent, "TextProvider", "service-provider", "--service-id", "e2e.text");
        var consumer = await CreateAndPackAsync(parent, "TextConsumer", "service-consumer", "--service-id", "e2e.text");
        await using var daemon = await DaemonSession.StartAsync(workspace, "C");
        await daemon.CliOkAsync("plugins", "install-zip", provider, "--allow-unverified");
        await daemon.CliOkAsync("plugins", "install-zip", consumer, "--allow-unverified");

        // The consumer starts first: the missing service is a handled failure, not a crash.
        await daemon.CliOkAsync("plugins", "start", "com.example.textconsumer");
        await daemon.WaitForLogAsync("Service 'e2e.text' call failed [service_not_found]", LogTimeout);
        await daemon.CliOkAsync("plugins", "start", "com.example.textprovider");
        await daemon.WaitForLogAsync("Service 'e2e.text' 1.0 answered: HELLO FROM COM.EXAMPLE.TEXTCONSUMER", LogTimeout);

        // Stopping the provider removes the service; the consumer keeps running and reports it.
        await daemon.CliOkAsync("plugins", "stop", "com.example.textprovider");
        var before = CountOf(daemon.ReadLogs(), "call failed [service_not_found]");
        await WaitUntilAsync(() => CountOf(daemon.ReadLogs(), "call failed [service_not_found]") > before, LogTimeout);
        Assert.Contains("Running", await daemon.CliOkAsync("plugins", "list"), StringComparison.Ordinal);
        await daemon.CliOkAsync("plugins", "stop", "com.example.textconsumer");
    }

    // D (extension points) + G (embedded) + H (unload in an embedded host).
    [Fact]
    public async Task D_G_H_EmbeddedHost_DeclaresAnExtensionPoint_AndRunsAGeneratedContribution()
    {
        var parent = Scenario("D");
        var zip = await CreateAndPackAsync(parent, "UpperFormatter", "extension", "--extension-point", "e2e.formatters");
        const string Id = "com.example.upperformatter";
        var plugins = Path.Combine(parent, "host-plugins");
        ZipFile.ExtractToDirectory(zip, Path.Combine(plugins, Id));

        var host = Path.Combine(parent, "EmbeddedHost");
        Directory.CreateDirectory(host);
        await File.WriteAllTextAsync(Path.Combine(host, "EmbeddedHost.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="ModularRuntime.Loader" Version="{ExternalWorkspace.Version}" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(host, "Program.cs"), """"
            using ModularRuntime.Contracts;
            using ModularRuntime.Loader;

            var pluginId = PluginId.Parse(args[1]);
            await using var runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = args[0], Logger = new ConsoleLogger() });
            await using var point = runtime.Host.Extensions.DeclareExtensionPoint(
                new ExtensionPointDefinition("e2e.formatters", "Formatters", new Version(1, 0)) { Operations = ["format"] });
            Console.WriteLine($"DISCOVERED {(await runtime.DiscoverAsync()).RegisteredCount}");

            for (var run = 1; run <= 2; run++)
            {
                var start = await runtime.StartAsync(pluginId);
                Console.WriteLine($"START {run} {start.Status} {runtime.GetPlugin(pluginId)?.InstanceId}");
                var results = await runtime.Host.Extensions.InvokeAllAsync("e2e.formatters", "format", JsonPayload.Parse("""{"text":"hello"}"""));
                foreach (var result in results.Results)
                {
                    Console.WriteLine($"RESULT {run} {result.Status} {result.Payload}");
                }

                Console.WriteLine($"STOP {run} {(await runtime.StopAsync(pluginId)).Status}");
                Console.WriteLine($"CONTRIBUTIONS {run} {runtime.Host.Extensions.GetContributions("e2e.formatters").Count}");
            }

            await runtime.StartAsync(pluginId);
            Console.WriteLine($"RESTART {(await runtime.RestartAsync(pluginId)).Status}");
            await runtime.StopAsync(pluginId);
            Console.WriteLine($"UNREGISTER {(await runtime.UnregisterAsync(pluginId)).Status}");
            var unloaded = await runtime.WaitForUnloadAsync(pluginId, TimeSpan.FromSeconds(30), PluginUnloadWait.InduceCollection);
            Console.WriteLine($"UNLOADED {unloaded} PENDING {runtime.GetPendingUnloadCount(pluginId)}");

            sealed class ConsoleLogger : IRuntimeLogger
            {
                public void Log(LogEntry entry) => Console.WriteLine($"LOG {entry.Level} {entry.PluginId} {entry.Message}");
            }
            """");

        (await workspace.DotnetAsync(host, "build", "-c", "Release", "-nologo")).AssertSuccess("build the embedded host");
        var run = await workspace.DotnetAsync(host, Path.Combine(host, "bin", "Release", "net10.0", "EmbeddedHost.dll"), plugins, Id);

        run.AssertSuccess("run the embedded host");
        Assert.True(run.Output.Contains("UNLOADED True PENDING 0", StringComparison.Ordinal), run.Output);
        Assert.Contains("DISCOVERED 1", run.Output, StringComparison.Ordinal);
        Assert.Contains("""RESULT 1 Succeeded {"text":"[HELLO]"}""", run.Output, StringComparison.Ordinal);
        Assert.Contains("""RESULT 2 Succeeded {"text":"[HELLO]"}""", run.Output, StringComparison.Ordinal);
        Assert.Contains("CONTRIBUTIONS 1 0", run.Output, StringComparison.Ordinal); // removed with its owner
        Assert.Contains("RESTART Succeeded", run.Output, StringComparison.Ordinal);
        Assert.Contains("UNLOADED True PENDING 0", run.Output, StringComparison.Ordinal);
        var instances = HostStarted().Matches(run.Output).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, instances.Distinct().Count());
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(250);
        }
    }

    [GeneratedRegex(@"MyFirstPlugin 1\.0\.0 started \(plugin 'com\.example\.myfirstplugin', instance ([0-9a-f]{32})\)")]
    private static partial Regex InstanceStarted();

    [GeneratedRegex(@"START \d Succeeded ([0-9a-f-]{36})")]
    private static partial Regex HostStarted();
}
