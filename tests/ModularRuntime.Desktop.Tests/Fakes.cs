using System.Text.Json;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Tests;

/// <summary>Runs posted work immediately; timers tick only when the test calls <see cref="Tick"/>.</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    private readonly List<Action> _timers = [];

    public void Post(Action action) => action();

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        _timers.Add(tick);
        return new Registration(() => _timers.Remove(tick));
    }

    public void Tick()
    {
        foreach (var tick in _timers.ToList())
        {
            tick();
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public List<string> Confirmations { get; } = [];

    public string? Clipboard { get; private set; }

    public bool Confirm(string title, string message, string confirmText)
    {
        Confirmations.Add(message);
        return ConfirmResult;
    }

    public void CopyToClipboard(string text) => Clipboard = text;

    public void OpenFolder(string path)
    {
    }

    public string? PackageFile { get; set; }

    public PackageInstallDecision Decision { get; set; } = PackageInstallDecision.Cancel;

    public List<PackagePreviewDto> Previews { get; } = [];

    public string? PickPackageFile() => PackageFile;

    public PackageInstallDecision ShowPackagePreview(PackagePreviewDto preview)
    {
        Previews.Add(preview);
        return Decision;
    }
}

internal sealed class FakeTheme : IThemeService
{
    public void Apply(AppTheme theme)
    {
    }
}

/// <summary>A scripted daemon connection. Every operation is a replaceable delegate.</summary>
internal sealed class FakeClient : IRuntimeClient
{
    private readonly TaskCompletionSource _completion = new();

    public FakeClient(Guid? daemonInstanceId = null)
    {
        Welcome = new WelcomePayload(IpcProtocol.Version, daemonInstanceId ?? Guid.NewGuid(), Guid.NewGuid(), "0.3.0", Guid.NewGuid(), IpcProtocol.DefaultMaxMessageBytes);
    }

    public WelcomePayload Welcome { get; }

    public bool IsConnected => !_completion.Task.IsCompleted;

    public Task Completion => _completion.Task;

    public bool Disposed { get; private set; }

    public int ShutdownCalls { get; private set; }

    public List<string> Calls { get; } = [];

    public event EventHandler<IpcNotificationEventArgs>? NotificationReceived;

    public List<PluginSummaryDto> Plugins { get; set; } = [];

    public Func<string, Task<OperationResultDto>> OnStart { get; set; } = id => Task.FromResult(Succeeded(id, "Start", "Running"));

    public Func<string, Task<OperationResultDto>> OnStop { get; set; } = id => Task.FromResult(Succeeded(id, "Stop", "Stopped"));

    public Func<Task<PluginListResponse>>? OnList { get; set; }

    public Func<LogQueryRequest, LogQueryResponse> OnQueryLogs { get; set; } = _ => new LogQueryResponse([], 0, false);

    public Guid LogSubscriptionId { get; set; } = Guid.NewGuid();

    public int RunningPluginCount { get; set; }

    public void Disconnect() => _completion.TrySetResult();

    public void Notify<T>(string name, T payload) =>
        NotificationReceived?.Invoke(this, new IpcNotificationEventArgs(name, JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions)));

    public static OperationResultDto Succeeded(string id, string operation, string state) => new(id, operation, "Succeeded", state, null, null, null);

    public static PluginSummaryDto Plugin(string id, string state) => new(id, id, "1.0.0", state, 0, null, null, null, state == "Failed", state == "Failed" ? "start_failed" : null);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Disconnect();
        return ValueTask.CompletedTask;
    }

    public Task<RuntimeStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RuntimeStatusDto(DaemonState.Running, "0.3.0", "0.3", 1, Welcome.DaemonInstanceId, Welcome.RuntimeId, 1234,
            DateTimeOffset.UtcNow, 1, 1, "p", "p\\plugins", "p\\logs", Plugins.Count, RunningPluginCount, 0, 0, 0, 0, []));

    public Task<ShutdownResponse> ShutdownAsync(string? reason, CancellationToken cancellationToken = default)
    {
        ShutdownCalls++;
        return Task.FromResult(new ShutdownResponse(true, RunningPluginCount));
    }

    public Task<PluginListResponse> ListPluginsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("list");
        return OnList?.Invoke() ?? Task.FromResult(new PluginListResponse(1, [.. Plugins]));
    }

    public Task<PluginDetailsDto> GetPluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PluginDetailsDto(Plugins.First(p => p.Id == pluginId), "0.3", "a.dll", "A.Plugin", null, []));

    public Task<OperationResultDto> StartPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        Calls.Add("start " + pluginId);
        return OnStart(pluginId);
    }

    public Task<OperationResultDto> StopPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        Calls.Add("stop " + pluginId);
        return OnStop(pluginId);
    }

    public Func<string, Task<OperationResultDto>> OnRestart { get; set; } = id => Task.FromResult(Succeeded(id, "restart", "Running"));

    public Task<OperationResultDto> RestartPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        Calls.Add("restart " + pluginId);
        return OnRestart(pluginId);
    }

    public Task<OperationResultsResponse> StartAllAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("startAll");
        return Task.FromResult(new OperationResultsResponse([.. Plugins.Select(p => Succeeded(p.Id, "Start", "Running"))]));
    }

    public Task<OperationResultsResponse> StopAllAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("stopAll");
        return Task.FromResult(new OperationResultsResponse([.. Plugins.Select(p => Succeeded(p.Id, "Stop", "Stopped"))]));
    }

    public Task<EventSubscriptionsResponse> GetSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new EventSubscriptionsResponse(1, [], []));

    public Task<ServicesResponse> GetServicesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ServicesResponse(1, []));

    public Task<ExtensionsResponse> GetExtensionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ExtensionsResponse(1, []));

    public Task<LogQueryResponse> QueryLogsAsync(LogQueryRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add($"queryLogs after={request.AfterSequence}");
        return Task.FromResult(OnQueryLogs(request));
    }

    public Task<LogSubscribeResponse> SubscribeLogsAsync(LogFilterDto? filter, CancellationToken cancellationToken = default)
    {
        Calls.Add("subscribeLogs");
        return Task.FromResult(new LogSubscribeResponse(LogSubscriptionId, 0));
    }

    public Task UnsubscribeLogsAsync(Guid subscriptionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<StateSubscribeResponse> SubscribeStateAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("subscribeState");
        return Task.FromResult(new StateSubscribeResponse(Guid.NewGuid(), 1));
    }

    public Task UnsubscribeStateAsync(Guid subscriptionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    // ----- Package manager -----

    public List<InstalledPackageDto> Packages { get; set; } = [];

    public RegistryListResponse Registry { get; set; } = new(EmptyRegistryStatus, []);

    public List<PackageUpdateDto> AvailableUpdates { get; set; } = [];

    public PackagePreviewDto? Preview { get; set; }

    public List<(Guid Id, long Offset, int Length)> Chunks { get; } = [];

    public List<Guid> Discarded { get; } = [];

    public List<(Guid UploadId, bool AcceptUnverified, bool StopIfRunning)> UploadInstalls { get; } = [];

    public static RegistryStatusDto EmptyRegistryStatus { get; } = new(true, "https://registry.example/", false, null, null, null, null, null, false, 0, 0, null, null);

    public static PackageOperationDto Operation(string pluginId, PackageOperationKindDto kind, PackageOperationStageDto stage = PackageOperationStageDto.Queued, string id = "0123456789abcdef0123456789abcdef") =>
        new(id, kind, pluginId, stage, null, stage.ToString(), stage == PackageOperationStageDto.Failed ? "integrity_mismatch" : null, DateTimeOffset.UtcNow, stage is PackageOperationStageDto.Completed or PackageOperationStageDto.Failed ? DateTimeOffset.UtcNow : null, null);

    public Task<RegistryStatusDto> GetRegistryStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Registry.Status);

    public Task<RegistryStatusDto> RefreshRegistryAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("registry.refresh");
        return Task.FromResult(Registry.Status);
    }

    public Task<RegistryListResponse> ListRegistryAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("registry.list");
        return Task.FromResult(Registry);
    }

    public Task<RegistryPluginDto> GetRegistryEntryAsync(string pluginId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Registry.Plugins.First(p => p.Id == pluginId));

    public Task<PackagesListResponse> ListPackagesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PackagesListResponse(1, [.. Packages]));

    public Task<InstalledPackageDto> GetPackageAsync(string pluginId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Packages.First(p => p.PluginId == pluginId));

    public Task<VerificationDto> VerifyPackageAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        Calls.Add("verify " + pluginId);
        return Task.FromResult(new VerificationDto("Modified", "plugin.dll: SHA-256 differs.", ["plugin.dll: SHA-256 differs"], DateTimeOffset.UtcNow));
    }

    public Task<UpdatesResponse> CheckUpdatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpdatesResponse(Registry.Status, [.. AvailableUpdates]));

    public Task<PackageOperationDto> InstallOfficialAsync(string pluginId, string? version = null, CancellationToken cancellationToken = default)
    {
        Calls.Add("install " + pluginId);
        return Task.FromResult(Operation(pluginId, PackageOperationKindDto.InstallOfficial));
    }

    public Task<PackageOperationDto> UpdatePackageAsync(string pluginId, bool stopIfRunning, CancellationToken cancellationToken = default)
    {
        Calls.Add($"update {pluginId} stop={stopIfRunning}");
        return Task.FromResult(Operation(pluginId, PackageOperationKindDto.Update));
    }

    public Task<PackageOperationDto> RemovePackageAsync(string pluginId, bool stopIfRunning, CancellationToken cancellationToken = default)
    {
        Calls.Add($"remove {pluginId} stop={stopIfRunning}");
        return Task.FromResult(Operation(pluginId, PackageOperationKindDto.Remove));
    }

    public Task<UploadBeginResponse> BeginUploadAsync(string fileName, long size, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UploadBeginResponse(Guid.Parse("11111111-1111-1111-1111-111111111111"), 4));

    public Task UploadChunkAsync(Guid uploadId, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Chunks.Add((uploadId, offset, data.Length));
        return Task.CompletedTask;
    }

    public Task<PackagePreviewDto> InspectUploadAsync(Guid uploadId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Preview ?? throw new InvalidOperationException("No preview configured."));

    public Task<PackageOperationDto> InstallUploadAsync(Guid uploadId, bool acceptUnverified, bool stopIfRunning, CancellationToken cancellationToken = default)
    {
        UploadInstalls.Add((uploadId, acceptUnverified, stopIfRunning));
        return Task.FromResult(Operation(Preview?.PluginId ?? "com.local", PackageOperationKindDto.InstallLocal));
    }

    public Task DiscardUploadAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        Discarded.Add(uploadId);
        return Task.CompletedTask;
    }

    public Task<OperationsResponse> ListOperationsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OperationsResponse([]));

    public Task<PackageOperationDto> GetOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Operation("x", PackageOperationKindDto.InstallOfficial, id: operationId));

    public Task<PackageOperationDto> CancelOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Operation("x", PackageOperationKindDto.InstallOfficial, PackageOperationStageDto.Canceled, operationId));
}

