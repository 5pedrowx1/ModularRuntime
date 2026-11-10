using System.Collections.Concurrent;
using ModularRuntime.Contracts;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;
using ModularRuntime.Loader;
using ModularRuntime.Packaging;
using ModularRuntime.Registry;

namespace ModularRuntime.Daemon;

/// <summary>
/// The daemon's plugin package manager: the only component that writes plugin installations of the
/// profile. Clients (desktop app, CLI) send requests over IPC and observe operations; they never
/// touch package files, trust decisions, or keys.
/// </summary>
/// <remarks>
/// <para>
/// Long operations (download, verification, installation, update, removal) run as tracked background
/// operations with an ID, a stage, progress, and a final result. Progress is broadcast as
/// <c>packages.operation</c> notifications; a client that reconnects lists them again. One
/// operation per plugin at a time; a second request for the same plugin gets
/// <c>operation_in_progress</c> with the ID of the running one.
/// </para>
/// <para>
/// Order for installs and updates: the new package is downloaded or uploaded into daemon-owned
/// staging, fully verified and extracted there, and only then is the running state touched (stop if
/// requested, unregister, wait for unload), the directories swapped atomically, and the plugin
/// registered again. If the swap fails, the previous installation is registered again unchanged.
/// Nothing is started automatically.
/// </para>
/// </remarks>
internal sealed class PackageManager : IAsyncDisposable
{
    public const int MaxChunkBytes = 256 * 1024;
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(30);

    private readonly DaemonHost _host;
    private readonly PackageStore _store;
    private readonly PackageVerifier _verifier;
    private readonly RegistryClient _registry;
    private readonly ConcurrentDictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByPlugin = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Upload> _uploads = new();
    private readonly ConcurrentDictionary<string, VerificationDto> _lastVerification = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposing = new();
    private readonly SemaphoreSlim _concurrency = new(2, 2);
    private RegistrySnapshot? _snapshot;
    private RegistryException? _registryError;

    public PackageManager(DaemonHost host, PackageStore store, RegistryClient registry)
    {
        _host = host;
        _store = store;
        _verifier = new PackageVerifier(store.TrustStore, store.Limits);
        _registry = registry;
    }

    public static Version RuntimeVersion { get; } = PluginVersions.TryParse(DaemonHost.Version, out var version) ? version : new Version(0, 0);

    private PluginRuntime Runtime => _host.Runtime;

    /// <summary>Loads the cached registry at startup (offline-capable) without network access.</summary>
    public void LoadCachedRegistry()
    {
        try
        {
            _snapshot = _registry.LoadCached();
            _registryError = null;
        }
        catch (RegistryException exception)
        {
            _snapshot = null;
            _registryError = exception;
            _host.Logs.Write(LogLevel.Warning, "Packages", $"The cached registry was not accepted: [{exception.Code}] {exception.Message}");
        }

        // Offline (or with an expired catalog), revocations from the last authentic catalog still apply.
        var revocationSource = _snapshot?.Document ?? _registry.LoadCachedRevocationSource();
        if (revocationSource is not null && _snapshot is null)
        {
            _host.Logs.Write(LogLevel.Information, "Packages", $"Applying the revocations of the last verified registry (revision {revocationSource.Revision}) while it cannot be used for installations.");
        }

        ApplyRevocations(revocationSource);
    }

    /// <summary>Makes <paramref name="document"/> the revocation source and reports running plugins it revokes.</summary>
    private void ApplyRevocations(RegistryDocument? document)
    {
        _host.Revocations.Update(document);
        if (document is null)
        {
            return;
        }

        foreach (var record in _store.ListRecords())
        {
            if (RevocationOf(record, document) is not { } revocation)
            {
                continue;
            }

            var running = Runtime.GetPlugin(PluginId.Parse(record.PluginId))?.State is PluginState.Running or PluginState.Loaded;
            _host.Logs.Write(
                LogLevel.Warning,
                "Packages",
                $"'{record.PluginId}' {record.Version} is REVOKED by the official registry ({Describe(revocation)}). " +
                (running
                    ? "It keeps running until it is stopped and will not be loaded again. Stop and remove it."
                    : "It will not be loaded. The installation is kept so you can inspect and remove it."));
        }
    }

    private static RegistryRevocation? RevocationOf(InstalledPackageRecord record, RegistryDocument document) =>
        PluginVersions.TryParse(record.Version, out var version)
            ? document.FindRevocation(PluginId.Parse(record.PluginId), version, record.PackageSha256, record.Trust == PackageTrust.Official ? record.SigningKeyId : null)
            : null;

    private static string Describe(RegistryRevocation revocation) =>
        revocation.Kind == RegistryRevocationKind.SigningKey
            ? $"its signing key '{revocation.KeyId}' was revoked: {revocation.Reason}"
            : revocation.Reason;

    // ----- Registry -----

    public RegistryStatusDto GetRegistryStatus()
    {
        var snapshot = _snapshot;
        return new RegistryStatusDto(
            _registry.IsConfigured,
            _registry.Location?.ToString(),
            snapshot is not null,
            snapshot?.Document.Revision,
            snapshot?.Document.GeneratedAt,
            snapshot?.Document.ExpiresAt,
            snapshot?.VerifiedAt,
            snapshot?.SigningKeyId,
            _store.TrustStore.ContainsTestKeys,
            snapshot?.Document.Plugins.Count ?? 0,
            SafeHighestRevision(),
            _registryError?.Code,
            _registryError?.Message);
    }

