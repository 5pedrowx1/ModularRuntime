using System.Windows;
using System.Windows.Automation;
using ModularRuntime.Desktop.Services;

namespace ModularRuntime.Desktop.Views;

/// <summary>
/// Themed modal dialog (confirmation or notice). Replaces the system MessageBox, which cannot follow
/// the app's Light/Dark palette. Colors come from the active palette through DynamicResource, so an
/// open dialog also follows a theme change.
/// </summary>
public partial class MessageDialog : Window
{
    private const string WarningGlyph = "";
    private const string InfoGlyph = "";

    private MessageDialog(string title, string message, string? confirmText, bool destructive)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        if (confirmText is null)
        {
            // Notice: a single acknowledgement button.
            IconGlyph.Text = InfoGlyph;
            IconGlyph.SetResourceReference(ForegroundProperty, "InfoBrush");
            CancelButton.Content = "OK";
            CancelButton.IsDefault = true;
            ConfirmButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconGlyph.Text = WarningGlyph;
            ConfirmButton.Content = confirmText;
            AutomationProperties.SetName(ConfirmButton, confirmText);
            ConfirmButton.SetResourceReference(StyleProperty, destructive ? "DangerButton" : "AccentButton");
        }

        // Focus the safe choice: Enter on a destructive confirmation must not confirm by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>Asks for confirmation. Returns <see langword="true"/> only when the user confirms.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText, bool destructive = true)
    {
        var dialog = new MessageDialog(title, message, confirmText, destructive);
        SetOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    /// <summary>Shows a notice with a single OK button.</summary>
    public static void Show(Window? owner, string title, string message)
    {
        var dialog = new MessageDialog(title, message, confirmText: null, destructive: false);
        SetOwner(dialog, owner);
        dialog.ShowDialog();
    }

    private static void SetOwner(Window dialog, Window? owner)
    {
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
