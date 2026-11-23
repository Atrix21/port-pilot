using System.Globalization;
using System.Net;
using System.Text;
using PortPilot.Core.Model;

namespace PortPilot.Cli;

internal static class Format
{
    public static string Protocol(PortBinding b) =>
        (b.Protocol == TransportProtocol.Tcp ? "tcp" : "udp") + (b.IsIPv6 ? "6" : "");

    public static string Address(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return "*";
        }

        if (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
        {
            return "localhost";
        }

        return address.ToString();
    }

    public static string Pid(PortEntry e) => e.ProcessId == 0 && !OperatingSystem.IsWindows() ? "?" : e.ProcessId.ToString(CultureInfo.InvariantCulture);

    public static string Process(ProcessDetails p) => p.ProcessId == 0 && !OperatingSystem.IsWindows() ? "(other user)" : p.DisplayName;

    public static string Age(DateTimeOffset? start)
    {
        if (start is not { } s)
        {
            return "";
        }

        var age = DateTimeOffset.Now - s;
        return age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds}s"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours}h"
            : $"{(int)age.TotalDays}d";
    }

    public static string Url(PortEntry e)
    {
        var scheme = e.Known?.PrefersHttps == true ? "https" : "http";
        var host = e.Binding.LocalAddress.Equals(IPAddress.Any) || e.Binding.LocalAddress.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(e.Binding.LocalAddress)
            ? "localhost"
            : e.Binding.IsIPv6 ? $"[{e.Binding.LocalAddress}]" : e.Binding.LocalAddress.ToString();
        return $"{scheme}://{host}:{e.Port}";
    }

    /// <summary>Appends <paramref name="text"/> clipped or padded to exactly <paramref name="width"/> cells, dropping control characters.</summary>
    public static void Cell(StringBuilder sb, string? text, int width, bool padRight = true)
    {
        if (width <= 0)
        {
            return;
        }

        text ??= "";
        var written = 0;
        foreach (var c in text)
        {
            if (written == width)
            {
                break;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (written == width - 1 && text.Length > width)
            {
                sb.Append('…');
                written++;
                break;
            }

            sb.Append(c);
            written++;
        }

        if (padRight)
        {
            sb.Append(' ', width - written);
        }
    }

    public static int Compare(PortEntry a, PortEntry b)
    {
        var c = a.Port.CompareTo(b.Port);
        if (c != 0)
        {
            return c;
        }

        c = a.Binding.Protocol.CompareTo(b.Binding.Protocol);
        if (c != 0)
        {
            return c;
        }

        c = a.Binding.IsIPv6.CompareTo(b.Binding.IsIPv6);
        return c != 0 ? c : a.ProcessId.CompareTo(b.ProcessId);
    }
}
