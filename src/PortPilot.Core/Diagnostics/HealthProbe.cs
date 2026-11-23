using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PortPilot.Core.Model;

namespace PortPilot.Core.Diagnostics;

public enum HealthState
{
    /// <summary>Answered HTTP with a 2xx or 3xx status.</summary>
    Healthy,

    /// <summary>Answered HTTP, but with a 4xx or 5xx status.</summary>
    Erroring,

    /// <summary>Accepts TCP connections but does not speak HTTP (databases, brokers, …).</summary>
    Accepting,

    /// <summary>Did not accept a connection in time.</summary>
    Unreachable,

    /// <summary>Not probed (UDP, or not listening).</summary>
    NotApplicable,
}

public sealed record HealthResult(HealthState State, int? StatusCode, TimeSpan Latency, string Summary);

/// <summary>
/// Checks whether a listening port is actually answering: a TCP connect, then for likely-HTTP ports a
/// single GET. Local development servers only: TLS certificates are not validated, since dev servers
/// almost always use self-signed ones.
/// </summary>
public static class HealthProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1.5);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = ConnectTimeout,
        AllowAutoRedirect = false,
        UseProxy = false,
#pragma warning disable CA5359 // Probing the user's own dev servers, which use self-signed certificates; nothing is sent.
        SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
#pragma warning restore CA5359
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    public static Task<HealthResult> CheckAsync(PortEntry entry, CancellationToken token = default) =>
        CheckAsync(entry.Binding, entry.Known, token);

    public static async Task<HealthResult> CheckAsync(PortBinding binding, KnownPort? known, CancellationToken token = default)
    {
        if (binding.Protocol != TransportProtocol.Tcp || binding.State != ConnectionState.Listening)
        {
            return new HealthResult(HealthState.NotApplicable, null, TimeSpan.Zero, "Not a TCP listener");
        }

        var address = binding.LocalAddress.Equals(IPAddress.Any) ? IPAddress.Loopback
            : binding.LocalAddress.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
            : binding.LocalAddress;
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new IPEndPoint(address, binding.LocalPort), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return new HealthResult(HealthState.Unreachable, null, Stopwatch.GetElapsedTime(started), "Not accepting connections");
        }

        var connected = Stopwatch.GetElapsedTime(started);
        if (known?.Kind == KnownPortKind.DataStore)
        {
            return new HealthResult(HealthState.Accepting, null, connected, $"Accepting connections · {Ms(connected)}");
        }

        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var schemes = known?.PrefersHttps == true ? new[] { "https", "http" } : ["http", "https"];
        foreach (var scheme in schemes)
        {
            started = Stopwatch.GetTimestamp();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{scheme}://{host}:{binding.LocalPort}/");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(started);
                var code = (int)response.StatusCode;
                return new HealthResult(
                    code < 400 ? HealthState.Healthy : HealthState.Erroring,
                    code,
                    elapsed,
                    $"{code} {response.ReasonPhrase} · {Ms(elapsed)}");
            }
            catch (HttpRequestException)
            {
                // Wrong scheme or not HTTP at all; try the other one.
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                return new HealthResult(HealthState.Accepting, null, connected, "Accepts connections but didn't answer HTTP in time");
            }
        }

        return new HealthResult(HealthState.Accepting, null, connected, $"Accepting connections (not HTTP) · {Ms(connected)}");
    }

    private static string Ms(TimeSpan t) => t.TotalMilliseconds < 1 ? "<1 ms" : $"{t.TotalMilliseconds:0} ms";
}
