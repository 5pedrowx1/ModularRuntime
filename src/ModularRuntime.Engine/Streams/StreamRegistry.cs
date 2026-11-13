using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ModularRuntime.Contracts;
using ModularRuntime.Engine.Internal;
using ModularRuntime.Engine.Ownership;

namespace ModularRuntime.Engine.Streams;

/// <summary>
/// Streams of one runtime: one producer per stream ID, any number of subscribers, each with its
/// own bounded channel.
/// </summary>
/// <remarks>
/// <para>
/// No owner code runs inside the registry: writes only move items into channels, so a stream
/// needs no admission or drain. The registry lock guards the two indexes; each stream guards
/// its subscriber array with its own lock, never held while writing to a channel.
/// </para>
/// <para>
/// Removing a stream (explicitly or because its owner stops) completes every subscriber with
/// <c>stream_removed</c> and releases the subscriptions, which also removes them from their
/// owners' scopes; buffered items stay readable. Removing a subscription detaches it from the
/// stream and completes its reader.
/// </para>
/// </remarks>
internal sealed class StreamRegistry
{
    private readonly RuntimeCore _core;
    private readonly object _sync = new();
    private readonly Dictionary<string, StreamEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, StreamEntry> _byRegistration = [];

    public StreamRegistry(RuntimeCore core)
    {
        _core = core;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byId.Count;
            }
        }
    }

    public IStreamProducer<T> Create<T>(OwnershipScope scope, StreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RuntimeName.Validate(definition.StreamId, nameof(definition));
        Validation.ValidateDisplayName(definition.Name, nameof(definition));
        Validation.ValidateVersion(definition.Version, nameof(definition));
        var metadata = Validation.CopyMetadata(definition.Metadata, nameof(definition));
        var capacity = ValidateCapacity(definition.DefaultCapacity, nameof(definition));
        if (!Enum.IsDefined(definition.DefaultFullMode))
        {
            throw new ArgumentException("Unknown stream full mode.", nameof(definition));
        }

        if (typeof(T).IsByRef || typeof(T).IsPointer || typeof(T).IsByRefLike)
        {
            throw new ArgumentException($"'{typeof(T)}' cannot be a stream item type.", nameof(definition));
        }

        _core.EnsureCanRegister(scope);
        if (_core.Limits.TypedValueCheck != TypedValueCheck.None && _core.SharedTypes.StaticVerdict(typeof(T)) is TypeVerdict.Private or TypeVerdict.Unverifiable)
        {
            var verdict = _core.SharedTypes.Analyzer.GetShape(typeof(T));
            var code = verdict.Static == TypeVerdict.Private ? PluginErrorCodes.ValueNotShared : PluginErrorCodes.ValueNotVerifiable;
            var message = verdict.Static == TypeVerdict.Private
                ? $"Stream '{definition.StreamId}': item type {SharedTypeAnalyzer.Describe(typeof(T))} is not a shared type (contract assembly, ModularRuntime.Contracts, or framework); {verdict.Reason}."
                : $"Stream '{definition.StreamId}': item type {SharedTypeAnalyzer.Describe(typeof(T))} cannot be analyzed ({verdict.Reason}).";
            if (_core.Limits.TypedValueCheck == TypedValueCheck.Reject)
            {
                throw new PluginRuntimeException(code, message);
            }

            _core.Log(LogLevel.Warning, "Streams", $"[{code}] {message}", scope.Owner);
        }

        var entry = new StreamEntry<T>(this, scope, definition, metadata, capacity);
        lock (_sync)
        {
            if (_byId.TryGetValue(definition.StreamId, out var existing))
            {
                throw new PluginRuntimeException(
                    PluginErrorCodes.DuplicateStream,
                    $"Stream '{definition.StreamId}' is already provided by '{existing.Owner}'.");
            }

            _byId.Add(entry.StreamId, entry);
            _byRegistration.Add(entry.ResourceId, entry);
        }

        if (!scope.TryAdd(entry))
        {
            Remove(entry);
            throw RuntimeCore.ScopeClosed(scope);
        }

        _core.Log(LogLevel.Information, "Streams", $"Stream '{entry.StreamId}' {entry.Version} created.", scope.Owner);
        return entry;
    }

    public IStreamSubscription<T>? Subscribe<T>(OwnershipScope scope, string streamId, StreamSubscribeOptions? options)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        int? capacity = options?.Capacity is { } requested ? ValidateCapacity(requested, nameof(options)) : null;
        if (options?.FullMode is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentException("Unknown stream full mode.", nameof(options));
        }

        _core.EnsureCanRegister(scope);
        StreamEntry? entry;
        lock (_sync)
        {
            entry = _byId.GetValueOrDefault(streamId);
        }

        if (entry is null || entry.IsReleased ||
            (options?.MinimumVersion is { } minimum && !Validation.IsVersionCompatible(entry.Version, minimum)))
        {
            return null;
        }

        if (entry is not StreamEntry<T> typed)
        {
            throw new PluginRuntimeException(
                PluginErrorCodes.StreamItemTypeMismatch,
                $"Stream '{streamId}' carries items of type '{entry.ItemTypeName}', not '{typeof(T).FullName}'.");
        }

        var subscription = typed.AddSubscriber(scope, capacity ?? typed.DefaultCapacity, options?.FullMode ?? typed.DefaultFullMode);
        if (subscription is null)
        {
            return null;
        }

        if (!scope.TryAdd(subscription))
        {
            typed.RemoveSubscriber(subscription);
            subscription.CompleteReader(new StreamCompletion(PluginErrorCodes.ScopeClosed, "The subscriber stopped."));
            throw RuntimeCore.ScopeClosed(scope);
        }

        return subscription;
    }

    public IReadOnlyList<StreamDescriptor> GetStreams()
    {
        StreamEntry[] entries;
        lock (_sync)
        {
            entries = [.. _byId.Values];
        }

        return entries.OrderBy(entry => entry.StreamId, StringComparer.Ordinal).Select(entry => entry.Descriptor).ToArray();
    }

    public StreamDescriptor? GetStream(string streamId)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        lock (_sync)
        {
            return _byId.GetValueOrDefault(streamId)?.Descriptor;
        }
    }

    internal void Remove(StreamEntry entry)
    {
        lock (_sync)
        {
            if (_byId.TryGetValue(entry.StreamId, out var current) && ReferenceEquals(current, entry))
            {
                _byId.Remove(entry.StreamId);
            }

            _byRegistration.Remove(entry.ResourceId);
        }
    }

    internal void Log(LogLevel level, string message, ResourceOwner owner) => _core.Log(level, "Streams", message, owner);

    internal SharedTypes SharedTypes => _core.SharedTypes;

    private int ValidateCapacity(int? capacity, string parameterName)
    {
        if (capacity is null)
        {
            return _core.Limits.DefaultStreamCapacity;
        }

        if (capacity.Value <= 0 || capacity.Value > _core.Limits.MaxStreamCapacity)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Stream capacity must be between 1 and {_core.Limits.MaxStreamCapacity}.");
        }

        return capacity.Value;
    }
}

