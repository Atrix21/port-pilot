using System.Windows.Input;
using PortPilot.App.Mvvm;

namespace PortPilot.App.ViewModels;

public enum ToastKind
{
    Success,
    Info,
    Warning,
    Error,
}

public sealed class ToastViewModel(string message, ToastKind kind, string? detail = null) : ObservableObject
{
    private bool _isLeaving;

    public string Message { get; } = message;

    public string? Detail { get; } = detail;

    public ToastKind Kind { get; } = kind;

    public bool IsLeaving
    {
        get => _isLeaving;
        set => Set(ref _isLeaving, value);
    }
}

/// <summary>
/// An in-window confirmation. The view renders it as a sheet over a scrim; the caller awaits <see cref="Result"/>.
/// </summary>
public sealed class ConfirmDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmDialogViewModel()
    {
        ConfirmCommand = new RelayCommand(() => _completion.TrySetResult(true));
        CancelCommand = new RelayCommand(() => _completion.TrySetResult(false));
    }

    public required string Title { get; init; }

    public required string Message { get; init; }

    /// <summary>Optional technical line shown in monospace, e.g. the executable and PID.</summary>
    public string? Detail { get; init; }

    public string? Note { get; init; }

    public string ConfirmText { get; init; } = "Confirm";

    public string CancelText { get; init; } = "Cancel";

    public bool IsDestructive { get; init; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelCommand { get; }

    public Task<bool> Result => _completion.Task;
}
