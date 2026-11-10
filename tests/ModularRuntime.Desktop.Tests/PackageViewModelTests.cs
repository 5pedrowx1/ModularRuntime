using ModularRuntime.Desktop.Services;
using ModularRuntime.Desktop.ViewModels;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Tests;

/// <summary>Installed plugins page: trust, integrity, removal and lifecycle of managed packages.</summary>
public sealed class InstalledPluginsViewModelTests : IAsyncDisposable
{
    private readonly FakeConnection _connection = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeClient _client = new();
    private readonly PackageOperationsTracker _operations;
    private readonly PluginsViewModel _plugins;

    public InstalledPluginsViewModelTests()
    {
        _operations = new PackageOperationsTracker(_connection, new InlineDispatcher());
        _plugins = new PluginsViewModel(_connection, new InlineDispatcher(), _dialogs, _operations) { IsActive = true };
        _client.Plugins = [FakeClient.Plugin("com.official", "Validated"), FakeClient.Plugin("com.local", "Running"), FakeClient.Plugin("com.manual", "Validated")];
        _client.Packages =
        [
            PackageFixtures.Package("com.official", TrustStateDto.OfficialVerified, "registry", "Validated", available: "2.0.0"),
            PackageFixtures.Package("com.local", TrustStateDto.LocalUnverified, "local-zip", "Running"),
            PackageFixtures.Package("com.manual", TrustStateDto.Unmanaged, "manual", "Validated"),
            PackageFixtures.Package("com.broken", TrustStateDto.Modified, "registry", engineState: null),
        ];
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        _connection.Connect(_client);
        await _plugins.RefreshCommand.Execution;
    }

    private PluginItemViewModel Item(string id) => _plugins.Items.Single(i => i.Id == id);

    [Fact]
    public async Task Installed_ShowsTheTrustOfEveryPlugin_IncludingManagedPackagesThatAreNotLoaded()
    {
        await ConnectAsync();

        Assert.Equal("Official (test key)", Item("com.official").TrustLabel);
        Assert.Equal("Local, unverified", Item("com.local").TrustLabel);
        Assert.Equal("Unmanaged", Item("com.manual").TrustLabel);
        Assert.Equal("Modified", Item("com.broken").TrustLabel);
        Assert.Equal("NotLoaded", Item("com.broken").State);
        Assert.Equal("2.0.0", Item("com.official").AvailableVersion);
        Assert.False(Item("com.manual").IsManaged);
        Assert.Equal(4, _plugins.TotalCount);
    }

