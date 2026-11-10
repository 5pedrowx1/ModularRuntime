using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ModularRuntime.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Loader.Tests;

public sealed class PluginDiscoveryTests
{
    [Fact]
    public async Task Discover_ValidManifest_ReturnsCandidate()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var loader = new PluginLoader(new CollectingLogger());

        var result = await loader.DiscoverAsync(root);

        Assert.False(result.HasErrors);
        var candidate = Assert.Single(result.Plugins);
        Assert.Equal("com.example.hello", candidate.Descriptor.Id.Value);
        Assert.Equal("Hello Plugin", candidate.Descriptor.Name);
        Assert.Equal(new Version(1, 0, 0), candidate.Descriptor.Version);
        Assert.Equal(PluginApiVersion.Supported, candidate.Descriptor.ApiVersion);
        Assert.Equal("HelloPlugin.HelloPlugin", candidate.Descriptor.EntryType);
        Assert.Equal(Path.Combine(pluginDirectory, "HelloPlugin.dll"), candidate.EntryAssemblyPath);
        Assert.Equal(Path.Combine(pluginDirectory, "plugin.json"), candidate.ManifestPath);
    }

    [Fact]
    public async Task Discover_InvalidJson_ReportsManifestJsonInvalid()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        StagedPlugins.WriteRawManifest(pluginDirectory, "{ this is not json }");
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestJsonInvalid, error.Code);
        Assert.Equal(Path.Combine(pluginDirectory, "plugin.json"), error.ManifestPath);
    }

    [Fact]
    public async Task Discover_UnknownManifestField_IsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest["unexpectedField"] = true;
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestJsonInvalid, error.Code);
    }

    [Fact]
    public async Task Discover_InvalidPluginId_IsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest["id"] = "Invalid ID";
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestFieldInvalid, error.Code);
        Assert.Equal("id", error.Field);
    }

    [Fact]
    public async Task Discover_MissingRequiredField_ReportsFieldMissing()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest.Remove("entryType");
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestFieldMissing, error.Code);
        Assert.Equal("entryType", error.Field);
    }

    [Fact]
    public async Task Discover_ApiVersionIncompatible_IsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest["apiVersion"] = "2.0";
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.ApiVersionIncompatible, error.Code);
        Assert.Equal("apiVersion", error.Field);
    }

    [Fact]
    public async Task Discover_MissingEntryAssembly_IsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        File.Delete(Path.Combine(pluginDirectory, "HelloPlugin.dll"));
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.EntryAssemblyMissing, error.Code);
        Assert.Equal("entryAssembly", error.Field);
    }

    [Fact]
    public async Task Discover_DuplicatePluginIds_ReturnsOneCandidateAndError()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        StagedPlugins.CopyPlugin(root, "HelloPlugin", "hello-one");
        StagedPlugins.CopyPlugin(root, "HelloPlugin", "hello-two");
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Single(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.DuplicatePluginId, error.Code);
        Assert.Equal("com.example.hello", error.PluginId);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("nested/../../outside.dll")]
    public async Task Discover_EntryAssemblyPathEscapingPluginDirectory_IsRejected(string entryAssembly)
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest["entryAssembly"] = entryAssembly;
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(root);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.EntryAssemblyPathInvalid, error.Code);
        Assert.Equal("entryAssembly", error.Field);
        Assert.Contains("escapes", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_MissingDirectory_ReturnsExplicitError()
    {
        using var temporary = new TemporaryDirectory();
        var missingRoot = Path.Combine(temporary.DirectoryPath, "missing");
        var loader = new PluginLoader();

        var result = await loader.DiscoverAsync(missingRoot);

        Assert.Empty(result.Plugins);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PluginDiscoveryErrorCodes.PluginDirectoryNotFound, error.Code);
    }

    private static string CreatePluginRoot(TemporaryDirectory temporary)
    {
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        return root;
    }
}

public sealed class PluginLoadingTests
{
    [Fact]
    public async Task Load_InvalidEntryType_ThrowsEntryTypeNotFound()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var manifest = StagedPlugins.ReadManifest(pluginDirectory);
        manifest["entryType"] = "HelloPlugin.DoesNotExist";
        StagedPlugins.WriteManifest(pluginDirectory, manifest);
        var loader = new PluginLoader();
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = loader.CreateInstanceFactory(candidate);

