using System.Windows.Input;

namespace PortPilot.App.Mvvm;

/// <summary>
/// Tells commands to re-evaluate <see cref="ICommand.CanExecute"/>. WPF hosts bridge this to
/// <c>CommandManager</c> (which requeries on input automatically); other hosts raise it themselves.
/// </summary>
public static class CommandRequery
{
    private static EventHandler? s_requested;

    /// <summary>Set by a host that has its own requery mechanism, such as WPF's <c>CommandManager</c>.</summary>
    public static (Action<EventHandler> Add, Action<EventHandler> Remove, Action Invalidate)? Bridge { get; set; }

    public static event EventHandler? RequerySuggested
    {
        add
        {
            if (Bridge is { } bridge) { bridge.Add(value!); } else { s_requested += value; }
        }
        remove
        {
            if (Bridge is { } bridge) { bridge.Remove(value!); } else { s_requested -= value; }
        }
    }

    public static void Invalidate()
    {
        if (Bridge is { } bridge)
        {
            bridge.Invalidate();
        }
        else
        {
            s_requested?.Invoke(null, EventArgs.Empty);
        }
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.RequerySuggested += value;
        remove => CommandRequery.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();
}

public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.RequerySuggested += value;
        remove => CommandRequery.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value)
        {
            execute(value);
        }
    }
}

/// <summary>
/// An async command that disables itself while running and routes failures to a handler instead of
/// crashing the dispatcher.
/// </summary>
public sealed class AsyncRelayCommand<T>(Func<T, Task> execute, Func<T, bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.RequerySuggested += value;
        remove => CommandRequery.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        !_running && parameter is T value && (canExecute?.Invoke(value) ?? true);

    public async void Execute(object? parameter)
    {
        if (parameter is not T value || _running)
        {
            return;
        }

        _running = true;
        CommandRequery.Invalidate();
        try
        {
            await execute(value);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            CommandRequery.Invalidate();
        }
    }
}
