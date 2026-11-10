using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntime.Loader;

// Embedded usage of ModularRuntime: an ordinary .NET application composes the same
// PluginRuntime used by the standalone host, and uses the extensibility platform through
// its host scope (runtime.Host). It never references plugin assemblies.

const string PluginDirectoryEnvironmentVariable = "MODULARRUNTIME_PLUGINS";
const string GreetingsTopic = "example.greetings";
const string TextServiceId = "example.text.transform";
const string ExportersPointId = "example.document.exporters";

string? pluginDirectory = null;
for (var index = 0; index < args.Length; index++)
{
    var argument = args[index];
    if (string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase))
    {
        PrintUsage();
        return 0;
    }

    if (string.Equals(argument, "--plugins", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "-p", StringComparison.OrdinalIgnoreCase))
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Option '{argument}' requires a directory.");
            PrintUsage();
            return 2;
        }

        pluginDirectory = args[++index];
        continue;
    }

    if (argument.StartsWith("--plugins=", StringComparison.OrdinalIgnoreCase))
    {
        pluginDirectory = argument["--plugins=".Length..];
        continue;
    }

    Console.Error.WriteLine($"Unknown argument '{argument}'.");
    PrintUsage();
    return 2;
}

pluginDirectory ??= Environment.GetEnvironmentVariable(PluginDirectoryEnvironmentVariable);
pluginDirectory ??= Path.Combine(Environment.CurrentDirectory, "plugins");

try
{
    pluginDirectory = Path.GetFullPath(pluginDirectory);
}
catch (Exception exception) when (
    exception is ArgumentException or NotSupportedException or PathTooLongException)
{
    Console.Error.WriteLine($"Plugin directory '{pluginDirectory}' is invalid: {exception.Message}");
    return 2;
}

Console.WriteLine("ModularRuntime embedded example (v0.2)");
Console.WriteLine($"Plugin directory: {pluginDirectory}");

// 1-2. Create an independent runtime instance for the configured plugin folder.
var runtime = new PluginRuntime(new PluginRuntimeOptions
{
    PluginDirectory = pluginDirectory,
    Logger = new ConsoleLogger(),
});

