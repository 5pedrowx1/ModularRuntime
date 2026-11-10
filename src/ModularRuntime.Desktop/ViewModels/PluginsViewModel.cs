using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.ViewModels;

public enum PluginStateFilter
{
    All,
    Running,
    Stopped,
    Failed,
    Ready,
}

/// <summary>
/// The Plugins page: the installed plugins and their management — start, stop, restart, verify
/// integrity, remove, details, and diagnostics. Discovering and installing plugins is the Store's job.
/// </summary>
/// <remarks>
/// Commands never change displayed state optimistically: items show busy indicators, the daemon's
/// answer is reported, and the list is refreshed from the daemon. Stopped and failed plugins can be
/// started again: the daemon gives them a new instance and load context and re-verifies managed
/// installations before loading.
/// </remarks>
public sealed class PluginsViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly List<PluginItemViewModel> _all = [];
    private Dictionary<string, InstalledPackageDto> _packages = new(StringComparer.Ordinal);
    private string _searchText = string.Empty;
    private PluginStateFilter _stateFilter = PluginStateFilter.All;
    private PluginItemViewModel? _selected;
    private PluginDetailsDto? _details;
    private string? _operationMessage;
    private bool _operationFailed;
    private bool _bulkBusy;

    public PluginsViewModel(IRuntimeConnection connection, IUiDispatcher dispatcher, IDialogService dialogs, PackageOperationsTracker operations)
        : base("Plugins", "", connection, dispatcher)
    {
        _dialogs = dialogs;
        Operations = operations;
        StartAllCommand = new AsyncRelayCommand(() => RunBulkAsync(c => c.StartAllAsync(), "Start all"), CanRunBulk, ReportError);
        StopAllCommand = new AsyncRelayCommand(StopAllAsync, CanRunBulk, ReportError);
        StartSelectedCommand = new AsyncRelayCommand(() => Selected is { } s ? StartAsync(s) : Task.CompletedTask, () => Selected?.StartCommand.CanExecute(null) == true, ReportError);
        StopSelectedCommand = new AsyncRelayCommand(() => Selected is { } s ? StopAsync(s) : Task.CompletedTask, () => Selected?.StopCommand.CanExecute(null) == true, ReportError);
        RestartSelectedCommand = new AsyncRelayCommand(() => Selected is { } s ? RestartAsync(s) : Task.CompletedTask, () => Selected?.RestartCommand.CanExecute(null) == true, ReportError);
        VerifyCommand = new AsyncRelayCommand(VerifySelectedAsync, () => CanOperate && Selected is { IsManaged: true }, ReportError);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => CanOperate && Selected is { IsManaged: true } s && !operations.HasActiveOperation(s.Id), ReportError);
        CopyDiagnosticCommand = new RelayCommand(CopyDiagnostic, () => Details?.LastError is not null);
        DismissMessageCommand = new RelayCommand(() => OperationMessage = null);
        operations.Changed += (_, _) => RaiseCommands();
        operations.OperationFinished += (_, operation) =>
        {
            // Removals started here are reported here; installs and updates also refresh the list.
            if (operation.Kind == PackageOperationKindDto.Remove)
            {
                OperationFailed = operation.Stage != PackageOperationStageDto.Completed;
                OperationMessage = OperationItemViewModel.Outcome(operation);
            }

            RequestRefresh();
        };
    }

    public ObservableCollection<PluginItemViewModel> Items { get; } = [];

    public PackageOperationsTracker Operations { get; }

    public AsyncRelayCommand StartAllCommand { get; }

    public AsyncRelayCommand StopAllCommand { get; }

    public AsyncRelayCommand StartSelectedCommand { get; }

    public AsyncRelayCommand StopSelectedCommand { get; }

    public AsyncRelayCommand RestartSelectedCommand { get; }

    public AsyncRelayCommand VerifyCommand { get; }

    public AsyncRelayCommand RemoveSelectedCommand { get; }

    public RelayCommand CopyDiagnosticCommand { get; }

    public RelayCommand DismissMessageCommand { get; }

    public IReadOnlyList<PluginStateFilter> StateFilters { get; } = Enum.GetValues<PluginStateFilter>();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public PluginStateFilter StateFilter
    {
        get => _stateFilter;
        set
        {
            if (SetProperty(ref _stateFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public PluginItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                Details = null;
                OnPropertyChanged(nameof(SelectedPackage));
                RaiseCommands();
                if (value is not null)
                {
                    _ = LoadDetailsAsync(value.Id);
                }
            }
        }
    }

    public PluginDetailsDto? Details
    {
        get => _details;
        private set
        {
            if (SetProperty(ref _details, value))
            {
                CopyDiagnosticCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Package information of the selected plugin, when it has any.</summary>
    public InstalledPackageDto? SelectedPackage => Selected is { } item && _packages.TryGetValue(item.Id, out var package) ? package : null;

    public string? OperationMessage
    {
        get => _operationMessage;
        private set => SetProperty(ref _operationMessage, value);
    }

    public bool OperationFailed
    {
        get => _operationFailed;
        private set => SetProperty(ref _operationFailed, value);
    }

    public int TotalCount => _all.Count;

    public int RunningCount => _all.Count(p => p.State == "Running");

    public int FailedCount => _all.Count(p => p.State == "Failed");

    internal bool IsBulkBusy
    {
        get => _bulkBusy;
        private set
        {
            _bulkBusy = value;
            RaiseCommands();
        }
    }

    internal bool CanOperate => Connection.State == ConnectionState.Connected && !IsBulkBusy;

    internal bool HasActiveOperation(string pluginId) => Operations.HasActiveOperation(pluginId);

    internal async Task StartAsync(PluginItemViewModel item) =>
        await RunOnItemAsync(item, client => client.StartPluginAsync(item.Id));

    internal async Task StopAsync(PluginItemViewModel item) =>
        await RunOnItemAsync(item, client => client.StopPluginAsync(item.Id));

    internal async Task RestartAsync(PluginItemViewModel item) =>
        await RunOnItemAsync(item, client => client.RestartPluginAsync(item.Id));

    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        var response = await client.ListPluginsAsync(cancellationToken);
        var packages = await client.ListPackagesAsync(cancellationToken);
        _packages = packages.Packages.ToDictionary(p => p.PluginId, StringComparer.Ordinal);

        // Managed installations that are not registered (for example a package whose plugin.json was
        // rejected) still appear, so they can be verified or removed.
        var summaries = response.Plugins.ToList();
        foreach (var package in packages.Packages.Where(p => p.EngineState is null))
        {
            summaries.Add(new PluginSummaryDto(package.PluginId, package.Name, package.Version, "NotLoaded", 0, null, null, null, false, null));
        }

        Merge(summaries);
        foreach (var item in _all)
        {
            item.SetPackage(_packages.GetValueOrDefault(item.Id));
        }

        OnPropertyChanged(nameof(SelectedPackage));
        if (Selected is { } selected)
        {
            await LoadDetailsAsync(selected.Id);
        }

        RaiseCommands();
        return _all.Count > 0;
    }

    protected override void Clear()
    {
        _all.Clear();
        Items.Clear();
        _packages.Clear();
        Selected = null;
        Details = null;
        OperationMessage = null;
        RaiseCounts();
    }

    protected override void OnConnected() => RaiseCommands();

    protected override void OnDisconnected() => RaiseCommands();

    private bool CanRunBulk() => CanOperate && _all.Count > 0;

    private void RaiseCommands()
    {
        StartAllCommand.RaiseCanExecuteChanged();
        StopAllCommand.RaiseCanExecuteChanged();
        foreach (var item in _all)
        {
            item.RaiseCommandsChanged();
        }

        StartSelectedCommand.RaiseCanExecuteChanged();
        StopSelectedCommand.RaiseCanExecuteChanged();
        RestartSelectedCommand.RaiseCanExecuteChanged();
        VerifyCommand.RaiseCanExecuteChanged();
        RemoveSelectedCommand.RaiseCanExecuteChanged();
    }

    private async Task StopAllAsync()
    {
        if (RunningCount > 0 &&
            !_dialogs.Confirm("Stop all plugins", $"Stop all {RunningCount} running plugin(s)? The daemon keeps running.", "Stop all"))
        {
            return;
        }

        await RunBulkAsync(c => c.StopAllAsync(), "Stop all");
    }

    private async Task RunBulkAsync(Func<IRuntimeClient, Task<OperationResultsResponse>> operation, string name)
    {
        var client = Connection.Client ?? throw new IpcConnectionLostException("Not connected.");
        IsBulkBusy = true;
        try
        {
            var response = await operation(client);
            var failures = response.Results.Where(r => !r.IsSuccess && r.ErrorCode != "invalid_state").ToList();
            OperationFailed = failures.Count > 0;
            OperationMessage = failures.Count == 0
                ? $"{name}: completed."
                : $"{name}: {failures.Count} plugin(s) failed — " + string.Join("; ", failures.Select(f => $"{f.PluginId}: {f.Message?.TrimEnd('.')}"));
        }
        finally
        {
            IsBulkBusy = false;
            await RefreshAsync();
        }
    }

    private async Task RunOnItemAsync(PluginItemViewModel item, Func<IRuntimeClient, Task<OperationResultDto>> operation)
    {
        var client = Connection.Client;
        if (client is null || item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        RaiseCommands();
        try
        {
            var result = await operation(client);
            OperationFailed = !result.IsSuccess;
            OperationMessage = result.IsSuccess
                ? $"{result.Operation} '{result.PluginId}': {result.State}."
                : $"{result.Operation} '{result.PluginId}' failed: [{result.ErrorCode}] {result.Message}";
        }
        catch (IpcClientException exception)
        {
            ReportError(exception);
        }
        finally
        {
            item.IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task VerifySelectedAsync()
    {
        var client = Connection.Client ?? throw new IpcConnectionLostException("Not connected.");
        if (Selected is not { } item)
        {
            return;
        }

        var result = await client.VerifyPackageAsync(item.Id);
        OperationFailed = result.Status != "Verified";
        OperationMessage = OperationFailed
            ? $"Verification of '{item.Id}' failed: {result.Message}"
            : $"'{item.Id}': {result.Message}";
        await RefreshAsync();
    }

    private async Task RemoveSelectedAsync()
    {
        var client = Connection.Client ?? throw new IpcConnectionLostException("Not connected.");
        if (Selected is not { } item)
        {
            return;
        }

        var running = item.State is "Running" or "Loaded";
        if (!_dialogs.Confirm("Remove plugin", $"Remove {item.Name} {item.Version} and delete its files?" + (running ? " It is running and will be stopped first." : string.Empty), "Remove"))
        {
            return;
        }

        Operations.Track(await client.RemovePackageAsync(item.Id, stopIfRunning: running));
    }

    private async Task LoadDetailsAsync(string pluginId)
    {
        var client = Connection.Client;
        if (client is null)
        {
            return;
        }

        try
        {
            var details = await client.GetPluginAsync(pluginId);
            if (Selected?.Id == pluginId)
            {
                Details = details;
            }
        }
        catch (Exception exception) when (exception is IpcClientException or ObjectDisposedException)
        {
            // Details are optional; the list keeps working. A lost connection is handled by the page.
            if (Selected?.Id == pluginId)
            {
                Details = null;
            }
        }
    }

    private void Merge(IReadOnlyList<PluginSummaryDto> plugins)
    {
        var byId = _all.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _all.Clear();
        foreach (var plugin in plugins)
        {
            if (byId.TryGetValue(plugin.Id, out var existing))
            {
                existing.Update(plugin);
                _all.Add(existing);
            }
            else
            {
                _all.Add(new PluginItemViewModel(this, plugin));
            }
        }

        if (Selected is { } selected && !_all.Contains(selected))
        {
            Selected = null;
        }

        ApplyFilter();
        RaiseCounts();
    }

    private void ApplyFilter()
    {
        var visible = _all.Where(Matches).ToList();
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (!visible.Contains(Items[index]))
            {
                Items.RemoveAt(index);
            }
        }

        for (var index = 0; index < visible.Count; index++)
        {
            var current = Items.IndexOf(visible[index]);
            if (current < 0)
            {
                Items.Insert(index, visible[index]);
            }
            else if (current != index)
            {
                Items.Move(current, index);
            }
        }
    }

    private bool Matches(PluginItemViewModel item)
    {
        var stateMatches = StateFilter switch
        {
            PluginStateFilter.Running => item.State == "Running",
            PluginStateFilter.Stopped => item.State == "Stopped",
            PluginStateFilter.Failed => item.State == "Failed",
            PluginStateFilter.Ready => item.State is "Validated" or "Discovered",
            _ => true,
        };
        return stateMatches &&
            (SearchText.Length == 0 ||
             item.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
             item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(FailedCount));
        StartAllCommand.RaiseCanExecuteChanged();
        StopAllCommand.RaiseCanExecuteChanged();
    }

    private void CopyDiagnostic()
    {
        if (Details?.LastError is not { } error)
        {
            return;
        }

        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"[{error.Code}] {error.Operation}: {error.Message}")
            .AppendLine(CultureInfo.InvariantCulture, $"Occurred: {error.OccurredAt:O}");
        if (error.Diagnostic is { } diagnostic)
        {
            text.AppendLine(diagnostic.Detail ?? $"{diagnostic.ExceptionType}: {diagnostic.Message}\n{diagnostic.StackTrace}");
        }

        _dialogs.CopyToClipboard(text.ToString());
    }

    private void ReportError(Exception exception)
    {
        OperationFailed = true;
        OperationMessage = exception is IpcClientException ipc ? $"[{ipc.Code}] {ipc.Message}" : exception.Message;
    }
}

/// <summary>One installed plugin row.</summary>
public sealed class PluginItemViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;
    private PluginSummaryDto _summary;
    private InstalledPackageDto? _package;
    private bool _isBusy;

    public PluginItemViewModel(PluginsViewModel owner, PluginSummaryDto summary)
    {
        _owner = owner;
        _summary = summary;
        StartCommand = new AsyncRelayCommand(() => owner.StartAsync(this), CanStart);
        StopCommand = new AsyncRelayCommand(() => owner.StopAsync(this), CanStop);
        RestartCommand = new AsyncRelayCommand(() => owner.RestartAsync(this), CanStop);
    }

    public string Id => _summary.Id;

    public string Name => _summary.Name;

    public string Version => _summary.Version;

    public string State => _summary.State;

    public bool HasError => _summary.HasError;

    public string? LastErrorCode => _summary.LastErrorCode;

    /// <summary>Validated, stopped, and failed plugins can be started (stopped and failed ones with a new instance).</summary>
    public bool CanBeStarted => State is "Validated" or "Stopped" or "Failed";

    public bool IsRunning => State is "Running";

    public TrustStateDto Trust => _package?.Trust ?? TrustStateDto.Unmanaged;

    public string TrustLabel => TrustText.Label(Trust, _package?.SignedWithTestKey ?? false);

    public string TrustExplanation => TrustText.Explanation(Trust, _package?.SignedWithTestKey ?? false) + RevocationText;

    /// <summary>The revocation from the signed catalog, when this installation is revoked.</summary>
    public string? RevocationReason => _package?.Revocation is { } revocation
        ? (revocation.Kind == "signing-key" ? $"Its signing key '{revocation.KeyId}' was revoked: " : string.Empty) + revocation.Reason + (revocation.AdvisoryUrl is null ? string.Empty : $" ({revocation.AdvisoryUrl})")
        : null;

    public bool IsRevoked => _package?.Revocation is not null;

    /// <summary>Set when the installed version was withdrawn from the Store (it keeps working).</summary>
    public string? CatalogNotice => _package?.CatalogStatus == "withdrawn"
        ? $"This version was withdrawn from the Store{(_package.CatalogStatusReason is null ? "." : $": {_package.CatalogStatusReason}")}"
        : null;

    private string RevocationText => RevocationReason is { } reason ? $" Reason: {reason}" : CatalogNotice is { } notice ? $" {notice}" : string.Empty;

    public bool IsManaged => _package is { Source: not "manual" };

    public string? AvailableVersion => _package?.AvailableVersion;

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandsChanged();
            }
        }
    }

    internal void Update(PluginSummaryDto summary)
    {
        if (summary == _summary)
        {
            return;
        }

        _summary = summary;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(LastErrorCode));
        OnPropertyChanged(nameof(CanBeStarted));
        OnPropertyChanged(nameof(IsRunning));
        RaiseCommandsChanged();
    }

    internal void SetPackage(InstalledPackageDto? package)
    {
        if (package == _package)
        {
            return;
        }

        _package = package;
        OnPropertyChanged(nameof(Trust));
        OnPropertyChanged(nameof(TrustLabel));
        OnPropertyChanged(nameof(TrustExplanation));
        OnPropertyChanged(nameof(RevocationReason));
        OnPropertyChanged(nameof(IsRevoked));
        OnPropertyChanged(nameof(CatalogNotice));
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(AvailableVersion));
    }

    internal void RaiseCommandsChanged()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private bool CanStart() => !IsBusy && _owner.CanOperate && CanBeStarted && !_owner.HasActiveOperation(Id);

    private bool CanStop() => !IsBusy && _owner.CanOperate && IsRunning && !_owner.HasActiveOperation(Id);
}
