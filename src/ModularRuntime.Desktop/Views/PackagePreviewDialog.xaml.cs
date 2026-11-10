using System.Globalization;
using System.Windows;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Desktop.ViewModels;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Views;

/// <summary>
/// Shows the daemon's verification of an uploaded package. For a package without an official
/// signature, Install stays disabled until the user explicitly acknowledges the unverified origin.
/// Never shows a verified state the daemon did not report.
/// </summary>
public partial class PackagePreviewDialog : Window
{
    private readonly PackagePreviewDto _preview;

    private PackagePreviewDialog(PackagePreviewDto preview)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        _preview = preview;
        Populate();
        Loaded += (_, _) => CancelButton.Focus();
    }

    public static PackageInstallDecision Show(Window? owner, PackagePreviewDto preview)
    {
        var dialog = new PackagePreviewDialog(preview);
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true
            ? new PackageInstallDecision(true, dialog.AcceptCheck.IsChecked == true, dialog.StopCheck.IsChecked == true)
            : PackageInstallDecision.Cancel;
    }

    private void Populate()
    {
        if (!_preview.Valid)
        {
            RejectedPanel.Visibility = Visibility.Visible;
            DetailsPanel.Visibility = Visibility.Collapsed;
            RejectedText.Text = $"[{_preview.ErrorCode}] {_preview.ErrorMessage}";
            InstallButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            return;
        }

        NameText.Text = _preview.Name;
        IdText.Text = _preview.PluginId;
        VersionText.Text = _preview.ReplacesExisting ? $"{_preview.Version} (replaces installed {_preview.InstalledVersion})" : _preview.Version;
        AuthorText.Text = _preview.Author ?? "(not declared)";
        var official = _preview.Trust == TrustStateDto.OfficialVerified;
        OriginText.Text = TrustText.Label(_preview.Trust, _preview.SignedWithTestKey);
        OriginText.SetResourceReference(ForegroundProperty, official ? "SuccessBrush" : "WarningBrush");
        SignatureText.Text = official
            ? $"Valid official signature (key {_preview.SigningKeyId}){(_preview.SignedWithTestKey ? " — TEST KEY, development only" : string.Empty)}"
            : "None";
        IntegrityText.Text = official
            ? $"{_preview.FileCount} files; each SHA-256 matches the signed manifest"
            : $"{_preview.FileCount} files inventoried (self-declared, not authenticated)";
        CompatibilityText.Text = _preview.Compatible ? $"Compatible (plugin API {_preview.ApiVersion})" : "Incompatible";
        WarningPanel.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
        if (_preview.ExistingIsRunning)
        {
            StopCheck.Visibility = Visibility.Visible;
            StopText.Text = string.Format(CultureInfo.CurrentCulture, "Stop the running version {0} to replace it", _preview.InstalledVersion);
        }

        UpdateInstallButton();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => UpdateInstallButton();

    private void UpdateInstallButton()
    {
        var official = _preview.Trust == TrustStateDto.OfficialVerified;
        InstallButton.IsEnabled = _preview.Valid && _preview.Compatible &&
            (official || AcceptCheck.IsChecked == true) &&
            (!_preview.ExistingIsRunning || StopCheck.IsChecked == true);
    }

    private void OnInstall(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
