using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader.Internal;
using ModularRuntime.TestSupport;

namespace ModularRuntime.Loader.Tests;

/// <summary>
/// Shared contract assemblies (manifest <c>sharedContracts</c>): manifest validation, loading into
/// one collectible context per version line, version and dependency rules, reference counting and
/// unload. Assemblies with chosen names and versions are generated on the fly, so every rule is
/// exercised without extra fixture projects.
/// </summary>
public sealed class SharedContractTests
{
    private const string Contract = "Contoso.Counter.Contracts";

    [Fact]
    public async Task Manifest_SharedContracts_AreValidated()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        var loader = new PluginLoader();

        async Task<PluginDiscoveryResult> DiscoverAsync(JsonArray? contracts, string installation)
        {
            var directory = StagedPlugins.CopyPlugin(root, "HelloPlugin", installation);
            var manifest = StagedPlugins.ReadManifest(directory);
            manifest["id"] = "com.test." + installation;
            if (contracts is not null)
            {
                manifest["sharedContracts"] = contracts;
            }

            StagedPlugins.WriteManifest(directory, manifest);
            return await loader.ReadInstallationAsync(directory);
        }

        Assert.Equal(PluginDiscoveryErrorCodes.SharedContractMissing, Assert.Single((await DiscoverAsync(["Missing.Contracts"], "missing")).Errors).Code);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestFieldInvalid, Assert.Single((await DiscoverAsync(["ModularRuntime.Contracts"], "reserved")).Errors).Code);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestFieldInvalid, Assert.Single((await DiscoverAsync(["ModularRuntime.Engine"], "host")).Errors).Code);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestFieldInvalid, Assert.Single((await DiscoverAsync(["Some.Contracts.dll"], "extension")).Errors).Code);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestFieldInvalid, Assert.Single((await DiscoverAsync(["../Some.Contracts"], "path")).Errors).Code);
        Assert.Equal(2, (await DiscoverAsync(["Dup.Contracts", "dup.contracts"], "duplicate")).Errors.Count);
        Assert.Equal(PluginDiscoveryErrorCodes.ManifestFieldInvalid, Assert.Single((await DiscoverAsync([.. Enumerable.Range(0, PluginDescriptor.MaxSharedContracts + 1).Select(i => (JsonNode)$"C{i}")], "toomany")).Errors, e => e.Field == "sharedContracts" && e.Message.Contains("more than", StringComparison.Ordinal)).Code);

        var none = await DiscoverAsync(null, "none");
        Assert.Empty(none.Errors);
        Assert.Empty(Assert.Single(none.Plugins).Descriptor.SharedContracts);
        Assert.Empty((await DiscoverAsync([], "empty")).Errors);

        // A declared contract that exists next to the entry assembly is accepted.
        var directory = StagedPlugins.CopyPlugin(root, "HelloPlugin", "declared");
        EmittedAssemblies.WriteContract(Path.Combine(directory, Contract + ".dll"), Contract, new Version(1, 0, 0, 0));
        var manifest = StagedPlugins.ReadManifest(directory);
        manifest["id"] = "com.test.declared";
        manifest["sharedContracts"] = new JsonArray(Contract);
        StagedPlugins.WriteManifest(directory, manifest);
        var declared = await loader.ReadInstallationAsync(directory);
        Assert.Empty(declared.Errors);
        Assert.Equal([Contract], Assert.Single(declared.Plugins).Descriptor.SharedContracts);
    }

    [Fact]
    public async Task TwoPlugins_ShareOneContractContext_WhichUnloadsAfterTheLastRelease()
    {
        using var temporary = new TemporaryDirectory();
        var (root, loader, logger) = Prepare(temporary);
        var first = EmittedAssemblies.WritePlugin(root, "com.test.first", Contract, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0));
        EmittedAssemblies.WritePlugin(root, "com.test.second", Contract, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0));
        var discovery = await loader.DiscoverAsync(root);
        Assert.Empty(discovery.Errors);
        var factories = discovery.Plugins.ToDictionary(c => c.Descriptor.Id.Value, c => loader.CreateInstanceFactory(c));

        // Instances and types live only inside this helper, so nothing in the test's own frames keeps them alive.
        var weakContext = await LoadBothAndInspectAsync(loader, discovery, factories, first, logger);

        await factories["com.test.first"].ReleaseAsync();
        Assert.Equal(1, Assert.Single(loader.GetSharedContracts()).UserCount);
        Assert.False(GcDiagnostics.WaitForCollection(weakContext, attempts: 2), "sanity: a user remains");

        await factories["com.test.second"].ReleaseAsync();
        Assert.Empty(loader.GetSharedContracts());
        Assert.True(GcDiagnostics.WaitForCollection(weakContext), "the shared contract context must be collected after its last user");
        Assert.True(loader.CreateSharedTypePolicy().IsShared(typeof(SharedContractTests)), "types in the default context are shared");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadBothAndInspectAsync(
        PluginLoader loader,
        PluginDiscoveryResult discovery,
        Dictionary<string, AssemblyPluginInstanceFactory> factories,
        string firstDirectory,
        CollectingLogger logger)
    {
        var firstInstance = await factories["com.test.first"].CreateAsync(discovery.Plugins.Single(c => c.Descriptor.Id.Value == "com.test.first").Descriptor, CancellationToken.None);
        var secondInstance = await factories["com.test.second"].CreateAsync(discovery.Plugins.Single(c => c.Descriptor.Id.Value == "com.test.second").Descriptor, CancellationToken.None);

        var contractType = firstInstance.GetType().GetField("Counter")!.FieldType;
        Assert.Same(contractType, secondInstance.GetType().GetField("Counter")!.FieldType);
        var context = AssemblyLoadContext.GetLoadContext(contractType.Assembly)!;
        Assert.StartsWith("ModularRuntime.Contract." + Contract, context.Name, StringComparison.Ordinal);
        Assert.True(context.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(firstInstance.GetType().Assembly), context);
        Assert.True(loader.CreateSharedTypePolicy().IsShared(contractType));
        Assert.False(loader.CreateSharedTypePolicy().IsShared(firstInstance.GetType()));
        var info = Assert.Single(loader.GetSharedContracts());
        Assert.Equal(Contract, info.Name);
        Assert.Equal(2, info.UserCount);
        Assert.Equal(Path.Combine(firstDirectory, Contract + ".dll"), info.LoadedFrom);
        Assert.True(logger.HasMessageContaining("loaded from plugin 'com.test.first' into a shared context"));
        return new WeakReference(context);
    }

    [Fact]
    public async Task VersionRules_SameMajorNewerLoaded_IsFine_OlderLoaded_IsAConflict()
    {
        using var temporary = new TemporaryDirectory();
        var (root, loader, _) = Prepare(temporary);
        EmittedAssemblies.WritePlugin(root, "com.test.new", Contract, new Version(1, 2, 0, 0), new Version(1, 2, 0, 0));
        EmittedAssemblies.WritePlugin(root, "com.test.old", Contract, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0));
        var discovery = await loader.DiscoverAsync(root);
        Assert.Empty(discovery.Errors);
        var candidates = discovery.Plugins.ToDictionary(c => c.Descriptor.Id.Value);

        // Older-first: the newer plugin cannot bind to the 1.0 context.
        var oldFactory = loader.CreateInstanceFactory(candidates["com.test.old"]);
        await oldFactory.CreateAsync(candidates["com.test.old"].Descriptor, CancellationToken.None);
        var newFactory = loader.CreateInstanceFactory(candidates["com.test.new"]);
        var conflict = await Assert.ThrowsAsync<PluginLoadException>(async () => await newFactory.CreateAsync(candidates["com.test.new"].Descriptor, CancellationToken.None));
        Assert.Equal(PluginLoadErrorCodes.SharedContractVersionConflict, conflict.ErrorCode);
        Assert.Contains("1.2.0.0 or newer", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("'com.test.old'", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(loader.GetSharedContracts()).UserCount);
        await oldFactory.ReleaseAsync();
        Assert.Empty(loader.GetSharedContracts());

        // Newer-first: the older plugin binds to the 1.2 context (same major, newer version).
        newFactory = loader.CreateInstanceFactory(candidates["com.test.new"]);
        await newFactory.CreateAsync(candidates["com.test.new"].Descriptor, CancellationToken.None);
        oldFactory = loader.CreateInstanceFactory(candidates["com.test.old"]);
        await oldFactory.CreateAsync(candidates["com.test.old"].Descriptor, CancellationToken.None);
        var info = Assert.Single(loader.GetSharedContracts());
        Assert.Equal(new Version(1, 2, 0, 0), info.Version);
        Assert.Equal(2, info.UserCount);
        await oldFactory.ReleaseAsync();
        await newFactory.ReleaseAsync();
    }

    [Fact]
    public async Task DifferentMajors_CoexistInSeparateContexts()
    {
        using var temporary = new TemporaryDirectory();
        var (root, loader, _) = Prepare(temporary);
        EmittedAssemblies.WritePlugin(root, "com.test.v1", Contract, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0));
        EmittedAssemblies.WritePlugin(root, "com.test.v2", Contract, new Version(2, 0, 0, 0), new Version(2, 0, 0, 0));
        var discovery = await loader.DiscoverAsync(root);
        Assert.Empty(discovery.Errors);

        var factories = discovery.Plugins.Select(c => (c, loader.CreateInstanceFactory(c))).ToArray();
        foreach (var (candidate, factory) in factories)
        {
            await factory.CreateAsync(candidate.Descriptor, CancellationToken.None);
        }

        Assert.Equal([new Version(1, 0, 0, 0), new Version(2, 0, 0, 0)], loader.GetSharedContracts().Select(c => c.Version));
        foreach (var (_, factory) in factories)
        {
            await factory.ReleaseAsync();
        }
    }

    [Fact]
    public async Task ContractReferencingAnotherContract_BindsToTheMajorItReferences_WhenTwoMajorsAreLoaded()
    {
        using var temporary = new TemporaryDirectory();
        var (root, loader, _) = Prepare(temporary);
        const string Dependent = "Contoso.Dependent.Contracts";
        EmittedAssemblies.WritePlugin(root, "com.test.v2", Contract, new Version(2, 0, 0, 0), new Version(2, 0, 0, 0));
        EmittedAssemblies.WritePlugin(root, "com.test.v1", Contract, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0), dependentContract: Dependent);
        var discovery = await loader.DiscoverAsync(root);
        Assert.Empty(discovery.Errors);
        var candidates = discovery.Plugins.ToDictionary(c => c.Descriptor.Id.Value);

        // The 2.0 line is loaded first; the dependent contract (compiled against 1.0) must bind to the 1.0 line.
        var v2 = loader.CreateInstanceFactory(candidates["com.test.v2"]);
        await v2.CreateAsync(candidates["com.test.v2"].Descriptor, CancellationToken.None);
        var v1 = loader.CreateInstanceFactory(candidates["com.test.v1"]);
        var instance = await v1.CreateAsync(candidates["com.test.v1"].Descriptor, CancellationToken.None);
        Assert.Equal([$"{Contract} 1.0.0.0", $"{Contract} 2.0.0.0", $"{Dependent} 1.0.0.0"], loader.GetSharedContracts().Select(c => $"{c.Name} {c.Version}"));

        var pluginContext = AssemblyLoadContext.GetLoadContext(instance.GetType().Assembly)!;
        var dependent = pluginContext.LoadFromAssemblyName(new AssemblyName(Dependent));
        var bound = dependent.GetType("Contoso.IDependent")!.GetMethod("Dependency")!.ReturnType;
        Assert.Equal(new Version(1, 0, 0, 0), bound.Assembly.GetName().Version);
        Assert.Same(instance.GetType().GetField("Counter")!.FieldType, bound);

        await v1.ReleaseAsync();
        await v2.ReleaseAsync();
        Assert.Empty(loader.GetSharedContracts());
    }

    [Fact]
    public async Task ContractWithAPrivateDependency_IsRefused_AndACorruptFile_IsReported()
    {
        using var temporary = new TemporaryDirectory();
        var (root, loader, _) = Prepare(temporary);
        var leaking = EmittedAssemblies.WritePlugin(root, "com.test.leaking", Contract, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0), privateDependency: "Contoso.Private");
        var corrupt = EmittedAssemblies.WritePlugin(root, "com.test.corrupt", Contract, new Version(1, 0, 0, 0), new Version(1, 0, 0, 0));
        File.WriteAllBytes(Path.Combine(corrupt, Contract + ".dll"), [1, 2, 3]);
        var discovery = await loader.DiscoverAsync(root);
        Assert.Empty(discovery.Errors);
        var candidates = discovery.Plugins.ToDictionary(c => c.Descriptor.Id.Value);

        var dependency = await Assert.ThrowsAsync<PluginLoadException>(async () =>
            await loader.CreateInstanceFactory(candidates["com.test.leaking"]).CreateAsync(candidates["com.test.leaking"].Descriptor, CancellationToken.None));
        Assert.Equal(PluginLoadErrorCodes.SharedContractDependency, dependency.ErrorCode);
        Assert.Contains("Contoso.Private", dependency.Message, StringComparison.Ordinal);

        var invalid = await Assert.ThrowsAsync<PluginLoadException>(async () =>
            await loader.CreateInstanceFactory(candidates["com.test.corrupt"]).CreateAsync(candidates["com.test.corrupt"].Descriptor, CancellationToken.None));
        Assert.Equal(PluginLoadErrorCodes.SharedContractInvalid, invalid.ErrorCode);
        Assert.Empty(loader.GetSharedContracts());
        GC.KeepAlive(leaking);
    }

    [Fact]
    public async Task PluginWhoseCopyIsOlderThanItsOwnReference_IsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var (root, loader, _) = Prepare(temporary);
        // The plugin was compiled against 1.1 but ships 1.0 (a packaging mistake).
        EmittedAssemblies.WritePlugin(root, "com.test.mismatch", Contract, referencedVersion: new Version(1, 1, 0, 0), shippedVersion: new Version(1, 0, 0, 0));
        var discovery = await loader.DiscoverAsync(root);
        var candidate = Assert.Single(discovery.Plugins);

        var error = await Assert.ThrowsAsync<PluginLoadException>(async () =>
            await loader.CreateInstanceFactory(candidate).CreateAsync(candidate.Descriptor, CancellationToken.None));
        Assert.Equal(PluginLoadErrorCodes.SharedContractVersionConflict, error.ErrorCode);
        Assert.Contains("contains version 1.0.0.0", error.Message, StringComparison.Ordinal);
    }

    private static (string Root, PluginLoader Loader, CollectingLogger Logger) Prepare(TemporaryDirectory temporary)
    {
        var root = Path.Combine(temporary.DirectoryPath, "plugins");
        Directory.CreateDirectory(root);
        var logger = new CollectingLogger();
        return (root, new PluginLoader(logger), logger);
    }
}

