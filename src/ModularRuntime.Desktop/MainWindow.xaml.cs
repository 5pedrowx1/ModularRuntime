using System.ComponentModel;
using System.Windows;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Desktop.ViewModels;

namespace ModularRuntime.Desktop;

/// <summary>
/// Window chrome glue only (caption buttons, maximize insets, orderly close). All behavior lives in
/// <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private bool _closeCompleted;

    public MainWindow()
    {
        InitializeComponent();

        // Native rounded corners, border, and frame mode, set on the HWND before the first frame.
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        StateChanged += (_, _) => UpdateMaximizeState();
        Closing += OnClosing;
    }

    private void UpdateMaximizeState()
    {
        // With a custom chrome, a maximized window extends past the screen by the resize border.
        var border = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = WindowState == WindowState.Maximized
            ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
            : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>
    /// Closing the window disconnects this client and exits the desktop app. It never sends a
    /// shutdown request: the daemon and every plugin keep running.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeCompleted || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        try
        {
            await viewModel.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            // The pipe closes with the process anyway; never block exit on the connection.
        }

        _closeCompleted = true;
        Close();
    }
}
