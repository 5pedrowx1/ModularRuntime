using System.Collections.ObjectModel;
using ModularRuntime.Desktop.Services;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.ViewModels;

/// <summary>Read-only searchable list page over one daemon snapshot.</summary>
public abstract class SearchablePageViewModel<T> : PageViewModel
{
    private readonly List<T> _all = [];
    private string _searchText = string.Empty;

    protected SearchablePageViewModel(string title, string glyph, IRuntimeConnection connection, IUiDispatcher dispatcher)
        : base(title, glyph, connection, dispatcher)
    {
    }

    public ObservableCollection<T> Items { get; } = [];

    public int TotalCount => _all.Count;

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

    protected void SetItems(IEnumerable<T> items)
    {
        _all.Clear();
        _all.AddRange(items);
        OnPropertyChanged(nameof(TotalCount));
        ApplyFilter();
    }

    protected override void Clear() => SetItems([]);

    protected abstract IEnumerable<string?> SearchFields(T item);

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in _all.Where(i => SearchText.Length == 0 ||
                     SearchFields(i).Any(f => f?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) == true)))
        {
            Items.Add(item);
        }
    }
}

public sealed class EventsViewModel(IRuntimeConnection connection, IUiDispatcher dispatcher)
    : SearchablePageViewModel<SubscriptionDto>("Events", "", connection, dispatcher)
{
    public ObservableCollection<TopicStatsDto> Topics { get; } = [];

    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetSubscriptionsAsync(cancellationToken);
        SetItems(response.Subscriptions);
        Topics.Clear();
        foreach (var topic in response.Topics)
        {
            Topics.Add(topic);
        }

        return response.Subscriptions.Count > 0;
    }

    protected override void Clear()
    {
        base.Clear();
        Topics.Clear();
    }

    protected override IEnumerable<string?> SearchFields(SubscriptionDto item) => [item.Topic, item.Subscriber.Display];
}

public sealed class ServicesViewModel(IRuntimeConnection connection, IUiDispatcher dispatcher)
    : SearchablePageViewModel<ServiceDto>("Services", "", connection, dispatcher)
{
    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetServicesAsync(cancellationToken);
        SetItems(response.Services);
        return response.Services.Count > 0;
    }

    protected override IEnumerable<string?> SearchFields(ServiceDto item) =>
        [item.ServiceId, item.Name, item.Owner.Display, .. item.Operations];
}

public sealed class ExtensionsViewModel(IRuntimeConnection connection, IUiDispatcher dispatcher)
    : SearchablePageViewModel<ExtensionPointDto>("Extensions", "", connection, dispatcher)
{
    protected override async Task<bool> LoadAsync(IRuntimeClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetExtensionsAsync(cancellationToken);
        SetItems(response.ExtensionPoints);
        return response.ExtensionPoints.Count > 0;
    }

    protected override IEnumerable<string?> SearchFields(ExtensionPointDto item) =>
        [item.ExtensionPointId, item.Name, item.Owner.Display, .. item.Contributions.Select(c => c.ContributionId), .. item.Contributions.Select(c => c.Owner.Display)];
}
