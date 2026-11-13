namespace ModularRuntime.Ipc.Contracts;

// Data transfer objects of the IPC protocol. They contain only strings, numbers, GUIDs,
// timestamps, and other DTOs: never engine types, plugin objects, Type, Assembly, or delegates.

/// <summary>Lifecycle state of the daemon process.</summary>
public enum DaemonState
{
    Starting = 0,
    Running = 1,
    Stopping = 2,
}

public sealed record ConnectedClientDto(Guid ConnectionId, Guid ClientId, string ClientName, DateTimeOffset ConnectedAt);

public sealed record RuntimeStatusDto(
    DaemonState State,
    string DaemonVersion,
    string PluginApiVersion,
    int ProtocolVersion,
    Guid DaemonInstanceId,
    Guid RuntimeId,
    int ProcessId,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    long StateVersion,
    string ProfileDirectory,
    string PluginDirectory,
    string LogDirectory,
    int PluginCount,
    int RunningPluginCount,
    int FailedPluginCount,
    int ServiceCount,
    int ExtensionPointCount,
    int SubscriptionCount,
    IReadOnlyList<ConnectedClientDto> Clients,
    int StreamCount = 0);

public sealed record ErrorInfoDto(
    string Code,
    string Operation,
    string Message,
    DateTimeOffset OccurredAt,
    DiagnosticDto? Diagnostic);

public sealed record PluginSummaryDto(
    string Id,
    string Name,
    string Version,
    string State,
    int StartOrder,
    Guid? InstanceId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    bool HasError,
    string? LastErrorCode);

public sealed record ResourceDto(Guid ResourceId, string Kind, string Name, DateTimeOffset CreatedAt);

public sealed record PluginDetailsDto(
    PluginSummaryDto Summary,
    string ApiVersion,
    string EntryAssembly,
    string EntryType,
    ErrorInfoDto? LastError,
    IReadOnlyList<ResourceDto> Resources);

/// <summary>Result of a lifecycle operation, mirroring PluginOperationResult.</summary>
public sealed record OperationResultDto(
    string PluginId,
    string Operation,
    string Status,
    string? State,
    string? ErrorCode,
    string? Message,
    DiagnosticDto? Diagnostic)
{
    public bool IsSuccess => Status == "Succeeded";
}

public sealed record PluginIdRequest(string PluginId);

public sealed record PluginListResponse(long StateVersion, IReadOnlyList<PluginSummaryDto> Plugins);

public sealed record OperationResultsResponse(IReadOnlyList<OperationResultDto> Results);

public sealed record ShutdownRequest(string? Reason);

public sealed record ShutdownResponse(bool Accepted, int RunningPluginCount);

/// <summary>Owner of a runtime resource: the host or one plugin instance.</summary>
public sealed record OwnerDto(string Kind, string? PluginId, Guid? InstanceId, string Display);

public sealed record SubscriptionDto(Guid SubscriptionId, string Topic, OwnerDto Subscriber, DateTimeOffset CreatedAt);

public sealed record TopicStatsDto(string Topic, int SubscriberCount);

public sealed record EventSubscriptionsResponse(
    long StateVersion,
    IReadOnlyList<SubscriptionDto> Subscriptions,
    IReadOnlyList<TopicStatsDto> Topics);

/// <param name="Kind"><c>Json</c>, <c>Binary</c>, or <c>Typed</c> (plugin API 0.4).</param>
/// <param name="ContractType">For typed services, the contract interface (type, assembly, version) as text.</param>
public sealed record ServiceDto(
    Guid RegistrationId,
    string ServiceId,
    string Name,
    string Version,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, string> Metadata,
    OwnerDto Owner,
    bool IsAvailable,
    DateTimeOffset RegisteredAt,
    string Kind = "Json",
    string? ContractType = null);

public sealed record ServicesResponse(long StateVersion, IReadOnlyList<ServiceDto> Services);

/// <summary>A stream (plugin API 0.4): one producer, buffered delivery to any number of subscribers.</summary>
public sealed record StreamDto(
    Guid RegistrationId,
    string StreamId,
    string Name,
    string Version,
    string ItemType,
    IReadOnlyDictionary<string, string> Metadata,
    OwnerDto Owner,
    bool IsAvailable,
    int SubscriberCount,
    long Sequence,
    int DefaultCapacity,
    string DefaultFullMode,
    DateTimeOffset CreatedAt);

/// <summary>A contract assembly loaded into a shared context for typed services and streams.</summary>
public sealed record SharedContractDto(string Name, string Version, IReadOnlyList<string> Users);

public sealed record StreamsResponse(long StateVersion, IReadOnlyList<StreamDto> Streams, IReadOnlyList<SharedContractDto> SharedContracts);

public sealed record ContributionDto(
    Guid RegistrationId,
    string ContributionId,
    string ContractVersion,
    string DisplayName,
    IReadOnlyDictionary<string, string> Metadata,
    OwnerDto Owner,
    bool IsActive);

public sealed record ExtensionPointDto(
    Guid RegistrationId,
    string ExtensionPointId,
    string Name,
    string ContractVersion,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, string> Metadata,
    OwnerDto Owner,
    bool IsActive,
    IReadOnlyList<ContributionDto> Contributions);

public sealed record ExtensionsResponse(long StateVersion, IReadOnlyList<ExtensionPointDto> ExtensionPoints);

/// <summary>Severity, ordered like ModularRuntime.Contracts.LogLevel.</summary>
public enum LogLevelDto
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

public sealed record LogEntryDto(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevelDto Level,
    string Category,
    string Message,
    string? PluginId,
    Guid? InstanceId,
    DiagnosticDto? Diagnostic);

/// <summary>Log filter. Every criterion is optional; they combine with AND.</summary>
public sealed record LogFilterDto(
    LogLevelDto? MinimumLevel = null,
    string? PluginId = null,
    string? Category = null,
    string? Text = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

public sealed record LogQueryRequest(LogFilterDto? Filter = null, long? AfterSequence = null, int Limit = 500);

/// <param name="Truncated">True when entries after <c>AfterSequence</c> were already evicted from the buffer.</param>
public sealed record LogQueryResponse(IReadOnlyList<LogEntryDto> Entries, long LastSequence, bool Truncated);

public sealed record LogSubscribeRequest(LogFilterDto? Filter = null);

/// <param name="LastSequence">Sequence of the newest entry when the subscription started; later entries are streamed.</param>
public sealed record LogSubscribeResponse(Guid SubscriptionId, long LastSequence);

public sealed record SubscriptionIdRequest(Guid SubscriptionId);

/// <param name="DroppedCount">Entries dropped for this subscriber since the previous batch (slow client).</param>
public sealed record LogBatchNotification(Guid SubscriptionId, IReadOnlyList<LogEntryDto> Entries, long DroppedCount);

public sealed record StateSubscribeResponse(Guid SubscriptionId, long StateVersion);

public sealed record StateChangedNotification(long StateVersion);

public sealed record DaemonStoppingNotification(string Reason);

/// <summary>Empty payload marker.</summary>
public sealed record EmptyPayload;
