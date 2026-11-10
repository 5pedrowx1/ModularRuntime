using System.Collections.Concurrent;
using System.Text.Json;
using ModularRuntime.Contracts;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Daemon;

/// <summary>
/// Maps each authorized IPC operation onto the real runtime API. No lifecycle logic lives here:
/// starting, stopping, and querying are delegated to <see cref="ModularRuntime.Loader.PluginRuntime"/>.
/// </summary>
internal sealed class DaemonRequestHandler(DaemonHost host) : IIpcRequestHandler
{
    private const int LogBatchSize = 200;
    private static readonly TimeSpan LogBatchWindow = TimeSpan.FromMilliseconds(50);

    private readonly ConcurrentDictionary<Guid, LogPump> _logPumps = new();

    public WelcomePayload CreateWelcome(Guid connectionId) =>
        new(IpcProtocol.Version, host.InstanceId, host.Runtime.RuntimeId, DaemonHost.Version, connectionId, host.Options.MaxMessageBytes);

    public void OnConnected(IpcConnection connection) =>
        host.Logs.Write(LogLevel.Debug, "Ipc", $"Client '{connection.ClientName}' connected ({connection.ConnectionId:N}).");

    public void OnDisconnected(IpcConnection connection)
    {
        host.RemoveStateSubscribers(connection);
        host.Packages.OnConnectionClosed(connection.ConnectionId);
        foreach (var (id, pump) in _logPumps)
        {
            if (ReferenceEquals(pump.Connection, connection) && _logPumps.TryRemove(id, out _))
            {
                pump.Stop();
            }
        }

        host.Logs.Write(LogLevel.Debug, "Ipc", $"Client '{connection.ClientName}' disconnected ({connection.ConnectionId:N}).");
    }