        var exception = await Assert.ThrowsAsync<PluginLoadException>(
            async () => await factory.CreateAsync(candidate.Descriptor, CancellationToken.None));

        Assert.Equal(PluginLoadErrorCodes.EntryTypeNotFound, exception.ErrorCode);
        Assert.Equal(candidate.Descriptor.Id, exception.PluginId);
        await factory.ReleaseAsync();
    }

    [Fact]
    public async Task Load_TypeNotImplementingIPlugin_ThrowsEntryTypeNotAPlugin()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        StagedPlugins.CopyPlugin(root, "ModularRuntime.NotAPlugin");
        var loader = new PluginLoader();
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = loader.CreateInstanceFactory(candidate);

        var exception = await Assert.ThrowsAsync<PluginLoadException>(
            async () => await factory.CreateAsync(candidate.Descriptor, CancellationToken.None));

        Assert.Equal(PluginLoadErrorCodes.EntryTypeNotAPlugin, exception.ErrorCode);
        await factory.ReleaseAsync();
    }

    [Fact]
    public async Task Load_SharedContractsKeepRuntimeIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        StagedPlugins.CopyPlugin(root, "ModularRuntime.TestPlugin");
        var loader = new PluginLoader();
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = loader.CreateInstanceFactory(candidate);

        IPlugin? plugin = null;
        try
        {
            plugin = await factory.CreateAsync(candidate.Descriptor, CancellationToken.None);

            Assert.True(plugin is IPlugin);
            Assert.NotSame(typeof(IPlugin).Assembly, plugin.GetType().Assembly);

            var contractsType = plugin.GetType().GetProperty("ContractsType")!.GetValue(plugin);
            Assert.Same(typeof(IPlugin), contractsType);
        }
        finally
        {
            plugin = null;
            await factory.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Load_PrivateDependency_IsResolvedIntoPluginLoadContext()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "ModularRuntime.TestPlugin");
        var loader = new PluginLoader();
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);
        var factory = loader.CreateInstanceFactory(candidate);

        IPlugin? plugin = null;
        try
        {
            plugin = await factory.CreateAsync(candidate.Descriptor, CancellationToken.None);
            var message = plugin.GetType().GetMethod("GetDependencyMessage")!.Invoke(plugin, null);
            Assert.Equal("private-dependency-ok", message);

            var loadContext = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);
            Assert.NotNull(loadContext);
            var dependency = Assert.Single(
                loadContext.Assemblies,
                assembly => assembly.GetName().Name == "ModularRuntime.TestPlugin.Dependency");
            Assert.Equal(
                Path.Combine(pluginDirectory, "ModularRuntime.TestPlugin.Dependency.dll"),
                dependency.Location);
        }
        finally
        {
            plugin = null;
            await factory.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Release_StartsCooperativeUnloadOfTheLoadContext()
    {
        using var temporary = new TemporaryDirectory();
        var root = CreatePluginRoot(temporary);
        var pluginDirectory = StagedPlugins.CopyPlugin(root, "HelloPlugin");
        var loader = new PluginLoader();
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);
        Assert.Equal(Path.Combine(pluginDirectory, "HelloPlugin.dll"), candidate.EntryAssemblyPath);
        var factory = loader.CreateInstanceFactory(candidate);

        var weakContext = await LoadReleaseAndCaptureContextAsync(factory, candidate.Descriptor);

        Assert.NotNull(weakContext);
        Assert.True(
            GcDiagnostics.WaitForCollection(weakContext),
            "The collectible AssemblyLoadContext should be collectible after references are cleared.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference?> LoadReleaseAndCaptureContextAsync(
        AssemblyPluginInstanceFactory factory,
        PluginDescriptor descriptor)
    {
        var plugin = await factory.CreateAsync(descriptor, CancellationToken.None);
        var weakContext = factory.CreateLoadContextWeakReference();
        await factory.ReleaseAsync();
        GC.KeepAlive(plugin);
        return weakContext;
    }

    private static string CreatePluginRoot(TemporaryDirectory temporary)
    {
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        return root;
    }
}
