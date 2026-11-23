using System.Net.Sockets;
using System.Text;

namespace PortPilot.Desktop.Services;

/// <summary>
/// One PortPilot per session, coordinated over a Unix socket in <c>$XDG_RUNTIME_DIR</c>. A second launch
/// asks the running copy to show its window and exits; an elevated launch asks it to quit and takes over.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string Show = "show";
    public const string Quit = "quit";

    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Socket listener) => _listener = listener;

    public static string SocketPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath(),
        "portpilot.sock");

    public event Action<string>? MessageReceived;

    /// <summary>Sends <paramref name="message"/> to a running instance. Returns false when none is running.</summary>
    public static bool TrySend(string message)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.ASCII.GetBytes(message));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Becomes the primary instance, or returns null if another one answers.</summary>
    public static SingleInstance? TryAcquire()
    {
        if (TrySend(Show))
        {
            return null;
        }

        // Nothing answered, so any socket file left behind is stale.
        File.Delete(SocketPath);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(4);
        var instance = new SingleInstance(listener);
        _ = instance.AcceptLoopAsync();
        return instance;
    }

    /// <summary>For an elevated restart: ask the running copy to quit, then take its place.</summary>
    public static SingleInstance? Replace()
    {
        if (TrySend(Quit))
        {
            for (var i = 0; i < 40 && File.Exists(SocketPath) && TrySendProbe(); i++)
            {
                Thread.Sleep(50);
            }
        }

        return TryAcquire();
    }

    private static bool TrySendProbe()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        var buffer = new byte[16];
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token).ConfigureAwait(false);
                if (read > 0)
                {
                    MessageReceived?.Invoke(Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
        }

        _stop.Dispose();
    }
}
