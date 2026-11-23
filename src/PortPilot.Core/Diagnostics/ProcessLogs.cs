using System.Diagnostics;
using System.Text;

namespace PortPilot.Core.Diagnostics;

/// <summary>The last lines a process wrote, and where they came from; or why they can't be read.</summary>
public sealed record LogTail(string? Source, IReadOnlyList<string> Lines, string? Unavailable)
{
    public bool IsAvailable => Unavailable is null;

    public static LogTail None(string reason) => new(null, [], reason);
}

/// <summary>
/// Reads a process's recent output without attaching to it. On Linux: if stdout or stderr is redirected
/// to a file (<c>npm run dev &gt; dev.log</c>), the end of that file; if the process belongs to a systemd
/// unit, its journal. Output going to a terminal or a pipe can't be read back by anyone else.
/// </summary>
public static class ProcessLogs
{
    private const int TailBytes = 64 * 1024;

    public static LogTail Read(int pid, int lines = 40)
    {
        if (!OperatingSystem.IsLinux())
        {
            return LogTail.None("Reading another process's output isn't possible on Windows.");
        }

        if (pid <= 0)
        {
            return LogTail.None("The process is unknown.");
        }

        string? terminal = null;
        foreach (var fd in new[] { 1, 2 })
        {
            var target = Processes.ProcFs.ReadLink($"/proc/{pid}/fd/{fd}");
            if (target is null)
            {
                return LogTail.None("Its output is private (it belongs to another user).");
            }

            if (target.StartsWith('/') && !target.StartsWith("/dev/", StringComparison.Ordinal) && File.Exists(target))
            {
                return new LogTail(target, Tail(target, lines), null);
            }

            terminal ??= target.StartsWith("/dev/pts/", StringComparison.Ordinal) ? "a terminal"
                : target.StartsWith("pipe:", StringComparison.Ordinal) ? "a pipe"
                : target.StartsWith("socket:", StringComparison.Ordinal) ? "a socket"
                : target == "/dev/null" ? "/dev/null" : null;
        }

        if (SystemdUnit(pid) is { } unit && Journal(unit.Name, unit.User, lines) is { Count: > 0 } journal)
        {
            return new LogTail($"journalctl {(unit.User ? "--user " : "")}-u {unit.Name}", journal, null);
        }

        return LogTail.None(terminal switch
        {
            "/dev/null" => "Its output is discarded (/dev/null).",
            null => "Its output isn't readable from outside.",
            _ => $"Its output goes to {terminal}, which only the reader on the other end can see. Redirect it to a file (e.g. > dev.log) to see it here.",
        });
    }

    private static List<string> Tail(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            var lines = text.Split('\n').Select(StripAnsi).ToList();
            if (start > 0 && lines.Count > 0)
            {
                lines.RemoveAt(0); // probably a partial line
            }

            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines.Count > count ? lines[^count..] : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Removes colour codes so dev-server output reads cleanly.</summary>
    private static string StripAnsi(string line)
    {
        if (!line.Contains('\x1b', StringComparison.Ordinal))
        {
            return line.TrimEnd('\r');
        }

        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\x1b' && i + 1 < line.Length && line[i + 1] == '[')
            {
                i += 2;
                while (i < line.Length && line[i] is not (>= '@' and <= '~'))
                {
                    i++;
                }

                continue;
            }

            if (line[i] != '\r')
            {
                sb.Append(line[i]);
            }
        }

        return sb.ToString();
    }

    private static (string Name, bool User)? SystemdUnit(int pid)
    {
        try
        {
            // e.g. "0::/user.slice/user-1000.slice/user@1000.service/app.slice/redis.service"
            var cgroup = File.ReadAllText($"/proc/{pid}/cgroup");
            var path = cgroup.Split('\n').FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal))?[3..];
            var unit = path?.Split('/').LastOrDefault(p => p.EndsWith(".service", StringComparison.Ordinal));
            if (unit is null || unit.StartsWith("user@", StringComparison.Ordinal))
            {
                return null;
            }

            return (unit, path!.Contains("/user@", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string>? Journal(string unit, bool user, int lines)
    {
        try
        {
            var info = new ProcessStartInfo("journalctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (user)
            {
                info.ArgumentList.Add("--user");
            }

            foreach (var arg in new[] { "-u", unit, "-n", lines.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-pager", "-o", "cat" })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
