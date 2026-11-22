using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortPilot.Core.Interop;

namespace PortPilot.Core.Processes;

public enum TerminationOutcome
{
    /// <summary>The process was terminated and has exited.</summary>
    Terminated,

    /// <summary>The process had already exited – nothing to do.</summary>
    AlreadyExited,

    /// <summary>The PID now belongs to a different process than the one the user chose.</summary>
    ProcessChanged,

    /// <summary>Windows denied the request; elevation is usually required.</summary>
    AccessDenied,

    /// <summary>PortPilot refuses to end processes that would destabilise Windows.</summary>
    Protected,

    /// <summary>Termination was requested but the process did not exit within the timeout.</summary>
    TimedOut,

    Failed,
}

public sealed record TerminationResult(TerminationOutcome Outcome, int ProcessId, string? Detail = null)
{
    public bool Succeeded => Outcome is TerminationOutcome.Terminated or TerminationOutcome.AlreadyExited;
}

public interface IProcessTerminator
{
    /// <summary>Checks, without side effects, whether the current user can terminate the process.</summary>
    TerminationAccess ProbeAccess(int processId);

    /// <param name="expectedStartTime">
    /// The start time observed when the user chose the process. When supplied, termination is refused if the
    /// PID has since been reused by a different process.
    /// </param>
    Task<TerminationResult> TerminateAsync(int processId, DateTimeOffset? expectedStartTime, CancellationToken cancellationToken = default);
}

public enum TerminationAccess
{
    Allowed,
    RequiresElevation,
    Protected,
    NotRunning,
}

public sealed class ProcessTerminator(ILogger<ProcessTerminator>? logger = null) : IProcessTerminator
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _log = logger ?? NullLogger<ProcessTerminator>.Instance;

    public TerminationAccess ProbeAccess(int processId)
    {
        if (IsSystemPid(processId))
        {
            return TerminationAccess.Protected;
        }

        var open = NativeProcess.Open(processId, Kernel32.PROCESS_TERMINATE | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
        if (!open.Success)
        {
            return open.NotFound ? TerminationAccess.NotRunning : TerminationAccess.RequiresElevation;
        }

        using var handle = open.Handle!;
        return NativeProcess.IsCritical(handle) == true ? TerminationAccess.Protected : TerminationAccess.Allowed;
    }

    public Task<TerminationResult> TerminateAsync(int processId, DateTimeOffset? expectedStartTime, CancellationToken cancellationToken = default) =>
        Task.Run(() => Terminate(processId, expectedStartTime), cancellationToken);

    private TerminationResult Terminate(int pid, DateTimeOffset? expectedStartTime)
    {
        if (IsSystemPid(pid) || pid == Environment.ProcessId)
        {
            return Log(new TerminationResult(TerminationOutcome.Protected, pid, "This process cannot be terminated."));
        }

        var open = NativeProcess.Open(pid, Kernel32.PROCESS_TERMINATE | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION | Kernel32.SYNCHRONIZE);
        if (!open.Success)
        {
            if (open.NotFound)
            {
                return Log(new TerminationResult(TerminationOutcome.AlreadyExited, pid));
            }

            return Log(new TerminationResult(
                open.AccessDenied ? TerminationOutcome.AccessDenied : TerminationOutcome.Failed,
                pid,
                Marshal.GetPInvokeErrorMessage(open.Error)));
        }

        using var handle = open.Handle!;

        if (NativeProcess.HasExited(handle))
        {
            return Log(new TerminationResult(TerminationOutcome.AlreadyExited, pid));
        }

        // Between the moment the user confirmed and now the PID may have been recycled.
        if (expectedStartTime is { } expected && NativeProcess.GetStartTime(handle) is { } actual && actual != expected)
        {
            return Log(new TerminationResult(TerminationOutcome.ProcessChanged, pid, "The PID now belongs to a different process."));
        }

        if (NativeProcess.IsCritical(handle) == true)
        {
            return Log(new TerminationResult(TerminationOutcome.Protected, pid, "Windows marks this process as critical."));
        }

        if (!Kernel32.TerminateProcess(handle, 1))
        {
            var error = Marshal.GetLastPInvokeError();

            // TerminateProcess reports access denied for a process that is already exiting.
            if (NativeProcess.HasExited(handle))
            {
                return Log(new TerminationResult(TerminationOutcome.AlreadyExited, pid));
            }

            return Log(new TerminationResult(
                error == Kernel32.ERROR_ACCESS_DENIED ? TerminationOutcome.AccessDenied : TerminationOutcome.Failed,
                pid,
                Marshal.GetPInvokeErrorMessage(error)));
        }

        var wait = Kernel32.WaitForSingleObject(handle, (uint)ExitTimeout.TotalMilliseconds);
        return Log(wait == Kernel32.WAIT_OBJECT_0
            ? new TerminationResult(TerminationOutcome.Terminated, pid)
            : new TerminationResult(TerminationOutcome.TimedOut, pid, "The process is still shutting down."));
    }

    private static bool IsSystemPid(int pid) => pid is 0 or 4;

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
