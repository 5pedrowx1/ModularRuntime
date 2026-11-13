using System.Reflection;
using System.Runtime.Loader;
using ModularRuntime.Contracts;

namespace ModularRuntime.Loader.Internal;

/// <summary>
/// Loads the contract assemblies plugins share (manifest <c>sharedContracts</c>) so that provider
/// and consumer see one <see cref="Type"/> identity, and unloads them when nobody uses them.
/// </summary>
/// <remarks>
/// <para>
/// One collectible <see cref="ContractLoadContext"/> per (assembly simple name, major version)
/// holds the assembly loaded from the <b>bytes</b> of the first plugin that needed it (after that
/// plugin's installation was verified), so no plugin file stays locked by a shared context. Every
/// plugin that declares the contract takes a lease; the context is unloaded when the last lease
/// is released. Nothing is ever loaded into <see cref="AssemblyLoadContext.Default"/>.
/// </para>
/// <para>
/// Compatibility: a plugin whose entry assembly references version <c>R</c> of the contract binds
/// to a loaded context of the same major with version <c>V &gt;= R</c>; a context with
/// <c>V &lt; R</c> is a conflict (<c>shared_contract_version_conflict</c>) that the operator resolves
/// by updating the plugin that brought the older copy or by starting the newer one first.
/// </para>
/// <para>
/// A contract assembly may reference only the framework, <c>ModularRuntime.Contracts</c>, and
/// other shared contracts declared by the same plugin; a private dependency would leak between
/// plugins and is refused (<c>shared_contract_dependency</c>).
/// </para>
/// </remarks>
internal sealed class SharedContractRegistry
{
    private readonly Lock _sync = new();
    private readonly Dictionary<ContractKey, ContractEntry> _contexts = [];
    private readonly HashSet<ContractLoadContext> _liveContexts = [];
    private readonly IRuntimeLogger _logger;

