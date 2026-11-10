using ModularRuntime.Desktop.Services;
using ModularRuntime.Desktop.ViewModels;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Tests;

/// <summary>
/// v0.6 catalog metadata in the desktop app: version status, revocations, provenance, version
/// history. The app only presents what the daemon verified; it never checks signatures itself.
/// </summary>
public sealed class PublicationMetadataTests : IAsyncDisposable
{
    private readonly FakeConnection _connection = new();
    private readonly FakeClient _client = new();
    private readonly PackageOperationsTracker _operations;
    private readonly StoreViewModel _store;
    private readonly PluginsViewModel _plugins;

    public PublicationMetadataTests()
    {
        _operations = new PackageOperationsTracker(_connection, new InlineDispatcher());
        _store = new StoreViewModel(_connection, new InlineDispatcher(), new FakeDialogs(), _operations) { IsActive = true };
        _plugins = new PluginsViewModel(_connection, new InlineDispatcher(), new FakeDialogs(), _operations) { IsActive = true };
        var history = new List<RegistryVersionDto>
        {
            new("1.2.0", "published", "0.3", "0.6.0", DateTimeOffset.UtcNow, true, null),
            new("1.1.0", "withdrawn", "0.2", "0.4.0", DateTimeOffset.UtcNow.AddDays(-30), false, "Built for plugin API 0.2."),
        };
        _client.Registry = new RegistryListResponse(FakeClient.EmptyRegistryStatus with { Loaded = true, Revision = 7, SigningKeyId = "registry-key" },
        [
            PackageFixtures.RegistryEntry("com.published", installed: null, compatible: true) with
            {
                License = "MIT", SourceRepository = "https://example.com/p", SourceCommit = "0123456789abcdef", BuildId = "build-42", ApprovalSha256 = new string('e', 64), Versions = history,
            },
            PackageFixtures.RegistryEntry("com.revoked", installed: null, compatible: true) with
            {
                Status = "revoked", StatusReason = "Sends data to an undisclosed server.", AdvisoryUrl = "https://example.com/advisories/7",
            },
            PackageFixtures.RegistryEntry("com.withdrawn", installed: null, compatible: true) with { Status = "withdrawn", StatusReason = "Published by mistake." },
            PackageFixtures.RegistryEntry("com.badkey", installed: null, compatible: true) with { SigningKeyRevoked = true },
            PackageFixtures.RegistryEntry("com.revoked-update", installed: "1.0.0", compatible: true) with { Status = "revoked", StatusReason = "Malicious.", UpdateAvailable = false },
        ]);
        _client.Plugins = [FakeClient.Plugin("com.installed-revoked", "Running"), FakeClient.Plugin("com.installed-withdrawn", "Validated")];
        _client.Packages =
        [
            PackageFixtures.Package("com.installed-revoked", TrustStateDto.Revoked, "registry", "Running") with
            {
                Revocation = new RevocationDto("package", "Serious vulnerability.", "https://example.com/advisories/8", DateTimeOffset.UtcNow, null),
            },
            PackageFixtures.Package("com.installed-withdrawn", TrustStateDto.OfficialVerified, "registry", "Validated") with
            {
                CatalogStatus = "withdrawn", CatalogStatusReason = "End of support.",
            },
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
        await _store.RefreshCommand.Execution;
        await _plugins.RefreshCommand.Execution;
    }

    private StoreItemViewModel Card(string id) => _store.Items.Single(i => i.Id == id);

    [Fact]
    public async Task RevokedWithdrawnAndBadKeyVersions_CannotBeInstalledFromTheStore()
    {
        await ConnectAsync();

        Assert.True(Card("com.published").ActionCommand.CanExecute(null));
        foreach (var id in new[] { "com.revoked", "com.withdrawn", "com.badkey", "com.revoked-update" })
        {
            Assert.False(Card(id).ActionCommand.CanExecute(null), id);
            Assert.True(Card(id).IsTrustWarning, id);
        }

        Assert.Equal("Revoked", Card("com.revoked").ActionText);
        Assert.Equal("Revoked for security", Card("com.revoked").StatusLabel);
        Assert.Equal("Revoked — do not use", Card("com.revoked").InstallState);
        Assert.Contains("Sends data to an undisclosed server.", Card("com.revoked").StatusDetails, StringComparison.Ordinal);
        Assert.Contains("https://example.com/advisories/7", Card("com.revoked").StatusDetails, StringComparison.Ordinal);

        Assert.Equal("Unavailable", Card("com.withdrawn").ActionText);
        Assert.Equal("Withdrawn from the Store", Card("com.withdrawn").StatusLabel);

        Assert.Equal("Signing key revoked", Card("com.badkey").TrustLabel);
        Assert.Equal("Revoked", Card("com.badkey").ActionText);
    }

    [Fact]
    public async Task Details_ShowLicenseProvenanceAndVersionHistory()
    {
        await ConnectAsync();
        var card = Card("com.published");

        Assert.Equal("Published", card.StatusLabel);
        Assert.Null(card.StatusDetails);
        Assert.Equal("Build build-42 · source 0123456789abcdef", card.Provenance);
        Assert.Equal("MIT", card.Plugin.License);
        Assert.Equal(2, card.VersionHistory.Count);
        Assert.StartsWith("1.2.0 — published", card.VersionHistory[0], StringComparison.Ordinal);
        Assert.Contains("1.1.0 — withdrawn (incompatible) · API 0.2", card.VersionHistory[1], StringComparison.Ordinal);
        Assert.Contains("Built for plugin API 0.2.", card.VersionHistory[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledRevokedPlugin_ShowsAWarningWithTheReason_AndWithdrawnOnesANotice()
    {
        await ConnectAsync();
        var revoked = _plugins.Items.Single(i => i.Id == "com.installed-revoked");
        var withdrawn = _plugins.Items.Single(i => i.Id == "com.installed-withdrawn");

        Assert.Equal("Revoked", revoked.TrustLabel);
        Assert.True(revoked.IsRevoked);
        Assert.Contains("Serious vulnerability.", revoked.RevocationReason, StringComparison.Ordinal);
        Assert.Contains("will not be loaded again", revoked.TrustExplanation, StringComparison.Ordinal);
        Assert.Contains("not deleted automatically", revoked.TrustExplanation, StringComparison.Ordinal);

        Assert.False(withdrawn.IsRevoked);
        Assert.Equal("Official (test key)", withdrawn.TrustLabel);
        Assert.Contains("withdrawn from the Store: End of support.", withdrawn.TrustExplanation, StringComparison.Ordinal);
    }
}
