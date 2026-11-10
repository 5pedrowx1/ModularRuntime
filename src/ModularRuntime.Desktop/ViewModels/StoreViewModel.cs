using System.Collections.ObjectModel;
using System.IO;
using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>The two areas of the Store page.</summary>
public enum StoreTab
{
    Discover,
    Updates,
}

/// <summary>One official registry entry shown as a card.</summary>
public sealed class StoreItemViewModel : ObservableObject
{
    private const int ShortDescriptionLength = 120;

    private readonly StoreViewModel _owner;

    public StoreItemViewModel(StoreViewModel owner, RegistryPluginDto plugin)
    {
        _owner = owner;
        Plugin = plugin;
        ActionCommand = new AsyncRelayCommand(() => owner.InstallOrUpdateAsync(this), CanAct, owner.Report);
        ShowMoreCommand = new RelayCommand(() => owner.Details = this);
    }

    public RegistryPluginDto Plugin { get; }

    public string Id => Plugin.Id;

    public string Name => Plugin.Name;

    public string Author => Plugin.Author;

    public string Category => Plugin.Category;

    public string Version => Plugin.Version;

    public string Description => Plugin.Description;

    public string ShortDescription => Plugin.Description.Length <= ShortDescriptionLength
        ? Plugin.Description
        : string.Concat(Plugin.Description.AsSpan(0, ShortDescriptionLength).TrimEnd(), "…");

    /// <summary>The registry has no icons; cards show the plugin's initial instead.</summary>
    public string Initial => Plugin.Name.Length > 0 ? char.ToUpperInvariant(Plugin.Name[0]).ToString() : "?";

    public bool IsInstalled => Plugin.InstalledVersion is not null;

    /// <summary>Signature trust of the catalog entry, re-verified on the package at installation.</summary>
    public string TrustLabel => Plugin.SigningKeyRevoked ? "Signing key revoked"
        : !Plugin.SigningKeyTrusted ? "Signing key not trusted"
        : Plugin.SigningKeyIsTest ? "Official (test key)"
        : "Official";

    public bool IsTrustWarning => !Plugin.SigningKeyTrusted || Plugin.SigningKeyIsTest || Plugin.SigningKeyRevoked || !IsPublished;

    public bool IsPublished => Plugin.Status == "published";

    public bool IsRevoked => Plugin.Status == "revoked" || Plugin.SigningKeyRevoked;

    /// <summary>Publication state from the signed catalog: published, withdrawn from the Store, or revoked for security.</summary>
    public string StatusLabel => Plugin.Status switch
    {
        "revoked" => "Revoked for security",
        "withdrawn" => "Withdrawn from the Store",
        _ => Plugin.SigningKeyRevoked ? "Signing key revoked" : "Published",
    };

    /// <summary>The reason (and advisory link) given by the ModularRuntime team, when the version is not published.</summary>
    public string? StatusDetails => Plugin.StatusReason is null && Plugin.AdvisoryUrl is null
        ? null
        : string.Join(" ", new[] { Plugin.StatusReason, Plugin.AdvisoryUrl is null ? null : $"Advisory: {Plugin.AdvisoryUrl}" }.Where(t => t is not null));

    /// <summary>Provenance recorded by the official pipeline: controlled build and source commit.</summary>
    public string? Provenance => Plugin.BuildId is null && Plugin.SourceCommit is null
        ? null
        : string.Join(" · ", new[] { Plugin.BuildId is null ? null : $"Build {Plugin.BuildId}", Plugin.SourceCommit is null ? null : $"source {Plugin.SourceCommit}" }.Where(t => t is not null));

    /// <summary>Every version in the catalog, newest first, with its status (the version history).</summary>
    public IReadOnlyList<string> VersionHistory => Plugin.Versions?.Select(v =>
        $"{v.Version} — {v.Status}{(v.Compatible ? string.Empty : " (incompatible)")} · API {v.ApiVersion} · {v.PublishedAt.ToLocalTime():d}{(v.StatusReason is null ? string.Empty : $" · {v.StatusReason}")}").ToList() ?? [];

    public string InstallState => Plugin.Status == "revoked" ? "Revoked — do not use"
        : Plugin.Status == "withdrawn" ? "Withdrawn from the Store"
        : !Plugin.Compatible ? "Incompatible"
        : Plugin.UpdateAvailable ? $"Installed {Plugin.InstalledVersion} · update available"
        : IsInstalled ? $"Installed {Plugin.InstalledVersion}"
        : "Not installed";