    public SharedContractRegistry(IRuntimeLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Acquires every shared contract declared by <paramref name="candidate"/>; returns the map the
    /// plugin's load context resolves them from. On failure nothing stays acquired.
    /// </summary>
    public SharedContractLeases Acquire(PluginCandidate candidate)
    {
        var declared = candidate.Descriptor.SharedContracts;
        if (declared.Count == 0)
        {
            return SharedContractLeases.Empty;
        }

        AssemblyMetadata entry;
        try
        {
            entry = AssemblyMetadata.Read(candidate.EntryAssemblyPath);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.AssemblyLoadFailed,
                candidate.Descriptor.Id,
                $"Plugin '{candidate.Descriptor.Id}' entry assembly could not be read: {exception.Message}",
                PluginDiagnostic.FromException(exception));
        }

        var acquired = new List<(ContractKey Key, ContractEntry Entry)>();
        var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in declared)
            {
                var (key, contract) = AcquireOne(candidate, name, entry.ReferencedVersion(name), declared);
                acquired.Add((key, contract));
                assemblies[name] = contract.Assembly;
            }
        }
        catch
        {
            foreach (var (key, contract) in acquired)
            {
                Release(key, contract);
            }

            throw;
        }

        return new SharedContractLeases(this, acquired, assemblies);
    }

    /// <summary>True when <paramref name="context"/> is one of the shared contract contexts (alive or unloading).</summary>
    public bool IsContractContext(AssemblyLoadContext context)
    {
        if (context is not ContractLoadContext contract)
        {
            return false;
        }

        lock (_sync)
        {
            return _liveContexts.Contains(contract);
        }
    }

    public IReadOnlyList<SharedContractInfo> Snapshot()
    {
        lock (_sync)
        {
            return _contexts
                .OrderBy(pair => pair.Key.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key.Major)
                .Select(pair => new SharedContractInfo(pair.Value.Name, pair.Value.Version, pair.Value.Users.Count, [.. pair.Value.Users.OrderBy(id => id.Value, StringComparer.Ordinal)], pair.Value.LoadedFrom))
                .ToArray();
        }
    }

    internal void Release(ContractKey key, ContractEntry entry)
    {
        ContractLoadContext? toUnload = null;
        lock (_sync)
        {
            if (--entry.Leases > 0)
            {
                return;
            }

            if (_contexts.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _contexts.Remove(key);
            }

            _liveContexts.Remove(entry.Context);
            toUnload = entry.Context;
        }

        toUnload.Unload();
        Log(LogLevel.Debug, $"Shared contract '{entry.Name}' {entry.Version} has no more users; unload requested (collection is cooperative).");
    }

    internal void ReleaseUser(ContractEntry entry, PluginId pluginId)
    {
        lock (_sync)
        {
            entry.Users.Remove(pluginId);
        }
    }

    private (ContractKey Key, ContractEntry Entry) AcquireOne(PluginCandidate candidate, string name, Version? referenced, IReadOnlyList<string> declared)
    {
        var pluginId = candidate.Descriptor.Id;
        var path = Path.Combine(candidate.PluginDirectory, name + ".dll");
        if (!File.Exists(path))
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.SharedContractMissing,
                pluginId,
                $"Plugin '{pluginId}' declares shared contract '{name}', but '{path}' does not exist.");
        }

        AssemblyMetadata metadata;
        byte[] bytes;
        byte[]? symbols = null;
        try
        {
            bytes = File.ReadAllBytes(path);
            metadata = AssemblyMetadata.Read(new MemoryStream(bytes, writable: false));
            var pdb = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(pdb))
            {
                symbols = File.ReadAllBytes(pdb);
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.SharedContractInvalid,
                pluginId,
                $"Shared contract '{name}' of plugin '{pluginId}' is not a valid assembly: {exception.Message}",
                PluginDiagnostic.FromException(exception));
        }

        if (!string.Equals(metadata.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.SharedContractInvalid,
                pluginId,
                $"Shared contract file '{path}' contains assembly '{metadata.Name}', not '{name}'.");
        }

        foreach (var reference in metadata.References)
        {
            var referenceName = reference.Name ?? string.Empty;
            if (!SharedContractRules.IsAllowedReference(referenceName, declared))
            {
                throw new PluginLoadException(
                    PluginLoadErrorCodes.SharedContractDependency,
                    pluginId,
                    $"Shared contract '{name}' of plugin '{pluginId}' references '{referenceName}', which is neither the framework, ModularRuntime.Contracts, nor a shared contract declared by the plugin. " +
                    "A shared contract assembly must not depend on a plugin's private assemblies.");
            }
        }

        // The plugin's own copy must satisfy what its entry assembly references.
        referenced ??= metadata.Version;
        if (referenced.Major != metadata.Version.Major || Normalize(metadata.Version) < Normalize(referenced))
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.SharedContractVersionConflict,
                pluginId,
                $"Plugin '{pluginId}' references shared contract '{name}' {referenced}, but its installation contains version {metadata.Version}.");
        }

        var key = new ContractKey(name.ToUpperInvariant(), referenced.Major);
        lock (_sync)
        {
            if (_contexts.TryGetValue(key, out var existing))
            {
                if (Normalize(existing.Version) < Normalize(referenced))
                {
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.SharedContractVersionConflict,
                        pluginId,
                        $"Plugin '{pluginId}' needs shared contract '{name}' {referenced} or newer, but version {existing.Version} is already loaded for {Describe(existing.Users)}. " +
                        "Stop those plugins or update them so that a compatible version is loaded.");
                }

                existing.Leases++;
                existing.Users.Add(pluginId);
                return (key, existing);
            }

            var context = new ContractLoadContext(name, metadata.Version, this);
            Assembly assembly;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var symbolStream = symbols is null ? null : new MemoryStream(symbols, writable: false);
                assembly = symbolStream is null ? context.LoadFromStream(stream) : context.LoadFromStream(stream, symbolStream);
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or IOException)
            {
                context.Unload();
                throw new PluginLoadException(
                    PluginLoadErrorCodes.SharedContractInvalid,
                    pluginId,
                    $"Shared contract '{name}' of plugin '{pluginId}' could not be loaded: {exception.Message}",
                    PluginDiagnostic.FromException(exception));
            }

            var created = new ContractEntry(name, context, assembly, metadata.Version, path) { Leases = 1 };
            created.Users.Add(pluginId);
            _contexts.Add(key, created);
            _liveContexts.Add(context);
            Log(LogLevel.Information, $"Shared contract '{name}' {metadata.Version} loaded from plugin '{pluginId}' into a shared context.");
            return (key, created);
        }
    }

    /// <summary>
    /// Resolves a shared contract referenced by another contract assembly. When the reference
    /// names a version, only a loaded copy of the same major that is at least that version
    /// qualifies (the newest one when several exist); a reference without a version takes the
    /// newest loaded copy. A reference to a major that is not loaded is unresolved rather than
    /// bound to another major, which would be a different type identity.
    /// </summary>
    internal Assembly? ResolveForContract(AssemblyName reference)
    {
        var name = reference.Name;
        if (name is null)
        {
            return null;
        }

        var wanted = reference.Version;
        lock (_sync)
        {
            ContractEntry? best = null;
            foreach (var entry in _contexts.Values)
            {
                if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (wanted is not null && (entry.Version.Major != wanted.Major || Normalize(entry.Version) < Normalize(wanted)))
                {
                    continue;
                }

                if (best is null || Normalize(entry.Version) > Normalize(best.Version))
                {
                    best = entry;
                }
            }

            return best?.Assembly;
        }
    }

    private static string Describe(IEnumerable<PluginId> users) => string.Join(", ", users.Select(id => $"'{id}'"));

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private void Log(LogLevel level, string message) =>
        SafeLogger.Log(_logger, new LogEntry(level, message, Category: "Loader"));

    /// <param name="Name">The assembly simple name in upper-invariant form (lookup key).</param>
    internal readonly record struct ContractKey(string Name, int Major);

    internal sealed class ContractEntry(string name, ContractLoadContext context, Assembly assembly, Version version, string loadedFrom)
    {
        /// <summary>The assembly simple name as declared.</summary>
        public string Name { get; } = name;

        public ContractLoadContext Context { get; } = context;

        public Assembly Assembly { get; } = assembly;

        public Version Version { get; } = version;

        public string LoadedFrom { get; } = loadedFrom;

        /// <summary>Guarded by the registry lock.</summary>
        public int Leases { get; set; }

        /// <summary>Guarded by the registry lock.</summary>
        public HashSet<PluginId> Users { get; } = [];
    }
}