internal abstract class StreamEntry : OwnedResource
{
    protected StreamEntry(OwnershipScope scope, StreamDefinition definition, IReadOnlyDictionary<string, string> metadata, int defaultCapacity)
        : base(scope, ResourceKind.Stream, definition.StreamId)
    {
        StreamId = definition.StreamId;
        DisplayName = definition.Name;
        Version = definition.Version;
        Metadata = metadata;
        DefaultCapacity = defaultCapacity;
        DefaultFullMode = definition.DefaultFullMode;
    }

    public string StreamId { get; }

    public string DisplayName { get; }

    public Version Version { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public int DefaultCapacity { get; }

    public StreamFullMode DefaultFullMode { get; }

    public abstract string ItemTypeName { get; }

    public abstract StreamDescriptor Descriptor { get; }
}

internal sealed class StreamEntry<T> : StreamEntry, IStreamProducer<T>
{
    private static readonly bool IsBinary = typeof(T) == typeof(ReadOnlyMemory<byte>);

    private readonly StreamRegistry _registry;
    private readonly object _sync = new();
    private readonly bool _copyBinaryItems;
    private readonly bool _checkItems;
    private readonly string _label;
    private readonly Func<ResourceOwner?> _owner;
    private StreamSubscriptionEntry<T>[] _subscribers = [];
    private long _sequence;
    private StreamCompletion? _completion;

