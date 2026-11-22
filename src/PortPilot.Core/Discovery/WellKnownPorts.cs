using System.Collections.Frozen;
using PortPilot.Core.Model;

namespace PortPilot.Core.Discovery;

/// <summary>
/// Catalogue of ports commonly used during local development.
/// </summary>
public static class WellKnownPorts
{
    private static readonly FrozenDictionary<int, KnownPort> Ports = new KnownPort[]
    {
        new(80, "HTTP", KnownPortKind.DevServer),
        new(443, "HTTPS", KnownPortKind.DevServer, PrefersHttps: true),
        new(1313, "Hugo", KnownPortKind.DevServer),
        new(1433, "SQL Server", KnownPortKind.DataStore),
        new(2375, "Docker API", KnownPortKind.Tooling),
        new(2376, "Docker API (TLS)", KnownPortKind.Tooling),
        new(3000, "Dev server", KnownPortKind.DevServer),
        new(3001, "Dev server", KnownPortKind.DevServer),
        new(3306, "MySQL", KnownPortKind.DataStore),
        new(4000, "Dev server", KnownPortKind.DevServer),
        new(4200, "Angular", KnownPortKind.DevServer),
        new(4321, "Astro", KnownPortKind.DevServer),
        new(5000, "Dev server", KnownPortKind.DevServer),
        new(5001, "Dev server (HTTPS)", KnownPortKind.DevServer, PrefersHttps: true),
        new(5173, "Vite", KnownPortKind.DevServer),
        new(5174, "Vite", KnownPortKind.DevServer),
        new(5432, "PostgreSQL", KnownPortKind.DataStore),
        new(5672, "RabbitMQ", KnownPortKind.DataStore),
        new(6006, "Storybook", KnownPortKind.DevServer),
        new(6379, "Redis", KnownPortKind.DataStore),
        new(7071, "Azure Functions", KnownPortKind.DevServer),
        new(8000, "HTTP dev server", KnownPortKind.DevServer),
        new(8008, "HTTP alternate", KnownPortKind.DevServer),
        new(8080, "HTTP alternate", KnownPortKind.DevServer),
        new(8081, "HTTP alternate", KnownPortKind.DevServer),
        new(8443, "HTTPS alternate", KnownPortKind.DevServer, PrefersHttps: true),
        new(8888, "Jupyter", KnownPortKind.DevServer),
        new(9000, "Dev server", KnownPortKind.DevServer),
        new(9090, "Prometheus", KnownPortKind.Tooling),
        new(9200, "Elasticsearch", KnownPortKind.DataStore),
        new(9229, "Node inspector", KnownPortKind.Tooling),
        new(11211, "Memcached", KnownPortKind.DataStore),
        new(15672, "RabbitMQ admin", KnownPortKind.Tooling),
        new(19000, "Expo", KnownPortKind.DevServer),
        new(24678, "Vite HMR", KnownPortKind.Tooling),
        new(27017, "MongoDB", KnownPortKind.DataStore),
    }.ToFrozenDictionary(p => p.Port);

    /// <summary>Ports PortPilot treats as first-class development endpoints.</summary>
    public static IReadOnlyCollection<int> All => Ports.Keys;

    public static KnownPort? Lookup(int port) => Ports.GetValueOrDefault(port);

    public static bool IsKnown(int port) => Ports.ContainsKey(port);
}
