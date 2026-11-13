using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>
/// End-to-end run of the plugin API 0.4 sample plugins (typed service, binary service, streams,
/// and a consumer that uses all three) through the shared <see cref="PluginRuntime"/>.
/// </summary>
public sealed class CommunicationSamplesIntegrationTests
{
    private static readonly string[] Samples =
    [
        "TypedProviderPlugin",
        "TypedConsumerPlugin",
        "BinaryProviderPlugin",
        "StreamPublisherPlugin",
        "StreamSubscriberPlugin",
    ];

    [Fact]
    public async Task Samples_CommunicateThroughTypedServicesBinaryServicesAndStreams()
    {
        using var temporary = new TemporaryDirectory("v04-samples");
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        foreach (var sample in Samples)
        {
            StagedPlugins.CopyPlugin(root, sample);
        }

        var logger = new CollectingLogger();
        await using var runtime = new PluginRuntime(new PluginRuntimeOptions { PluginDirectory = root, Logger = logger });

        var discovery = await runtime.DiscoverAsync();
        Assert.Empty(discovery.Discovery.Errors);
        Assert.Equal(5, discovery.RegisteredCount);
        var starts = await runtime.StartAllAsync();
        Assert.All(starts, result => Assert.True(result.IsSuccess, $"{result.PluginId}: {result.Message}"));

        // Typed service: provider <- consumer through the shared contract, one identity.
        Assert.True(logger.HasMessageContaining("TypedConsumerPlugin recorded 3 sample(s); summary: count=3, min=0.42, max=512."), "typed calls; log: " + string.Join(" | ", logger.Entries.Where(e => e.Message.Contains("TypedConsumerPlugin", StringComparison.Ordinal)).Select(e => e.Message)));
        Assert.True(logger.HasMessageContaining("provider refused a sample as expected"), "contract exception crosses unchanged");
        var typed = Assert.Single(runtime.Host.Services.GetServices(), s => s.ServiceId == "example.telemetry");
        Assert.Equal(ServiceKind.Typed, typed.Kind);
        Assert.StartsWith("Example.Telemetry.ITelemetryService, Example.Telemetry.Contracts, Version=1.0.0.0", typed.ContractTypeName, StringComparison.Ordinal);
        var contract = Assert.Single(runtime.GetSharedContracts());
        Assert.Equal("Example.Telemetry.Contracts", contract.Name);
        Assert.Equal(2, contract.UserCount);

        // Binary service: the host and the consumer both call it.
        Assert.True(logger.HasMessageContaining("crc32(\"hello\") = 3610A686"), "binary call from the consumer");
        using (var reversed = await runtime.Host.Services.InvokeBinaryAsync("example.checksum", "reverse", "abc"u8.ToArray()))
        {
            Assert.True(reversed.IsSuccess, reversed.Message);
            Assert.Equal("cba"u8.ToArray(), reversed.Payload.ToArray());
        }

        // Streams: the publisher keeps writing; the subscriber reads with and without backpressure.
        await WaitForAsync(() => logger.HasMessageContaining("StreamSubscriberPlugin read tick #10"));
        Assert.Equal(3, runtime.Host.Streams.GetStreams().Count);
        var raw = runtime.Host.Streams.GetStream("example.ticks.raw")!;
        Assert.Equal(1, raw.SubscriberCount);

        // Stopping the publisher first (StopAll would stop subscribers first, in reverse start order)
        // completes the subscriber's streams with stream_removed while the subscriber keeps running.
        Assert.True((await runtime.StopAsync(PluginId.Parse("com.example.streams.publisher"))).IsSuccess);
        await WaitForAsync(() => logger.HasMessageContaining("completed (stream_removed)"));
        Assert.Equal(PluginState.Running, runtime.GetPlugin(PluginId.Parse("com.example.streams.subscriber"))!.State);

        // The consumer streamed the telemetry samples written by the provider.
        var stops = await runtime.StopAllAsync();
        Assert.All(stops.Where(r => r.PluginId.Value != "com.example.streams.publisher"), result => Assert.True(result.IsSuccess, $"{result.PluginId}: {result.Message}"));
        Assert.True(logger.HasMessageContaining("TypedConsumerPlugin shut down after streaming 3 sample(s)."), "typed stream delivery");
        Assert.Empty(runtime.GetSharedContracts());
        Assert.Empty(runtime.Host.Streams.GetStreams());
        Assert.Empty(runtime.Host.Services.GetServices());

        foreach (var plugin in runtime.Plugins)
        {
            Assert.True(await runtime.WaitForUnloadAsync(plugin.Descriptor.Id, TimeSpan.FromSeconds(15), PluginUnloadWait.InduceCollection), $"{plugin.Descriptor.Id} did not unload");
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }
}
