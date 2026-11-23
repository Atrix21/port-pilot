using System.Text.Json;
using PortPilot.Core.Model;

namespace PortPilot.Cli;

/// <summary>The JSON shape of one endpoint, shared by list --json and stream.</summary>
internal static class JsonOut
{
    public static void Entry(Utf8JsonWriter w, PortEntry e)
    {
        var p = e.Process;
        w.WriteStartObject();
        w.WriteNumber("port", e.Port);
        w.WriteString("protocol", Format.Protocol(e.Binding));
        w.WriteString("address", e.Binding.LocalAddress.ToString());
        w.WriteString("state", e.Binding.State.ToString());
        if (e.Binding.RemoteAddress is { } remote)
        {
            w.WriteString("remoteAddress", remote.ToString());
            w.WriteNumber("remotePort", e.Binding.RemotePort);
        }

        w.WriteNumber("pid", e.ProcessId);
        w.WriteString("process", p.ImageName);
        w.WriteString("executable", p.ExecutablePath);
        w.WriteString("commandLine", p.CommandLine);
        if (p.StartTime is { } start)
        {
            w.WriteString("started", start);
        }

        w.WriteString("access", p.Access.ToString());
        w.WriteString("workingDirectory", p.WorkingDirectory);
        if (p.Project is { } project)
        {
            w.WriteStartObject("project");
            w.WriteString("name", project.Name);
            w.WriteString("branch", project.Branch);
            w.WriteString("root", project.Root);
            w.WriteBoolean("worktree", project.IsWorktree);
            w.WriteEndObject();
        }

        if (p.Agent is { } agent)
        {
            w.WriteStartObject("agent");
            w.WriteString("name", agent.Name);
            w.WriteNumber("pid", agent.ProcessId);
            w.WriteBoolean("running", agent.IsRunning);
            w.WriteEndObject();
        }

        w.WriteBoolean("orphaned", p.IsOrphaned);
        w.WriteBoolean("tooling", PortPilot.Core.Processes.ToolingDetector.IsTooling(p));
        w.WriteString("service", e.Known?.Name);
        w.WriteEndObject();
    }
}
