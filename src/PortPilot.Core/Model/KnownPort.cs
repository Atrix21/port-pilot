namespace PortPilot.Core.Model;

public enum KnownPortKind
{
    /// <summary>Web dev servers, bundlers and app frameworks.</summary>
    DevServer,

    /// <summary>Databases, caches and message brokers.</summary>
    DataStore,

    /// <summary>Debuggers, tooling and infrastructure developers commonly run.</summary>
    Tooling,
}

/// <summary>
/// A port number with a well-established meaning in local development.
/// </summary>
public sealed record KnownPort(int Port, string Name, KnownPortKind Kind, bool PrefersHttps = false)
{
    public bool IsLikelyHttp => Kind == KnownPortKind.DevServer || Name.Contains("HTTP", StringComparison.OrdinalIgnoreCase);
}