    public async Task<object?> HandleAsync(IpcConnection connection, string operation, JsonElement? payload, CancellationToken cancellationToken)
    {
        var runtime = host.Runtime;
        switch (operation)
        {
            case IpcOperations.RuntimeStatus:
                return BuildStatus();

            case IpcOperations.RuntimeShutdown:
            {
                var request = Read<ShutdownRequest>(payload) ?? new ShutdownRequest(null);
                if (request.Reason is { Length: > 200 })
                {
                    throw Invalid("The shutdown reason is limited to 200 characters.");
                }

                var running = runtime.Plugins.Count(p => p.State == PluginState.Running);
                host.Logs.Write(LogLevel.Information, "Daemon", $"Shutdown requested by '{connection.ClientName}' ({running} plugin(s) running).");
                host.RequestShutdown(request.Reason is { Length: > 0 } reason ? $"requested by {connection.ClientName}: {reason}" : $"requested by {connection.ClientName}");
                return new ShutdownResponse(true, running);
            }

            case IpcOperations.PluginsList:
            {
                // Read the version before the snapshot: a change made while collecting only causes
                // a redundant refresh, never a missed one.
                var version = runtime.StateVersion;
                return new PluginListResponse(version, [.. runtime.Plugins.Select(DtoMapper.ToSummary)]);
            }

            case IpcOperations.PluginsGet:
            {
                var id = ReadPluginId(payload);
                var info = runtime.GetPlugin(id) ?? throw new IpcRequestException(IpcErrorCodes.PluginNotFound, $"Plugin '{id}' is not registered.");
                return DtoMapper.ToDetails(info, runtime.GetResources(id));
            }

            case IpcOperations.PluginsStart:
            {
                EnsureNotStopping();
                var id = ReadPluginId(payload);
                var result = await runtime.StartAsync(id, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess && result.ErrorCode == PluginErrorCodes.FactoryFailed)
                {
                    // The load gate may have rejected a modified installation: record why.
                    host.Packages.RecordLoadRejection(id, result.Message ?? "The start failed.");
                }

                return DtoMapper.ToResult(result);
            }

            case IpcOperations.PluginsRestart:
            {
                EnsureNotStopping();
                var id = ReadPluginId(payload);
                var result = await runtime.RestartAsync(id, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess && result.ErrorCode == PluginErrorCodes.FactoryFailed)
                {
                    host.Packages.RecordLoadRejection(id, result.Message ?? "The restart failed.");
                }

                return DtoMapper.ToResult(result);
            }

            case IpcOperations.PluginsStop:
                EnsureNotStopping();
                return DtoMapper.ToResult(await runtime.StopAsync(ReadPluginId(payload), cancellationToken).ConfigureAwait(false));

            case IpcOperations.PluginsStartAll:
                EnsureNotStopping();
                return new OperationResultsResponse([.. (await runtime.StartAllAsync(cancellationToken).ConfigureAwait(false)).Select(DtoMapper.ToResult)]);

            case IpcOperations.PluginsStopAll:
                EnsureNotStopping();
                return new OperationResultsResponse([.. (await runtime.StopAllAsync(cancellationToken).ConfigureAwait(false)).Select(DtoMapper.ToResult)]);

            case IpcOperations.EventsSubscriptions:
            {
                var version = runtime.StateVersion;
                var subscriptions = runtime.Host.Events.GetSubscriptions();
                return new EventSubscriptionsResponse(
                    version,
                    [.. subscriptions.Select(DtoMapper.ToDto)],
                    [.. subscriptions.GroupBy(s => s.Topic, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => new TopicStatsDto(g.Key, g.Count()))]);
            }

            case IpcOperations.ServicesList:
            {
                var version = runtime.StateVersion;
                return new ServicesResponse(version, [.. runtime.Host.Services.GetServices().Select(DtoMapper.ToDto)]);
            }

            case IpcOperations.ExtensionsList:
            {
                var version = runtime.StateVersion;
                var extensions = runtime.Host.Extensions;
                return new ExtensionsResponse(
                    version,
                    [.. extensions.GetExtensionPoints().Select(point => DtoMapper.ToDto(point, extensions.GetContributions(point.ExtensionPointId)))]);
            }

            case IpcOperations.LogsQuery:
            {
                var request = Read<LogQueryRequest>(payload) ?? new LogQueryRequest();
                ValidateFilter(request.Filter);
                if (request.Limit is < 1 or > 5000)
                {
                    throw Invalid("limit must be between 1 and 5000.");
                }

                return host.Logs.Query(request.Filter, request.AfterSequence, request.Limit);
            }

            case IpcOperations.LogsSubscribe:
            {
                var request = Read<LogSubscribeRequest>(payload) ?? new LogSubscribeRequest();
                ValidateFilter(request.Filter);
                if (_logPumps.Values.Count(p => ReferenceEquals(p.Connection, connection)) >= 8)
                {
                    throw Invalid("At most 8 log subscriptions per connection.");
                }

                var subscription = host.Logs.Subscribe(request.Filter);
                var pump = new LogPump(connection, subscription);
                _logPumps[subscription.Id] = pump;
                pump.Start();
                return new LogSubscribeResponse(subscription.Id, subscription.StartSequence);
            }

            case IpcOperations.LogsUnsubscribe:
            {
                var id = (Read<SubscriptionIdRequest>(payload) ?? throw Invalid("subscriptionId is required.")).SubscriptionId;
                if (!_logPumps.TryGetValue(id, out var pump) || !ReferenceEquals(pump.Connection, connection) || !_logPumps.TryRemove(id, out _))
                {
                    throw new IpcRequestException(IpcErrorCodes.SubscriptionNotFound, "No such log subscription on this connection.");
                }

                await pump.StopAsync().ConfigureAwait(false);
                return new EmptyPayload();
            }

            case IpcOperations.StateSubscribe:
                return host.SubscribeState(connection);

            case IpcOperations.StateUnsubscribe:
            {
                var id = (Read<SubscriptionIdRequest>(payload) ?? throw Invalid("subscriptionId is required.")).SubscriptionId;
                return host.UnsubscribeState(connection, id)
                    ? new EmptyPayload()
                    : throw new IpcRequestException(IpcErrorCodes.SubscriptionNotFound, "No such state subscription on this connection.");
            }

            case IpcOperations.RegistryStatus:
                return host.Packages.GetRegistryStatus();

            case IpcOperations.RegistryRefresh:
                EnsureNotStopping();
                return await host.Packages.RefreshRegistryAsync(cancellationToken).ConfigureAwait(false);

            case IpcOperations.RegistryList:
                return host.Packages.ListRegistry();

            case IpcOperations.RegistryGet:
                return host.Packages.GetRegistryEntry(ReadId(Read<RegistryGetRequest>(payload)?.PluginId));

            case IpcOperations.PackagesList:
                return host.Packages.ListInstalled();

            case IpcOperations.PackagesGet:
                return host.Packages.GetInstalled(ReadPluginId(payload));

            case IpcOperations.PackagesVerify:
                return await host.Packages.VerifyAsync(ReadPluginId(payload), cancellationToken).ConfigureAwait(false);

            case IpcOperations.PackagesCheckUpdates:
                return host.Packages.CheckUpdates();

            case IpcOperations.PackagesInstallOfficial:
            {
                EnsureNotStopping();
                var request = Read<InstallOfficialRequest>(payload) ?? throw Invalid("pluginId is required.");
                Version? version = null;
                if (request.Version is not null && !ModularRuntime.Packaging.PluginVersions.TryParse(request.Version, out version))
                {
                    throw Invalid("version must have 2 to 4 numeric components.");
                }

                return host.Packages.StartInstallOfficial(ReadId(request.PluginId), version);
            }

            case IpcOperations.PackagesUpdate:
            {
                EnsureNotStopping();
                var request = Read<PackageChangeRequest>(payload) ?? throw Invalid("pluginId is required.");
                return host.Packages.StartUpdate(ReadId(request.PluginId), request.StopIfRunning);
            }

            case IpcOperations.PackagesRemove:
            {
                EnsureNotStopping();
                var request = Read<PackageChangeRequest>(payload) ?? throw Invalid("pluginId is required.");
                return host.Packages.StartRemove(ReadId(request.PluginId), request.StopIfRunning);
            }

            case IpcOperations.PackagesUploadBegin:
                EnsureNotStopping();
                return host.Packages.BeginUpload(connection, Read<UploadBeginRequest>(payload) ?? throw Invalid("fileName and size are required."));

            case IpcOperations.PackagesUploadChunk:
                return await host.Packages.AppendChunkAsync(connection, Read<UploadChunkRequest>(payload) ?? throw Invalid("uploadId, offset, and data are required."), cancellationToken).ConfigureAwait(false);

            case IpcOperations.PackagesUploadInspect:
                return await host.Packages.InspectUploadAsync(connection, ReadUploadId(payload), cancellationToken).ConfigureAwait(false);

            case IpcOperations.PackagesUploadInstall:
                EnsureNotStopping();
                return host.Packages.StartInstallUpload(connection, Read<UploadInstallRequest>(payload) ?? throw Invalid("uploadId is required."));

            case IpcOperations.PackagesUploadDiscard:
                host.Packages.DiscardUpload(connection, ReadUploadId(payload));
                return new EmptyPayload();

            case IpcOperations.PackagesOperations:
                return new OperationsResponse(host.Packages.ListOperations());

            case IpcOperations.PackagesGetOperation:
                return host.Packages.GetOperation(ReadOperationId(payload));

            case IpcOperations.PackagesCancel:
                return host.Packages.Cancel(ReadOperationId(payload));

            default:
                throw new IpcRequestException(IpcErrorCodes.UnknownOperation, $"Unknown operation '{operation}'.");
        }
    }