    public StreamEntry(StreamRegistry registry, OwnershipScope scope, StreamDefinition definition, IReadOnlyDictionary<string, string> metadata, int defaultCapacity)
        : base(scope, definition, metadata, defaultCapacity)
    {
        _registry = registry;
        _copyBinaryItems = IsBinary && definition.CopyBinaryItems;
        // An item type that does not settle what an item may contain (object, an interface, a
        // non-sealed class, a type with such fields) is checked on every write, like a typed-service
        // result. Binary items copied on write are fresh arrays and need no check.
        _checkItems = !_copyBinaryItems && !registry.SharedTypes.IsClosed(typeof(T));
        _label = $"Stream '{StreamId}'";
        _owner = () => Owner;
    }

    public override string ItemTypeName => typeof(T).FullName ?? typeof(T).Name;

    public int SubscriberCount => Volatile.Read(ref _subscribers).Length;

    public long Sequence => Interlocked.Read(ref _sequence);

    public override StreamDescriptor Descriptor =>
        new(ResourceId, StreamId, DisplayName, Version, ItemTypeName, Metadata, Owner, IsActive && _completion is null, SubscriberCount, Sequence, DefaultCapacity, DefaultFullMode, CreatedAt);

    public async ValueTask<bool> WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        if (!TryBegin(ref item, out var subscribers, out var sequence))
        {
            return false;
        }

