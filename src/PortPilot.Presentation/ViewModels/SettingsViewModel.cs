using System.Reflection;
using System.Windows.Input;
using PortPilot.App.Mvvm;
using PortPilot.App.Services;
using PortPilot.Core.Settings;

namespace PortPilot.App.ViewModels;

/// <summary>
/// Preferences sheet. Every change applies immediately – there is no Save button.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly Func<AppSettings> _get;
    private readonly Action<Func<AppSettings, AppSettings>> _update;
    private readonly IStartupRegistration _startup;
    private readonly IShellService _shell;
    private readonly string _logDirectory;
    private readonly string _settingsPath;

    public SettingsViewModel(
        Func<AppSettings> get,
        Action<Func<AppSettings, AppSettings>> update,
        IStartupRegistration startup,
        IShellService shell,
        string logDirectory,
        string settingsPath)
    {
        _get = get;
        _update = update;
        _startup = startup;
        _shell = shell;
        _logDirectory = logDirectory;
        _settingsPath = settingsPath;
        OpenLogsCommand = new RelayCommand(() => _shell.RevealInExplorer(_logDirectory));
        OpenSettingsFileCommand = new RelayCommand(() => _shell.RevealInExplorer(_settingsPath));
        SetIntervalCommand = new RelayCommand<string>(value =>
        {
            if (int.TryParse(value, out var seconds))
            {
                RefreshIntervalSeconds = seconds;
            }
        });
    }

    public ICommand OpenLogsCommand { get; }

    public ICommand OpenSettingsFileCommand { get; }

    public ICommand SetIntervalCommand { get; }

    public static string VersionText => $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

    public ThemePreference Theme
    {
        get => _get().Theme;
        set => Apply(s => s with { Theme = value });
    }

    public int RefreshIntervalSeconds
    {
        get => _get().RefreshIntervalSeconds;
        set => Apply(s => s with { RefreshIntervalSeconds = value });
    }

    public bool CloseToTray
    {
        get => _get().CloseToTray;
        set => Apply(s => s with { CloseToTray = value });
    }

    public bool MinimizeToTray
    {
        get => _get().MinimizeToTray;
        set => Apply(s => s with { MinimizeToTray = value });
    }

    public bool NotifyOnNewDevPorts
    {
        get => _get().NotifyOnNewDevPorts;
        set => Apply(s => s with { NotifyOnNewDevPorts = value });
    }

    public bool StartMinimized
    {
        get => _get().StartMinimized;
        set => Apply(s => s with { StartMinimized = value });
    }

    public bool StartWithWindows
    {
        get => _startup.IsEnabled;
        set
        {
            _startup.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Raises change notifications after an external change (e.g. the title bar theme toggle).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    private void Apply(Func<AppSettings, AppSettings> change)
    {
        _update(change);
        OnPropertyChanged(string.Empty);
    }
}
