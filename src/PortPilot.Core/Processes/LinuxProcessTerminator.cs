using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Interop;

namespace PortPilot.Core.Processes;

/// <summary>
/// Terminates processes on Linux: SIGTERM first so the process can clean up, then SIGKILL if it
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

        if (!ProcFs.TryReadStat(processId, out var stat) || stat.IsGone)
        {
            return TerminationAccess.NotRunning;
        }

        if (IsKernelThread(processId, stat))
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

        if (!ProcFs.TryReadStat(pid, out var stat) || stat.IsGone)
        {
            return Log(new TerminationResult(TerminationOutcome.AlreadyExited, pid));
        }

        if (IsKernelThread(pid, stat))
        {
            return Log(new TerminationResult(TerminationOutcome.Protected, pid, "Kernel threads cannot be terminated."));
        }

        // Between the moment the user confirmed and now the PID may have been recycled.
        if (expectedStartTime is { } expected && ProcFs.ToStartTime(stat.StartTicks) != expected)
        {
            return Log(new TerminationResult(TerminationOutcome.ProcessChanged, pid, "The PID now belongs to a different process."));
        }

        var startTicks = stat.StartTicks;
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
            if (!ProcFs.TryReadStat(pid, out var stat) || stat.IsGone || stat.StartTicks != startTicks)
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

    private static bool IsKernelThread(int pid, ProcFs.Stat stat) => pid == 2 || stat.ParentId == 2;

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
