using System.Reflection;
using ModularRuntime.Contracts;
using ModularRuntime.Engine;
using ModularRuntime.Loader.Internal;

namespace ModularRuntime.Loader;

/// <summary>
/// Engine factory that loads one plugin assembly in its own collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// </summary>
public sealed class AssemblyPluginInstanceFactory : IPluginInstanceFactory
{
    private readonly PluginCandidate _candidate;
    private readonly IRuntimeLogger _logger;
    private readonly IPluginLoadVerifier? _verifier;
    private IDisposable? _verificationLease;
    private readonly object _sync = new();
    private readonly SharedContractRegistry? _sharedContracts;
    private SharedContractLeases? _contractLeases;
    private PluginLoadContext? _loadContext;
    private WeakReference? _loadContextReference;
    private bool _consumed;
    private bool _released;

    public AssemblyPluginInstanceFactory(PluginCandidate candidate, IRuntimeLogger logger, IPluginLoadVerifier? verifier = null)
        : this(candidate, logger, verifier, null)
    {
    }

    internal AssemblyPluginInstanceFactory(PluginCandidate candidate, IRuntimeLogger logger, IPluginLoadVerifier? verifier, SharedContractRegistry? sharedContracts)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(logger);
        _candidate = candidate;
        _logger = logger;
        _verifier = verifier;
        _sharedContracts = sharedContracts;
    }

    public ValueTask<IPlugin> CreateAsync(
        PluginDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_sync)
        {
            if (_released)
            {
                throw new PluginLoadException(
                    PluginLoadErrorCodes.FactoryReleased,
                    descriptor.Id,
                    $"The loading factory for plugin '{descriptor.Id}' was already released.");
            }

            if (_consumed)
            {
                throw new PluginLoadException(
                    PluginLoadErrorCodes.FactoryAlreadyConsumed,
                    descriptor.Id,
                    $"The loading factory for plugin '{descriptor.Id}' can create only one instance.");
            }

            _consumed = true;

            EnsureDescriptorMatches(descriptor);
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(_candidate.EntryAssemblyPath))
            {
                throw new PluginLoadException(
                    PluginLoadErrorCodes.EntryAssemblyMissing,
                    descriptor.Id,
                    $"Plugin '{descriptor.Id}' entry assembly '{_candidate.EntryAssemblyPath}' was not found.");
            }

            // Verification happens immediately before loading, every time an instance is created.
            // The verifier may return a lease (for example read handles that deny writes) that this
            // factory holds until it is released, so the verified bytes are the bytes that load.
            if (_verifier is not null)
            {
                try
                {
                    _verificationLease = _verifier.Verify(_candidate);
                }
                catch (PluginLoadException)
                {
                    throw;
                }
                catch (Exception exception) when (!LoaderExceptionClassifier.IsFatal(exception))
                {
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.InstallationVerificationFailed,
                        descriptor.Id,
                        $"Plugin '{descriptor.Id}' failed verification before loading: {exception.Message}",
                        PluginDiagnostic.FromException(exception));
                }
            }

            PluginLoadContext? loadContext = null;
            try
            {
                // Shared contracts come from this plugin's verified files and are held until release.
                if (_candidate.Descriptor.SharedContracts.Count > 0)
                {
                    if (_sharedContracts is null)
                    {
                        throw new PluginLoadException(
                            PluginLoadErrorCodes.SharedContractMissing,
                            descriptor.Id,
                            $"Plugin '{descriptor.Id}' declares shared contracts, but this factory was created without a shared contract registry (use PluginLoader.CreateInstanceFactory).");
                    }

                    _contractLeases = _sharedContracts.Acquire(_candidate);
                }

                loadContext = new PluginLoadContext(descriptor.Id, _candidate.EntryAssemblyPath, _contractLeases?.Assemblies);
                _loadContext = loadContext;
                _loadContextReference = new WeakReference(loadContext);

                Log(
                    LogLevel.Information,
                    $"Loading entry assembly '{_candidate.EntryAssemblyPath}' for plugin '{descriptor.Id}'.",
                    descriptor.Id);

                var assembly = loadContext.LoadFromAssemblyPath(_candidate.EntryAssemblyPath);
                VerifyContractsReference(assembly, descriptor);

                var entryType = assembly.GetType(
                    descriptor.EntryType,
                    throwOnError: false,
                    ignoreCase: false);

                if (entryType is null)
                {
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.EntryTypeNotFound,
                        descriptor.Id,
                        $"Plugin '{descriptor.Id}' entry type '{descriptor.EntryType}' was not found in " +
                        $"assembly '{assembly.GetName().Name}'.");
                }

                if (!typeof(IPlugin).IsAssignableFrom(entryType))
                {
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.EntryTypeNotAPlugin,
                        descriptor.Id,
                        $"Plugin '{descriptor.Id}' entry type '{descriptor.EntryType}' does not implement " +
                        $"{typeof(IPlugin).FullName}.");
                }

                if (!entryType.IsVisible ||
                    entryType.IsAbstract ||
                    entryType.IsInterface ||
                    entryType.ContainsGenericParameters)
                {
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.EntryTypeNotInstantiable,
                        descriptor.Id,
                        $"Plugin '{descriptor.Id}' entry type '{descriptor.EntryType}' must be a public, " +
                        "concrete, non-generic type.");
                }

                var constructor = entryType.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    Type.EmptyTypes,
                    modifiers: null);

                if (constructor is null)
                {
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.EntryTypeNotInstantiable,
                        descriptor.Id,
                        $"Plugin '{descriptor.Id}' entry type '{descriptor.EntryType}' does not expose a " +
                        "public parameterless constructor.");
                }

                IPlugin? instance;
                try
                {
                    instance = Activator.CreateInstance(entryType) as IPlugin;
                }
                catch (TargetInvocationException exception)
                {
                    // The inner exception is usually a plugin-defined type. Only its text
                    // snapshot crosses this boundary; the exception object is discarded.
                    var diagnostic = PluginDiagnostic.FromException(exception.InnerException ?? exception);
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.PluginInstanceCreationFailed,
                        descriptor.Id,
                        $"Creating plugin '{descriptor.Id}' instance failed: {diagnostic.Message}",
                        diagnostic);
                }
                catch (Exception exception) when (!LoaderExceptionClassifier.IsFatal(exception))
                {
                    var diagnostic = PluginDiagnostic.FromException(exception);
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.PluginInstanceCreationFailed,
                        descriptor.Id,
                        $"Creating plugin '{descriptor.Id}' instance failed: {diagnostic.Message}",
                        diagnostic);
                }

                if (instance is null)
                {
                    throw new PluginLoadException(
                        PluginLoadErrorCodes.PluginInstanceCreationFailed,
                        descriptor.Id,
                        $"Plugin '{descriptor.Id}' entry type '{descriptor.EntryType}' produced a null instance.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                Log(
                    LogLevel.Debug,
                    $"Loaded plugin '{descriptor.Id}' from '{_candidate.EntryAssemblyPath}'.",
                    descriptor.Id);

                return ValueTask.FromResult(instance);
            }
            catch (OperationCanceledException)
            {
                ReleaseContext(loadContext);
                throw;
            }
            catch (PluginLoadException)
            {
                ReleaseContext(loadContext);
                throw;
            }
            catch (Exception exception) when (!LoaderExceptionClassifier.IsFatal(exception))
            {
                ReleaseContext(loadContext);
                var diagnostic = PluginDiagnostic.FromException(exception);
                throw new PluginLoadException(
                    PluginLoadErrorCodes.AssemblyLoadFailed,
                    descriptor.Id,
                    $"Plugin '{descriptor.Id}' could not be loaded from " +
                    $"'{_candidate.EntryAssemblyPath}': {diagnostic.Message}",
                    diagnostic);
            }
        }
    }

    public ValueTask ReleaseAsync()
    {
        PluginLoadContext? loadContext;
        IDisposable? lease;
        SharedContractLeases? contracts;
        lock (_sync)
        {
            if (_released)
            {
                return ValueTask.CompletedTask;
            }

            _released = true;
            loadContext = _loadContext;
            _loadContext = null;
            lease = _verificationLease;
            _verificationLease = null;
            contracts = _contractLeases;
            _contractLeases = null;
        }

        lease?.Dispose();
        if (loadContext is null)
        {
            contracts?.Release(_candidate.Descriptor.Id);
            return ValueTask.CompletedTask;
        }

        loadContext.Unload();
        contracts?.Release(_candidate.Descriptor.Id);
        Log(
            LogLevel.Debug,
            $"Unload requested for plugin '{_candidate.Descriptor.Id}' load context. " +
            "Collection is cooperative.",
            _candidate.Descriptor.Id);

        return ValueTask.CompletedTask;
    }

    internal WeakReference? CreateLoadContextWeakReference()
    {
        lock (_sync)
        {
            return _loadContextReference;
        }
    }

    private void EnsureDescriptorMatches(PluginDescriptor descriptor)
    {
        var candidateDescriptor = _candidate.Descriptor;
        if (descriptor.Id != candidateDescriptor.Id ||
            !string.Equals(descriptor.EntryType, candidateDescriptor.EntryType, StringComparison.Ordinal) ||
            !string.Equals(descriptor.EntryAssembly, candidateDescriptor.EntryAssembly, StringComparison.Ordinal))
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.PluginDescriptorMismatch,
                descriptor.Id,
                $"The descriptor passed to the factory for plugin '{descriptor.Id}' does not match " +
                "the discovered plugin candidate.");
        }
    }

    private static void VerifyContractsReference(Assembly assembly, PluginDescriptor descriptor)
    {
        var sharedContracts = typeof(IPlugin).Assembly.GetName();
        var reference = assembly
            .GetReferencedAssemblies()
            .FirstOrDefault(name =>
                string.Equals(name.Name, sharedContracts.Name, StringComparison.OrdinalIgnoreCase));

        if (reference is null)
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.ContractsAssemblyMismatch,
                descriptor.Id,
                $"Plugin '{descriptor.Id}' does not reference the shared contracts assembly " +
                $"'{sharedContracts.Name}'.");
        }

        // Same rule as the plugin API: same major; for experimental 0.x contracts the minor must
        // match too, because any 0.x minor may contain breaking changes.
        if (reference.Version is not null &&
            sharedContracts.Version is not null &&
            (reference.Version.Major != sharedContracts.Version.Major ||
             (sharedContracts.Version.Major == 0 && reference.Version.Minor != sharedContracts.Version.Minor)))
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.ContractsAssemblyMismatch,
                descriptor.Id,
                $"Plugin '{descriptor.Id}' was built against contracts version {reference.Version}, " +
                $"but this runtime provides {sharedContracts.Version}.");
        }
    }

    private void ReleaseContext(PluginLoadContext? loadContext)
    {
        // A failed load also gives up the verification lease and the shared contracts (called while holding _sync).
        _verificationLease?.Dispose();
        _verificationLease = null;

        if (loadContext is not null)
        {
            if (ReferenceEquals(_loadContext, loadContext))
            {
                _loadContext = null;
            }

            loadContext.Unload();
        }

        var contracts = _contractLeases;
        _contractLeases = null;
        contracts?.Release(_candidate.Descriptor.Id);
    }

    private void Log(
        LogLevel level,
        string message,
        PluginId pluginId,
        PluginDiagnostic? diagnostic = null) =>
        SafeLogger.Log(
            _logger,
            new LogEntry(
                level,
                message,
                Category: "Loader",
                PluginId: pluginId,
                Diagnostic: diagnostic));
}
