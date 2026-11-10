namespace ModularRuntime.Contracts;

/// <summary>Stable error codes used in results and <see cref="PluginRuntimeException.ErrorCode"/>.</summary>
public static class PluginErrorCodes
{
    // Lifecycle
    public const string PluginNotFound = "plugin_not_found";
    public const string DuplicatePluginId = "duplicate_plugin_id";
    public const string InvalidState = "invalid_state";
    public const string InvalidDescriptor = "invalid_descriptor";
    public const string ApiVersionIncompatible = "api_version_incompatible";
    public const string EngineDisposed = "engine_disposed";
    public const string OperationCanceled = "operation_canceled";
    public const string FactoryFailed = "factory_failed";
    public const string InitializationFailed = "initialization_failed";
    public const string ShutdownFailed = "shutdown_failed";
    public const string ReleaseFailed = "release_failed";
    public const string EngineShutdownFailed = "engine_shutdown_failed";
    public const string CancellationCallbackFailed = "cancellation_callback_failed";
    public const string ReentrantLifecycleOperation = "reentrant_lifecycle_operation";
    public const string ResourceCleanupFailed = "resource_cleanup_failed";
    public const string DrainTimeout = "drain_timeout";

    // Ownership and validation
    public const string ScopeClosed = "scope_closed";
    public const string InvalidIdentifier = "invalid_identifier";
    public const string InvalidMetadata = "invalid_metadata";
    public const string InvalidPayload = "invalid_payload";
    public const string PayloadTooLarge = "payload_too_large";

    // Invocation
    public const string HandlerFailed = "handler_failed";
    public const string InvalidResponse = "invalid_response";
    public const string OperationNotSupported = "operation_not_supported";

    // Events
    public const string EventDispatchDepthExceeded = "event_dispatch_depth_exceeded";

    // Services
    public const string DuplicateService = "duplicate_service";
    public const string ServiceNotFound = "service_not_found";
    public const string ServiceUnavailable = "service_unavailable";
    public const string ServiceRemoved = "service_removed";
    public const string ServiceVersionIncompatible = "service_version_incompatible";

    // Extensions
    public const string DuplicateExtensionPoint = "duplicate_extension_point";
    public const string ExtensionPointNotFound = "extension_point_not_found";
    public const string ExtensionPointUnavailable = "extension_point_unavailable";
    public const string ExtensionVersionIncompatible = "extension_version_incompatible";
    public const string DuplicateContribution = "duplicate_contribution";
    public const string ContributionNotFound = "contribution_not_found";
    public const string ContributionUnavailable = "contribution_unavailable";
}
