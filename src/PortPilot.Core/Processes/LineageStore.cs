using System.Globalization;
using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// A tiny tab-separated file recording which agent session started which process, shared between
/// PortPilot processes of one user. Lines are <c>pid  startTicks  agentPid  agentStartTicks  name  cwd</c>.
/// </summary>
internal sealed class LineageStore(string path)
{
    public IEnumerable<KeyValuePair<int, (long StartTicks, AgentSession Agent)>> Load()
    {
        string[] lines;
        try
        {
            lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length >= 5
                && int.TryParse(f[0], CultureInfo.InvariantCulture, out var pid)
                && long.TryParse(f[1], CultureInfo.InvariantCulture, out var ticks)
                && int.TryParse(f[2], CultureInfo.InvariantCulture, out var agentPid)
                && long.TryParse(f[3], CultureInfo.InvariantCulture, out var agentTicks))
            {
                var agent = new AgentSession(agentPid, new DateTimeOffset(agentTicks, TimeSpan.Zero), f[4], f.Length > 5 && f[5].Length > 0 ? f[5] : null, IsRunning: true);
                yield return new(pid, (ticks, agent));
            }
        }
    }

    public void Save(IReadOnlyDictionary<int, (long StartTicks, AgentSession Agent)> lineage)
    {
        try
        {
            var lines = lineage.Select(kv => string.Join('\t',
                kv.Key.ToString(CultureInfo.InvariantCulture),
                kv.Value.StartTicks.ToString(CultureInfo.InvariantCulture),
                kv.Value.Agent.ProcessId.ToString(CultureInfo.InvariantCulture),
                (kv.Value.Agent.StartTime?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture),
                kv.Value.Agent.Name,
                kv.Value.Agent.WorkingDirectory?.Replace('\t', ' ') ?? ""));
            var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            File.WriteAllLines(temp, lines);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