/// <summary>Generates minimal contract and plugin assemblies with chosen identities (persisted with Reflection.Emit).</summary>
internal static class EmittedAssemblies
{
    /// <summary>
    /// Writes a contract assembly with <c>ICounter { int Next(); }</c>; optionally depending on a
    /// private assembly, or on another contract assembly in the same folder (<c>IDependent.Dependency()</c>
    /// then returns that contract's <c>ICounter</c>, so the assembly references it with the version on disk).
    /// </summary>
    public static void WriteContract(string path, string name, Version version, string? privateDependency = null, string? dependsOnContract = null)
    {
        Type? privateType = null;
        Type? dependencyType = null;
        AssemblyLoadContext? scratch = null;
        if (privateDependency is not null)
        {
            var privatePath = Path.Combine(Path.GetDirectoryName(path)!, privateDependency + ".dll");
            var privateBuilder = new PersistedAssemblyBuilder(new AssemblyName(privateDependency) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
            privateBuilder.DefineDynamicModule(privateDependency).DefineType("Contoso.Secret", TypeAttributes.Public | TypeAttributes.Class).CreateType();
            privateBuilder.Save(privatePath);
            scratch = new AssemblyLoadContext("scratch", isCollectible: true);
            using var stream = File.OpenRead(privatePath);
            privateType = scratch.LoadFromStream(stream).GetType("Contoso.Secret");
        }

        if (dependsOnContract is not null)
        {
            scratch ??= new AssemblyLoadContext("scratch", isCollectible: true);
            using var stream = File.OpenRead(Path.Combine(Path.GetDirectoryName(path)!, dependsOnContract + ".dll"));
            dependencyType = scratch.LoadFromStream(stream).GetType("Contoso.ICounter");
        }

        var builder = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = version }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule(name);
        var contract = module.DefineType(dependencyType is null ? "Contoso.ICounter" : "Contoso.IDependent", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        contract.DefineMethod("Next", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot, typeof(int), Type.EmptyTypes);
        if (privateType is not null)
        {
            contract.DefineMethod("Secret", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot, privateType, Type.EmptyTypes);
        }

        if (dependencyType is not null)
        {
            contract.DefineMethod("Dependency", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot, dependencyType, Type.EmptyTypes);
        }

        contract.CreateType();
        builder.Save(path);
        scratch?.Unload();
    }

    /// <summary>
    /// Writes an installation: plugin.json (declaring the contract), an entry assembly whose
    /// <c>Plugin</c> type implements <see cref="IPlugin"/> and has a public field of the contract's
    /// <c>ICounter</c> type (so it references the contract with <paramref name="referencedVersion"/>),
    /// and the shipped contract file with <paramref name="shippedVersion"/>.
    /// </summary>
    public static string WritePlugin(string root, string pluginId, string contractName, Version referencedVersion, Version shippedVersion, string? privateDependency = null, string? dependentContract = null)
    {
        var directory = Path.Combine(root, pluginId);
        Directory.CreateDirectory(directory);
        var contractPath = Path.Combine(directory, contractName + ".dll");
        WriteContract(contractPath, contractName, referencedVersion, privateDependency);
        if (dependentContract is not null)
        {
            // A second shared contract of this plugin that references the first one (as shipped).
            WriteContract(Path.Combine(directory, dependentContract + ".dll"), dependentContract, new Version(1, 0, 0, 0), dependsOnContract: contractName);
        }

        // Load the referenced contract in a throwaway context to obtain the Type to reference.
        var scratch = new AssemblyLoadContext("scratch-" + pluginId, isCollectible: true);
        Type counterType;
        using (var stream = File.OpenRead(contractPath))
        {
            counterType = scratch.LoadFromStream(stream).GetType("Contoso.ICounter")!;
        }

        var assemblyName = "Plugin." + pluginId.Replace('.', '_');
        var builder = new PersistedAssemblyBuilder(new AssemblyName(assemblyName) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule(assemblyName);
        var plugin = module.DefineType("Generated.Plugin", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(object), [typeof(IPlugin)]);
        plugin.DefineField("Counter", counterType, FieldAttributes.Public);
        var completed = typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetMethod!;
        var initialize = plugin.DefineMethod(nameof(IPlugin.InitializeAsync), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final, typeof(Task), [typeof(IPluginContext), typeof(CancellationToken)]);
        var il = initialize.GetILGenerator();
        il.Emit(OpCodes.Call, completed);
        il.Emit(OpCodes.Ret);
        plugin.DefineMethodOverride(initialize, typeof(IPlugin).GetMethod(nameof(IPlugin.InitializeAsync))!);
        var shutdown = plugin.DefineMethod(nameof(IPlugin.ShutdownAsync), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final, typeof(Task), [typeof(CancellationToken)]);
        il = shutdown.GetILGenerator();
        il.Emit(OpCodes.Call, completed);
        il.Emit(OpCodes.Ret);
        plugin.DefineMethodOverride(shutdown, typeof(IPlugin).GetMethod(nameof(IPlugin.ShutdownAsync))!);
        plugin.CreateType();
        builder.Save(Path.Combine(directory, assemblyName + ".dll"));
        scratch.Unload();

        if (shippedVersion != referencedVersion)
        {
            WriteContract(contractPath, contractName, shippedVersion, privateDependency);
        }

        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "id": "{{pluginId}}",
              "name": "Generated {{pluginId}}",
              "version": "1.0.0",
              "apiVersion": "{{PluginApiVersion.Supported}}",
              "entryAssembly": "{{assemblyName}}.dll",
              "entryType": "Generated.Plugin",
              "sharedContracts": [{{string.Join(", ", new[] { contractName, dependentContract }.Where(c => c is not null).Select(c => $"\"{c}\""))}}]
            }
            """);
        return directory;
    }
}