    [Fact]
    public async Task OnlyAnOfficialSignature_IsEverPresentedAsVerified()
    {
        foreach (var trust in Enum.GetValues<TrustStateDto>().Where(t => t != TrustStateDto.OfficialVerified))
        {
            Assert.DoesNotContain("Official", TrustText.Label(trust, testKey: false), StringComparison.Ordinal);
            Assert.DoesNotContain("were verified", TrustText.Explanation(trust, testKey: false), StringComparison.Ordinal);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task InstalledPackage_FromTheStore_AppearsInPlugins()
    {
        await ConnectAsync();
        _client.Plugins = [.. _client.Plugins, FakeClient.Plugin("com.new", "Validated")];
        _client.Packages = [.. _client.Packages, PackageFixtures.Package("com.new", TrustStateDto.OfficialVerified, "registry", "Validated")];

        _connection.RaiseOperation(FakeClient.Operation("com.new", PackageOperationKindDto.InstallOfficial, PackageOperationStageDto.Completed));
        await _plugins.RefreshCommand.Execution;

        Assert.Equal("Validated", Item("com.new").State);
        Assert.True(Item("com.new").StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Remove_AsksForConfirmation_AndStopsARunningPluginOnlyThen()
    {
        await ConnectAsync();
        _plugins.Selected = Item("com.local");

        _dialogs.ConfirmResult = false;
        await _plugins.RemoveSelectedCommand.ExecuteAsync();
        Assert.DoesNotContain(_client.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));

        _dialogs.ConfirmResult = true;
        await _plugins.RemoveSelectedCommand.ExecuteAsync();
        Assert.Contains("remove com.local stop=True", _client.Calls);
        Assert.Contains("stopped first", _dialogs.Confirmations.Last(), StringComparison.Ordinal);
        Assert.Single(_operations.Operations);
        Assert.Null(_plugins.OperationMessage); // reported only when the daemon finishes
    }

    [Fact]
    public async Task UnmanagedPlugins_CannotBeVerifiedOrRemoved()
    {
        await ConnectAsync();

        _plugins.Selected = Item("com.manual");

        Assert.False(_plugins.VerifyCommand.CanExecute(null));
        Assert.False(_plugins.RemoveSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Verify_ShowsTheDaemonsResult()
    {
        await ConnectAsync();
        _plugins.Selected = Item("com.official");

        await _plugins.VerifyCommand.ExecuteAsync();

        Assert.Contains("verify com.official", _client.Calls);
        Assert.True(_plugins.OperationFailed);
        Assert.Contains("SHA-256 differs", _plugins.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivePackageOperation_BlocksLifecycleCommandsForThatPlugin()
    {
        await ConnectAsync();
        var id = "fedcba9876543210fedcba9876543210";

        _connection.RaiseOperation(FakeClient.Operation("com.local", PackageOperationKindDto.Update, PackageOperationStageDto.Verifying, id));

        Assert.False(Item("com.local").StopCommand.CanExecute(null));
        Assert.False(Item("com.local").RestartCommand.CanExecute(null));
        Assert.True(Item("com.official").StartCommand.CanExecute(null));

        _connection.RaiseOperation(FakeClient.Operation("com.local", PackageOperationKindDto.Update, PackageOperationStageDto.Completed, id));
        Assert.Empty(_operations.Operations);
        Assert.True(Item("com.local").StopCommand.CanExecute(null));
        Assert.Null(_plugins.OperationMessage); // updates are reported by the Store, removals by this page
    }

    [Fact]
    public async Task Disconnection_ClearsOperations()
    {
        await ConnectAsync();
        _connection.RaiseOperation(FakeClient.Operation("com.official", PackageOperationKindDto.InstallOfficial, PackageOperationStageDto.Downloading));
        Assert.Single(_operations.Operations);

        _connection.Disconnect();

        Assert.Empty(_operations.Operations);
        Assert.Empty(_plugins.Items);
    }
}

/// <summary>Store page: official catalog, search, details, installation, updates and local ZIP installation.</summary>
public sealed class StoreViewModelTests : IAsyncDisposable
{
    private readonly FakeConnection _connection = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeClient _client = new();
    private readonly PackageOperationsTracker _operations;
    private readonly StoreViewModel _store;
    private readonly string _zip = Path.Combine(Path.GetTempPath(), $"mr-desktop-test-{Guid.NewGuid():N}.zip");

    public StoreViewModelTests()
    {
        _operations = new PackageOperationsTracker(_connection, new InlineDispatcher());
        _store = new StoreViewModel(_connection, new InlineDispatcher(), _dialogs, _operations) { IsActive = true };
        _client.Packages = [PackageFixtures.Package("com.official", TrustStateDto.OfficialVerified, "registry", "Running")];
        _client.Registry = new RegistryListResponse(FakeClient.EmptyRegistryStatus with { Loaded = true, Revision = 4, SigningKeyId = "test-key" },
        [
            PackageFixtures.RegistryEntry("com.new", installed: null, compatible: true) with { Category = "Files", Name = "Alpha Files" },
            PackageFixtures.RegistryEntry("com.old", installed: null, compatible: false) with { Category = "Network", Name = "Old Net" },
            PackageFixtures.RegistryEntry("com.official", installed: "1.0.0", compatible: true) with { Category = "Network", Name = "Feeds", Description = "Fetches feeds", Author = "Contoso" },
            PackageFixtures.RegistryEntry("com.update", installed: "0.9.0", compatible: true) with { Category = "Files", Name = "Updater", UpdateAvailable = true },
            PackageFixtures.RegistryEntry("com.untrusted", installed: null, compatible: true) with { Category = "Files", Name = "Untrusted", SigningKeyTrusted = false },
        ]);
        File.WriteAllBytes(_zip, new byte[10]);
    }

    public async ValueTask DisposeAsync()
    {
        File.Delete(_zip);
        await _client.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        _connection.Connect(_client);
        await _store.RefreshCommand.Execution;
    }

    private StoreItemViewModel Card(string id) => _store.Items.Single(i => i.Id == id);

    private static PackagePreviewDto Preview(TrustStateDto trust, bool valid = true) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), valid, valid ? null : "integrity_mismatch", valid ? null : "bad", "com.zip", "Zip Plugin", "1.0.0",
            null, null, "0.3", trust, trust == TrustStateDto.OfficialVerified ? "test-key" : null, false, 3, 10, new string('b', 64), true,
            null, false, false);

    [Fact]
    public async Task Catalog_IsLoadedFromTheDaemon_AsCardsWithTheirStates()
    {
        await ConnectAsync();

        Assert.Equal(PageState.Ready, _store.State);
        Assert.Equal(5, _store.Items.Count);
        Assert.DoesNotContain("registry.refresh", _client.Calls); // the catalog was already verified by the daemon
        Assert.Contains("revision 4", _store.StatusText, StringComparison.Ordinal);
        Assert.Equal(["Files", "Network"], _store.Categories.Skip(1));

        Assert.Equal("Install", Card("com.new").ActionText);
        Assert.Equal("Not installed", Card("com.new").InstallState);
        Assert.Equal("Incompatible", Card("com.old").ActionText);
        Assert.Equal("Installed", Card("com.official").ActionText);
        Assert.Equal("Update", Card("com.update").ActionText);
        Assert.Equal("Official", Card("com.new").TrustLabel);
        Assert.Equal("Signing key not trusted", Card("com.untrusted").TrustLabel);
        Assert.Equal("A", Card("com.new").Initial);
    }

    [Fact]
    public async Task FirstVisit_WithoutACachedCatalog_RefreshesItOnce()
    {
        _client.Registry = _client.Registry with { Status = FakeClient.EmptyRegistryStatus };

        await ConnectAsync();

        Assert.Single(_client.Calls, "registry.refresh");
        await _store.RefreshCommand.ExecuteAsync();
        Assert.Single(_client.Calls, "registry.refresh");
    }

    [Fact]
    public async Task RefreshCatalog_AsksTheDaemonToDownloadAndVerifyAgain()
    {
        await ConnectAsync();

        await _store.RefreshCatalogCommand.ExecuteAsync();

        Assert.Contains("registry.refresh", _client.Calls);
    }

    [Fact]
    public async Task Search_MatchesNameAuthorAndDescription_AndCategoryFilters()
    {
        await ConnectAsync();

        _store.Category = "Network";
        Assert.Equal(["com.old", "com.official"], _store.Items.Select(i => i.Id));

        _store.Category = StoreViewModel.AllCategories;
        _store.SearchText = "feeds";
        Assert.Equal(["com.official"], _store.Items.Select(i => i.Id));
        _store.SearchText = "contoso";
        Assert.Equal(["com.official"], _store.Items.Select(i => i.Id));
        _store.SearchText = "alpha";
        Assert.Equal(["com.new"], _store.Items.Select(i => i.Id));

        _store.Category = "Network";
        Assert.False(_store.HasItems);
    }

    [Fact]
    public async Task Cards_AreGroupedInRowsOfTheAvailableColumnCount()
    {
        await ConnectAsync();

        _store.ColumnCount = 2;
        Assert.Equal([2, 2, 1], _store.Rows.Select(r => r.Items.Count));

        _store.ColumnCount = 4;
        Assert.Equal([4, 1], _store.Rows.Select(r => r.Items.Count));

        _store.ColumnCount = 0;
        Assert.Equal(1, _store.ColumnCount);
    }

    [Fact]
    public async Task ShowMore_OpensTheDetails_AndSurvivesARefresh()
    {
        await ConnectAsync();

        Card("com.official").ShowMoreCommand.Execute(null);
        Assert.Equal("com.official", _store.Details?.Id);
        Assert.Equal("Fetches feeds", _store.Details?.Description);

        await _store.RefreshCommand.ExecuteAsync();
        Assert.Equal("com.official", _store.Details?.Id);

        _store.CloseDetailsCommand.Execute(null);
        Assert.Null(_store.Details);
    }

    [Fact]
    public async Task ShortDescription_IsTruncated_TheDetailsShowTheFullText()
    {
        var text = new string('x', 300);
        _client.Registry = _client.Registry with { Plugins = [PackageFixtures.RegistryEntry("com.long", null, true) with { Description = text }] };
        await ConnectAsync();

        Assert.True(Card("com.long").ShortDescription.Length <= 121);
        Assert.EndsWith("…", Card("com.long").ShortDescription, StringComparison.Ordinal);
        Assert.Equal(text, Card("com.long").Description);
    }

    [Fact]
    public async Task Install_IsAllowedOnlyForCompatibleTrustedPluginsNotInstalled()
    {
        await ConnectAsync();

        Assert.True(Card("com.new").ActionCommand.CanExecute(null));
        Assert.True(Card("com.update").ActionCommand.CanExecute(null));
        Assert.False(Card("com.old").ActionCommand.CanExecute(null));
        Assert.False(Card("com.official").ActionCommand.CanExecute(null));
        Assert.False(Card("com.untrusted").ActionCommand.CanExecute(null));

        await Card("com.new").ActionCommand.ExecuteAsync();

        Assert.Contains("install com.new", _client.Calls);
        Assert.Single(_operations.Operations);
        Assert.True(Card("com.new").IsBusy);
        Assert.False(Card("com.new").ActionCommand.CanExecute(null)); // one operation per plugin
        Assert.Null(_store.Message); // nothing is reported before the daemon finishes
    }

    [Fact]
    public async Task OperationSuccess_IsReportedOnlyAfterTheDaemonConfirms()
    {
        await ConnectAsync();
        await Card("com.new").ActionCommand.ExecuteAsync();
        var operation = Assert.Single(_operations.Operations);

        _connection.RaiseOperation(FakeClient.Operation("com.new", PackageOperationKindDto.InstallOfficial, PackageOperationStageDto.Verifying, operation.OperationId));
        Assert.True(operation.IsIndeterminate);
        Assert.Null(_store.Message);

        _connection.RaiseOperation(FakeClient.Operation("com.new", PackageOperationKindDto.InstallOfficial, PackageOperationStageDto.Completed, operation.OperationId));
        Assert.Empty(_operations.Operations);
        Assert.False(_store.MessageFailed);
        Assert.NotNull(_store.Message);
    }

    [Fact]
    public async Task FailedOperation_IsReportedWithItsCode()
    {
        await ConnectAsync();

        _connection.RaiseOperation(FakeClient.Operation("com.new", PackageOperationKindDto.InstallOfficial, PackageOperationStageDto.Failed));

        Assert.True(_store.MessageFailed);
        Assert.Contains("integrity_mismatch", _store.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_FromACard_AsksForConfirmation_AndStopsOnlyARunningPlugin()
    {
        _client.Packages = [PackageFixtures.Package("com.update", TrustStateDto.OfficialVerified, "registry", "Running")];
        await ConnectAsync();

        _dialogs.ConfirmResult = false;
        await Card("com.update").ActionCommand.ExecuteAsync();
        Assert.DoesNotContain(_client.Calls, c => c.StartsWith("update", StringComparison.Ordinal));

        _dialogs.ConfirmResult = true;
        await Card("com.update").ActionCommand.ExecuteAsync();
        Assert.Contains("update com.update stop=True", _client.Calls);
    }

    [Fact]
    public async Task UpdatesTab_ListsNewerVersions_AndUpdatesAfterConfirmation()
    {
        _client.AvailableUpdates = [new PackageUpdateDto("com.local", "Local", "1.0.0", "1.1.0", "Fixes", true, null)];
        await ConnectAsync();
        var update = Assert.Single(_store.Updates);
        Assert.Equal(1, _store.UpdateCount);

        _dialogs.ConfirmResult = true;
        await _store.UpdateCommand.ExecuteAsync(update);

        Assert.Contains("update com.local stop=False", _client.Calls);
    }

    [Fact]
    public async Task InstallFromZip_Unverified_IsInstalledOnlyAfterExplicitAcceptance()
    {
        await ConnectAsync();
        _dialogs.PackageFile = _zip;
        _client.Preview = Preview(TrustStateDto.LocalUnverified);

        _dialogs.Decision = new PackageInstallDecision(Install: true, AcceptedUnverified: false, StopIfRunning: false);
        await _store.InstallZipCommand.ExecuteAsync();
        Assert.Empty(_client.UploadInstalls);
        Assert.Single(_client.Discarded);

        _dialogs.Decision = new PackageInstallDecision(Install: true, AcceptedUnverified: true, StopIfRunning: false);
        await _store.InstallZipCommand.ExecuteAsync();

        var install = Assert.Single(_client.UploadInstalls);
        Assert.True(install.AcceptUnverified);
        Assert.Equal(6, _client.Chunks.Count(c => c.Id == install.UploadId)); // 10 bytes in 4-byte chunks (3), once per attempt; the fake reuses the upload ID
        Assert.Single(_operations.Operations);
        Assert.Null(_store.Message);
        Assert.Null(_store.UploadStatus);
    }

    [Fact]
    public async Task InstallFromZip_OfficialSignature_DoesNotClaimUnverifiedAcceptance()
    {
        await ConnectAsync();
        _dialogs.PackageFile = _zip;
        _client.Preview = Preview(TrustStateDto.OfficialVerified);
        _dialogs.Decision = new PackageInstallDecision(true, false, false);

        await _store.InstallZipCommand.ExecuteAsync();

        Assert.False(Assert.Single(_client.UploadInstalls).AcceptUnverified);
    }

    [Fact]
    public async Task InstallFromZip_RejectedPackage_IsShownAndDiscarded()
    {
        await ConnectAsync();
        _dialogs.PackageFile = _zip;
        _client.Preview = Preview(TrustStateDto.VerificationFailed, valid: false);
        _dialogs.Decision = new PackageInstallDecision(true, true, false);

        await _store.InstallZipCommand.ExecuteAsync();

        Assert.False(Assert.Single(_dialogs.Previews).Valid);
        Assert.Empty(_client.UploadInstalls);
        Assert.Single(_client.Discarded);
    }

    [Fact]
    public async Task InstallFromZip_CanceledFilePicker_DoesNothing()
    {
        await ConnectAsync();
        _dialogs.PackageFile = null;

        await _store.InstallZipCommand.ExecuteAsync();

        Assert.Empty(_client.Chunks);
        Assert.Empty(_dialogs.Previews);
    }

    [Fact]
    public async Task Disconnection_ClearsTheCatalog_AndDisablesActions()
    {
        await ConnectAsync();

        _connection.Disconnect();

        Assert.Empty(_store.Items);
        Assert.Empty(_store.Updates);
        Assert.False(_store.InstallZipCommand.CanExecute(null));
        Assert.False(_store.RefreshCatalogCommand.CanExecute(null));
    }
}

internal static class PackageFixtures
{
    public static InstalledPackageDto Package(string id, TrustStateDto trust, string source, string? engineState, string? available = null) =>
        new(id, id, "1.0.0", "Author", null, source, trust, trust == TrustStateDto.OfficialVerified ? "test-key" : null, trust == TrustStateDto.OfficialVerified,
            new string('a', 64), 3, DateTimeOffset.UtcNow, engineState, null, available);

    public static RegistryPluginDto RegistryEntry(string id, string? installed, bool compatible) =>
        new(id, id, "Description", "Team", "Examples", "1.0.0", "0.3", "0.5.0", DateTimeOffset.UtcNow, 10, new string('c', 64), "test-key",
            null, null, null, null, compatible, compatible ? null : "Requires ModularRuntime 9.0.0 or later.", installed, false,
            SigningKeyTrusted: true, SigningKeyIsTest: false);
}
