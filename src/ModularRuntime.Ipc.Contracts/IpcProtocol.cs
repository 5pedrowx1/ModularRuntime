using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModularRuntime.Ipc.Contracts;

/// <summary>Constants of the ModularRuntime local IPC protocol.</summary>
/// <remarks>
/// Frames are <c>[uint32 little-endian length][UTF-8 JSON IpcMessage]</c>. The protocol version
/// must match exactly; there is no negotiation between unreleased versions. See docs/IPC_PROTOCOL.md.
/// </remarks>
public static class IpcProtocol
{
    /// <summary>Current protocol version. Client and daemon must use the same value.</summary>
    public const int Version = 1;

    /// <summary>Default maximum size of one frame payload (JSON bytes).</summary>
    public const int DefaultMaxMessageBytes = 1024 * 1024;

    /// <summary>Absolute upper bound accepted by any configuration.</summary>
    public const int HardMaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Maximum concurrent in-flight requests per connection.</summary>
    public const int MaxConcurrentRequestsPerConnection = 32;

    /// <summary>Maximum length of string identifiers carried in requests.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>JSON settings shared by every party.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 32,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Kinds of protocol messages.</summary>
public static class IpcMessageTypes
{
    /// <summary>Client → daemon, first message: <see cref="HelloPayload"/>.</summary>
    public const string Hello = "hello";

    /// <summary>Daemon → client, handshake answer: <see cref="WelcomePayload"/>.</summary>
    public const string Welcome = "welcome";

    /// <summary>Client → daemon: operation <see cref="IpcMessage.Op"/> with id and payload.</summary>
    public const string Request = "request";

    /// <summary>Daemon → client: result or error for the request with the same id.</summary>
    public const string Response = "response";

    /// <summary>Client → daemon: cancel the in-flight request with the same id.</summary>
    public const string Cancel = "cancel";

    /// <summary>Daemon → client: unsolicited message named by <see cref="IpcMessage.Op"/>.</summary>
    public const string Notification = "notification";

    /// <summary>Daemon → client: protocol-level failure (handshake, framing); the connection closes.</summary>
    public const string Error = "error";
}

/// <summary>Operations a client may request. Every operation is explicitly authorized and validated by the daemon.</summary>
public static class IpcOperations
{
    public const string RuntimeStatus = "runtime.status";
    public const string RuntimeShutdown = "runtime.shutdown";

    public const string PluginsList = "plugins.list";
    public const string PluginsGet = "plugins.get";
    public const string PluginsStart = "plugins.start";
    public const string PluginsStop = "plugins.stop";

    /// <summary>Stop (when running) and start again with a new instance and load context.</summary>
    public const string PluginsRestart = "plugins.restart";
    public const string PluginsStartAll = "plugins.startAll";
    public const string PluginsStopAll = "plugins.stopAll";

    public const string EventsSubscriptions = "events.subscriptions";
    public const string ServicesList = "services.list";
    public const string ExtensionsList = "extensions.list";

    /// <summary>Streams and loaded shared contract assemblies (plugin API 0.4).</summary>
    public const string StreamsList = "streams.list";

    public const string LogsQuery = "logs.query";
    public const string LogsSubscribe = "logs.subscribe";
    public const string LogsUnsubscribe = "logs.unsubscribe";

    public const string StateSubscribe = "state.subscribe";
    public const string StateUnsubscribe = "state.unsubscribe";

    // Official registry (v0.4)
    public const string RegistryStatus = "registry.status";
    public const string RegistryRefresh = "registry.refresh";
    public const string RegistryList = "registry.list";
    public const string RegistryGet = "registry.get";

