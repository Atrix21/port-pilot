using System.Windows;
using Microsoft.Win32;
using PortPilot.Core.Settings;

namespace PortPilot.App.Services;

/// <summary>A surface that can cross-fade from the old theme to the new one.</summary>
public interface IThemeTransitionHost
{
    void RunThemeTransition(Action swap);
}

/// <summary>
/// Swaps the colour dictionary at runtime. All brushes are referenced via DynamicResource, so the
/// swap restyles the whole UI in one pass.
/// </summary>
public sealed class ThemeService(ResourceDictionary appResources) : IThemeService
{
    private static readonly Uri DarkUri = new("pack://application:,,,/PortPilot;component/Themes/Colors.Dark.xaml");
    private static readonly Uri LightUri = new("pack://application:,,,/PortPilot;component/Themes/Colors.Light.xaml");

    // App.xaml merges a colour dictionary at index 0 so the designer has something to render.
    private ResourceDictionary _current = appResources.MergedDictionaries[0];
    private bool _applied;
    private ThemePreference _preference = ThemePreference.System;

    public event EventHandler? ThemeChanged;

    public IThemeTransitionHost? TransitionHost { get; set; }

    public bool IsDark { get; private set; }

    public void Apply(ThemePreference preference, bool animate)
    {
        _preference = preference;
        var dark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemPrefersDark(),
        };

        if (_applied && dark == IsDark)
        {
            return;
        }

        void Swap()
        {
            var next = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
            var index = Math.Max(0, appResources.MergedDictionaries.IndexOf(_current));
            appResources.MergedDictionaries[index] = next;
            _applied = true;
            _current = next;
            IsDark = dark;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        if (animate && _applied && TransitionHost is { } host)
        {
            host.RunThemeTransition(Swap);
        }
        else
        {
            Swap();
        }
    }

    /// <summary>Re-evaluates the system theme; called when Windows broadcasts a settings change.</summary>
    public void OnSystemThemeChanged()
    {
        if (_preference == ThemePreference.System)
        {
            Apply(ThemePreference.System, animate: true);
        }
    }

    public static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
