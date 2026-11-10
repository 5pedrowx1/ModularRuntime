using ModularRuntime.Loader;

namespace ModularRuntime.Packaging;

/// <summary>
/// Loader gate: before a managed installation's assemblies are loaded, re-verifies its official
/// signature (official packages), identity, and every file's size and SHA-256, and keeps the files
/// locked against modification while the plugin is loaded.
/// </summary>
/// <remarks>
/// Installations without a package record (plugins copied into the plugin folder by hand) are not
/// managed by the package manager and load without this check; they are reported as unmanaged and
/// never as verified.
/// </remarks>
public sealed class PackageLoadVerifier(PackageStore store) : IPluginLoadVerifier
{
    public IDisposable? Verify(PluginCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var record = store.FindRecordForDirectory(candidate.PluginDirectory);
        if (record is null)
        {
            return null;
        }

        try
        {
            return store.VerifyAndLock(candidate.Descriptor.Id, candidate.Descriptor);
        }
        catch (InstallationCheckException exception)
        {
            throw new PluginLoadException(
                PluginLoadErrorCodes.InstallationVerificationFailed,
                candidate.Descriptor.Id,
                $"Plugin '{candidate.Descriptor.Id}' was not loaded: {exception.Message}");
        }
    }
}
