using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

/// <summary>
/// v0.1.1 reliability scenarios executed against real collectible load contexts.
/// Each scenario retains every result, info snapshot, and log entry the host received
/// and proves that the plugin load context can still be collected.
/// </summary>
public sealed class PluginReliabilityUnloadTests
{
    private const string StagedPluginName = "ModularRuntime.TestPlugin";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task HostilePluginException_IsCapturedAndDoesNotKeepLoadContextAlive()
    {
        using var temporary = new TemporaryDirectory("unload-hostile");
        var root = Stage(temporary, "com.test.hostile-exception", "ModularRuntime.TestPlugin.HostileExceptionPlugin");
        var logger = new CollectingLogger();

        var retained = await RunAsync(root, logger, static (engine, id) => engine.StartAsync(id));

        Assert.Equal(PluginOperationStatus.Failed, retained.Result.Status);
        Assert.Equal(PluginErrorCodes.InitializationFailed, retained.Result.ErrorCode);
        Assert.Equal("ModularRuntime.TestPlugin.HostilePluginException", retained.Result.Diagnostic!.ExceptionType);
        Assert.Equal(PluginState.Failed, retained.Info!.State);
        AssertCollected(retained);
    }

    [Fact]
    public async Task PluginCancellationCallbackFailure_IsLoggedAndDoesNotKeepLoadContextAlive()
    {
        using var temporary = new TemporaryDirectory("unload-callback");
        var root = Stage(temporary, "com.test.callback-failure", "ModularRuntime.TestPlugin.CancellationCallbackFailurePlugin");
        var logger = new CollectingLogger();

        var retained = await RunAsync(
            root,
            logger,
            static async (engine, id) =>
            {
                Assert.True((await engine.StartAsync(id).ConfigureAwait(false)).IsSuccess);
                return await engine.StopAsync(id).ConfigureAwait(false);
            });

        Assert.True(retained.Result.IsSuccess);
        Assert.Equal(PluginState.Stopped, retained.Info!.State);
        Assert.Contains(
            retained.Logs,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Diagnostic?.ExceptionType == "ModularRuntime.TestPlugin.PluginFailureException");
        AssertCollected(retained);
    }

    [Fact]
    public async Task DisposeDuringInitialization_CancelsPluginAndCollectsLoadContext()
    {
        using var temporary = new TemporaryDirectory("unload-dispose-init");
        var root = Stage(temporary, "com.test.blocking-init", "ModularRuntime.TestPlugin.BlockingInitializationPlugin");
        var logger = new CollectingLogger();

        var retained = await RunDisposeDuringInitializationAsync(root, logger);

        Assert.Equal(PluginOperationStatus.Canceled, retained.Result.Status);
        Assert.Equal(PluginErrorCodes.OperationCanceled, retained.Result.ErrorCode);
        Assert.Equal(PluginState.Failed, retained.Info!.State);
        AssertCollected(retained);
    }

    [Fact]
    public async Task PluginRuntime_StandaloneAndEmbeddedPath_ToleratesThrowingLogger()
    {
        using var temporary = new TemporaryDirectory("runtime-throwing-logger");
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        StagedPlugins.CopyPlugin(root, "HelloPlugin");
        StagedPlugins.CopyPlugin(root, "FailingPlugin");
        var helloId = PluginId.Parse("com.example.hello");
        var failingId = PluginId.Parse("com.example.failing");

        var runtime = new PluginRuntime(new PluginRuntimeOptions
        {
            PluginDirectory = root,
            Logger = new ThrowingLogger(),
        });

        var discovery = await runtime.DiscoverAsync();
        Assert.Equal(2, discovery.RegisteredCount);

        var results = await runtime.StartAllAsync();
        Assert.Contains(results, result => result.PluginId == helloId && result.IsSuccess);
        Assert.Contains(results, result => result.PluginId == failingId && result.Status == PluginOperationStatus.Failed);

        await runtime.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Stopped, runtime.GetPlugin(helloId)!.State);
        Assert.Equal(PluginState.Failed, runtime.GetPlugin(failingId)!.State);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetainedScenario> RunAsync(
        string root,
        CollectingLogger logger,
        Func<PluginEngine, PluginId, Task<PluginOperationResult>> scenario)
    {
        var loader = new PluginLoader(logger);
        var discovery = await loader.DiscoverAsync(root).ConfigureAwait(false);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = new TrackingFactory(loader.CreateInstanceFactory(candidate));
        var engine = new PluginEngine(logger);
        try
        {
            Assert.True(engine.Register(candidate.Descriptor, factory).IsSuccess);
            Assert.True(engine.MarkValidated(candidate.Descriptor.Id).IsSuccess);

            var result = await scenario(engine, candidate.Descriptor.Id).ConfigureAwait(false);
            return new RetainedScenario(
                result,
                engine.GetPlugin(candidate.Descriptor.Id),
                factory.LoadContextReference,
                logger.Entries);
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetainedScenario> RunDisposeDuringInitializationAsync(
        string root,
        CollectingLogger logger)
    {
        var loader = new PluginLoader(logger);
        var discovery = await loader.DiscoverAsync(root).ConfigureAwait(false);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = new TrackingFactory(loader.CreateInstanceFactory(candidate));
        var engine = new PluginEngine(logger);
        var id = candidate.Descriptor.Id;

        Assert.True(engine.Register(candidate.Descriptor, factory).IsSuccess);
        Assert.True(engine.MarkValidated(id).IsSuccess);

        var startTask = engine.StartAsync(id);
        var deadline = DateTime.UtcNow + Timeout;
        while (!logger.HasMessageContaining("waiting for cancellation"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The plugin did not begin initialization.");
            await Task.Delay(10).ConfigureAwait(false);
        }

        await engine.DisposeAsync().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
        var result = await startTask.WaitAsync(Timeout).ConfigureAwait(false);

        return new RetainedScenario(result, engine.GetPlugin(id), factory.LoadContextReference, logger.Entries);
    }

    private static string Stage(TemporaryDirectory temporary, string pluginId, string entryType)
    {
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, StagedPluginName, "scenario");
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest["id"] = pluginId;
        manifest["entryType"] = entryType;
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
        return root;
    }

    private static void AssertCollected(RetainedScenario retained)
    {
        Assert.NotNull(retained.LoadContext);
        Assert.True(
            GcDiagnostics.WaitForCollection(retained.LoadContext),
            "Retained results, info snapshots, and log entries must not keep the plugin load context alive.");
        GC.KeepAlive(retained);
    }

    private sealed record RetainedScenario(
        PluginOperationResult Result,
        PluginRuntimeInfo? Info,
        WeakReference? LoadContext,
        IReadOnlyList<LogEntry> Logs);

    private sealed class ThrowingLogger : IRuntimeLogger
    {
        public void Log(LogEntry entry) =>
            throw new InvalidOperationException("intentional host logger failure");
    }

    private sealed class TrackingFactory : IPluginInstanceFactory
    {
        private readonly AssemblyPluginInstanceFactory _inner;

        public TrackingFactory(AssemblyPluginInstanceFactory inner)
        {
            _inner = inner;
        }

        public WeakReference? LoadContextReference { get; private set; }

        public async ValueTask<IPlugin> CreateAsync(
            PluginDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            var instance = await _inner.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
            LoadContextReference = _inner.CreateLoadContextWeakReference();
            return instance;
        }

        public ValueTask ReleaseAsync() => _inner.ReleaseAsync();
    }
}
