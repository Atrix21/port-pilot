using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Discovery;
using PortPilot.Core.Processes;

namespace PortPilot.Core;

/// <summary>
/// Creates the discovery and process backends for the operating system PortPilot is running on.
/// </summary>
public static class PlatformServices
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static IPortTableReader CreatePortTableReader(ILoggerFactory? logs = null)
    {
        logs ??= NullLoggerFactory.Instance;
        if (OperatingSystem.IsLinux())
        {
            return new ProcNetPortTableReader(logger: logs.CreateLogger<ProcNetPortTableReader>());
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacPortTableReader(logs.CreateLogger<MacPortTableReader>());
        }

        EnsureWindows();
        return new IpHelperPortTableReader(logs.CreateLogger<IpHelperPortTableReader>());
    }

    public static IProcessInfoProvider CreateProcessInfoProvider(ILoggerFactory? logs = null)
    {
        logs ??= NullLoggerFactory.Instance;
        if (OperatingSystem.IsLinux())
        {
            return new LinuxProcessInfoProvider(logs.CreateLogger<LinuxProcessInfoProvider>(), AgentLineage.DefaultPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacProcessInfoProvider(logs.CreateLogger<MacProcessInfoProvider>(), AgentLineage.DefaultPath);
        }

        EnsureWindows();
        return new ProcessInfoProvider(logger: logs.CreateLogger<ProcessInfoProvider>(), lineagePath: AgentLineage.DefaultPath);
    }

    public static IProcessTerminator CreateProcessTerminator(ILoggerFactory? logs = null)
    {
        logs ??= NullLoggerFactory.Instance;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new LinuxProcessTerminator(logs.CreateLogger<LinuxProcessTerminator>());
        }

        EnsureWindows();
        return new ProcessTerminator(logs.CreateLogger<ProcessTerminator>());
    }

    public static IPortScanner CreateScanner(ILoggerFactory? logs = null) =>
        new PortScanner(CreatePortTableReader(logs), CreateProcessInfoProvider(logs), logger: (logs ?? NullLoggerFactory.Instance).CreateLogger<PortScanner>());

    /// <summary>Fresh, uncached details for one process.</summary>
    public static Model.ProcessDetails Inspect(int processId) =>
        OperatingSystem.IsLinux() ? LinuxProcessInfoProvider.Inspect(processId)
        : OperatingSystem.IsMacOS() ? MacProcessInfoProvider.Inspect(processId)
        : ProcessInfoProvider.Inspect(processId);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("PortPilot supports Windows, Linux and macOS.");
        }
    }
}
