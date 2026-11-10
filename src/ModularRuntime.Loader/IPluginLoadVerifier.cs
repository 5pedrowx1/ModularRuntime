namespace ModularRuntime.Loader;

/// <summary>
/// Gate run by <see cref="AssemblyPluginInstanceFactory"/> immediately before a plugin's assemblies
/// are loaded, on every start.
/// </summary>
/// <remarks>
/// Implementations throw (preferably <see cref="PluginLoadException"/> with
/// <see cref="PluginLoadErrorCodes.InstallationVerificationFailed"/>) to reject the load. They may
/// return a lease that the factory keeps until it is released, for example file handles that
/// prevent the verified files from being replaced while the plugin is loaded. Implementations must
/// not load or execute plugin code.
/// </remarks>
public interface IPluginLoadVerifier
{
    IDisposable? Verify(PluginCandidate candidate);
}
