using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PortPilot.App.Services;

/// <summary>
/// Registers PortPilot under HKCU\…\Run so it starts with Windows for the current user.
/// </summary>
public sealed class StartupRegistration(ILogger<StartupRegistration> log) : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PortPilot";

    public static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            log.LogInformation("Start with Windows set to {Enabled}", enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            log.LogWarning(ex, "Could not update startup registration");
        }
    }

    /// <summary>Keeps the registered path current if the app was moved or updated.</summary>
    public void RepairIfEnabled()
    {
        if (IsEnabled)
        {
            SetEnabled(true);
        }
    }
}

/// <summary>
/// Relaunches PortPilot with administrator rights. The new instance waits for this one to exit before
/// claiming the single-instance lock.
/// </summary>
public sealed class ElevationService(ILogger<ElevationService> log, Action afterRestartStarted) : IElevationService
{
    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public bool TryRestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated-restart")
            {
                UseShellExecute = true,
                Verb = "runas",
            })?.Dispose();
            log.LogInformation("Restarting elevated");
            afterRestartStarted();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt.
            log.LogInformation("Elevation cancelled by user");
            return false;
        }
        catch (Win32Exception ex)
        {
            log.LogWarning(ex, "Elevated restart failed");
            return false;
        }
    }
}
