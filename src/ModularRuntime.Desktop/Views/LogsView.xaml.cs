using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using ModularRuntime.Desktop.ViewModels;

namespace ModularRuntime.Desktop.Views;

/// <summary>View-only behavior: keep the newest entry visible when auto-scroll is on.</summary>
public partial class LogsView : UserControl
{
    private LogsViewModel? _viewModel;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _viewModel = DataContext as LogsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Entries.CollectionChanged += OnEntriesChanged;
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is { AutoScroll: true } && e.Action == NotifyCollectionChangedAction.Add && EntriesList.Items.Count > 0)
        {
            EntriesList.ScrollIntoView(EntriesList.Items[^1]);
        }
    }
}