    public string ActionText => IsRevoked ? "Revoked"
        : Plugin.Status == "withdrawn" ? "Unavailable"
        : !Plugin.Compatible ? "Incompatible"
        : Plugin.UpdateAvailable ? "Update"
        : IsInstalled ? "Installed"
        : "Install";

    public bool IsBusy => _owner.Operations.HasActiveOperation(Id);

    public AsyncRelayCommand ActionCommand { get; }

    public RelayCommand ShowMoreCommand { get; }

    internal void RaiseState()
    {
        OnPropertyChanged(nameof(IsBusy));
        ActionCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Install and Update are offered only for a published, compatible version signed by a trusted,
    /// non-revoked key. The daemon enforces the same rules and verifies the package itself.
    /// </summary>
    private bool CanAct() =>
        _owner.IsConnected && Plugin.Installable && (!IsInstalled || Plugin.UpdateAvailable) && !IsBusy;
}

/// <summary>A row of cards. Rows are virtualized; the column count follows the available width.</summary>
public sealed class StoreRow(IReadOnlyList<StoreItemViewModel> items)
{
    public IReadOnlyList<StoreItemViewModel> Items { get; } = items;
}

/// <summary>
/// Store: discover and install plugins from the authenticated official catalog, update them, and
/// install local ZIP packages. All work happens in the daemon; this page sends requests, uploads a
/// chosen ZIP to the daemon's staging area, and shows the daemon's results and progress.
/// </summary>
public sealed class StoreViewModel : PageViewModel
{
    public const string AllCategories = "All categories";

    private readonly IDialogService _dialogs;
    private readonly List<StoreItemViewModel> _all = [];
    private RegistryStatusDto? _status;
    private StoreItemViewModel? _details;
    private StoreTab _tab = StoreTab.Discover;
    private string _searchText = string.Empty;
    private string _category = AllCategories;
    private string? _catalogError;
    private string? _uploadStatus;
    private string? _message;
    private bool _messageFailed;
    private bool _refreshedOnce;
    private int _columnCount = 3;

    public StoreViewModel(IRuntimeConnection connection, IUiDispatcher dispatcher, IDialogService dialogs, PackageOperationsTracker operations)
        : base("Store", "", connection, dispatcher)
    {
        _dialogs = dialogs;
        Operations = operations;
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshCatalogAsync, () => IsConnected, Report);
        InstallZipCommand = new AsyncRelayCommand(InstallZipAsync, () => IsConnected, Report);
        UpdateCommand = new AsyncRelayCommand(p => UpdateAsync(p as PackageUpdateDto), p => p is PackageUpdateDto { Compatible: true } u && IsConnected && !operations.HasActiveOperation(u.PluginId), Report);
        CloseDetailsCommand = new RelayCommand(() => Details = null);
        DismissMessageCommand = new RelayCommand(() => Message = null);
        operations.Changed += (_, _) => RaiseItemStates();
        operations.OperationFinished += (_, operation) =>
        {
            // Success is shown only now, after the daemon reported it.
            MessageFailed = operation.Stage != PackageOperationStageDto.Completed;
            Message = OperationItemViewModel.Outcome(operation);
            RequestRefresh();
        };
    }

    public PackageOperationsTracker Operations { get; }

    /// <summary>Visible cards grouped in rows of <see cref="ColumnCount"/>.</summary>
    public ObservableCollection<StoreRow> Rows { get; } = [];

    public ObservableCollection<string> Categories { get; } = [AllCategories];

    public ObservableCollection<PackageUpdateDto> Updates { get; } = [];

    public AsyncRelayCommand RefreshCatalogCommand { get; }

    public AsyncRelayCommand InstallZipCommand { get; }

    public AsyncRelayCommand UpdateCommand { get; }

    public RelayCommand CloseDetailsCommand { get; }

    public RelayCommand DismissMessageCommand { get; }

    public IReadOnlyList<StoreItemViewModel> Items => [.. Rows.SelectMany(r => r.Items)];

    public bool HasItems => Rows.Count > 0;

    public bool HasUpdates => Updates.Count > 0;

    public int UpdateCount => Updates.Count;

    public StoreTab Tab
    {
        get => _tab;
        set => SetProperty(ref _tab, value);
    }

