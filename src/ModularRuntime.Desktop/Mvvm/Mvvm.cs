using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ModularRuntime.Desktop.Mvvm;

/// <summary>Minimal INotifyPropertyChanged base.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Synchronous command.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute(parameter);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Asynchronous command that cannot run twice concurrently (duplicate clicks are ignored), reports
/// <see cref="IsRunning"/>, and routes failures to an error handler instead of crashing the UI.
/// </summary>
/// <remarks>
/// The command owns the task of its current execution (<see cref="Execution"/>); nothing is
/// fire-and-forget without supervision. Must be executed on the UI thread.
/// </remarks>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), onError)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The current (or last) execution; completes when it has finished, never faulted.</summary>
    public Task Execution { get; private set; } = Task.CompletedTask;

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        Execution = RunAsync(parameter);
    }

    public Task ExecuteAsync(object? parameter = null)
    {
        Execute(parameter);
        return Execution;
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync(object? parameter)
    {
        IsRunning = true;
        try
        {
            await _execute(parameter);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _onError?.Invoke(exception);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
