using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Ipc;

/// <summary>Typed operations over one daemon connection, shared by the CLI and the desktop application.</summary>
public interface IRuntimeClient : IAsyncDisposable
{
    WelcomePayload Welcome { get; }

    bool IsConnected { get; }

    /// <summary>Completes when the connection closes.</summary>
    Task Completion { get; }

    event EventHandler<IpcNotificationEventArgs>? NotificationReceived;

    Task<RuntimeStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<ShutdownResponse> ShutdownAsync(string? reason, CancellationToken cancellationToken = default);

    Task<PluginListResponse> ListPluginsAsync(CancellationToken cancellationToken = default);

    Task<PluginDetailsDto> GetPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<OperationResultDto> StartPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<OperationResultDto> StopPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>Stops (when running) and starts again with a new instance and load context.</summary>
    Task<OperationResultDto> RestartPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<OperationResultsResponse> StartAllAsync(CancellationToken cancellationToken = default);

    Task<OperationResultsResponse> StopAllAsync(CancellationToken cancellationToken = default);

    Task<EventSubscriptionsResponse> GetSubscriptionsAsync(CancellationToken cancellationToken = default);

    Task<ServicesResponse> GetServicesAsync(CancellationToken cancellationToken = default);

    Task<ExtensionsResponse> GetExtensionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams and loaded shared contract assemblies (plugin API 0.4).</summary>
    Task<StreamsResponse> GetStreamsAsync(CancellationToken cancellationToken = default);

    Task<LogQueryResponse> QueryLogsAsync(LogQueryRequest request, CancellationToken cancellationToken = default);

    Task<LogSubscribeResponse> SubscribeLogsAsync(LogFilterDto? filter, CancellationToken cancellationToken = default);

    Task UnsubscribeLogsAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    Task<StateSubscribeResponse> SubscribeStateAsync(CancellationToken cancellationToken = default);

    Task UnsubscribeStateAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    // Official registry and package manager (v0.4)
    Task<RegistryStatusDto> GetRegistryStatusAsync(CancellationToken cancellationToken = default);

    Task<RegistryStatusDto> RefreshRegistryAsync(CancellationToken cancellationToken = default);

    Task<RegistryListResponse> ListRegistryAsync(CancellationToken cancellationToken = default);

    Task<RegistryPluginDto> GetRegistryEntryAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<PackagesListResponse> ListPackagesAsync(CancellationToken cancellationToken = default);

    Task<InstalledPackageDto> GetPackageAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<VerificationDto> VerifyPackageAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<UpdatesResponse> CheckUpdatesAsync(CancellationToken cancellationToken = default);

    Task<PackageOperationDto> InstallOfficialAsync(string pluginId, string? version = null, CancellationToken cancellationToken = default);

    Task<PackageOperationDto> UpdatePackageAsync(string pluginId, bool stopIfRunning, CancellationToken cancellationToken = default);

    Task<PackageOperationDto> RemovePackageAsync(string pluginId, bool stopIfRunning, CancellationToken cancellationToken = default);

    Task<UploadBeginResponse> BeginUploadAsync(string fileName, long size, CancellationToken cancellationToken = default);

    Task UploadChunkAsync(Guid uploadId, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    Task<PackagePreviewDto> InspectUploadAsync(Guid uploadId, CancellationToken cancellationToken = default);

    Task<PackageOperationDto> InstallUploadAsync(Guid uploadId, bool acceptUnverified, bool stopIfRunning, CancellationToken cancellationToken = default);

    Task DiscardUploadAsync(Guid uploadId, CancellationToken cancellationToken = default);

    Task<OperationsResponse> ListOperationsAsync(CancellationToken cancellationToken = default);

    Task<PackageOperationDto> GetOperationAsync(string operationId, CancellationToken cancellationToken = default);

    Task<PackageOperationDto> CancelOperationAsync(string operationId, CancellationToken cancellationToken = default);
}

/// <summary>Package upload helper shared by the CLI and the desktop app.</summary>
public static class RuntimeClientPackageExtensions
{
    /// <summary>
    /// Sends a local package to the daemon's staging area in chunks and returns the upload ID. The
    /// daemon verifies only its own staged copy, so later changes to the local file have no effect.
    /// </summary>
    public static async Task<Guid> UploadPackageAsync(
        this IRuntimeClient client,
        Stream package,
        string fileName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(package);
        var size = package.Length;
        var begin = await client.BeginUploadAsync(fileName, size, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[begin.MaxChunkBytes];
        long offset = 0;
        try
        {
            while (offset < size)
            {
                var read = await package.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - offset)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The package file ended before its expected length; it changed while it was being sent.");
                }

                await client.UploadChunkAsync(begin.UploadId, offset, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                offset += read;
                progress?.Report((double)offset / size);
            }
        }
        catch
        {
            try
            {
                await client.DiscardUploadAsync(begin.UploadId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IpcClientException)
            {
                // The daemon also discards uploads when the connection closes.
            }

            throw;
        }

        return begin.UploadId;
    }
}

/// <summary>Default <see cref="IRuntimeClient"/> over an <see cref="IpcClient"/>.</summary>
public sealed class RuntimeClient : IRuntimeClient
{
    private readonly IpcClient _client;

