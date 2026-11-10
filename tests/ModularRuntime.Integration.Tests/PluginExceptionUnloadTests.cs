using System.Reflection;
using System.Runtime.CompilerServices;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Integration.Tests;

public sealed class PluginExceptionUnloadTests
{
    private const string StagedPluginName = "ModularRuntime.TestPlugin";
    private const string ExceptionTypeName = "ModularRuntime.TestPlugin.PluginFailureException";

    [Fact]
    public async Task InitializationFailure_RetainedResultAndErrorDoNotKeepLoadContextAlive()
    {
        using var temporary = new TemporaryDirectory("unload-init-failure");
        var root = CreatePluginRoot(temporary);
        StageExceptionPlugin(
            root,
            "com.test.exception-initialization",
            "ModularRuntime.TestPlugin.InitializationFailurePlugin",
            "initialization-failure");
        var logger = new CollectingLogger();

        var retained = await RunInitializationFailureAsync(root, logger);

        Assert.Equal(PluginOperationStatus.Failed, retained.Result.Status);
        AssertDiagnosticType(retained.Result.Diagnostic);
        Assert.NotNull(retained.Info);
        Assert.NotNull(retained.Info.LastError);
        Assert.Contains(ExceptionTypeName, retained.Info.LastError.ExceptionType, StringComparison.Ordinal);
        Assert.NotNull(retained.LoadContext);

        Assert.True(
            GcDiagnostics.WaitForCollection(retained.LoadContext),
            "The plugin load context should be collectible while the host retains the failed result and LastError.");

        GC.KeepAlive(retained.Result);
        GC.KeepAlive(retained.Info);
        GC.KeepAlive(retained.Logs);
    }

    [Fact]
    public async Task PluginLogs_RetainedInMemoryDoNotKeepLoadContextAlive()
    {
        using var temporary = new TemporaryDirectory("unload-logging");
        var root = CreatePluginRoot(temporary);
        StageExceptionPlugin(
            root,
            "com.test.exception-logging",
            "ModularRuntime.TestPlugin.LoggingFailurePlugin",
            "logging-failure");
        var logger = new CollectingLogger();

        var retained = await RunLoggingScenarioAsync(root, logger);

        Assert.True(retained.Result.IsSuccess);
        Assert.Contains(
            retained.Logs,
            entry => entry.Diagnostic is { } diagnostic &&
                     diagnostic.ExceptionType.Contains(ExceptionTypeName, StringComparison.Ordinal));
        Assert.NotNull(retained.LoadContext);

        Assert.True(
            GcDiagnostics.WaitForCollection(retained.LoadContext),
            "A logger that retains entries must not retain plugin exceptions or the plugin load context.");

        GC.KeepAlive(retained.Result);
        GC.KeepAlive(retained.Logs);
    }

    [Fact]
    public async Task ShutdownFailure_RetainedResultAndLogsDoNotKeepLoadContextAlive()
    {
        using var temporary = new TemporaryDirectory("unload-shutdown-failure");
        var root = CreatePluginRoot(temporary);
        StageExceptionPlugin(
            root,
            "com.test.exception-shutdown",
            "ModularRuntime.TestPlugin.ShutdownFailurePlugin",
            "shutdown-failure");
        var logger = new CollectingLogger();

        var retained = await RunShutdownFailureAsync(root, logger);

        Assert.Equal(PluginOperationStatus.Failed, retained.Result.Status);
        Assert.Equal(PluginErrorCodes.ShutdownFailed, retained.Result.ErrorCode);
        AssertDiagnosticType(retained.Result.Diagnostic);
        Assert.NotNull(retained.Info);
        Assert.NotNull(retained.Info.LastError);
        Assert.Contains(ExceptionTypeName, retained.Info.LastError.ExceptionType, StringComparison.Ordinal);
        Assert.NotNull(retained.LoadContext);

        Assert.True(
            GcDiagnostics.WaitForCollection(retained.LoadContext),
            "A retained shutdown failure result must not keep the plugin load context alive.");

        GC.KeepAlive(retained.Result);
        GC.KeepAlive(retained.Info);
        GC.KeepAlive(retained.Logs);
    }

