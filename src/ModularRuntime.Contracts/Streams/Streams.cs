using System.Threading.Channels;

namespace ModularRuntime.Contracts;

/// <summary>What happens when a subscriber's buffer is full.</summary>
public enum StreamFullMode
{
    /// <summary>The producer's <see cref="IStreamProducer{T}.WriteAsync"/> waits for room (backpressure). Default.</summary>
    Wait = 0,

    /// <summary>The oldest buffered item is discarded; the subscriber sees a gap in the sequence numbers.</summary>
    DropOldest = 1,

    /// <summary>The new item is discarded for that subscriber; the subscriber sees a gap in the sequence numbers.</summary>
    DropNewest = 2,
}

/// <summary>Definition supplied when creating a stream.</summary>
/// <param name="StreamId">Unique stream identifier (a <see cref="RuntimeName"/>).</param>
/// <param name="Name">Display name, 1-200 characters.</param>
/// <param name="Version">Stream contract version; subscribers may require a minimum.</param>
public sealed record StreamDefinition(
    string StreamId,
    string Name,
    Version Version)
{
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Buffer capacity per subscriber unless the subscriber chooses its own (default 256, bounded by the runtime).</summary>
    public int? DefaultCapacity { get; init; }

    /// <summary>Full-buffer policy per subscriber unless the subscriber chooses its own.</summary>
    public StreamFullMode DefaultFullMode { get; init; } = StreamFullMode.Wait;

    /// <summary>
    /// For <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/> streams: copy every item on write
    /// (default, safe for pooled producer buffers). When <see langword="false"/> the producer's memory
    /// is shared read-only with every subscriber and must never be modified or returned to a pool.
    /// </summary>
    public bool CopyBinaryItems { get; init; } = true;
}

/// <summary>Per-subscription settings.</summary>
public sealed record StreamSubscribeOptions
{
    /// <summary>Buffer capacity (1 to the runtime maximum); defaults to the stream's default capacity.</summary>
    public int? Capacity { get; init; }

    /// <summary>Full-buffer policy; defaults to the stream's default.</summary>
    public StreamFullMode? FullMode { get; init; }

    /// <summary>Minimum compatible stream version (same major, version &gt;= minimum).</summary>
    public Version? MinimumVersion { get; init; }
}

/// <summary>Immutable snapshot describing a stream.</summary>
public sealed record StreamDescriptor(
    Guid RegistrationId,
    string StreamId,
    string Name,
    Version Version,
    string ItemTypeName,
    IReadOnlyDictionary<string, string> Metadata,
    ResourceOwner Owner,
    bool IsAvailable,
    int SubscriberCount,
    long Sequence,
    int DefaultCapacity,
    StreamFullMode DefaultFullMode,
    DateTimeOffset CreatedAt);

/// <summary>One item of a stream with its position. Gaps in <see cref="Sequence"/> mean items were dropped for this subscriber.</summary>
public readonly record struct StreamMessage<T>(long Sequence, T Item);

/// <summary>Why a subscription's reader completed.</summary>
public sealed record StreamCompletion(string ErrorCode, string Message);

/// <summary>
/// The producer side of a stream, owned by the host or plugin instance that created it.
/// </summary>
/// <typeparam name="T">
/// A shared type: from the framework, from <c>ModularRuntime.Contracts</c> (for example
/// <see cref="JsonPayload"/>), or from a shared contract assembly. Items are delivered to every
/// subscriber without copying (except binary items when <see cref="StreamDefinition.CopyBinaryItems"/>
/// is set), so they must be immutable once written. When the item type does not settle what an
/// item may contain (<see cref="object"/>, an interface, a non-sealed class, a type with such
/// fields), every written item is verified like a typed-service value and refused with
/// <c>value_not_shared</c> or <c>value_not_verifiable</c> (host setting <c>RuntimeLimits.TypedValueCheck</c>).
/// </typeparam>
/// <remarks>
/// Writes fan out to every current subscriber. With <see cref="StreamFullMode.Wait"/> the slowest
/// subscriber throttles the producer; with a drop mode the producer never waits. Disposing the
/// stream (or the owner stopping) completes every subscriber with <c>stream_removed</c>.
/// </remarks>
public interface IStreamProducer<T> : IPluginResource
{
    StreamDescriptor Descriptor { get; }

    int SubscriberCount { get; }

    /// <summary>Sequence number of the last item written (0 before the first).</summary>
    long Sequence { get; }

    /// <summary>
    /// Delivers <paramref name="item"/> to every subscriber, waiting for subscribers in
    /// <see cref="StreamFullMode.Wait"/> mode. Returns <see langword="false"/> when the stream is
    /// completed or removed, or the owner is shutting down.
    /// </summary>
    ValueTask<bool> WriteAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers <paramref name="item"/> without waiting. Returns <see langword="false"/> when the
    /// stream is unusable or when a subscriber in <see cref="StreamFullMode.Wait"/> mode is full
    /// (that subscriber misses the item and sees a sequence gap; the others receive it).
    /// </summary>
    bool TryWrite(T item);

    /// <summary>Ends the stream: subscribers read the buffered items, then complete with <paramref name="completion"/> (default <c>stream_completed</c>).</summary>
    void Complete(StreamCompletion? completion = null);
}

/// <summary>
/// The consumer side of a stream, owned by the subscribing host or plugin instance.
/// </summary>
/// <remarks>
/// <see cref="Reader"/> completes when the stream is completed or removed, when the subscription
/// is disposed, or when the subscriber stops; <see cref="Completion"/> then says why. Items
/// buffered before completion remain readable.
/// </remarks>
public interface IStreamSubscription<T> : IPluginResource
{
    string StreamId { get; }

    /// <summary>Registration ID of the stream this subscription is bound to.</summary>
    Guid StreamRegistrationId { get; }

    ChannelReader<StreamMessage<T>> Reader { get; }

    /// <summary>Items dropped for this subscriber because its buffer was full.</summary>
    long DroppedCount { get; }

    /// <summary>Set once the reader has been completed.</summary>
    StreamCompletion? Completion { get; }

    /// <summary>Reads until the stream completes or <paramref name="cancellationToken"/> is canceled.</summary>
    IAsyncEnumerable<StreamMessage<T>> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry of streams of one runtime: continuous, buffered, asynchronous delivery from one producer
/// to any number of subscribers, each with its own bounded buffer and full-buffer policy.
/// </summary>
/// <remarks>
/// Streams complement the event bus: the bus delivers one event to every subscriber sequentially and
/// reports each delivery to the publisher; a stream decouples producer and subscribers through
/// bounded buffers with explicit backpressure or loss. Streams share nothing with services or the
/// event bus except identifiers, ownership, and limits.
/// </remarks>
public interface IEventStreamRegistry
{
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="PluginRuntimeException"><c>duplicate_stream</c>, <c>value_not_shared</c> (the item type is not shared), <c>scope_closed</c>, <c>engine_disposed</c>.</exception>
    IStreamProducer<T> Create<T>(StreamDefinition definition);

    /// <summary>
    /// Subscribes to the current registration of <paramref name="streamId"/>, or returns
    /// <see langword="null"/> when no such stream exists or its version is incompatible.
    /// </summary>
    /// <exception cref="PluginRuntimeException"><c>stream_item_type_mismatch</c>, <c>scope_closed</c>, <c>engine_disposed</c>.</exception>
    IStreamSubscription<T>? Subscribe<T>(string streamId, StreamSubscribeOptions? options = null);

    IReadOnlyList<StreamDescriptor> GetStreams();

    StreamDescriptor? GetStream(string streamId);
}
