using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Events;

/// <summary>
/// In-memory event bus of one runtime.
/// </summary>
/// <remarks>
/// <para>
/// Subscriptions are stored per topic in copy-on-write arrays guarded by one lock. Publishing
/// takes the lock only to read the current array (the snapshot) and then dispatches without
/// any lock held. Subscribing or unsubscribing during dispatch therefore never blocks and never
/// changes the snapshot being dispatched.
/// </para>
/// <para>
/// Dispatch is sequential in subscription order. Before each handler runs, the subscription is
/// admitted through its owner's scope (rejected once the owner is shutting down) and checked
/// again for removal. Removal is effective for every invocation that has not yet passed that
/// check; invocations already running complete normally (they observe cancellation only if their
/// owner shuts down or the publisher cancels).
/// </para>
/// </remarks>
internal sealed class EventBus
{
    private static readonly AsyncLocal<int> DispatchDepth = new();

    private readonly RuntimeCore _core;
    private readonly object _sync = new();
    private readonly Dictionary<string, SubscriptionEntry[]> _byTopic = new(StringComparer.Ordinal);
    private long _sequence;

    public EventBus(RuntimeCore core)
    {
        _core = core;
    }

    public IEventSubscription Subscribe(OwnershipScope scope, string topic, PluginEventCallback handler)
    {
        RuntimeName.Validate(topic, nameof(topic));
        ArgumentNullException.ThrowIfNull(handler);
        _core.EnsureCanRegister(scope);

        var entry = new SubscriptionEntry(this, scope, topic, handler);
        lock (_sync)
        {
            entry.Sequence = ++_sequence;
            _byTopic[topic] = _byTopic.TryGetValue(topic, out var existing) ? [.. existing, entry] : [entry];
        }

        // Register with the scope after the bus: if the scope began closing in between, undo.
        if (!scope.TryAdd(entry))
        {
            Remove(entry);
            throw RuntimeCore.ScopeClosed(scope);
        }

        return entry;
    }