/// <summary>Collectible context of one shared contract assembly (plus nothing else).</summary>
internal sealed class ContractLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ContractsAssembly = typeof(IPlugin).Assembly;
    private readonly SharedContractRegistry _registry;

    public ContractLoadContext(string name, Version version, SharedContractRegistry registry)
        : base($"ModularRuntime.Contract.{name}.{version}.{Guid.NewGuid():N}", isCollectible: true)
    {
        _registry = registry;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractsAssembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            return ContractsAssembly;
        }

        // Other shared contracts (same major as referenced); framework assemblies fall through to the default context.
        return _registry.ResolveForContract(assemblyName);
    }
}

/// <summary>The shared contracts one plugin instance holds; released by the factory.</summary>
internal sealed class SharedContractLeases
{
    public static SharedContractLeases Empty { get; } = new(null, [], new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase));

    private readonly SharedContractRegistry? _registry;
    private List<(SharedContractRegistry.ContractKey Key, SharedContractRegistry.ContractEntry Entry)>? _leases;

    internal SharedContractLeases(
        SharedContractRegistry? registry,
        List<(SharedContractRegistry.ContractKey Key, SharedContractRegistry.ContractEntry Entry)> leases,
        IReadOnlyDictionary<string, Assembly> assemblies)
    {
        _registry = registry;
        _leases = leases;
        Assemblies = assemblies;
    }

    /// <summary>Shared contract assemblies by simple name, for the plugin's load context.</summary>
    public IReadOnlyDictionary<string, Assembly> Assemblies { get; }

    public void Release(PluginId pluginId)
    {
        var leases = Interlocked.Exchange(ref _leases, null);
        if (leases is null || _registry is null)
        {
            return;
        }

        foreach (var (key, entry) in leases)
        {
            _registry.ReleaseUser(entry, pluginId);
            _registry.Release(key, entry);
        }
    }
}
