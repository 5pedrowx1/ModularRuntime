using System.Collections.ObjectModel;
using System.Globalization;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>Daemon overview. Every value comes from <c>runtime.status</c> and <c>logs.query</c>.</summary>
public sealed class DashboardViewModel : PageViewModel
{
    private RuntimeStatusDto? _status;
    private DateTimeOffset _statusReceivedAt;
    private IDisposable? _uptimeTimer;

    public DashboardViewModel(IRuntimeConnection connection, IUiDispatcher dispatcher)
        : base("Dashboard", "", connection, dispatcher)
    {
    }

    public RuntimeStatusDto? Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(Uptime));
            }
        }
    }

    /// <summary>Daemon uptime: reported value plus local time elapsed since it was received (no polling).</summary>
    public string Uptime
    {
        get
        {
            if (Status is null)
            {
                return "—";
            }

            var span = TimeSpan.FromSeconds(Status.UptimeSeconds) + (DateTimeOffset.UtcNow - _statusReceivedAt);
            var clock = span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {clock}" : clock;
        }
    }

    public ObservableCollection<LogEntryDto> RecentActivity { get; } = [];

    public ObservableCollection<LogEntryDto> RecentErrors { get; } = [];

    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        var status = await client.GetStatusAsync(cancellationToken);
        var activity = await client.QueryLogsAsync(new LogQueryRequest(new LogFilterDto(MinimumLevel: LogLevelDto.Information), null, 8), cancellationToken);
        var errors = await client.QueryLogsAsync(new LogQueryRequest(new LogFilterDto(MinimumLevel: LogLevelDto.Warning), null, 8), cancellationToken);

        _statusReceivedAt = DateTimeOffset.UtcNow;
        Status = status;
        Replace(RecentActivity, activity.Entries.Reverse());
        Replace(RecentErrors, errors.Entries.Reverse());
        _uptimeTimer ??= Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () => OnPropertyChanged(nameof(Uptime)));
        return true;
    }

    protected override void Clear()
    {
        Status = null;
        RecentActivity.Clear();
        RecentErrors.Clear();
        _uptimeTimer?.Dispose();
        _uptimeTimer = null;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