/// <summary>A connection whose state the test drives directly.</summary>
internal sealed class FakeConnection : IRuntimeConnection
{
    public RuntimeProfile Profile { get; } = RuntimeProfile.Resolve(Path.Combine(Path.GetTempPath(), "ModularRuntime.Tests", "desktop-unused"));

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string StatusMessage { get; private set; } = "Not connected.";

    public IRuntimeClient? Client { get; private set; }

    public int RetryCount { get; private set; }

    public int DaemonStartRequests { get; private set; }

    public int StopDaemonCalls { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler? StateChanged;

    public event EventHandler<long>? RuntimeStateChanged;

    public event EventHandler<LogBatchNotification>? LogBatchReceived;

    public event EventHandler<PackageOperationDto>? PackageOperationChanged;

    public void RaiseOperation(PackageOperationDto operation) => PackageOperationChanged?.Invoke(this, operation);

    public void Connect(FakeClient client)
    {
        Client = client;
        Set(ConnectionState.Connected, "Connected.");
    }

    public void Disconnect(ConnectionState state = ConnectionState.Disconnected)
    {
        Client = null;
        Set(state, "Lost the connection.");
    }

    public void Set(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseRuntimeChanged(long version) => RuntimeStateChanged?.Invoke(this, version);

    public void RaiseLogs(LogBatchNotification batch) => LogBatchReceived?.Invoke(this, batch);

    public void Start(bool autoStartDaemon)
    {
    }

    public void RetryNow() => RetryCount++;

    public void RequestDaemonStart() => DaemonStartRequests++;

    public async Task<ShutdownResponse> StopDaemonAsync(CancellationToken cancellationToken = default)
    {
        StopDaemonCalls++;
        return await Client!.ShutdownAsync("test", cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