    private RuntimeStatusDto BuildStatus()
    {
        var runtime = host.Runtime;
        var version = runtime.StateVersion;
        var plugins = runtime.Plugins;
        return new RuntimeStatusDto(
            host.State,
            DaemonHost.Version,
            PluginApiVersion.Supported.ToString(),
            IpcProtocol.Version,
            host.InstanceId,
            runtime.RuntimeId,
            Environment.ProcessId,
            host.StartedAt,
            Math.Round(host.Uptime.TotalSeconds, 1),
            version,
            host.Options.Profile.ProfileDirectory,
            host.Options.PluginDirectory,
            host.Options.Profile.LogDirectory,
            plugins.Count,
            plugins.Count(p => p.State == PluginState.Running),
            plugins.Count(p => p.State == PluginState.Failed),
            runtime.Host.Services.GetServices().Count,
            runtime.Host.Extensions.GetExtensionPoints().Count,
            runtime.Host.Events.GetSubscriptions().Count,
            [.. host.Server.Connections
                .Where(c => c.ClientName != "probe")
                .Select(c => new ConnectedClientDto(c.ConnectionId, c.ClientId, c.ClientName, c.ConnectedAt))]);
    }

    private void EnsureNotStopping()
    {
        if (host.IsStopping)
        {
            throw new IpcRequestException(IpcErrorCodes.DaemonStopping, "The daemon is shutting down; lifecycle operations are rejected.");
        }
    }

