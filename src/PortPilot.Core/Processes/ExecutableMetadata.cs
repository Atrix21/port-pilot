using System.Diagnostics;

namespace PortPilot.Core.Processes;

/// <summary>
/// Version resource information for an executable, used to describe unfamiliar processes.
/// </summary>
public sealed record ExecutableMetadata(string? Description, string? Company, string? Version)
{
    public static ExecutableMetadata Empty { get; } = new(null, null, null);

    public static ExecutableMetadata Read(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Empty;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new ExecutableMetadata(Clean(info.FileDescription), Clean(info.CompanyName), Clean(info.ProductVersion));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
