using System.Globalization;
using System.Text;
using System.Threading.Channels;
using ModularRuntime.Contracts;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Daemon;

/// <summary>
/// The daemon's log sink: a bounded in-memory history for clients, live subscriptions, and a
/// daily log file.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Log"/> never blocks and never runs foreign code: it appends to the ring buffer under
/// a short lock, then offers the entry to each subscription with a non-blocking write. A subscriber
/// that does not keep up loses entries; the loss is counted and reported with its next batch. The
/// file writer has its own bounded queue and drops lines (and counts them) rather than blocking.
/// </para>
/// <para>
/// Entries are text only (<see cref="LogEntryDto"/>); plugin exceptions arrive as text
/// diagnostics already, so no exception object is retained.
/// </para>
/// </remarks>
internal sealed class LogStore : IRuntimeLogger, IAsyncDisposable
{
    public const int SubscriptionQueueCapacity = 2000;

    private readonly object _sync = new();
    private readonly LogEntryDto[] _ring;
    private readonly Channel<LogEntryDto>? _fileQueue;
    private readonly Task? _fileWriter;
    private LogSubscription[] _subscriptions = [];
    private int _start;
    private int _count;
    private long _sequence;
    private long _droppedFileLines;

    public LogStore(int capacity, string? logDirectory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new LogEntryDto[capacity];
        if (logDirectory is not null)
        {
            Directory.CreateDirectory(logDirectory);
            LogDirectory = logDirectory;
            _fileQueue = Channel.CreateBounded<LogEntryDto>(new BoundedChannelOptions(10_000)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _fileWriter = Task.Run(WriteFileAsync);
        }
    }

    public int Capacity => _ring.Length;

    public string? LogDirectory { get; }

    public long LastSequence
    {
        get
        {
            lock (_sync)
            {
                return _sequence;
            }
        }
    }

    public void Log(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        LogEntryDto dto;
        lock (_sync)
        {
            dto = new LogEntryDto(
                ++_sequence,
                entry.Timestamp,
                (LogLevelDto)(int)entry.Level,
                string.IsNullOrWhiteSpace(entry.Category) ? "Runtime" : entry.Category,
                entry.Message,
                entry.PluginId?.Value,
                entry.InstanceId,
                entry.Diagnostic is { } d ? new DiagnosticDto(d.ExceptionType, d.Message, d.StackTrace, d.Detail) : null);

            var index = (_start + _count) % _ring.Length;
            _ring[index] = dto;
            if (_count < _ring.Length)
            {
                _count++;
            }
            else
            {
                _start = (_start + 1) % _ring.Length;
            }

            // Offered under the lock so every queue receives entries in sequence order. TryWrite on
            // a channel never blocks and never runs consumer code synchronously.
            foreach (var subscription in _subscriptions)
            {
                subscription.Offer(dto);
            }

            if (_fileQueue is not null && !_fileQueue.Writer.TryWrite(dto))
            {
                Interlocked.Increment(ref _droppedFileLines);
            }
        }
    }

    /// <summary>Daemon-originated entry (category Daemon, Ipc, ...).</summary>
    public void Write(LogLevel level, string category, string message) =>
        Log(new LogEntry(level, message, Category: category));

    /// <summary>
    /// Entries matching <paramref name="filter"/>. Without <paramref name="afterSequence"/> returns the
    /// newest <paramref name="limit"/> entries; with it, the oldest entries after that sequence.
    /// </summary>
    public LogQueryResponse Query(LogFilterDto? filter, long? afterSequence, int limit)
    {
        limit = Math.Clamp(limit, 1, 5000);
        lock (_sync)
        {
            var oldest = _count == 0 ? _sequence + 1 : _ring[_start].Sequence;
            var truncated = afterSequence is { } after && after < oldest - 1;
            var matches = new List<LogEntryDto>();
            if (afterSequence is { } since)
            {
                for (var i = 0; i < _count && matches.Count < limit; i++)
                {
                    var entry = _ring[(_start + i) % _ring.Length];
                    if (entry.Sequence > since && Matches(entry, filter))
                    {
                        matches.Add(entry);
                    }
                }
            }
            else
            {
                for (var i = _count - 1; i >= 0 && matches.Count < limit; i--)
                {
                    var entry = _ring[(_start + i) % _ring.Length];
                    if (Matches(entry, filter))
                    {
                        matches.Add(entry);
                    }
                }

                matches.Reverse();
            }

            return new LogQueryResponse(matches, _sequence, truncated);
        }
    }

    public LogSubscription Subscribe(LogFilterDto? filter)
    {
        lock (_sync)
        {
            var subscription = new LogSubscription(this, filter, _sequence);
            _subscriptions = [.. _subscriptions, subscription];
            return subscription;
        }
    }

    public static bool Matches(LogEntryDto entry, LogFilterDto? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return (filter.MinimumLevel is not { } level || entry.Level >= level)
            && (filter.PluginId is not { } plugin || string.Equals(entry.PluginId, plugin, StringComparison.Ordinal))
            && (filter.Category is not { } category || string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
            && (filter.Text is not { Length: > 0 } text
                || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (entry.Diagnostic?.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            && (filter.Since is not { } since || entry.Timestamp >= since)
            && (filter.Until is not { } until || entry.Timestamp <= until);
    }

    public async ValueTask DisposeAsync()
    {
        LogSubscription[] subscriptions;
        lock (_sync)
        {
            subscriptions = _subscriptions;
            _subscriptions = [];
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Complete();
        }

        if (_fileQueue is not null)
        {
            _fileQueue.Writer.TryComplete();
            await _fileWriter!.ConfigureAwait(false);
        }
    }

    internal void Remove(LogSubscription subscription)
    {
        lock (_sync)
        {
            _subscriptions = [.. _subscriptions.Where(s => !ReferenceEquals(s, subscription))];
        }
    }

    private async Task WriteFileAsync()
    {
        StreamWriter? writer = null;
        var currentDate = DateOnly.MinValue;
        try
        {
            while (await _fileQueue!.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_fileQueue.Reader.TryRead(out var entry))
                {
                    var date = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);
                    if (writer is null || date != currentDate)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }

                        currentDate = date;
                        var path = Path.Combine(LogDirectory!, $"daemon-{date:yyyyMMdd}.log");
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
                    }

                    await writer.WriteLineAsync(Format(entry)).ConfigureAwait(false);
                }

                var dropped = Interlocked.Exchange(ref _droppedFileLines, 0);
                if (dropped > 0 && writer is not null)
                {
                    await writer.WriteLineAsync($"{DateTimeOffset.Now:O} WRN [Daemon] {dropped} log line(s) were dropped because the log file writer fell behind.").ConfigureAwait(false);
                }

                if (writer is not null)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file sink is best effort; in-memory history and IPC streaming keep working.
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Format(LogEntryDto entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Timestamp.ToLocalTime().ToString("O", CultureInfo.InvariantCulture))
            .Append(' ').Append(entry.Level.ToString().ToUpperInvariant()[..3])
            .Append(" [").Append(entry.Category).Append(']');
        if (entry.PluginId is not null)
        {
            builder.Append(" [").Append(entry.PluginId).Append(']');
        }

        builder.Append(' ').Append(entry.Message);
        if (entry.Diagnostic is { } diagnostic)
        {
            builder.AppendLine().Append("    ").Append((diagnostic.Detail ?? $"{diagnostic.ExceptionType}: {diagnostic.Message}").Replace("\n", "\n    ", StringComparison.Ordinal));
        }

        return builder.ToString();
    }
}

/// <summary>A live log subscription with a bounded queue; entries beyond capacity are dropped and counted.</summary>
internal sealed class LogSubscription : IDisposable
{
    private readonly LogStore _store;
    private readonly LogFilterDto? _filter;
    private readonly Channel<LogEntryDto> _queue = Channel.CreateBounded<LogEntryDto>(new BoundedChannelOptions(LogStore.SubscriptionQueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private long _dropped;
    private int _disposed;

    internal LogSubscription(LogStore store, LogFilterDto? filter, long startSequence)
    {
        _store = store;
        _filter = filter;
        StartSequence = startSequence;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Newest sequence when the subscription began; later entries are delivered.</summary>
    public long StartSequence { get; }

    public ChannelReader<LogEntryDto> Reader => _queue.Reader;

    /// <summary>Returns and resets the number of entries dropped since the last call.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    /// <summary>Counts entries dropped downstream (for example a full client notification queue).</summary>
    public void AddDropped(long count) => Interlocked.Add(ref _dropped, count);

    internal void Offer(LogEntryDto entry)
    {
        if (!LogStore.Matches(entry, _filter))
        {
            return;
        }

        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    internal void Complete() => _queue.Writer.TryComplete();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _store.Remove(this);
        Complete();
    }
}
