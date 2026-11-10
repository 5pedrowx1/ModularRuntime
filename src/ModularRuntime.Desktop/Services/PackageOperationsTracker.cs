using System.Collections.ObjectModel;
using ModularRuntime.Desktop.Mvvm;
using ModularRuntime.Desktop.ViewModels;
using ModularRuntime.Ipc;
using ModularRuntime.Ipc.Contracts;

namespace ModularRuntime.Desktop.Services;

/// <summary>
/// The package operations running in the daemon, shared by the Store and Plugins pages so both show
/// the same progress and neither re-implements operation handling. Operations are owned by the
/// daemon; this only mirrors their notifications (and lists them again after a reconnection).
/// </summary>
public sealed class PackageOperationsTracker : ObservableObject
{
    private readonly IRuntimeConnection _connection;

    public PackageOperationsTracker(IRuntimeConnection connection, IUiDispatcher dispatcher)
    {
        _connection = connection;
        connection.PackageOperationChanged += (_, operation) => dispatcher.Post(() => Apply(operation));
        connection.StateChanged += (_, _) => dispatcher.Post(OnConnectionChanged);
    }

    /// <summary>Operations in progress, most recent first. Finished operations leave the list.</summary>
    public ObservableCollection<OperationItemViewModel> Operations { get; } = [];

    /// <summary>Raised once per operation when the daemon reports its final stage.</summary>
    public event EventHandler<PackageOperationDto>? OperationFinished;

    /// <summary>Raised when any operation starts, progresses, or finishes (to refresh command states).</summary>
    public event EventHandler? Changed;

    public bool HasActiveOperation(string pluginId) => Operations.Any(o => !o.IsFinished && o.PluginId == pluginId);

    /// <summary>Shows an operation the daemon has accepted; progress then arrives as notifications.</summary>
    public void Track(PackageOperationDto operation) => Apply(operation);

    private void Apply(PackageOperationDto operation)
    {
        var item = Operations.FirstOrDefault(o => o.OperationId == operation.OperationId);
        if (item is null)
        {
            if (operation.IsFinished && operation.CompletedAt < DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                return;
            }

            item = new OperationItemViewModel(operation);
            Operations.Insert(0, item);
        }
        else
        {
            item.Update(operation);
        }

        if (operation.IsFinished)
        {
            Operations.Remove(item);
            OperationFinished?.Invoke(this, operation);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnConnectionChanged()
    {
        if (_connection.State == ConnectionState.Connected)
        {
            _ = RestoreAsync();
        }
        else if (_connection.State is ConnectionState.Disconnected or ConnectionState.Error)
        {
            Operations.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>After a reconnection, show operations that kept running in the daemon meanwhile.</summary>
    private async Task RestoreAsync()
    {
        if (_connection.Client is not { } client)
        {
            return;
        }

        try
        {
            var response = await client.ListOperationsAsync();
            foreach (var operation in response.Operations.Where(o => !o.IsFinished))
            {
                Apply(operation);
            }
        }
        catch (IpcClientException)
        {
            // Connection problems are reported by the pages.
        }
    }
}