    public IReadOnlyList<EventSubscriptionInfo> GetSubscriptions(string? topic)
    {
        SubscriptionEntry[] entries;
        lock (_sync)
        {
            entries = topic is null
                ? [.. _byTopic.Values.SelectMany(array => array)]
                : _byTopic.TryGetValue(topic, out var array) ? array : [];
        }

        // IsActive reads the owner scope's state; never do that while holding the bus lock.
        return entries
            .Where(entry => entry.IsActive)
            .OrderBy(entry => entry.Sequence)
            .Select(entry => entry.ToSubscriptionInfo())
            .ToArray();
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byTopic.Values.Sum(array => array.Length);
            }
        }
    }

    public async ValueTask<EventPublishResult> PublishAsync(
        OwnershipScope publisher,
        string topic,
        JsonPayload payload,
        EventPublishOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(payload);
        var eventId = Guid.NewGuid();

        if (!RuntimeName.IsValid(topic))
        {
            return Rejected(eventId, topic, PluginErrorCodes.InvalidIdentifier, $"'{topic}' is not a valid topic name.");
        }

        if (_core.IsDisposed)
        {
            return Rejected(eventId, topic, PluginErrorCodes.EngineDisposed, "The runtime has been disposed.");
        }

        if (!publisher.CanCallOut)
        {
            return Rejected(eventId, topic, PluginErrorCodes.ScopeClosed, $"Owner '{publisher.Owner}' has stopped and cannot publish.");
        }

        if (!_core.IsPayloadWithinLimit(payload))
        {
            return Rejected(eventId, topic, PluginErrorCodes.PayloadTooLarge, _core.PayloadTooLargeMessage(payload));
        }

        var metadata = Validation.TryCopyMetadata(options?.Metadata, out var metadataError);
        if (metadata is null)
        {
            return Rejected(eventId, topic, PluginErrorCodes.InvalidMetadata, metadataError!);
        }

        var correlationId = options?.CorrelationId;
        if (correlationId is not null && (correlationId.Length == 0 || correlationId.Length > Validation.MaxCorrelationIdLength))
        {
            return Rejected(eventId, topic, PluginErrorCodes.InvalidMetadata, $"Correlation IDs must have 1-{Validation.MaxCorrelationIdLength} characters.");
        }

        if (DispatchDepth.Value >= _core.Limits.MaxEventDispatchDepth)
        {
            return Rejected(
                eventId,
                topic,
                PluginErrorCodes.EventDispatchDepthExceeded,
                $"Event dispatch depth limit ({_core.Limits.MaxEventDispatchDepth}) exceeded; the event was not published.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new EventPublishResult(eventId, topic, PluginOperationStatus.Canceled, [], PluginErrorCodes.OperationCanceled, "The publication was canceled before dispatch.");
        }

        SubscriptionEntry[] snapshot;
        lock (_sync)
        {
            snapshot = _byTopic.TryGetValue(topic, out var array) ? array : [];
        }

        var pluginEvent = new PluginEvent(eventId, topic, payload, publisher.Owner, DateTimeOffset.UtcNow, correlationId, metadata);
        return await DispatchAsync(pluginEvent, snapshot, cancellationToken).ConfigureAwait(false);
    }

    internal void Remove(SubscriptionEntry entry)
    {
        lock (_sync)
        {
            if (!_byTopic.TryGetValue(entry.Topic, out var existing))
            {
                return;
            }

            var index = Array.IndexOf(existing, entry);
            if (index < 0)
            {
                return;
            }

            if (existing.Length == 1)
            {
                _byTopic.Remove(entry.Topic);
                return;
            }

            var updated = new SubscriptionEntry[existing.Length - 1];
            Array.Copy(existing, 0, updated, 0, index);
            Array.Copy(existing, index + 1, updated, index, existing.Length - index - 1);
            _byTopic[entry.Topic] = updated;
        }
    }

    private async ValueTask<EventPublishResult> DispatchAsync(
        PluginEvent pluginEvent,
        SubscriptionEntry[] snapshot,
        CancellationToken cancellationToken)
    {
        // Async method: the depth change is visible to handlers and reverted for the caller.
        DispatchDepth.Value++;

        var deliveries = new List<EventDeliveryResult>(snapshot.Length);
        foreach (var entry in snapshot)
        {
            if (entry.IsReleased)
            {
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                deliveries.Add(new EventDeliveryResult(entry.ResourceId, entry.Owner, PluginOperationStatus.Canceled, PluginErrorCodes.OperationCanceled, "The publication was canceled before this delivery."));
                continue;
            }

            var delivery = await DeliverAsync(entry, pluginEvent, cancellationToken).ConfigureAwait(false);
            if (delivery is not null)
            {
                deliveries.Add(delivery);
            }
        }

        var status = cancellationToken.IsCancellationRequested && deliveries.Any(d => d.Status == PluginOperationStatus.Canceled)
            ? PluginOperationStatus.Canceled
            : deliveries.Any(d => d.Status == PluginOperationStatus.Failed)
                ? PluginOperationStatus.Failed
                : PluginOperationStatus.Succeeded;

        return new EventPublishResult(pluginEvent.EventId, pluginEvent.Topic, status, deliveries);
    }

    /// <summary>Returns <see langword="null"/> when the subscription was skipped (removed or owner closing).</summary>
    private async ValueTask<EventDeliveryResult?> DeliverAsync(
        SubscriptionEntry entry,
        PluginEvent pluginEvent,
        CancellationToken cancellationToken)
    {
        if (!entry.Scope.TryEnterInvocation(out var scopeToken))
        {
            return null;
        }

        var completedAsynchronously = false;
        ValueTask pending = default;
        try
        {
            var handler = entry.Handler;
            if (handler is null || entry.IsReleased)
            {
                return null;
            }

            InvocationFrames.Enter(_core.RuntimeId, entry.Scope.OwnerKey);
            using var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scopeToken)
                : null;
            var token = linked?.Token ?? scopeToken;

            try
            {
                pending = handler(pluginEvent, token);
                handler = null;
                completedAsynchronously = !pending.IsCompleted;
                await pending.ConfigureAwait(false);
                pending = default;
                return new EventDeliveryResult(entry.ResourceId, entry.Owner, PluginOperationStatus.Succeeded);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new EventDeliveryResult(entry.ResourceId, entry.Owner, PluginOperationStatus.Canceled, PluginErrorCodes.OperationCanceled, "The handler observed cancellation.");
            }
            catch (Exception exception) when (!ExceptionClassifier.IsFatal(exception))
            {
                var diagnostic = PluginDiagnostic.FromException(exception);
                exception = null!;
                var message = $"Event handler for topic '{pluginEvent.Topic}' failed: {diagnostic.Message}";
                _core.Log(LogLevel.Warning, "Events", message, entry.Owner, diagnostic);
                return new EventDeliveryResult(entry.ResourceId, entry.Owner, PluginOperationStatus.Failed, PluginErrorCodes.HandlerFailed, message, diagnostic);
            }
        }
        finally
        {
            pending = default;
            entry.Scope.ExitInvocation();
            if (completedAsynchronously)
            {
                // The next subscriber and the publisher must not run on top of this subscriber's
                // frames; see ThreadPoolHop.
                await new ThreadPoolHop();
            }
        }
    }

    private static EventPublishResult Rejected(Guid eventId, string topic, string code, string message) =>
        new(eventId, topic, PluginOperationStatus.Rejected, [], code, message);
}

internal sealed class SubscriptionEntry : OwnedResource, IEventSubscription
{
    private readonly EventBus _bus;
    private PluginEventCallback? _handler;

    public SubscriptionEntry(EventBus bus, OwnershipScope scope, string topic, PluginEventCallback handler)
        : base(scope, ResourceKind.EventSubscription, topic)
    {
        _bus = bus;
        _handler = handler;
        Topic = topic;
    }

    public string Topic { get; }

    public long Sequence { get; set; }

    public PluginEventCallback? Handler => Volatile.Read(ref _handler);

    public EventSubscriptionInfo ToSubscriptionInfo() => new(ResourceId, Topic, Owner, CreatedAt);

    protected override ValueTask ReleaseCoreAsync()
    {
        _bus.Remove(this);
        Volatile.Write(ref _handler, null);
        return ValueTask.CompletedTask;
    }
}
