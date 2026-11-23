using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Interop;

namespace PortPilot.Core.Processes;

/// <summary>
/// Terminates processes on Linux and macOS: SIGTERM first so the process can clean up, then SIGKILL if it
/// has not exited within the grace period.
/// </summary>
public sealed class LinuxProcessTerminator(ILogger<LinuxProcessTerminator>? logger = null) : IProcessTerminator
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly ILogger _log = logger ?? NullLogger<LinuxProcessTerminator>.Instance;

    public TerminationAccess ProbeAccess(int processId)
    {
        if (IsProtected(processId))
        {
            return TerminationAccess.Protected;
        }

        if (Probe(processId) is not { } stat)
        {
            return TerminationAccess.NotRunning;
        }

        if (stat.IsKernelThread)
        {
            return TerminationAccess.Protected;
        }

        // Signal 0 performs the permission check without delivering anything.
        if (Libc.kill(processId, 0) == 0)
        {
            return TerminationAccess.Allowed;
        }

        return Marshal.GetLastPInvokeError() == Libc.ESRCH ? TerminationAccess.NotRunning : TerminationAccess.RequiresElevation;
    }

    public Task<TerminationResult> TerminateAsync(int processId, DateTimeOffset? expectedStartTime, CancellationToken cancellationToken = default) =>
        TerminateCoreAsync(processId, expectedStartTime, cancellationToken);

    private async Task<TerminationResult> TerminateCoreAsync(int pid, DateTimeOffset? expectedStartTime, CancellationToken token)
    {
        if (IsProtected(pid))
        {
            return Log(new TerminationResult(TerminationOutcome.Protected, pid, "This process cannot be terminated."));
        }

        if (Probe(pid) is not { } stat)
        {
            return Log(new TerminationResult(TerminationOutcome.AlreadyExited, pid));
        }

        if (stat.IsKernelThread)
        {
            return Log(new TerminationResult(TerminationOutcome.Protected, pid, "Kernel threads cannot be terminated."));
        }

        // Between the moment the user confirmed and now the PID may have been recycled.
        if (expectedStartTime is { } expected && stat.Start != expected)
        {
            return Log(new TerminationResult(TerminationOutcome.ProcessChanged, pid, "The PID now belongs to a different process."));
        }

        var startTicks = stat.Start.UtcTicks;
        var failure = Signal(pid, Libc.SIGTERM);
        if (failure is not null)
        {
            return Log(failure);
        }

        if (await WaitForExitAsync(pid, startTicks, GracePeriod, token).ConfigureAwait(false))
        {
            return Log(new TerminationResult(TerminationOutcome.Terminated, pid));
        }

        _log.LogInformation("{Pid} ignored SIGTERM; sending SIGKILL", pid);
        failure = Signal(pid, Libc.SIGKILL);
        if (failure is not null)
        {
            return Log(failure);
        }

        return Log(await WaitForExitAsync(pid, startTicks, KillTimeout, token).ConfigureAwait(false)
            ? new TerminationResult(TerminationOutcome.Terminated, pid)
            : new TerminationResult(TerminationOutcome.TimedOut, pid, "The process is still shutting down."));
    }

    private static TerminationResult? Signal(int pid, int signal)
    {
        if (Libc.kill(pid, signal) == 0)
        {
            return null;
        }

        var error = Marshal.GetLastPInvokeError();
        return error switch
        {
            Libc.ESRCH => new TerminationResult(TerminationOutcome.AlreadyExited, pid),
            Libc.EPERM => new TerminationResult(TerminationOutcome.AccessDenied, pid, "Operation not permitted; the process belongs to another user."),
            _ => new TerminationResult(TerminationOutcome.Failed, pid, Marshal.GetPInvokeErrorMessage(error)),
        };
    }

    /// <summary>A process counts as gone once it is a zombie or its PID belongs to someone else.</summary>
    private static async Task<bool> WaitForExitAsync(int pid, long startTicks, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Probe(pid) is not { } stat || stat.Start.UtcTicks != startTicks)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }
    }

    private static bool IsProtected(int pid) => pid <= 1 || pid == Environment.ProcessId;

    private readonly record struct ProcessState(DateTimeOffset Start, bool IsKernelThread);

    /// <summary>A live (non-zombie) process's start time, on Linux (procfs) or macOS (libproc).</summary>
    private static ProcessState? Probe(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacProc.TryGetBsdInfo(pid, out var info) && !info.IsZombie ? new ProcessState(info.StartTime, IsKernelThread: false) : null;
        }

        return ProcFs.TryReadStat(pid, out var stat) && !stat.IsGone
            ? new ProcessState(ProcFs.ToStartTime(stat.StartTicks), pid == 2 || stat.ParentId == 2)
            : null;
    }

    private TerminationResult Log(TerminationResult result)
    {
        if (result.Succeeded)
        {
            _log.LogInformation("Termination of {Pid} finished with {Outcome}", result.ProcessId, result.Outcome);
        }
        else
        {
            _log.LogWarning("Termination of {Pid} finished with {Outcome}: {Detail}", result.ProcessId, result.Outcome, result.Detail);
        }

        return result;
    }
}