        foreach (var subscriber in subscribers)
        {
            await subscriber.OfferAsync(sequence, item, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public bool TryWrite(T item)
    {
        if (!TryBegin(ref item, out var subscribers, out var sequence))
        {
            return false;
        }

        var delivered = true;
        foreach (var subscriber in subscribers)
        {
            delivered &= subscriber.TryOffer(sequence, item);
        }

        return delivered;
    }

    public void Complete(StreamCompletion? completion = null)
    {
        completion ??= new StreamCompletion(PluginErrorCodes.StreamCompleted, $"Stream '{StreamId}' was completed by its producer.");
        StreamSubscriptionEntry<T>[] subscribers;
        lock (_sync)
        {
            if (_completion is not null)
            {
                return;
            }

            _completion = completion;
            subscribers = _subscribers;
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.CompleteReader(completion);
        }
    }

    internal StreamSubscriptionEntry<T>? AddSubscriber(OwnershipScope scope, int capacity, StreamFullMode mode)
    {
        var subscription = new StreamSubscriptionEntry<T>(this, scope, capacity, mode);
        lock (_sync)
        {
            if (IsReleased || _completion is not null)
            {
                return null;
            }

            _subscribers = [.. _subscribers, subscription];
        }

        return subscription;
    }

    internal void RemoveSubscriber(StreamSubscriptionEntry<T> subscription)
    {
        lock (_sync)
        {
            var index = Array.IndexOf(_subscribers, subscription);
            if (index < 0)
            {
                return;
            }

            var updated = new StreamSubscriptionEntry<T>[_subscribers.Length - 1];
            Array.Copy(_subscribers, 0, updated, 0, index);
            Array.Copy(_subscribers, index + 1, updated, index, _subscribers.Length - index - 1);
            _subscribers = updated;
        }
    }

    protected override async ValueTask ReleaseCoreAsync()
    {
        _registry.Remove(this);
        Complete(new StreamCompletion(PluginErrorCodes.StreamRemoved, $"Stream '{StreamId}' was removed."));
        StreamSubscriptionEntry<T>[] subscribers;
        lock (_sync)
        {
            subscribers = _subscribers;
            _subscribers = [];
        }

        foreach (var subscription in subscribers)
        {
            await subscription.ReleaseAsync().ConfigureAwait(false);
        }

        _registry.Log(LogLevel.Information, $"Stream '{StreamId}' removed with {subscribers.Length} subscription(s).", Owner);
    }

    private bool TryBegin(ref T item, out StreamSubscriptionEntry<T>[] subscribers, out long sequence)
    {
        if (IsReleased || !Scope.CanCallOut || Volatile.Read(ref _completion) is not null)
        {
            subscribers = [];
            sequence = 0;
            return false;
        }

        if (_checkItems && _registry.SharedTypes.Check(item, new ValueBoundary(_label, "item", null), _owner) is { } failure)
        {
            subscribers = [];
            sequence = 0;
            throw new PluginRuntimeException(failure.Code, failure.Message);
        }

        if (_copyBinaryItems)
        {
            item = (T)(object)new ReadOnlyMemory<byte>(((ReadOnlyMemory<byte>)(object)item!).ToArray());
        }

        subscribers = Volatile.Read(ref _subscribers);
        sequence = Interlocked.Increment(ref _sequence);
        return true;
    }
}

internal sealed class StreamSubscriptionEntry<T> : OwnedResource, IStreamSubscription<T>
{
    private readonly StreamEntry<T> _stream;
    private readonly Channel<StreamMessage<T>> _channel;
    private readonly StreamFullMode _mode;
    private long _dropped;
    private StreamCompletion? _completion;

    public StreamSubscriptionEntry(StreamEntry<T> stream, OwnershipScope scope, int capacity, StreamFullMode mode)
        : base(scope, ResourceKind.StreamSubscription, stream.StreamId)
    {
        _stream = stream;
        _mode = mode;
        var options = new BoundedChannelOptions(capacity)
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false,
            FullMode = mode switch
            {
                StreamFullMode.DropOldest => BoundedChannelFullMode.DropOldest,
                StreamFullMode.DropNewest => BoundedChannelFullMode.DropWrite,
                _ => BoundedChannelFullMode.Wait,
            },
        };
        _channel = mode == StreamFullMode.Wait
            ? Channel.CreateBounded<StreamMessage<T>>(options)
            : Channel.CreateBounded<StreamMessage<T>>(options, _ => Interlocked.Increment(ref _dropped));
    }

    public string StreamId => _stream.StreamId;

    public Guid StreamRegistrationId => _stream.ResourceId;

    public ChannelReader<StreamMessage<T>> Reader => _channel.Reader;

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public StreamCompletion? Completion => Volatile.Read(ref _completion);

    public async IAsyncEnumerable<StreamMessage<T>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    internal async ValueTask OfferAsync(long sequence, T item, CancellationToken cancellationToken)
    {
        var message = new StreamMessage<T>(sequence, item);
        if (_mode != StreamFullMode.Wait)
        {
            // Drop modes never wait: the channel's own policy decides and counts the drop.
            _channel.Writer.TryWrite(message);
            return;
        }

        if (_channel.Writer.TryWrite(message))
        {
            return;
        }

        try
        {
            await _channel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The subscriber went away while the producer waited: nothing to deliver.
        }
    }

    internal bool TryOffer(long sequence, T item)
    {
        if (_channel.Writer.TryWrite(new StreamMessage<T>(sequence, item)))
        {
            return true;
        }

        if (_mode == StreamFullMode.Wait && !_channel.Reader.Completion.IsCompleted)
        {
            Interlocked.Increment(ref _dropped);
        }

        return false;
    }

    internal void CompleteReader(StreamCompletion completion)
    {
        if (Interlocked.CompareExchange(ref _completion, completion, null) is null)
        {
            _channel.Writer.TryComplete();
        }
    }

    protected override ValueTask ReleaseCoreAsync()
    {
        _stream.RemoveSubscriber(this);
        CompleteReader(new StreamCompletion(PluginErrorCodes.SubscriptionDisposed, $"The subscription to stream '{StreamId}' was disposed."));
        return ValueTask.CompletedTask;
    }
}