    // Package manager (v0.4)
    public const string PackagesList = "packages.list";
    public const string PackagesGet = "packages.get";
    public const string PackagesVerify = "packages.verify";
    public const string PackagesCheckUpdates = "packages.checkUpdates";
    public const string PackagesInstallOfficial = "packages.installOfficial";
    public const string PackagesUpdate = "packages.update";
    public const string PackagesRemove = "packages.remove";
    public const string PackagesUploadBegin = "packages.upload.begin";
    public const string PackagesUploadChunk = "packages.upload.chunk";
    public const string PackagesUploadInspect = "packages.upload.inspect";
    public const string PackagesUploadInstall = "packages.upload.install";
    public const string PackagesUploadDiscard = "packages.upload.discard";
    public const string PackagesOperations = "packages.operations";
    public const string PackagesGetOperation = "packages.getOperation";
    public const string PackagesCancel = "packages.cancel";

    /// <summary>Every operation the daemon accepts; anything else is <c>unknown_operation</c>.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RuntimeStatus, RuntimeShutdown,
        PluginsList, PluginsGet, PluginsStart, PluginsStop, PluginsRestart, PluginsStartAll, PluginsStopAll,
        EventsSubscriptions, ServicesList, ExtensionsList, StreamsList,
        LogsQuery, LogsSubscribe, LogsUnsubscribe,
        StateSubscribe, StateUnsubscribe,
        RegistryStatus, RegistryRefresh, RegistryList, RegistryGet,
        PackagesList, PackagesGet, PackagesVerify, PackagesCheckUpdates,
        PackagesInstallOfficial, PackagesUpdate, PackagesRemove,
        PackagesUploadBegin, PackagesUploadChunk, PackagesUploadInspect, PackagesUploadInstall, PackagesUploadDiscard,
        PackagesOperations, PackagesGetOperation, PackagesCancel,
    };
}

/// <summary>Names of daemon notifications.</summary>
public static class IpcNotifications
{
    /// <summary><see cref="StateChangedNotification"/>: the runtime state version advanced; refresh snapshots.</summary>
    public const string StateChanged = "state.changed";

    /// <summary><see cref="LogBatchNotification"/>: new log entries for a subscription.</summary>
    public const string LogEntries = "logs.entries";

    /// <summary><see cref="DaemonStoppingNotification"/>: the daemon is shutting down.</summary>
    public const string DaemonStopping = "daemon.stopping";

    /// <summary><see cref="PackageOperationDto"/>: a package operation changed stage or progress (sent to every client).</summary>
    public const string PackageOperation = "packages.operation";
}

/// <summary>Stable error codes in <see cref="IpcError.Code"/>.</summary>
public static class IpcErrorCodes
{
    // Protocol
    public const string ProtocolVersionMismatch = "protocol_version_mismatch";
    public const string HandshakeRequired = "handshake_required";
    public const string InvalidMessage = "invalid_message";
    public const string MessageTooLarge = "message_too_large";
    public const string UnknownOperation = "unknown_operation";
    public const string InvalidRequest = "invalid_request";
    public const string TooManyRequests = "too_many_requests";
    public const string Canceled = "canceled";
    public const string InternalError = "internal_error";

    // Daemon state
    public const string DaemonStopping = "daemon_stopping";

    // Runtime
    public const string PluginNotFound = "plugin_not_found";
    public const string OperationFailed = "operation_failed";
    public const string SubscriptionNotFound = "subscription_not_found";

    // Package manager (v0.4). Package verification failures use the package error codes
    // (for example signature_invalid, integrity_mismatch, unsafe_entry_path), documented in
    // docs/PLUGIN_PACKAGE_FORMAT.md.
    public const string OperationInProgress = "operation_in_progress";
    public const string OperationNotFound = "operation_not_found";
    public const string UploadNotFound = "upload_not_found";
    public const string PluginRunning = "plugin_running";
    public const string UnverifiedNotAccepted = "unverified_not_accepted";
    public const string AlreadyInstalled = "already_installed";
    public const string NotManaged = "not_managed";

    // Client-side
    public const string ConnectionLost = "connection_lost";
    public const string DaemonUnavailable = "daemon_unavailable";
}
