using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>
/// Live daemon logs. The daemon owns the logs; this page keeps a bounded local view.
/// </summary>
/// <remarks>
/// <para>
/// Synchronization: subscribe first, then query the history, and merge both by sequence number,
/// so no entry between the snapshot and the stream is lost or duplicated. After a reconnection the
/// page queries the entries after the last sequence it has; if the daemon's buffer no longer holds
/// them, a marker shows the gap. A daemon restart (new instance id) restarts sequence numbers and is
/// marked as well.
/// </para>
/// <para>
/// Threading: notifications arrive on the IPC reader thread and only enqueue entries; a UI timer
/// drains the queue in batches, so the Dispatcher is never flooded and collections are only touched
/// on the UI thread. "Clear" only clears this local view.
/// </para>
/// </remarks>
public sealed class LogsViewModel : PageViewModel
{
    public const int MaxEntries = 5000;
    private const int MaxDrainPerTick = 2000;

    private readonly IDialogService _dialogs;
    private readonly ConcurrentQueue<LogEntryDto> _incoming = new();
    private readonly List<LogEntryItem> _all = [];
    private IDisposable? _timer;
    private Guid _subscriptionId;
    private Guid? _daemonInstanceId;
    private long _lastSequence;
    private long _droppedSinceTick;
    private bool _subscribed;
    private bool _isPaused;
    private bool _autoScroll;
    private int _pendingCount;
    private LogLevelDto _minimumLevel = LogLevelDto.Information;
    private string _pluginFilter = AllPlugins;
    private string _searchText = string.Empty;
    private LogEntryItem? _selected;

    public const string AllPlugins = "All plugins";

    public LogsViewModel(IRuntimeConnection connection, IUiDispatcher dispatcher, IDialogService dialogs, bool autoScroll)
        : base("Logs", "", connection, dispatcher)
    {
        _dialogs = dialogs;
        _autoScroll = autoScroll;
        PauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
        ClearCommand = new RelayCommand(ClearLocal);
        CopyCommand = new RelayCommand(CopySelected, () => Selected is not null);
        connection.LogBatchReceived += OnLogBatch;
        _timer = dispatcher.StartTimer(TimeSpan.FromMilliseconds(150), Drain);
    }

    public ObservableCollection<LogEntryItem> Entries { get; } = [];

    public ObservableCollection<string> PluginFilters { get; } = [AllPlugins];

    public IReadOnlyList<LogLevelDto> Levels { get; } = Enum.GetValues<LogLevelDto>();

