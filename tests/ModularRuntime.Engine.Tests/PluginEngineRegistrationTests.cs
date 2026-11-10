using ModularRuntime.Contracts;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Engine.Tests;

public sealed class PluginEngineRegistrationTests
{
    [Fact]
    public async Task Register_ValidPlugin_ReturnsDiscoveredState()
    {
        var logger = new CollectingLogger();
        await using var engine = new PluginEngine(logger);
        var descriptor = TestDescriptors.Create("com.test.registration");
        var factory = new TestPluginFactory(new TestPlugin());

        var result = engine.Register(descriptor, factory);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginOperationStatus.Succeeded, result.Status);
        Assert.Equal(PluginState.Discovered, result.State);
        Assert.Equal(PluginState.Discovered, engine.GetPlugin(descriptor.Id)!.State);
        Assert.True(logger.HasMessageContaining("registered"));
    }

    [Fact]
    public async Task Register_DuplicateIdentifier_IsRejected()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.duplicate");
        var firstFactory = new TestPluginFactory(new TestPlugin());
        var secondFactory = new TestPluginFactory(new TestPlugin());

        var first = engine.Register(descriptor, firstFactory);
        var second = engine.Register(descriptor, secondFactory);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal(PluginOperationStatus.Rejected, second.Status);
        Assert.Equal(PluginErrorCodes.DuplicatePluginId, second.ErrorCode);
        Assert.Equal(PluginState.Discovered, engine.GetPlugin(descriptor.Id)!.State);
    }

    [Fact]
    public async Task Register_IncompatibleApiVersion_IsRejected()
    {
        await using var engine = new PluginEngine();
        var compatible = TestDescriptors.Create("com.test.api-valid");
        var incompatible = new PluginDescriptor(
            PluginId.Parse("com.test.api-invalid"),
            "Incompatible API",
            new Version(1, 0, 0),
            new Version(2, 0),
            "TestPlugin.dll",
            "TestPlugin.TestPlugin");

        var accepted = engine.Register(compatible, new TestPluginFactory(new TestPlugin()));
        var rejected = engine.Register(incompatible, new TestPluginFactory(new TestPlugin()));

        Assert.True(accepted.IsSuccess);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(PluginErrorCodes.ApiVersionIncompatible, rejected.ErrorCode);
        Assert.Null(engine.GetPlugin(incompatible.Id));
    }

    [Fact]
    public async Task UnknownPlugin_OperationIsRejected()
    {
        await using var engine = new PluginEngine();
        var missingId = PluginId.Parse("com.test.missing");

        var start = await engine.StartAsync(missingId);
        var stop = await engine.StopAsync(missingId);

        Assert.Equal(PluginOperationStatus.Rejected, start.Status);
        Assert.Equal(PluginErrorCodes.PluginNotFound, start.ErrorCode);
        Assert.Equal(PluginOperationStatus.Rejected, stop.Status);
        Assert.Equal(PluginErrorCodes.PluginNotFound, stop.ErrorCode);
    }
}
