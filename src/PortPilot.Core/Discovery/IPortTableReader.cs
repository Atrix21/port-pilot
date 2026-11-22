using PortPilot.Core.Model;

namespace PortPilot.Core.Discovery;

/// <summary>
/// Reads the raw TCP and UDP endpoint tables from the operating system.
/// </summary>
public interface IPortTableReader
{
    /// <param name="includeAllTcpStates">
    /// When false only TCP listeners are returned; when true every TCP connection is included.
    /// UDP endpoints are always returned since UDP has no connection state.
    /// </param>
    IReadOnlyList<PortBinding> Read(bool includeAllTcpStates);
}