    private static PluginId ReadPluginId(JsonElement? payload)
    {
        var request = Read<PluginIdRequest>(payload) ?? throw Invalid("pluginId is required.");
        return PluginId.TryParse(request.PluginId, out var id)
            ? id
            : throw Invalid($"'{request.PluginId}' is not a valid plugin ID.");
    }

    private static PluginId ReadId(string? value) =>
        PluginId.TryParse(value, out var id) ? id : throw Invalid($"'{value}' is not a valid plugin ID.");

    private static Guid ReadUploadId(JsonElement? payload) =>
        (Read<UploadIdRequest>(payload) ?? throw Invalid("uploadId is required.")).UploadId;

    private static string ReadOperationId(JsonElement? payload)
    {
        var id = (Read<OperationIdRequest>(payload) ?? throw Invalid("operationId is required.")).OperationId;
        return id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower) ? id : throw Invalid("operationId is invalid.");
    }

    private static void ValidateFilter(LogFilterDto? filter)
    {
        if (filter is null)
        {
            return;
        }

        if (filter.PluginId is { Length: > IpcProtocol.MaxIdentifierLength } ||
            filter.Category is { Length: > 64 } ||
            filter.Text is { Length: > 256 } ||
            (filter.MinimumLevel is { } level && !Enum.IsDefined(level)))
        {
            throw Invalid("The log filter is invalid (plugin ≤ 128, category ≤ 64, text ≤ 256 characters).");
        }
    }

    private static T? Read<T>(JsonElement? payload) where T : class =>
        payload is { ValueKind: JsonValueKind.Object } element ? element.Deserialize<T>(IpcProtocol.JsonOptions) : null;

    private static IpcRequestException Invalid(string message) => new(IpcErrorCodes.InvalidRequest, message);

    /// <summary>Forwards one log subscription to its client in batches; owned by the handler, ended on unsubscribe or disconnect.</summary>
    private sealed class LogPump(IpcConnection connection, LogSubscription subscription)
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(connection.Closing);
        private Task _task = Task.CompletedTask;

        public IpcConnection Connection { get; } = connection;

        public void Start() => _task = Task.Run(RunAsync);

        public void Stop()
        {
            subscription.Dispose();
            _stop.Cancel();
        }

        public async Task StopAsync()
        {
            Stop();
            await _task.ConfigureAwait(false);
            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            var reader = subscription.Reader;
            var batch = new List<LogEntryDto>(LogBatchSize);
            try
            {
                while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
                {
                    await Task.Delay(LogBatchWindow, _stop.Token).ConfigureAwait(false);
                    while (batch.Count < LogBatchSize && reader.TryRead(out var entry))
                    {
                        batch.Add(entry);
                    }

                    var dropped = subscription.TakeDropped();
                    if (!Connection.TrySendNotification(IpcNotifications.LogEntries, new LogBatchNotification(subscription.Id, [.. batch], dropped)))
                    {
                        // The client is not reading: count everything in this batch as lost.
                        subscription.AddDropped(batch.Count + dropped);
                    }

                    batch.Clear();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
