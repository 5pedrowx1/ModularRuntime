namespace ModularRuntime.Contracts;

/// <summary>Asynchronous event callback (handler). The token is canceled when the publisher cancels or the subscriber shuts down.</summary>
public delegate ValueTask PluginEventCallback(PluginEvent pluginEvent, CancellationToken cancellationToken);

/// <summary>Immutable event delivered to subscribers.</summary>
/// <param name="EventId">Unique identifier of this publication.</param>
/// <param name="Topic">Topic name (a <see cref="RuntimeName"/>), matched exactly.</param>
/// <param name="Payload">Validated JSON payload shared by all subscribers.</param>
/// <param name="Source">Owner that published the event.</param>
/// <param name="Timestamp">UTC publication time.</param>
/// <param name="CorrelationId">Optional publisher-supplied correlation identifier.</param>
/// <param name="Metadata">Simple string metadata; never <see langword="null"/>.</param>
public sealed record PluginEvent(
    Guid EventId,
    string Topic,
    JsonPayload Payload,
    ResourceOwner Source,
    DateTimeOffset Timestamp,
    string? CorrelationId,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>Optional publication settings.</summary>
public sealed record EventPublishOptions
{
    /// <summary>Correlation identifier (at most 128 characters).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>At most 32 entries; keys up to 64 characters, values up to 1024 characters.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>An event subscription owned by a host or plugin instance.</summary>
public interface IEventSubscription : IPluginResource
{
    string Topic { get; }
}

/// <summary>Immutable metadata about an active subscription.</summary>
public sealed record EventSubscriptionInfo(
    Guid SubscriptionId,
    string Topic,
    ResourceOwner Subscriber,
    DateTimeOffset CreatedAt);

/// <summary>Outcome of delivering one event to one subscription.</summary>
public sealed record EventDeliveryResult(
    Guid SubscriptionId,
    ResourceOwner Subscriber,
    PluginOperationStatus Status,
    string? ErrorCode = null,
    string? Message = null,
    PluginDiagnostic? Diagnostic = null);

/// <summary>
/// Outcome of a publication.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is <see cref="PluginOperationStatus.Succeeded"/> when every attempted
/// delivery succeeded (including when there were no subscribers), <see cref="PluginOperationStatus.Failed"/>
/// when at least one handler failed, <see cref="PluginOperationStatus.Canceled"/> when the
/// publication was canceled, and <see cref="PluginOperationStatus.Rejected"/> when the event was
/// not dispatched at all (invalid topic, payload too large, closed scope, disposed runtime,
/// dispatch depth exceeded). Subscriptions removed or whose owner is shutting down are skipped
/// and do not appear in <see cref="Deliveries"/>.
/// </remarks>
public sealed record EventPublishResult(
    Guid EventId,
    string Topic,
    PluginOperationStatus Status,
    IReadOnlyList<EventDeliveryResult> Deliveries,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool IsSuccess => Status == PluginOperationStatus.Succeeded;

    public int SucceededCount => Deliveries.Count(d => d.Status == PluginOperationStatus.Succeeded);

    public int FailedCount => Deliveries.Count(d => d.Status == PluginOperationStatus.Failed);

    public int CanceledCount => Deliveries.Count(d => d.Status == PluginOperationStatus.Canceled);
}

/// <summary>
/// In-memory publish/subscribe bus shared by the host and all plugins of one runtime.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is asynchronous and sequential in subscription order: each handler is awaited
/// before the next one runs, and <see cref="PublishAsync"/> completes when all deliveries
/// have completed. A failing handler does not stop delivery to the others.
/// </para>
/// <para>
/// Each publication dispatches to a snapshot of the subscriptions that exist when it starts.
/// Subscriptions created during dispatch receive only later events; subscriptions removed during
/// dispatch receive no further invocations. Handlers may publish events (bounded by the
/// runtime's dispatch depth limit) and subscribe or unsubscribe.
/// </para>
/// </remarks>
public interface IPluginEventBus
{
    /// <exception cref="ArgumentException">The topic is not a valid <see cref="RuntimeName"/>.</exception>
    /// <exception cref="PluginRuntimeException">The owner is shutting down (<c>scope_closed</c>) or the runtime is disposed (<c>engine_disposed</c>).</exception>
    IEventSubscription Subscribe(string topic, PluginEventCallback handler);

    ValueTask<EventPublishResult> PublishAsync(
        string topic,
        JsonPayload payload,
        EventPublishOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Snapshot of active subscriptions, optionally filtered by topic, in subscription order.</summary>
    IReadOnlyList<EventSubscriptionInfo> GetSubscriptions(string? topic = null);
}
