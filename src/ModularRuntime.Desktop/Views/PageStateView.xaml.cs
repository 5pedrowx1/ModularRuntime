using System.Windows;
using System.Windows.Controls;

namespace ModularRuntime.Desktop.Views;

/// <summary>Loading / Empty / Error / Disconnected presentation shared by every page.</summary>
public partial class PageStateView : UserControl
{
    public static readonly DependencyProperty EmptyTextProperty =
        DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(PageStateView), new PropertyMetadata("Nothing to show."));

    public PageStateView()
    {
        InitializeComponent();
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }
}