    public RelayCommand PauseCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand CopyCommand { get; }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value) && !value)
            {
                PendingCount = 0;
                Rebuild();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    /// <summary>Entries received while paused and not yet shown.</summary>
    public int PendingCount
    {
        get => _pendingCount;
        private set => SetProperty(ref _pendingCount, value);
    }

    public LogLevelDto MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (SetProperty(ref _minimumLevel, value))
            {
                Rebuild();
            }
        }
    }

    public string PluginFilter
    {
        get => _pluginFilter;
        set
        {
            if (SetProperty(ref _pluginFilter, value ?? AllPlugins))
            {
                Rebuild();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public LogEntryItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                CopyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int TotalCount => _all.Count;

    /// <summary>Stops streaming handlers (called when the window closes).</summary>
    public void Detach()
    {
        Connection.LogBatchReceived -= OnLogBatch;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Processes queued entries now (the UI timer calls this periodically).</summary>
    internal void Drain()
    {
        var dropped = Interlocked.Exchange(ref _droppedSinceTick, 0);
        if (dropped > 0)
        {
            Append(LogEntryItem.Marker($"{dropped} log entries were dropped by the daemon because this client fell behind."));
        }

        for (var count = 0; count < MaxDrainPerTick && _incoming.TryDequeue(out var entry); count++)
        {
            if (entry.Sequence > _lastSequence)
            {
                _lastSequence = entry.Sequence;
                Append(new LogEntryItem(entry));
            }
        }
    }

    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        if (!_subscribed)
        {
            if (_daemonInstanceId is { } previous && previous != client.Welcome.DaemonInstanceId)
            {
                Append(LogEntryItem.Marker("The daemon was restarted; sequence numbers restarted."));
                _lastSequence = 0;
            }

            _daemonInstanceId = client.Welcome.DaemonInstanceId;

            // Subscribe before querying so nothing between the two is missed; duplicates are
            // removed by sequence number.
            var subscription = await client.SubscribeLogsAsync(null, cancellationToken);
            _subscriptionId = subscription.SubscriptionId;
            _subscribed = true;

            var history = _lastSequence == 0
                ? await client.QueryLogsAsync(new LogQueryRequest(null, null, 1000), cancellationToken)
                : await client.QueryLogsAsync(new LogQueryRequest(null, _lastSequence, 5000), cancellationToken);
            if (history.Truncated)
            {
                Append(LogEntryItem.Marker("Some log entries were missed while disconnected (the daemon's buffer no longer holds them)."));
            }

            foreach (var entry in history.Entries)
            {
                if (entry.Sequence > _lastSequence)
                {
                    _lastSequence = entry.Sequence;
                    Append(new LogEntryItem(entry));
                }
            }
        }

        Drain();
        return true;
    }

    /// <summary>Logs are history: keep the local view while disconnected; only the subscription is gone.</summary>
    protected override void Clear()
    {
        if (_subscribed)
        {
            Append(LogEntryItem.Marker("Disconnected from the daemon; live updates paused until reconnection."));
        }

        _subscribed = false;
        _subscriptionId = Guid.Empty;
    }

    protected override void OnConnected()
    {
        // Logs stream in the background even when the page is not visible.
        RequestRefresh();
    }

    private void OnLogBatch(object? sender, LogBatchNotification batch)
    {
        if (batch.SubscriptionId != _subscriptionId)
        {
            return;
        }

        if (batch.DroppedCount > 0)
        {
            Interlocked.Add(ref _droppedSinceTick, batch.DroppedCount);
        }

        foreach (var entry in batch.Entries)
        {
            _incoming.Enqueue(entry);
        }
    }

    private void Append(LogEntryItem item)
    {
        _all.Add(item);
        if (_all.Count > MaxEntries)
        {
            var removed = _all[0];
            _all.RemoveAt(0);
            if (Entries.Count > 0 && ReferenceEquals(Entries[0], removed))
            {
                Entries.RemoveAt(0);
            }
        }

        if (item.PluginId is { } plugin && !PluginFilters.Contains(plugin))
        {
            PluginFilters.Add(plugin);
        }

        if (!Matches(item))
        {
            return;
        }

        if (IsPaused)
        {
            PendingCount++;
        }
        else
        {
            Entries.Add(item);
        }

        OnPropertyChanged(nameof(TotalCount));
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var item in _all.Where(Matches))
        {
            Entries.Add(item);
        }
    }

    private bool Matches(LogEntryItem item) =>
        item.IsMarker ||
        (item.Level >= MinimumLevel &&
         (PluginFilter == AllPlugins || item.PluginId == PluginFilter) &&
         (SearchText.Length == 0 ||
          item.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
          item.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
          (item.DiagnosticText?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)));

    private void ClearLocal()
    {
        _all.Clear();
        Entries.Clear();
        PendingCount = 0;
        Selected = null;
        OnPropertyChanged(nameof(TotalCount));
    }

    private void CopySelected()
    {
        if (Selected is { } item)
        {
            _dialogs.CopyToClipboard(item.FullText);
        }
    }
}

/// <summary>Display model of one log entry (or a local marker such as a gap notice).</summary>
public sealed class LogEntryItem
{
    public LogEntryItem(LogEntryDto entry)
    {
        Sequence = entry.Sequence;
        Timestamp = entry.Timestamp.ToLocalTime();
        Level = entry.Level;
        Category = entry.Category;
        PluginId = entry.PluginId;
        Message = entry.Message;
        DiagnosticText = entry.Diagnostic is { } d ? d.Detail ?? $"{d.ExceptionType}: {d.Message}\n{d.StackTrace}" : null;
    }

    private LogEntryItem(string message)
    {
        Timestamp = DateTimeOffset.Now;
        Level = LogLevelDto.Warning;
        Category = "Desktop";
        Message = message;
        IsMarker = true;
    }

    public long Sequence { get; }

    public DateTimeOffset Timestamp { get; }

    public string Time => Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public LogLevelDto Level { get; }

    public string LevelText => Level switch
    {
        LogLevelDto.Trace => "TRACE",
        LogLevelDto.Debug => "DEBUG",
        LogLevelDto.Information => "INFO",
        LogLevelDto.Warning => "WARN",
        LogLevelDto.Error => "ERROR",
        _ => "CRIT",
    };

    public string Category { get; }

    public string? PluginId { get; }

    public string Message { get; }

    public string? DiagnosticText { get; }

    public bool HasDiagnostic => DiagnosticText is not null;

    public bool IsMarker { get; }

    public string FullText
    {
        get
        {
            var text = new StringBuilder()
                .Append(Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(' ')
                .Append(LevelText).Append(" [").Append(Category).Append(']');
            if (PluginId is not null)
            {
                text.Append(" [").Append(PluginId).Append(']');
            }

            text.Append(' ').Append(Message);
            if (DiagnosticText is not null)
            {
                text.AppendLine().Append(DiagnosticText);
            }

            return text.ToString();
        }
    }

    internal static LogEntryItem Marker(string message) => new(message);
}