    private RuntimeClient(IpcClient client)
    {
        _client = client;
    }

    public WelcomePayload Welcome => _client.Welcome;

    public bool IsConnected => _client.IsConnected;

    public Task Completion => _client.Completion;

    public event EventHandler<IpcNotificationEventArgs>? NotificationReceived
    {
        add => _client.NotificationReceived += value;
        remove => _client.NotificationReceived -= value;
    }

    public static async Task<RuntimeClient> ConnectAsync(
        RuntimeProfile profile,
        string clientName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new RuntimeClient(await IpcClient.ConnectAsync(profile.PipeName, clientName, timeout, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public Task<RuntimeStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<RuntimeStatusDto>(IpcOperations.RuntimeStatus, null, cancellationToken);

    public Task<ShutdownResponse> ShutdownAsync(string? reason, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<ShutdownResponse>(IpcOperations.RuntimeShutdown, new ShutdownRequest(reason), cancellationToken);

    public Task<PluginListResponse> ListPluginsAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PluginListResponse>(IpcOperations.PluginsList, null, cancellationToken);

    public Task<PluginDetailsDto> GetPluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PluginDetailsDto>(IpcOperations.PluginsGet, new PluginIdRequest(pluginId), cancellationToken);

    public Task<OperationResultDto> StartPluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<OperationResultDto>(IpcOperations.PluginsStart, new PluginIdRequest(pluginId), cancellationToken);

    public Task<OperationResultDto> StopPluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<OperationResultDto>(IpcOperations.PluginsStop, new PluginIdRequest(pluginId), cancellationToken);

    public Task<OperationResultDto> RestartPluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<OperationResultDto>(IpcOperations.PluginsRestart, new PluginIdRequest(pluginId), cancellationToken);

    public Task<OperationResultsResponse> StartAllAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<OperationResultsResponse>(IpcOperations.PluginsStartAll, null, cancellationToken);

    public Task<OperationResultsResponse> StopAllAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<OperationResultsResponse>(IpcOperations.PluginsStopAll, null, cancellationToken);

    public Task<EventSubscriptionsResponse> GetSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<EventSubscriptionsResponse>(IpcOperations.EventsSubscriptions, null, cancellationToken);

    public Task<ServicesResponse> GetServicesAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<ServicesResponse>(IpcOperations.ServicesList, null, cancellationToken);

    public Task<ExtensionsResponse> GetExtensionsAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<ExtensionsResponse>(IpcOperations.ExtensionsList, null, cancellationToken);

    public Task<StreamsResponse> GetStreamsAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<StreamsResponse>(IpcOperations.StreamsList, null, cancellationToken);

    public Task<LogQueryResponse> QueryLogsAsync(LogQueryRequest request, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<LogQueryResponse>(IpcOperations.LogsQuery, request, cancellationToken);

    public Task<LogSubscribeResponse> SubscribeLogsAsync(LogFilterDto? filter, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<LogSubscribeResponse>(IpcOperations.LogsSubscribe, new LogSubscribeRequest(filter), cancellationToken);

    public Task UnsubscribeLogsAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        _client.SendRequestAsync(IpcOperations.LogsUnsubscribe, new SubscriptionIdRequest(subscriptionId), cancellationToken);

    public Task<StateSubscribeResponse> SubscribeStateAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<StateSubscribeResponse>(IpcOperations.StateSubscribe, null, cancellationToken);

    public Task UnsubscribeStateAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        _client.SendRequestAsync(IpcOperations.StateUnsubscribe, new SubscriptionIdRequest(subscriptionId), cancellationToken);

    public Task<RegistryStatusDto> GetRegistryStatusAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<RegistryStatusDto>(IpcOperations.RegistryStatus, null, cancellationToken);

    public Task<RegistryStatusDto> RefreshRegistryAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<RegistryStatusDto>(IpcOperations.RegistryRefresh, null, cancellationToken);

    public Task<RegistryListResponse> ListRegistryAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<RegistryListResponse>(IpcOperations.RegistryList, null, cancellationToken);

    public Task<RegistryPluginDto> GetRegistryEntryAsync(string pluginId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<RegistryPluginDto>(IpcOperations.RegistryGet, new RegistryGetRequest(pluginId), cancellationToken);

    public Task<PackagesListResponse> ListPackagesAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackagesListResponse>(IpcOperations.PackagesList, null, cancellationToken);

    public Task<InstalledPackageDto> GetPackageAsync(string pluginId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<InstalledPackageDto>(IpcOperations.PackagesGet, new PluginIdRequest(pluginId), cancellationToken);

    public Task<VerificationDto> VerifyPackageAsync(string pluginId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<VerificationDto>(IpcOperations.PackagesVerify, new PluginIdRequest(pluginId), cancellationToken);

    public Task<UpdatesResponse> CheckUpdatesAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<UpdatesResponse>(IpcOperations.PackagesCheckUpdates, null, cancellationToken);

    public Task<PackageOperationDto> InstallOfficialAsync(string pluginId, string? version = null, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackageOperationDto>(IpcOperations.PackagesInstallOfficial, new InstallOfficialRequest(pluginId, version), cancellationToken);

    public Task<PackageOperationDto> UpdatePackageAsync(string pluginId, bool stopIfRunning, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackageOperationDto>(IpcOperations.PackagesUpdate, new PackageChangeRequest(pluginId, stopIfRunning), cancellationToken);

    public Task<PackageOperationDto> RemovePackageAsync(string pluginId, bool stopIfRunning, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackageOperationDto>(IpcOperations.PackagesRemove, new PackageChangeRequest(pluginId, stopIfRunning), cancellationToken);

    public Task<UploadBeginResponse> BeginUploadAsync(string fileName, long size, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<UploadBeginResponse>(IpcOperations.PackagesUploadBegin, new UploadBeginRequest(fileName, size), cancellationToken);

    public Task UploadChunkAsync(Guid uploadId, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _client.SendRequestAsync(IpcOperations.PackagesUploadChunk, new UploadChunkRequest(uploadId, offset, Convert.ToBase64String(data.Span)), cancellationToken);

    public Task<PackagePreviewDto> InspectUploadAsync(Guid uploadId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackagePreviewDto>(IpcOperations.PackagesUploadInspect, new UploadIdRequest(uploadId), cancellationToken);

    public Task<PackageOperationDto> InstallUploadAsync(Guid uploadId, bool acceptUnverified, bool stopIfRunning, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackageOperationDto>(IpcOperations.PackagesUploadInstall, new UploadInstallRequest(uploadId, acceptUnverified, stopIfRunning), cancellationToken);

    public Task DiscardUploadAsync(Guid uploadId, CancellationToken cancellationToken = default) =>
        _client.SendRequestAsync(IpcOperations.PackagesUploadDiscard, new UploadIdRequest(uploadId), cancellationToken);

    public Task<OperationsResponse> ListOperationsAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync<OperationsResponse>(IpcOperations.PackagesOperations, null, cancellationToken);

    public Task<PackageOperationDto> GetOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackageOperationDto>(IpcOperations.PackagesGetOperation, new OperationIdRequest(operationId), cancellationToken);

    public Task<PackageOperationDto> CancelOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        _client.RequestAsync<PackageOperationDto>(IpcOperations.PackagesCancel, new OperationIdRequest(operationId), cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
