namespace PortPilot.App.Services;

/// <summary>Wording that differs between Windows and Linux, so the shared view models read naturally on both.</summary>
public static class PlatformText
{
    public static string Os { get; } = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>"administrator" or "root".</summary>
    public static string Admin { get; } = OperatingSystem.IsWindows() ? "administrator" : "root";

    public static string AdminTitle { get; } = OperatingSystem.IsWindows() ? "Administrator" : "Root";

    public static string AdminPrivileges { get; } = OperatingSystem.IsWindows() ? "administrator privileges" : "root privileges";

    public static string FileManager { get; } = OperatingSystem.IsWindows() ? "File Explorer" : OperatingSystem.IsMacOS() ? "Finder" : "the file manager";
}