var failures = 0;
try
{
    var host = runtime.Host;

    // The host contributes its own functionality before plugins start.
    Step("Host declares the extension point, a host service, and a subscription");
    host.Extensions.DeclareExtensionPoint(
        new ExtensionPointDefinition(ExportersPointId, "Document exporters", new Version(1, 0))
        {
            Operations = ["export"],
            Metadata = new Dictionary<string, string> { ["input"] = "{\"title\": string, \"body\": string}" },
        });
    host.Services.Register(
        new PluginServiceDefinition("host.clock", "Host clock", new Version(1, 0), ["now"]),
        (_, _) => ValueTask.FromResult(PluginResponse.Success(
            JsonPayload.FromNode(new JsonObject { ["utc"] = DateTimeOffset.UtcNow.ToString("O") }))));
    host.Events.Subscribe(GreetingsTopic, (pluginEvent, _) =>
    {
        Console.WriteLine($"  [host subscriber] '{pluginEvent.Topic}' from {pluginEvent.Source}: {pluginEvent.Payload.Json}");
        return ValueTask.CompletedTask;
    });

    // 3. Discover plugins.
    Step("Discover plugins");
    var discovery = await runtime.DiscoverAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"  Discovered {discovery.Discovery.Plugins.Count} valid plugin installation(s); registered {discovery.RegisteredCount}.");
    foreach (var error in discovery.Discovery.Errors)
    {
        Console.WriteLine($"  discovery error [{error.Code}] {error.Message}");
    }

    if (discovery.RegisteredCount == 0)
    {
        Console.Error.WriteLine(
            "No plugins are registered. Prepare the samples with './scripts/prepare-plugins.ps1' " +
            "or pass --plugins <directory>.");
        return 1;
    }

    // 4. Start plugins in manifest start order (subscribers and providers before publishers and consumers).
    Step("Start all plugins (ordered by startOrder)");
    foreach (var result in await runtime.StartAllAsync().ConfigureAwait(false))
    {
        Console.WriteLine($"  start {result.PluginId,-36} {result.Status,-9} {result.ErrorCode}");
    }

    // 5. Publish an event from the host.
    Step("Publish an event from the host");
    var publish = await host.Events.PublishAsync(
        GreetingsTopic,
        JsonPayload.FromNode(new JsonObject { ["text"] = "Hello from the embedding application" })).ConfigureAwait(false);
    Console.WriteLine($"  publish status={publish.Status} delivered={publish.SucceededCount} failed={publish.FailedCount}");
    failures += publish.IsSuccess ? 0 : 1;

    // 6. Query services.
    Step("Query services");
    foreach (var service in host.Services.GetServices())
    {
        Console.WriteLine($"  {service.ServiceId,-28} {service.Version,-6} ops=[{string.Join(", ", service.Operations)}] owner={service.Owner}");
    }

    // 7. Invoke a plugin service.
    Step($"Invoke '{TextServiceId}'");
    var invocation = await host.Services.InvokeAsync(
        TextServiceId,
        "reverse",
        JsonPayload.FromNode(new JsonObject { ["text"] = "ModularRuntime" }),
        new ServiceInvocationOptions { MinimumVersion = new Version(1, 0) }).ConfigureAwait(false);
    Console.WriteLine(invocation.IsSuccess
        ? $"  reverse -> {invocation.Payload!.GetString("text")}"
        : $"  invocation {invocation.Status} [{invocation.ErrorCode}] {invocation.Message}");
    failures += invocation.IsSuccess ? 0 : 1;

    // 8-9. Discover contributions to the host's extension point.
    Step($"Discover contributions to '{ExportersPointId}'");
    var contributions = host.Extensions.GetContributions(ExportersPointId);
    foreach (var contribution in contributions)
    {
        Console.WriteLine($"  {contribution.ContributionId} ({contribution.DisplayName}) by {contribution.Owner}");
    }

    if (contributions.Count == 0)
    {
        Console.WriteLine("  (no contributions)");
        failures++;
    }

    // 10. Invoke every contribution.
    Step("Invoke all exporters");
    var exports = await host.Extensions.InvokeAllAsync(
        ExportersPointId,
        "export",
        JsonPayload.FromNode(new JsonObject { ["title"] = "Release notes", ["body"] = "ModularRuntime 0.2 is extensible." })).ConfigureAwait(false);
    foreach (var export in exports.Results)
    {
        Console.WriteLine(export.IsSuccess
            ? $"  {export.TargetId}: {export.Payload!.Json}"
            : $"  {export.TargetId}: {export.Status} [{export.ErrorCode}] {export.Message}");
    }

    failures += exports.IsSuccess ? 0 : 1;

    Step("Resources owned by each plugin");
    foreach (var plugin in runtime.Plugins)
    {
        var resources = runtime.GetResources(plugin.Descriptor.Id);
        Console.WriteLine($"  {plugin.Descriptor.Id,-36} {plugin.State,-8} resources={resources.Count}");
    }

    Console.WriteLine(
        $"  'example.cleanup.probe' registered after FailureCleanupPlugin failed: {host.Services.GetService("example.cleanup.probe") is not null}");

    // 11. Stop and release.
    Step("Stop all plugins");
    foreach (var result in await runtime.StopAllAsync().ConfigureAwait(false))
    {
        if (result.ErrorCode != PluginErrorCodes.InvalidState)
        {
            Console.WriteLine($"  stop {result.PluginId,-36} {result.Status}");
        }
    }

    Console.WriteLine($"  services after stop: {string.Join(", ", host.Services.GetServices().Select(s => s.ServiceId))}");
    Console.WriteLine($"  contributions after stop: {host.Extensions.GetContributions(ExportersPointId).Count}");
    Console.WriteLine($"  subscriptions after stop: {host.Events.GetSubscriptions().Count} (host only)");
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine(
        $"Embedded example failed ({exception.GetType().FullName}): {exception.Message}");
    return 1;
}
finally
{
    Step("Dispose the runtime (releases host resources)");
    await runtime.DisposeAsync().ConfigureAwait(false);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "Embedded example completed successfully." : $"Embedded example completed with {failures} failed step(s).");
return failures == 0 ? 0 : 1;

static void Step(string title)
{
    Console.WriteLine();
    Console.WriteLine($"== {title}");
}

static void PrintUsage()
{
    Console.WriteLine("ModularRuntime embedded example");
    Console.WriteLine("Usage: EmbeddedExample [--plugins <directory>] [--help]");
    Console.WriteLine();
    Console.WriteLine("Declares a host extension point and service, discovers and starts plugins, publishes an");
    Console.WriteLine("event, invokes a plugin service, invokes extension contributions, and shuts down.");
    Console.WriteLine();
    Console.WriteLine(
        $"Plugin directory precedence: --plugins, then " +
        $"{PluginDirectoryEnvironmentVariable}, then <current directory>/plugins.");
}

internal sealed class ConsoleLogger : IRuntimeLogger
{
    private readonly object _sync = new();

    public void Log(LogEntry entry)
    {
        if (entry.Level < LogLevel.Information)
        {
            return;
        }

        var scope = entry.PluginId is { } pluginId
            ? $"[plugin {pluginId}]"
            : "[runtime]";

        lock (_sync)
        {
            Console.WriteLine($"  [{entry.Level,-11}] {scope} {entry.Message}");
        }
    }
}
