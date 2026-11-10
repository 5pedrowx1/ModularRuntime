using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>End-to-end tests of the v0.2 sample plugins through the shared <see cref="PluginRuntime"/>.</summary>
public sealed class PlatformSamplesIntegrationTests
{
    private static readonly string[] AllSamples =
    [
        "HelloPlugin",
        "FailingPlugin",
        "EventPublisherPlugin",
        "EventSubscriberPlugin",
        "ServiceProviderPlugin",
        "ServiceConsumerPlugin",
        "ExtensionContributorPlugin",
        "FailureCleanupPlugin",
    ];

    [Fact]
    public async Task Samples_CommunicateThroughEventsServicesAndExtensions()
    {
        using var temporary = new TemporaryDirectory("v02-samples");
        var root = Stage(temporary, AllSamples);
        var logger = new CollectingLogger();
        await using var runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = root, Logger = logger });
        runtime.Host.Extensions.DeclareExtensionPoint(
            new ExtensionPointDefinition("example.document.exporters", "Document exporters", new Version(1, 0)) { Operations = ["export"] });

        var discovery = await runtime.DiscoverAsync();
        var starts = await runtime.StartAllAsync();

        Assert.Equal(8, discovery.RegisteredCount);
        // Start order comes from the manifests, not from discovery (alphabetical) order.
        Assert.Equal(
            [
                "com.example.failing", "com.example.hello", "com.example.events.subscriber", "com.example.services.provider",
                "com.example.events.publisher", "com.example.services.consumer", "com.example.extensions.contributor",
                "com.example.failure-cleanup",
            ],
            starts.Select(s => s.PluginId.Value));
        Assert.Equal(6, starts.Count(s => s.IsSuccess));

        // Event: publisher -> subscriber, without references between their assemblies.
        Assert.True(logger.HasMessageContaining("EventSubscriberPlugin received 'example.greetings' from com.example.events.publisher"));
        Assert.True(logger.HasMessageContaining("delivered=1, failed=0"));

        // Service: consumer -> provider.
        Assert.True(logger.HasMessageContaining("ServiceConsumerPlugin received: HELLO FROM THE SERVICE CONSUMER"));
        var reverse = await runtime.Host.Services.InvokeAsync(
            "example.text.transform", "reverse", JsonPayload.FromNode(new JsonObject { ["text"] = "abc" }));
        Assert.Equal("cba", reverse.Payload!.GetString("text"));
        var invalid = await runtime.Host.Services.InvokeAsync("example.text.transform", "uppercase", JsonPayload.Parse("[1,2]"));
        Assert.Equal("invalid_payload", invalid.ErrorCode);

        // Extension: host point <- contributor plugin.
        var exports = await runtime.Host.Extensions.InvokeAllAsync(
            "example.document.exporters", "export", JsonPayload.FromNode(new JsonObject { ["title"] = "T", ["body"] = "B" }));
        Assert.True(exports.IsSuccess);
        Assert.Equal("# T\n\nB\n", Assert.Single(exports.Results).Payload!.GetString("content"));

        // Failure cleanup: resources created before the failure are gone.
        var failureCleanup = PluginId.Parse("com.example.failure-cleanup");
        Assert.Equal(PluginState.Failed, runtime.GetPlugin(failureCleanup)!.State);
        Assert.Null(runtime.Host.Services.GetService("example.cleanup.probe"));
        Assert.Empty(runtime.GetResources(failureCleanup));
        Assert.True(logger.HasMessageContaining("FailureCleanupPlugin tracked resource was disposed by the runtime."));
        Assert.DoesNotContain(
            runtime.Host.Events.GetSubscriptions(),
            s => s.Subscriber.PluginId == failureCleanup);

        // Stop: everything owned by plugins disappears; host resources remain until disposal.
        await runtime.StopAllAsync();
        Assert.Empty(runtime.Host.Services.GetServices());
        Assert.Empty(runtime.Host.Events.GetSubscriptions());
        Assert.Empty(runtime.Host.Extensions.GetContributions("example.document.exporters"));
        Assert.NotNull(runtime.Host.Extensions.GetExtensionPoint("example.document.exporters"));
    }

    [Fact]
    public async Task TwoRuntimes_WithTheSamePlugins_DoNotShareEventsServicesOrExtensions()
    {
        using var firstDirectory = new TemporaryDirectory("v02-iso-a");
        using var secondDirectory = new TemporaryDirectory("v02-iso-b");
        string[] samples = ["EventSubscriberPlugin", "ServiceProviderPlugin", "ExtensionContributorPlugin"];
        var firstLogger = new CollectingLogger();
        var secondLogger = new CollectingLogger();
        await using var first = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = Stage(firstDirectory, samples), Logger = firstLogger });
        await using var second = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = Stage(secondDirectory, samples), Logger = secondLogger });
        first.Host.Extensions.DeclareExtensionPoint(new ExtensionPointDefinition("example.document.exporters", "Exporters", new Version(1, 0)));
        await first.DiscoverAsync();
        await second.DiscoverAsync();
        await first.StartAllAsync();
        await second.StartAllAsync();

        // The same service ID is registered once in each runtime without a duplicate conflict.
        Assert.Single(first.Host.Services.GetServices());
        Assert.Single(second.Host.Services.GetServices());
        Assert.NotEqual(first.RuntimeId, second.RuntimeId);

        var published = await first.Host.Events.PublishAsync("example.greetings", JsonPayload.FromNode(new JsonObject { ["text"] = "only-first" }));
        Assert.Equal(1, published.SucceededCount);
        Assert.True(firstLogger.HasMessageContaining("only-first"));
        Assert.False(secondLogger.HasMessageContaining("only-first"));

        Assert.Single(first.Host.Extensions.GetContributions("example.document.exporters"));
        Assert.Empty(second.Host.Extensions.GetExtensionPoints());

        await first.StopAsync(PluginId.Parse("com.example.services.provider"));
        Assert.Empty(first.Host.Services.GetServices());
        var stillThere = await second.Host.Services.InvokeAsync(
            "example.text.transform", "uppercase", JsonPayload.FromNode(new JsonObject { ["text"] = "b" }));
        Assert.Equal("B", stillThere.Payload!.GetString("text"));
    }

    private static string Stage(TemporaryDirectory temporary, IEnumerable<string> samples)
    {
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        foreach (var sample in samples)
        {
            StagedPlugins.CopyPlugin(root, sample);
        }

        return root;
    }
}
