using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

/// <summary>v0.4: removing inactive plugins so installations can be updated or removed without restarting.</summary>
public sealed class PluginEngineUnregisterTests
{
    [Fact]
    public async Task ValidatedPlugin_IsUnregistered_AndItsFactoryReleased()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.unregister");
        var factory = new TestPluginFactory(new TestPlugin());
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        var version = engine.StateVersion;

        var result = await engine.UnregisterAsync(descriptor.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(engine.GetPlugin(descriptor.Id));
        Assert.Equal(1, factory.ReleaseCount);
        Assert.True(engine.StateVersion > version);
    }

    [Fact]
    public async Task StoppedPlugin_CanBeUnregisteredAndRegisteredAgain_AndStartedWithTheNewFactory()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.reregister");
        engine.Register(descriptor, new TestPluginFactory(new TestPlugin()));
        engine.MarkValidated(descriptor.Id);
        Assert.True((await engine.StartAsync(descriptor.Id)).IsSuccess);
        Assert.True((await engine.StopAsync(descriptor.Id)).IsSuccess);

        Assert.True((await engine.UnregisterAsync(descriptor.Id)).IsSuccess);
        var second = new TestPlugin();
        Assert.True(engine.Register(descriptor, new TestPluginFactory(second)).IsSuccess);
        engine.MarkValidated(descriptor.Id);

        Assert.True((await engine.StartAsync(descriptor.Id)).IsSuccess);
        Assert.Equal(1, second.InitializeCount);
    }

    [Fact]
    public async Task RunningPlugin_CannotBeUnregistered()
    {
        await using var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.running");
        var factory = new TestPluginFactory(new TestPlugin());
        engine.Register(descriptor, factory);
        engine.MarkValidated(descriptor.Id);
        await engine.StartAsync(descriptor.Id);

        var result = await engine.UnregisterAsync(descriptor.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginErrorCodes.InvalidState, result.ErrorCode);
        Assert.Equal(PluginState.Running, engine.GetPlugin(descriptor.Id)!.State);
        Assert.Equal(0, factory.ReleaseCount);
    }

    [Fact]
    public async Task UnknownPlugin_IsNotFound()
    {
        await using var engine = new PluginEngine();

        var result = await engine.UnregisterAsync(PluginId.Parse("com.test.missing"));

        Assert.Equal(PluginErrorCodes.PluginNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task StartRacingWithUnregister_SeesEitherARunningPluginOrNotFound()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var engine = new PluginEngine();
            var descriptor = TestDescriptors.Create("com.test.race");
            engine.Register(descriptor, new TestPluginFactory(new TestPlugin()));
            engine.MarkValidated(descriptor.Id);

            var start = Task.Run(() => engine.StartAsync(descriptor.Id));
            var unregister = Task.Run(() => engine.UnregisterAsync(descriptor.Id));
            var (started, removed) = (await start, await unregister);

            if (removed.IsSuccess)
            {
                Assert.False(started.IsSuccess);
                Assert.Equal(PluginErrorCodes.PluginNotFound, started.ErrorCode);
                Assert.Null(engine.GetPlugin(descriptor.Id));
            }
            else
            {
                Assert.True(started.IsSuccess);
                Assert.Equal(PluginState.Running, engine.GetPlugin(descriptor.Id)!.State);
            }
        }
    }

    [Fact]
    public async Task UnregisterAfterShutdownBegan_IsRejected()
    {
        var engine = new PluginEngine();
        var descriptor = TestDescriptors.Create("com.test.shutdown");
        engine.Register(descriptor, new TestPluginFactory(new TestPlugin()));
        await engine.DisposeAsync();

        Assert.Equal(PluginErrorCodes.EngineDisposed, (await engine.UnregisterAsync(descriptor.Id)).ErrorCode);
    }
}
