using ModularRuntime.Loader;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;

namespace ModularRuntime.Daemon;

/// <summary>
/// The daemon's load gate. Before a managed installation is loaded, it refuses packages revoked by
/// the last verified official catalog (the exact package of a version, or any package signed by a
/// revoked key), then runs the installation's integrity verification (<see cref="PackageLoadVerifier"/>).
/// </summary>
/// <remarks>
/// <para>
/// The revocation source is only ever a catalog whose signature was verified with an official
/// registry key (<see cref="Update"/> is called by the package manager after verification). An
/// expired catalog may still be the source: an authentic revocation stays in force. Nothing else
/// (local files, IPC requests) can revoke or un-revoke a package.
/// </para>
/// <para>
/// Policy for a plugin that is already running when its revocation arrives: it is not stopped
/// automatically (the daemon logs a warning and clients show it); it is never loaded again. Offline,
/// the daemon knows only the revocations of the last catalog it verified.
/// </para>
/// </remarks>
internal sealed class RevocationGate(PackageStore store, IPluginLoadVerifier inner) : IPluginLoadVerifier
{
    private RegistryDocument? _source;

    /// <summary>The verified catalog whose revocations are enforced (may be expired), or <see langword="null"/>.</summary>
    public RegistryDocument? Source => Volatile.Read(ref _source);

    public void Update(RegistryDocument? verifiedDocument) => Volatile.Write(ref _source, verifiedDocument);

    public IDisposable? Verify(PluginCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var record = store.FindRecordForDirectory(candidate.PluginDirectory);
        if (record is not null && Source is { } source && PluginVersions.TryParse(record.Version, out var version) &&
            source.FindRevocation(candidate.Descriptor.Id, version, record.PackageSha256, record.Trust == PackageTrust.Official ? record.SigningKeyId : null) is { } revocation)
        {
            var reason = revocation.Kind == RegistryRevocationKind.SigningKey
                ? $"its signing key '{revocation.KeyId}' was revoked ({revocation.Reason})"
                : revocation.Reason;
            throw new PluginLoadException(
                PluginLoadErrorCodes.InstallationRevoked,
                candidate.Descriptor.Id,
                $"Plugin '{candidate.Descriptor.Id}' {record.Version} was not loaded: it is revoked by the official registry: {reason}");
        }

        return inner.Verify(candidate);
    }
}