    public async Task<RegistryStatusDto> RefreshRegistryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _snapshot = snapshot;
            _registryError = null;
            _host.Logs.Write(LogLevel.Information, "Packages", $"Registry revision {snapshot.Document.Revision} verified (key '{snapshot.SigningKeyId}', {snapshot.Document.Plugins.Count} entries).");
            ApplyRevocations(snapshot.Document);
        }
        catch (RegistryException exception)
        {
            _registryError = exception;
            _host.Logs.Write(LogLevel.Warning, "Packages", $"Registry refresh failed: [{exception.Code}] {exception.Message}");
            throw new IpcRequestException(exception.Code, exception.Message);
        }

        return GetRegistryStatus();
    }

    public RegistryListResponse ListRegistry()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return new RegistryListResponse(GetRegistryStatus(), []);
        }

        // The highest published version of each plugin; a plugin without a published version shows its
        // highest version with its status (withdrawn or revoked) so the Store can explain it.
        var latest = snapshot.Document.Plugins
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => snapshot.Document.Find(g.First().PluginId) ?? g.OrderByDescending(e => e.ParsedVersion, Comparer<Version>.Create(PluginVersions.Compare)).First());
        return new RegistryListResponse(GetRegistryStatus(), [.. latest.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto)]);
    }

    public RegistryPluginDto GetRegistryEntry(PluginId pluginId)
    {
        var document = RequireSnapshot().Document;
        var versions = document.Versions(pluginId);
        var entry = document.Find(pluginId) ?? (versions.Count > 0 ? versions[0] : null)
            ?? throw new IpcRequestException(RegistryErrorCodes.PluginNotFound, $"'{pluginId}' is not in the official registry.");
        return ToDto(entry);
    }

    public UpdatesResponse CheckUpdates()
    {
        var updates = new List<PackageUpdateDto>();
        if (_snapshot is { } snapshot)
        {
            foreach (var record in _store.ListRecords())
            {
                var entry = snapshot.Document.Find(PluginId.Parse(record.PluginId));
                if (entry is not null && PluginVersions.TryParse(record.Version, out var installed) &&
                    PluginVersions.Compare(entry.ParsedVersion, installed) > 0)
                {
                    var reason = Incompatibility(entry);
                    updates.Add(new PackageUpdateDto(record.PluginId, entry.Name, record.Version, entry.Version, entry.ReleaseNotes, reason is null, reason));
                }
            }
        }

        return new UpdatesResponse(GetRegistryStatus(), updates);
    }

    // ----- Installed packages -----

    public PackagesListResponse ListInstalled()
    {
        var stateVersion = Runtime.StateVersion;
        var records = _store.ListRecords().ToDictionary(r => r.PluginId, StringComparer.Ordinal);
        var packages = new List<InstalledPackageDto>();
        foreach (var info in Runtime.Plugins)
        {
            records.Remove(info.Descriptor.Id.Value, out var record);
            packages.Add(ToDto(info.Descriptor.Id.Value, info.Descriptor.Name, info.Descriptor.Version.ToString(), record, info.State.ToString(), Runtime.GetInstallation(info.Descriptor.Id)));
        }

        foreach (var record in records.Values)
        {
            packages.Add(ToDto(record.PluginId, record.Name, record.Version, record, engineState: null, candidate: null));
        }

        return new PackagesListResponse(stateVersion, [.. packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    public InstalledPackageDto GetInstalled(PluginId pluginId) =>
        ListInstalled().Packages.FirstOrDefault(p => p.PluginId == pluginId.Value)
        ?? throw new IpcRequestException(IpcErrorCodes.PluginNotFound, $"'{pluginId}' is not installed.");

    /// <summary>Re-verifies a managed installation now (signature and every file). Loads nothing.</summary>
    public async Task<VerificationDto> VerifyAsync(PluginId pluginId, CancellationToken cancellationToken)
    {
        if (_store.GetRecord(pluginId) is null)
        {
            throw new IpcRequestException(IpcErrorCodes.NotManaged, $"'{pluginId}' was not installed by the package manager; there is no recorded file list to verify against.");
        }

        if (Runtime.GetPlugin(pluginId)?.State is PluginState.Running or PluginState.Loaded)
        {
            // The files are locked by the running instance's verification lease and were verified when it loaded.
            var running = new VerificationDto("Verified", "Verified when the running instance was loaded; its files are locked against modification while it runs.", [], DateTimeOffset.UtcNow);
            _lastVerification[pluginId.Value] = running;
            return running;
        }

        var result = await Task.Run(() => _store.Verify(pluginId), cancellationToken).ConfigureAwait(false);
        var dto = new VerificationDto(result.Status.ToString(), result.Message, result.Problems, result.CheckedAt);
        _lastVerification[pluginId.Value] = dto;
        _host.Logs.Write(
            result.Status == InstallationStatus.Verified ? LogLevel.Information : LogLevel.Warning,
            "Packages",
            $"Verification of '{pluginId}': {result.Status}. {result.Message}");
        return dto;
    }

    // ----- Operations -----

    public PackageOperationDto StartInstallOfficial(PluginId pluginId, Version? version)
    {
        var entry = RequireSnapshot().Document.Find(pluginId, version)
            ?? throw new IpcRequestException(RegistryErrorCodes.PluginNotFound, $"'{pluginId}'{(version is null ? " has no published version" : $" {version} is not")} in the official registry.");
        EnsureInstallable(entry);
        if (Incompatibility(entry) is { } reason)
        {
            throw new IpcRequestException(PackageErrorCodes.RuntimeVersionIncompatible, reason);
        }

        if (_store.GetRecord(pluginId) is not null)
        {
            throw new IpcRequestException(IpcErrorCodes.AlreadyInstalled, $"'{pluginId}' is already installed; use update.");
        }

        if (Runtime.GetPlugin(pluginId) is not null)
        {
            throw new IpcRequestException(PackageErrorCodes.InstallationConflict, $"A plugin with ID '{pluginId}' is already present (copied into the plugin folder by hand); remove it first.");
        }

        return Begin(PackageOperationKindDto.InstallOfficial, pluginId, (operation, token) => InstallFromRegistryAsync(operation, entry, stopIfRunning: false, token));
    }

    public PackageOperationDto StartUpdate(PluginId pluginId, bool stopIfRunning)
    {
        var record = _store.GetRecord(pluginId)
            ?? throw new IpcRequestException(IpcErrorCodes.NotManaged, $"'{pluginId}' was not installed by the package manager.");
        var entry = RequireSnapshot().Document.Find(pluginId)
            ?? throw new IpcRequestException(RegistryErrorCodes.PluginNotFound, $"'{pluginId}' is not in the official registry.");
        if (!PluginVersions.TryParse(record.Version, out var installed) || PluginVersions.Compare(entry.ParsedVersion, installed) <= 0)
        {
            throw new IpcRequestException(IpcErrorCodes.AlreadyInstalled, $"'{pluginId}' {record.Version} is already the latest version.");
        }

        EnsureInstallable(entry);
        if (Incompatibility(entry) is { } reason)
        {
            throw new IpcRequestException(PackageErrorCodes.RuntimeVersionIncompatible, reason);
        }

        EnsureNotRunningUnlessAllowed(pluginId, stopIfRunning);
        return Begin(PackageOperationKindDto.Update, pluginId, (operation, token) => InstallFromRegistryAsync(operation, entry, stopIfRunning, token));
    }

    public PackageOperationDto StartRemove(PluginId pluginId, bool stopIfRunning)
    {
        if (_store.GetRecord(pluginId) is null)
        {
            throw new IpcRequestException(IpcErrorCodes.NotManaged, $"'{pluginId}' was not installed by the package manager; remove its folder manually.");
        }

        EnsureNotRunningUnlessAllowed(pluginId, stopIfRunning);
        return Begin(PackageOperationKindDto.Remove, pluginId, (operation, token) => RemoveAsync(operation, pluginId, stopIfRunning, token));
    }

    public IReadOnlyList<PackageOperationDto> ListOperations() =>
        [.. _operations.Values.Select(o => o.Snapshot()).OrderByDescending(o => o.StartedAt)];

    public PackageOperationDto GetOperation(string operationId) =>
        _operations.TryGetValue(operationId, out var operation)
            ? operation.Snapshot()
            : throw new IpcRequestException(IpcErrorCodes.OperationNotFound, $"Operation '{operationId}' was not found.");

    public PackageOperationDto Cancel(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            throw new IpcRequestException(IpcErrorCodes.OperationNotFound, $"Operation '{operationId}' was not found.");
        }

        operation.RequestCancel();
        return operation.Snapshot();
    }

    // ----- Local ZIP uploads -----

    public UploadBeginResponse BeginUpload(IpcConnection connection, UploadBeginRequest request)
    {
        if (request.Size <= 0 || request.Size > _store.Limits.MaxPackageBytes)
        {
            throw new IpcRequestException(PackageErrorCodes.PackageTooLarge, $"Packages must be 1 byte to {_store.Limits.MaxPackageBytes} bytes.");
        }

        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 260)
        {
            throw new IpcRequestException(IpcErrorCodes.InvalidRequest, "fileName is required (at most 260 characters).");
        }

        if (_uploads.Values.Count(u => u.ConnectionId == connection.ConnectionId) >= 4)
        {
            throw new IpcRequestException(IpcErrorCodes.InvalidRequest, "At most 4 uploads per connection.");
        }

        var id = Guid.NewGuid();
        var directory = _store.CreateOperationDirectory("upload-" + id.ToString("N"));
        var upload = new Upload(id, connection.ConnectionId, Path.GetFileName(request.FileName), request.Size, directory);
        _uploads[id] = upload;
        return new UploadBeginResponse(id, MaxChunkBytes);
    }

    public async Task<EmptyPayload> AppendChunkAsync(IpcConnection connection, UploadChunkRequest request, CancellationToken cancellationToken)
    {
        var upload = GetUpload(connection, request.UploadId);
        byte[] data;
        try
        {
            data = Convert.FromBase64String(request.Data ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new IpcRequestException(IpcErrorCodes.InvalidRequest, "data must be base64.");
        }

        if (data.Length is 0 or > MaxChunkBytes)
        {
            throw new IpcRequestException(IpcErrorCodes.InvalidRequest, $"Chunks are 1 to {MaxChunkBytes} bytes.");
        }

        await upload.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (upload.Completed || request.Offset != upload.Received || upload.Received + data.Length > upload.Size)
            {
                throw new IpcRequestException(IpcErrorCodes.InvalidRequest, $"Unexpected chunk: offset {request.Offset}, received {upload.Received} of {upload.Size}.");
            }

            await using (var file = new FileStream(upload.PackagePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

            upload.Received += data.Length;
            upload.Touch();
        }
        finally
        {
            upload.Lock.Release();
        }

        return new EmptyPayload();
    }

    /// <summary>Verifies an uploaded package completely (in staging) and describes it. Installs nothing.</summary>
    public async Task<PackagePreviewDto> InspectUploadAsync(IpcConnection connection, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = GetUpload(connection, uploadId);
        await upload.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (upload.Received != upload.Size)
            {
                throw new IpcRequestException(IpcErrorCodes.InvalidRequest, $"The upload is incomplete ({upload.Received} of {upload.Size} bytes).");
            }

            upload.Completed = true;
            var inspectDirectory = Path.Combine(upload.Directory, "inspect-" + Guid.NewGuid().ToString("N"));
            try
            {
                var package = await _verifier.VerifyAndExtractAsync(upload.PackagePath, inspectDirectory, new PackageExpectation(), cancellationToken: cancellationToken).ConfigureAwait(false);
                var existing = Runtime.GetPlugin(package.PluginId);
                var record = _store.GetRecord(package.PluginId);
                return new PackagePreviewDto(
                    uploadId,
                    true,
                    null,
                    null,
                    package.PluginId.Value,
                    package.Candidate.Descriptor.Name,
                    package.Version.ToString(),
                    package.Candidate.Author,
                    package.Candidate.Description,
                    package.Candidate.Descriptor.ApiVersion.ToString(),
                    package.Trust == PackageTrust.Official ? TrustStateDto.OfficialVerified : TrustStateDto.LocalUnverified,
                    package.SigningKey?.KeyId,
                    package.SigningKey?.IsTestKey ?? false,
                    package.Files.Count,
                    package.PackageSize,
                    package.PackageSha256,
                    true,
                    record?.Version ?? existing?.Descriptor.Version.ToString(),
                    record is not null,
                    existing?.State is PluginState.Running or PluginState.Loaded);
            }
            catch (PackageException exception)
            {
                return new PackagePreviewDto(uploadId, false, exception.Code, exception.Message, null, null, null, null, null, null,
                    exception.Code is PackageErrorCodes.ApiVersionIncompatible ? TrustStateDto.Incompatible : TrustStateDto.VerificationFailed,
                    null, false, 0, upload.Size, null, exception.Code is not PackageErrorCodes.ApiVersionIncompatible, null, false, false);
            }
            finally
            {
                PackageStoreCleanup(inspectDirectory);
            }
        }
        finally
        {
            upload.Lock.Release();
        }
    }

    public PackageOperationDto StartInstallUpload(IpcConnection connection, UploadInstallRequest request)
    {
        var upload = GetUpload(connection, request.UploadId);
        if (!upload.Completed || upload.Received != upload.Size)
        {
            throw new IpcRequestException(IpcErrorCodes.InvalidRequest, "Inspect the upload before installing it.");
        }

        _uploads.TryRemove(upload.Id, out _);
        return Begin(PackageOperationKindDto.InstallLocal, pluginId: null, (operation, token) => InstallUploadAsync(operation, upload, request, token), upload);
    }

    public void DiscardUpload(IpcConnection connection, Guid uploadId)
    {
        var upload = GetUpload(connection, uploadId);
        if (_uploads.TryRemove(upload.Id, out _))
        {
            PackageStoreCleanup(upload.Directory);
        }
    }

    /// <summary>Uploads belong to their connection: discard them when it closes.</summary>
    public void OnConnectionClosed(Guid connectionId)
    {
        foreach (var upload in _uploads.Values.Where(u => u.ConnectionId == connectionId).ToList())
        {
            if (_uploads.TryRemove(upload.Id, out _))
            {
                PackageStoreCleanup(upload.Directory);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync().ConfigureAwait(false);
        var running = _operations.Values.Select(o => o.Task).ToArray();
        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _host.Logs.Write(LogLevel.Warning, "Packages", "Package operations did not finish in time; staging recovery will complete or roll them back at the next start.");
        }

        foreach (var upload in _uploads.Values)
        {
            PackageStoreCleanup(upload.Directory);
        }

        _disposing.Dispose();
        _registry.Dispose();
    }

    /// <summary>The trust shown for a registered plugin (used by plugins.list consumers and start policy).</summary>
    public TrustStateDto GetTrust(PluginId pluginId)
    {
        var record = _store.GetRecord(pluginId);
        return record is null ? TrustStateDto.Unmanaged : Trust(record);
    }

    // ----- Operation bodies -----

    private async Task<InstalledPackageDto> InstallFromRegistryAsync(Operation operation, RegistryEntry entry, bool stopIfRunning, CancellationToken token)
    {
        var pluginId = entry.PluginId;
        var directory = _store.CreateOperationDirectory(operation.Id);
        try
        {
            operation.Update(PackageOperationStageDto.Downloading, 0, $"Downloading {entry.Name} {entry.Version}…");
            var packagePath = Path.Combine(directory, "package.zip");
            await _registry.DownloadPackageAsync(
                entry,
                packagePath,
                new Progress<long>(bytes => operation.Update(PackageOperationStageDto.Downloading, Math.Min(1.0, (double)bytes / entry.PackageSize), $"Downloading {entry.Name} {entry.Version}…")),
                token).ConfigureAwait(false);

            var package = await VerifyAsync(operation, packagePath, directory, new PackageExpectation
            {
                PluginId = pluginId,
                Version = entry.ParsedVersion,
                PackageSha256 = entry.PackageSha256,
                SigningKeyId = entry.SigningKeyId,
                RequireOfficialSignature = true,
            }, token).ConfigureAwait(false);

            return await PublishAsync(operation, package, PackageSource.Registry, _snapshot?.Document.Revision, directory, stopIfRunning, token).ConfigureAwait(false);
        }
        catch
        {
            PackageStoreCleanup(directory);
            throw;
        }
    }

    private async Task<InstalledPackageDto> InstallUploadAsync(Operation operation, Upload upload, UploadInstallRequest request, CancellationToken token)
    {
        try
        {
            var package = await VerifyAsync(operation, upload.PackagePath, upload.Directory, new PackageExpectation(), token).ConfigureAwait(false);
            operation.PluginId = package.PluginId.Value;
            if (!_activeByPlugin.TryAdd(package.PluginId.Value, operation.Id))
            {
                throw new IpcRequestException(IpcErrorCodes.OperationInProgress, $"Another operation is already changing '{package.PluginId}'.");
            }

            try
            {
                if ((_snapshot?.Document ?? _host.Revocations.Source) is { } revocationSource &&
                    revocationSource.FindRevocation(package.PluginId, package.Version, package.PackageSha256, package.SigningKey?.KeyId) is { } revoked)
                {
                    throw new IpcRequestException(RegistryErrorCodes.VersionRevoked, $"'{package.PluginId}' {package.Version} is revoked by the official registry: {Describe(revoked)}");
                }

                if (package.Trust != PackageTrust.Official && !request.AcceptUnverified)
                {
                    throw new IpcRequestException(IpcErrorCodes.UnverifiedNotAccepted, "The package has no official signature; installing it requires explicit acceptance of the unverified origin.");
                }

                if (_store.GetRecord(package.PluginId) is null && Runtime.GetPlugin(package.PluginId) is not null)
                {
                    throw new IpcRequestException(PackageErrorCodes.InstallationConflict, $"A plugin with ID '{package.PluginId}' is already present (copied into the plugin folder by hand); remove it first.");
                }

                if (_store.GetRecord(package.PluginId) is not null)
                {
                    EnsureNotRunningUnlessAllowed(package.PluginId, request.StopIfRunning);
                }

                return await PublishAsync(operation, package, PackageSource.LocalZip, registryRevision: null, upload.Directory, request.StopIfRunning, token).ConfigureAwait(false);
            }
            finally
            {
                _activeByPlugin.TryRemove(new KeyValuePair<string, string>(package.PluginId.Value, operation.Id));
            }
        }
        finally
        {
            PackageStoreCleanup(upload.Directory);
        }
    }

    private async Task<VerifiedPackage> VerifyAsync(Operation operation, string packagePath, string directory, PackageExpectation expectation, CancellationToken token)
    {
        operation.Update(PackageOperationStageDto.Verifying, null, "Verifying the package…");
        try
        {
            return await _verifier.VerifyAndExtractAsync(
                packagePath,
                Path.Combine(directory, "content-" + Guid.NewGuid().ToString("N")),
                expectation,
                new Progress<VerificationStage>(stage => operation.Update(PackageOperationStageDto.Verifying, null, Describe(stage))),
                token).ConfigureAwait(false);
        }
        catch (PackageException exception)
        {
            _host.Logs.Write(LogLevel.Warning, "Packages", $"Package rejected: [{exception.Code}] {exception.Message}");
            throw;
        }
    }

    private async Task<InstalledPackageDto> PublishAsync(
        Operation operation,
        VerifiedPackage package,
        PackageSource source,
        long? registryRevision,
        string operationDirectory,
        bool stopIfRunning,
        CancellationToken token)
    {
        var pluginId = package.PluginId;
        var previous = Runtime.GetInstallation(pluginId);
        var replacing = _store.GetRecord(pluginId) is not null;
        if (replacing)
        {
            await TakeOutOfRuntimeAsync(operation, pluginId, stopIfRunning, token).ConfigureAwait(false);
        }

        // From here on the operation is not cancelable: the swap must complete or roll back.
        operation.Update(PackageOperationStageDto.Installing, null, $"Installing {package.Candidate.Descriptor.Name} {package.Version}…");
        InstalledPackageRecord record;
        try
        {
            record = _store.Publish(operationDirectory, package, source, registryRevision);
        }
        catch (PackageException exception)
        {
            _host.Logs.Write(LogLevel.Error, "Packages", $"Installing '{pluginId}' failed; the previous state is kept: {exception.Message}");
            if (previous is not null)
            {
                await Runtime.RegisterInstallationAsync(previous.PluginDirectory, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        var registration = await Runtime.RegisterInstallationAsync(_store.GetInstallationDirectory(pluginId), CancellationToken.None).ConfigureAwait(false);
        var verification = new VerificationDto("Verified", "Verified during installation.", [], DateTimeOffset.UtcNow);
        _lastVerification[pluginId.Value] = verification;
        _host.Logs.Write(
            LogLevel.Information,
            "Packages",
            $"Installed '{pluginId}' {record.Version} from {(source == PackageSource.Registry ? "the official registry" : "a local ZIP")} " +
            $"({(record.Trust == PackageTrust.Official ? $"official signature, key '{record.SigningKeyId}'" : "UNVERIFIED origin")}, {record.FileCount} files verified). " +
            (registration.RegisteredCount == 1 ? "Ready to start." : "It could not be registered; see the discovery errors."));
        return ToDto(record.PluginId, record.Name, record.Version, record, Runtime.GetPlugin(pluginId)?.State.ToString(), Runtime.GetInstallation(pluginId));
    }

    private async Task<InstalledPackageDto> RemoveAsync(Operation operation, PluginId pluginId, bool stopIfRunning, CancellationToken token)
    {
        var previous = Runtime.GetInstallation(pluginId);
        await TakeOutOfRuntimeAsync(operation, pluginId, stopIfRunning, token).ConfigureAwait(false);
        operation.Update(PackageOperationStageDto.Removing, null, $"Removing '{pluginId}'…");
        var record = _store.GetRecord(pluginId)!;
        var directory = _store.CreateOperationDirectory(operation.Id);
        try
        {
            _store.Remove(directory, pluginId);
        }
        catch (PackageException exception)
        {
            PackageStoreCleanup(directory);
            _host.Logs.Write(LogLevel.Error, "Packages", $"Removing '{pluginId}' failed; it stays installed: {exception.Message}");
            if (previous is not null)
            {
                await Runtime.RegisterInstallationAsync(previous.PluginDirectory, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        _lastVerification.TryRemove(pluginId.Value, out _);
        _host.Logs.Write(LogLevel.Information, "Packages", $"Removed '{pluginId}' {record.Version}.");
        return ToDto(record.PluginId, record.Name, record.Version, record, engineState: null, candidate: null);
    }

    /// <summary>Stops (when allowed), unregisters, and waits for the load context to unload.</summary>
    private async Task TakeOutOfRuntimeAsync(Operation operation, PluginId pluginId, bool stopIfRunning, CancellationToken token)
    {
        var info = Runtime.GetPlugin(pluginId);
        if (info is null)
        {
            return;
        }

        if (info.State is PluginState.Running or PluginState.Loaded or PluginState.Stopping)
        {
            if (!stopIfRunning)
            {
                throw new IpcRequestException(IpcErrorCodes.PluginRunning, $"'{pluginId}' is running; stop it first or allow stopping it.");
            }

            operation.Update(PackageOperationStageDto.Stopping, null, $"Stopping '{pluginId}'…");
            var stop = await Runtime.StopAsync(pluginId, token).ConfigureAwait(false);
            if (!stop.IsSuccess && Runtime.GetPlugin(pluginId)?.State is not (PluginState.Stopped or PluginState.Failed))
            {
                throw new IpcRequestException(IpcErrorCodes.OperationFailed, $"'{pluginId}' could not be stopped: {stop.Message}");
            }
        }

        var unregister = await Runtime.UnregisterAsync(pluginId, token).ConfigureAwait(false);
        if (!unregister.IsSuccess)
        {
            throw new IpcRequestException(IpcErrorCodes.OperationFailed, $"'{pluginId}' could not be taken out of the runtime: {unregister.Message}");
        }

        // Explicit maintenance: the files are about to be replaced or deleted, so collection is
        // induced here (and only here) to release the plugin's assemblies.
        if (!await Runtime.WaitForUnloadAsync(pluginId, UnloadTimeout, PluginUnloadWait.InduceCollection, CancellationToken.None).ConfigureAwait(false))
        {
            _host.Logs.Write(LogLevel.Warning, "Packages", $"The load context of '{pluginId}' has not unloaded after {UnloadTimeout.TotalSeconds:0}s (the plugin keeps references); its files may still be in use.");
        }
    }

    // ----- Helpers -----

    private PackageOperationDto Begin(PackageOperationKindDto kind, PluginId? pluginId, Func<Operation, CancellationToken, Task<InstalledPackageDto>> body, Upload? upload = null)
    {
        if (_host.IsStopping)
        {
            throw new IpcRequestException(IpcErrorCodes.DaemonStopping, "The daemon is shutting down.");
        }

        var operation = new Operation(Guid.NewGuid().ToString("N"), kind, pluginId?.Value, Notify, _disposing.Token);
        if (pluginId is { } id && !_activeByPlugin.TryAdd(id.Value, operation.Id))
        {
            upload?.Dispose(this);
            throw new IpcRequestException(IpcErrorCodes.OperationInProgress, $"Operation {_activeByPlugin.GetValueOrDefault(id.Value)} is already changing '{id}'.");
        }

        _operations[operation.Id] = operation;
        TrimFinishedOperations();
        operation.Task = Task.Run(async () =>
        {
            try
            {
                await _concurrency.WaitAsync(operation.Token).ConfigureAwait(false);
                try
                {
                    var result = await body(operation, operation.Token).ConfigureAwait(false);
                    operation.Complete(result);
                }
                finally
                {
                    _concurrency.Release();
                }
            }
            catch (OperationCanceledException)
            {
                operation.Fail(PackageOperationStageDto.Canceled, "canceled", "The operation was canceled; nothing was changed.");
            }
            catch (PackageException exception)
            {
                operation.Fail(PackageOperationStageDto.Failed, exception.Code, exception.Message);
            }
            catch (RegistryException exception)
            {
                operation.Fail(PackageOperationStageDto.Failed, exception.Code, exception.Message);
            }
            catch (IpcRequestException exception)
            {
                operation.Fail(PackageOperationStageDto.Failed, exception.Error.Code, exception.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                operation.Fail(PackageOperationStageDto.Failed, PackageErrorCodes.InstallationFailed, exception.Message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _host.Logs.Log(new LogEntry(LogLevel.Error, $"Package operation {operation.Id} failed unexpectedly: {exception.Message}", "Packages", Diagnostic: PluginDiagnostic.FromException(exception)));
                operation.Fail(PackageOperationStageDto.Failed, IpcErrorCodes.InternalError, $"The operation failed unexpectedly: {exception.Message}");
            }
            finally
            {
                if (pluginId is { } id)
                {
                    _activeByPlugin.TryRemove(new KeyValuePair<string, string>(id.Value, operation.Id));
                }
            }
        });
        return operation.Snapshot();
    }

    private void Notify(PackageOperationDto snapshot)
    {
        _host.Server.Broadcast(IpcNotifications.PackageOperation, snapshot);
        if (snapshot.IsFinished)
        {
            var level = snapshot.Stage == PackageOperationStageDto.Completed ? LogLevel.Information : LogLevel.Warning;
            _host.Logs.Write(level, "Packages", $"{snapshot.Kind} {snapshot.PluginId ?? "(package)"}: {snapshot.Stage}. {snapshot.Message}");
        }
    }

    private void TrimFinishedOperations()
    {
        foreach (var old in _operations.Values.Where(o => o.Snapshot().IsFinished).OrderByDescending(o => o.Snapshot().StartedAt).Skip(50).ToList())
        {
            _operations.TryRemove(old.Id, out _);
        }
    }

    private void EnsureNotRunningUnlessAllowed(PluginId pluginId, bool stopIfRunning)
    {
        if (!stopIfRunning && Runtime.GetPlugin(pluginId)?.State is PluginState.Running or PluginState.Loaded or PluginState.Stopping)
        {
            throw new IpcRequestException(IpcErrorCodes.PluginRunning, $"'{pluginId}' is running; stop it first or allow stopping it.");
        }
    }

    private RegistrySnapshot RequireSnapshot() =>
        _snapshot ?? throw new IpcRequestException(
            _registry.IsConfigured ? RegistryErrorCodes.Unavailable : RegistryErrorCodes.NotConfigured,
            _registry.IsConfigured ? "The official registry has not been loaded; refresh it first." : "No official registry is configured.");

    private Upload GetUpload(IpcConnection connection, Guid uploadId)
    {
        foreach (var expired in _uploads.Values.Where(u => u.IsExpired(UploadLifetime)).ToList())
        {
            if (_uploads.TryRemove(expired.Id, out _))
            {
                PackageStoreCleanup(expired.Directory);
            }
        }

        return _uploads.TryGetValue(uploadId, out var upload) && upload.ConnectionId == connection.ConnectionId
            ? upload
            : throw new IpcRequestException(IpcErrorCodes.UploadNotFound, "No such upload on this connection.");
    }

    private long SafeHighestRevision()
    {
        try
        {
            return _registry.HighestAcceptedRevision;
        }
        catch (RegistryException)
        {
            return -1;
        }
    }

    /// <summary>Withdrawn and revoked versions, and packages signed by a revoked key, are never installed.</summary>
    private void EnsureInstallable(RegistryEntry entry)
    {
        if (entry.IsRevoked)
        {
            throw new IpcRequestException(RegistryErrorCodes.VersionRevoked, $"'{entry.Id}' {entry.Version} was revoked: {entry.StatusReason}");
        }

        if (!entry.IsPublished)
        {
            throw new IpcRequestException(RegistryErrorCodes.VersionWithdrawn, $"'{entry.Id}' {entry.Version} was withdrawn from the Store{(entry.StatusReason is null ? "." : $": {entry.StatusReason}")}");
        }

        if (_snapshot?.Document.RevokedKeys?.FirstOrDefault(k => k.KeyId == entry.SigningKeyId) is { } key)
        {
            throw new IpcRequestException(RegistryErrorCodes.VersionRevoked, $"'{entry.Id}' {entry.Version} is signed by key '{key.KeyId}', which was revoked: {key.Reason}");
        }
    }

    private static string? Incompatibility(RegistryEntry entry)
    {
        if (!PluginVersions.TryParse(entry.MinimumRuntimeVersion, out var minimum) || PluginVersions.Compare(RuntimeVersion, minimum) < 0)
        {
            return $"Requires ModularRuntime {entry.MinimumRuntimeVersion} or later (this is {DaemonHost.Version}).";
        }

        if (!Version.TryParse(entry.ApiVersion, out var api) || !PluginApiVersion.IsCompatible(api))
        {
            return $"Targets plugin API {entry.ApiVersion}; this runtime supports {PluginApiVersion.Supported}.";
        }

        return null;
    }

    private RegistryPluginDto ToDto(RegistryEntry entry)
    {
        var record = _store.GetRecord(entry.PluginId);
        var installed = record?.Version ?? Runtime.GetPlugin(entry.PluginId)?.Descriptor.Version.ToString();
        var reason = Incompatibility(entry);
        var update = record is not null && PluginVersions.TryParse(record.Version, out var v) && PluginVersions.Compare(entry.ParsedVersion, v) > 0;

        // Whether this daemon would accept the package's signing key (verified again at install).
        var key = _store.TrustStore.Keys.FirstOrDefault(k => k.KeyId == entry.SigningKeyId);
        var keyTrusted = key is { Revoked: false } && key.Purposes.HasFlag(KeyPurposes.Packages);
        var document = _snapshot?.Document;
        var keyRevoked = key is { Revoked: true } || document?.RevokedKeys?.Any(k => k.KeyId == entry.SigningKeyId) == true;
        var versions = document?.Versions(entry.PluginId)
            .Select(v => new RegistryVersionDto(v.Version, v.EffectiveStatus, v.ApiVersion, v.MinimumRuntimeVersion, v.PublishedAt, Incompatibility(v) is null, v.StatusReason))
            .ToList();
        return new RegistryPluginDto(
            entry.Id, entry.Name, entry.Description, entry.Author, entry.Category, entry.Version, entry.ApiVersion, entry.MinimumRuntimeVersion,
            entry.PublishedAt, entry.PackageSize, entry.PackageSha256, entry.SigningKeyId, entry.License, entry.SourceRepository, entry.SourceCommit,
            entry.ReleaseNotes, reason is null, reason, installed, update && entry.IsPublished, keyTrusted, keyTrusted && key!.IsTestKey,
            entry.EffectiveStatus, entry.StatusReason, entry.AdvisoryUrl, entry.BuildId, entry.ApprovalSha256, versions, keyRevoked);
    }

    private InstalledPackageDto ToDto(string pluginId, string name, string version, InstalledPackageRecord? record, string? engineState, PluginCandidate? candidate)
    {
        _lastVerification.TryGetValue(pluginId, out var verification);
        string? available = null;
        if (record is not null && _snapshot?.Document.Find(PluginId.Parse(pluginId)) is { } entry &&
            PluginVersions.TryParse(record.Version, out var installed) && PluginVersions.Compare(entry.ParsedVersion, installed) > 0)
        {
            available = entry.Version;
        }

        var trust = record is null ? TrustStateDto.Unmanaged : Trust(record);
        if (record is not null && verification is not null)
        {
            trust = verification.Status switch
            {
                nameof(InstallationStatus.Modified) or nameof(InstallationStatus.Missing) => TrustStateDto.Modified,
                nameof(InstallationStatus.SignatureInvalid) => TrustStateDto.VerificationFailed,
                _ => trust,
            };
        }

        RevocationDto? revocationDto = null;
        string? catalogStatus = null;
        string? catalogStatusReason = null;
        if (record is not null)
        {
            if (_host.Revocations.Source is { } source && RevocationOf(record, source) is { } revocation)
            {
                trust = TrustStateDto.Revoked;
                revocationDto = new RevocationDto(revocation.Kind == RegistryRevocationKind.Package ? "package" : "signing-key", revocation.Reason, revocation.AdvisoryUrl, revocation.RevokedAt, revocation.KeyId);
            }

            if (_snapshot?.Document is { } catalog && PluginVersions.TryParse(record.Version, out var recordVersion) &&
                catalog.Find(PluginId.Parse(record.PluginId), recordVersion) is { } catalogEntry && Sha256Hex.Equal(catalogEntry.PackageSha256, record.PackageSha256))
            {
                catalogStatus = catalogEntry.EffectiveStatus;
                catalogStatusReason = catalogEntry.StatusReason;
            }
        }

        return new InstalledPackageDto(
            pluginId,
            name,
            version,
            record?.Author ?? candidate?.Author,
            record?.Description ?? candidate?.Description,
            record is null ? "manual" : record.Source == PackageSource.Registry ? "registry" : "local-zip",
            trust,
            record?.SigningKeyId,
            record?.SignedWithTestKey ?? false,
            record?.PackageSha256,
            record?.FileCount ?? 0,
            record?.InstalledAt,
            engineState,
            verification,
            available,
            revocationDto,
            catalogStatus,
            catalogStatusReason);
    }

    private static TrustStateDto Trust(InstalledPackageRecord record) =>
        record.Trust == PackageTrust.Official ? TrustStateDto.OfficialVerified : TrustStateDto.LocalUnverified;

    /// <summary>Records a verification failure detected when a start was rejected by the load gate.</summary>
    public void RecordLoadRejection(PluginId pluginId, string message)
    {
        if (_store.GetRecord(pluginId) is null)
        {
            return;
        }

        var result = _store.Verify(pluginId);
        _lastVerification[pluginId.Value] = new VerificationDto(result.Status.ToString(), result.Status == InstallationStatus.Verified ? message : result.Message, result.Problems, result.CheckedAt);
    }

    private void PackageStoreCleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                _store.DeleteOperationDirectory(directory);
            }
        }
        catch (InvalidOperationException)
        {
            // Not a staging directory; never delete it.
        }
    }

    private static string Describe(VerificationStage stage) => stage switch
    {
        VerificationStage.HashingPackage => "Checking the package SHA-256…",
        VerificationStage.ReadingArchive => "Checking the archive structure and limits…",
        VerificationStage.CheckingSignature => "Checking the signature…",
        VerificationStage.Extracting => "Extracting into the staging area…",
        VerificationStage.CheckingFiles => "Checking the SHA-256 of every file…",
        _ => "Checking plugin.json and compatibility…",
    };

    /// <summary>One tracked operation.</summary>
    private sealed class Operation
    {
        private readonly object _sync = new();
        private readonly Action<PackageOperationDto> _notify;
        private readonly CancellationTokenSource _cancel;
        private PackageOperationDto _snapshot;
        private DateTime _lastProgressNotification;

        public Operation(string id, PackageOperationKindDto kind, string? pluginId, Action<PackageOperationDto> notify, CancellationToken disposing)
        {
            Id = id;
            _notify = notify;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(disposing);
            _snapshot = new PackageOperationDto(id, kind, pluginId, PackageOperationStageDto.Queued, null, "Queued.", null, DateTimeOffset.UtcNow, null, null);
        }

        public string Id { get; }

        public Task Task { get; set; } = Task.CompletedTask;

        public CancellationToken Token => _cancel.Token;

        public string? PluginId
        {
            set => Set(s => s with { PluginId = value }, force: true);
        }

        public PackageOperationDto Snapshot()
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }

        public void RequestCancel()
        {
            if (!Snapshot().IsFinished && Snapshot().Stage is not (PackageOperationStageDto.Installing or PackageOperationStageDto.Removing))
            {
                _cancel.Cancel();
            }
        }

        public void Update(PackageOperationStageDto stage, double? progress, string message)
        {
            var stageChanged = Snapshot().Stage != stage;
            Set(s => s with { Stage = stage, Progress = progress, Message = message }, force: stageChanged);
        }

        public void Complete(InstalledPackageDto result) =>
            Set(s => s with { Stage = PackageOperationStageDto.Completed, Progress = 1, Message = $"{s.Kind} of '{result.PluginId}' {result.Version} completed.", Result = result, CompletedAt = DateTimeOffset.UtcNow }, force: true);

        public void Fail(PackageOperationStageDto stage, string code, string message) =>
            Set(s => s with { Stage = stage, ErrorCode = code, Message = message, CompletedAt = DateTimeOffset.UtcNow }, force: true);

        private void Set(Func<PackageOperationDto, PackageOperationDto> change, bool force)
        {
            PackageOperationDto snapshot;
            lock (_sync)
            {
                if (_snapshot.IsFinished)
                {
                    return;
                }

                _snapshot = change(_snapshot);
                snapshot = _snapshot;
                var now = DateTime.UtcNow;
                if (!force && now - _lastProgressNotification < TimeSpan.FromMilliseconds(200))
                {
                    return;
                }

                _lastProgressNotification = now;
            }

            _notify(snapshot);
        }
    }

    /// <summary>A local ZIP being uploaded by one connection into its own staging directory.</summary>
    private sealed class Upload(Guid id, Guid connectionId, string fileName, long size, string directory)
    {
        private long _lastActivity = DateTime.UtcNow.Ticks;

        public Guid Id { get; } = id;

        public Guid ConnectionId { get; } = connectionId;

        public string FileName { get; } = fileName;

        public long Size { get; } = size;

        public string Directory { get; } = directory;

        public string PackagePath => Path.Combine(Directory, "package.zip");

        public long Received { get; set; }

        public bool Completed { get; set; }

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public void Touch() => Interlocked.Exchange(ref _lastActivity, DateTime.UtcNow.Ticks);

        public bool IsExpired(TimeSpan lifetime) => DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivity), DateTimeKind.Utc) > lifetime;

        public void Dispose(PackageManager owner) => owner.PackageStoreCleanup(Directory);
    }
}