    [Fact]
    public async Task ConstructorFailure_LoadExceptionDoesNotRetainPluginExceptionOrLoadContext()
    {
        using var temporary = new TemporaryDirectory("unload-load-error");
        var root = CreatePluginRoot(temporary);
        StageExceptionPlugin(
            root,
            "com.test.exception-constructor",
            "ModularRuntime.TestPlugin.ConstructorFailurePlugin",
            "constructor-failure");
        var loader = new PluginLoader(new CollectingLogger());
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = loader.CreateInstanceFactory(candidate);

        PluginLoadException? failure = null;
        try
        {
            await factory.CreateAsync(candidate.Descriptor, CancellationToken.None);
        }
        catch (PluginLoadException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Null(failure.InnerException);
        Assert.NotNull(failure.Diagnostic);
        Assert.Equal(ExceptionTypeName, failure.Diagnostic.ExceptionType);

        var weakContext = factory.CreateLoadContextWeakReference();
        await factory.ReleaseAsync();

        Assert.NotNull(weakContext);
        Assert.True(
            GcDiagnostics.WaitForCollection(weakContext),
            "A retained PluginLoadException must not keep the plugin load context alive.");
        GC.KeepAlive(failure);
    }

    [Fact]
    public void PublicResultAndLogContracts_DoNotExposeExceptionProperties()
    {
        AssertNoExceptionProperty(typeof(PluginOperationResult));
        AssertNoExceptionProperty(typeof(LogEntry));
        AssertNoExceptionProperty(typeof(PluginDiscoveryError));
        AssertNoExceptionProperty(typeof(PluginDiagnostic));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetainedScenario> RunInitializationFailureAsync(
        string root,
        CollectingLogger logger)
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

            var result = await engine.StartAsync(candidate.Descriptor.Id).ConfigureAwait(false);
            var info = engine.GetPlugin(candidate.Descriptor.Id);
            var weakContext = factory.LoadContextReference;
            return new RetainedScenario(result, info, weakContext, logger.Entries);
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetainedScenario> RunLoggingScenarioAsync(
        string root,
        CollectingLogger logger)
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
            Assert.True((await engine.StartAsync(candidate.Descriptor.Id).ConfigureAwait(false)).IsSuccess);

            var weakContext = factory.LoadContextReference;
            var result = await engine.StopAsync(candidate.Descriptor.Id).ConfigureAwait(false);
            var info = engine.GetPlugin(candidate.Descriptor.Id);
            return new RetainedScenario(result, info, weakContext, logger.Entries);
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<RetainedScenario> RunShutdownFailureAsync(
        string root,
        CollectingLogger logger)
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
            Assert.True((await engine.StartAsync(candidate.Descriptor.Id).ConfigureAwait(false)).IsSuccess);

            var weakContext = factory.LoadContextReference;
            var result = await engine.StopAsync(candidate.Descriptor.Id).ConfigureAwait(false);
            var info = engine.GetPlugin(candidate.Descriptor.Id);
            return new RetainedScenario(result, info, weakContext, logger.Entries);
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void StageExceptionPlugin(
        string root,
        string pluginId,
        string entryType,
        string directoryName)
    {
        var pluginDirectory = StagedPlugins.CopyPlugin(root, StagedPluginName, directoryName);
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest["id"] = pluginId;
        manifest["entryType"] = entryType;
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
    }

    private static void AssertDiagnosticType(PluginDiagnostic? diagnostic)
    {
        Assert.NotNull(diagnostic);
        Assert.Equal(ExceptionTypeName, diagnostic.ExceptionType);
    }

    private static void AssertNoExceptionProperty(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.False(
                typeof(Exception).IsAssignableFrom(property.PropertyType),
                $"Public property '{type.FullName}.{property.Name}' must not expose an Exception.");
        }
    }

    private static string CreatePluginRoot(TemporaryDirectory temporary)
    {
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record RetainedScenario(
        PluginOperationResult Result,
        PluginRuntimeInfo? Info,
        WeakReference? LoadContext,
        IReadOnlyList<LogEntry> Logs);

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
