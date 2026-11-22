using PortPilot.Core.Settings;

namespace PortPilot.App.Services;

/// <summary>Marshals work onto the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> once after <paramref name="delay"/> on the UI thread.</summary>
    void Schedule(TimeSpan delay, Action action);
}

/// <summary>Operating-system shell actions.</summary>
public interface IShellService
{
    bool OpenUrl(string url);

    bool RevealInExplorer(string path);

    bool CopyText(string text);
}

public interface IThemeService
{
    bool IsDark { get; }

    void Apply(ThemePreference preference, bool animate);

    event EventHandler? ThemeChanged;
}

public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>Starts an elevated copy of PortPilot. Returns false if the user declined the UAC prompt.</summary>
    bool TryRestartElevated();
}

public interface INotificationService
{
    void Notify(string title, string message);
}

/// <summary>Window-level application behaviour the view models can request.</summary>
public interface IAppShell
{
    bool IsMainWindowVisible { get; }

    void ShowMainWindow();

    void Quit();
}
