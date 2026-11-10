using System.Windows;
using System.Windows.Controls;
using ModularRuntime.Desktop.ViewModels;

namespace ModularRuntime.Desktop.Views;

public partial class StoreView : UserControl
{
    /// <summary>Minimum card width including its right margin.</summary>
    private const double CardSlotWidth = 300;

    public StoreView() => InitializeComponent();

    /// <summary>Layout only: the number of cards per row follows the available width.</summary>
    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is StoreViewModel store && e.NewSize.Width > 0)
        {
            store.ColumnCount = Math.Max(1, (int)((e.NewSize.Width - SystemParameters.VerticalScrollBarWidth) / CardSlotWidth));
        }
    }
}