    /// <summary>The card whose details panel is open ("Show more").</summary>
    public StoreItemViewModel? Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }

    public RegistryStatusDto? Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(UsesTestKeys));
            }
        }
    }

    public bool UsesTestKeys => Status?.TestKeysTrusted ?? false;

    public string StatusText => Status switch
    {
        null => "Loading the official registry…",
        { Configured: false } => "No official registry is configured for this daemon. Local ZIP packages can still be installed.",
        { Loaded: false, ErrorCode: { } code } => $"The registry could not be verified [{code}]: {Status.ErrorMessage}",
        { Loaded: false } => "The official registry has not been downloaded yet. Select Refresh catalog.",
        _ => $"Official registry · revision {Status.Revision} · signed by {Status.SigningKeyId} · verified {Status.VerifiedAt?.ToLocalTime():g} · expires {Status.ExpiresAt?.ToLocalTime():d}",
    };

    public string? CatalogError
    {
        get => _catalogError;
        private set => SetProperty(ref _catalogError, value);
    }

    public string? UploadStatus
    {
        get => _uploadStatus;
        private set => SetProperty(ref _uploadStatus, value);
    }

    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool MessageFailed
    {
        get => _messageFailed;
        private set => SetProperty(ref _messageFailed, value);
    }

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

    public string Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value ?? AllCategories))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Cards per row; set by the view from its width.</summary>
    public int ColumnCount
    {
        get => _columnCount;
        set
        {
            if (SetProperty(ref _columnCount, Math.Clamp(value, 1, 8)))
            {
                ApplyFilter();
            }
        }
    }

    internal bool IsConnected => Connection.State == ConnectionState.Connected;

    internal void Report(Exception exception)
    {
        MessageFailed = true;
        Message = exception is IpcClientException ipc ? $"[{ipc.Code}] {ipc.Message}" : exception.Message;
    }

    internal async Task InstallOrUpdateAsync(StoreItemViewModel item)
    {
        var client = Connection.Client ?? throw new IpcConnectionLostException("Not connected.");
        if (item.Plugin.UpdateAvailable)
        {
            var running = await IsRunningAsync(client, item.Id);
            if (!_dialogs.Confirm("Update plugin", $"Update {item.Name} from {item.Plugin.InstalledVersion} to {item.Version}?" + (running ? " It is running and will be stopped first; start it again in Plugins." : string.Empty), "Update"))
            {
                return;
            }

            Operations.Track(await client.UpdatePackageAsync(item.Id, stopIfRunning: running));
        }
        else
        {
            Operations.Track(await client.InstallOfficialAsync(item.Id, item.Version));
        }
    }

    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        var response = await client.ListRegistryAsync(cancellationToken);
        if (!_refreshedOnce && response.Status is { Configured: true, Loaded: false, ErrorCode: null })
        {
            // First visit with no cached catalog: download and verify it once.
            _refreshedOnce = true;
            try
            {
                await client.RefreshRegistryAsync(cancellationToken);
                response = await client.ListRegistryAsync(cancellationToken);
            }
            catch (IpcClientException exception) when (exception is not IpcConnectionLostException)
            {
                CatalogError = $"[{exception.Code}] {exception.Message}";
            }
        }

        _refreshedOnce = true;
        Status = response.Status;
        var detailsId = Details?.Id;
        _all.Clear();
        _all.AddRange(response.Plugins.Select(p => new StoreItemViewModel(this, p)));
        SyncCategories();
        ApplyFilter();
        Details = detailsId is null ? null : _all.FirstOrDefault(i => i.Id == detailsId);

        var updates = await client.CheckUpdatesAsync(cancellationToken);
        Updates.Clear();
        foreach (var update in updates.Updates)
        {
            Updates.Add(update);
        }

        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdateCount));
        UpdateCommand.RaiseCanExecuteChanged();

        // The page has its own empty states (no registry, empty catalog); it is always "ready".
        return true;
    }

    protected override void Clear()
    {
        _all.Clear();
        Rows.Clear();
        Updates.Clear();
        Details = null;
        Status = null;
        CatalogError = null;
        UploadStatus = null;
        Message = null;
        _refreshedOnce = false;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdateCount));
    }

    protected override void OnConnected() => RaiseCommands();

    protected override void OnDisconnected() => RaiseCommands();

    private void RaiseCommands()
    {
        RefreshCatalogCommand.RaiseCanExecuteChanged();
        InstallZipCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        RaiseItemStates();
    }

    private void RaiseItemStates()
    {
        foreach (var item in _all)
        {
            item.RaiseState();
        }

        UpdateCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshCatalogAsync()
    {
        var client = Connection.Client ?? throw new IpcConnectionLostException("Not connected.");
        CatalogError = null;
        try
        {
            await client.RefreshRegistryAsync();
        }
        catch (IpcClientException exception) when (exception is not IpcConnectionLostException)
        {
            CatalogError = $"[{exception.Code}] {exception.Message}";
        }

        await RefreshAsync();
    }

    private async Task UpdateAsync(PackageUpdateDto? update)
    {
        var client = Connection.Client ?? throw new IpcConnectionLostException("Not connected.");
        if (update is null)
        {
            return;
        }

        var running = await IsRunningAsync(client, update.PluginId);
        if (!_dialogs.Confirm(
                "Update plugin",
                $"Update {update.Name} from {update.InstalledVersion} to {update.AvailableVersion}?" + (running ? " It is running and will be stopped first; start it again in Plugins." : string.Empty),
                "Update"))
        {
            return;
        }

        Operations.Track(await client.UpdatePackageAsync(update.PluginId, stopIfRunning: running));
    }

    /// <summary>
    /// Install from ZIP: the file is uploaded to the daemon's staging area, the daemon verifies its
    /// own copy and returns a preview; unsigned packages require the user's explicit acknowledgement.
    /// </summary>
    private async Task InstallZipAsync()
    {
        var path = _dialogs.PickPackageFile();
        if (path is null)
        {
            return;
        }

        var client = Connection.Client ?? throw new IpcConnectionLostException("Not connected.");
        var uploading = true;
        try
        {
            Guid uploadId;
            UploadStatus = $"Sending {Path.GetFileName(path)} to the daemon…";
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                // Progress<T> reports asynchronously: ignore reports that arrive after the upload finished.
                uploadId = await client.UploadPackageAsync(
                    file,
                    Path.GetFileName(path),
                    new Progress<double>(p =>
                    {
                        if (uploading)
                        {
                            UploadStatus = $"Sending {Path.GetFileName(path)} to the daemon… {p:P0}";
                        }
                    }));
            }

            uploading = false;

            UploadStatus = "The daemon is verifying the package…";
            var preview = await client.InspectUploadAsync(uploadId);
            UploadStatus = null;
            var decision = _dialogs.ShowPackagePreview(preview);
            var unverified = preview.Trust != TrustStateDto.OfficialVerified;
            if (!preview.Valid || !decision.Install || (unverified && !decision.AcceptedUnverified))
            {
                await client.DiscardUploadAsync(uploadId);
                return;
            }

            Operations.Track(await client.InstallUploadAsync(uploadId, acceptUnverified: unverified, decision.StopIfRunning));
        }
        finally
        {
            uploading = false;
            UploadStatus = null;
        }
    }

    private static async Task<bool> IsRunningAsync(IRuntimeClient client, string pluginId) =>
        (await client.ListPackagesAsync()).Packages.Any(p => p.PluginId == pluginId && p.EngineState is "Running" or "Loaded");

    private void SyncCategories()
    {
        // Synchronize instead of clearing: clearing the bound collection resets the ComboBox selection.
        var categories = _all.Select(i => i.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var stale in Categories.Skip(1).Where(c => !categories.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Categories.Remove(stale);
        }

        for (var index = 0; index < categories.Count; index++)
        {
            if (index + 1 >= Categories.Count || !string.Equals(Categories[index + 1], categories[index], StringComparison.OrdinalIgnoreCase))
            {
                Categories.Insert(index + 1, categories[index]);
            }
        }

        if (!Categories.Contains(Category))
        {
            Category = AllCategories;
        }
    }

    private void ApplyFilter()
    {
        var visible = _all.Where(Matches).ToList();
        Rows.Clear();
        for (var index = 0; index < visible.Count; index += ColumnCount)
        {
            Rows.Add(new StoreRow(visible.Skip(index).Take(ColumnCount).ToList()));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Items));
    }

    private bool Matches(StoreItemViewModel item) =>
        (Category == AllCategories || string.Equals(item.Category, Category, StringComparison.OrdinalIgnoreCase)) &&
        (SearchText.Length == 0 ||
         item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         item.Author.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         item.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
}
