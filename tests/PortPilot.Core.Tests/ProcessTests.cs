using System.Diagnostics;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Tests;

[TestClass]
public sealed class ProcessInfoProviderTests
{
    [TestMethod]
    public void Inspects_the_current_process()
    {
        using var self = Process.GetCurrentProcess();

        var details = ProcessInfoProvider.Inspect(self.Id);

        Assert.AreEqual(ProcessAccessLevel.Full, details.Access);
        Assert.AreEqual(self.MainModule!.FileName, details.ExecutablePath, ignoreCase: true);
        Assert.AreEqual(Path.GetFileName(self.MainModule.FileName), details.ImageName, ignoreCase: true);
        Assert.IsNotNull(details.CommandLine);
        Assert.IsNotNull(details.StartTime);
        Assert.AreEqual(self.StartTime.ToUniversalTime(), details.StartTime!.Value.UtcDateTime);
    }

    [TestMethod]
    public void Reads_command_line_of_child_process()
    {
        using var child = new SleepingProcess();

        var details = ProcessInfoProvider.Inspect(child.Id);

        Assert.AreEqual("PING.EXE", details.ImageName.ToUpperInvariant());
        StringAssert.Contains(details.CommandLine, "-n 120 127.0.0.1");
    }

    [TestMethod]
    public void System_pids_are_reported_without_opening_them()
    {
        Assert.AreEqual(ProcessAccessLevel.System, ProcessInfoProvider.Inspect(4).Access);
        Assert.AreEqual("System", ProcessInfoProvider.Inspect(4).ImageName);
        Assert.AreEqual(ProcessAccessLevel.System, ProcessInfoProvider.Inspect(0).Access);
    }

    [TestMethod]
    public void Exited_process_is_reported_gracefully()
    {
        int pid;
        using (var child = new SleepingProcess())
        {
            pid = child.Id;
        }

        Thread.Sleep(100);
        var details = ProcessInfoProvider.Inspect(pid);

        Assert.AreNotEqual(ProcessAccessLevel.Full, details.Access);
        Assert.IsNull(details.ExecutablePath);
    }

    [TestMethod]
    public void Resolve_caches_and_prunes()
    {
        var provider = new ProcessInfoProvider();
        using var child = new SleepingProcess();

        var first = provider.Resolve([child.Id, Environment.ProcessId]);
        var second = provider.Resolve([child.Id, Environment.ProcessId]);

        Assert.AreSame(first[child.Id], second[child.Id], "Unchanged processes should be served from cache");
        Assert.AreEqual(2, second.Count);

        var third = provider.Resolve([Environment.ProcessId]);
        Assert.AreEqual(1, third.Count);
    }

    [TestMethod]
    public void Resolve_detects_exited_process()
    {
        var provider = new ProcessInfoProvider();
        var child = new SleepingProcess();
        var pid = child.Id;
        Assert.AreEqual(ProcessAccessLevel.Full, provider.Resolve([pid])[pid].Access);

        child.Dispose();
        Thread.Sleep(100);

        Assert.AreNotEqual(ProcessAccessLevel.Full, provider.Resolve([pid])[pid].Access);
    }
}

[TestClass]
public sealed class ProcessTerminatorTests
{
    private readonly ProcessTerminator _terminator = new();

    [TestMethod]
    public async Task Terminates_a_process_and_waits_for_exit()
    {
        using var child = new SleepingProcess();
        var start = ProcessInfoProvider.Inspect(child.Id).StartTime;

        Assert.AreEqual(TerminationAccess.Allowed, _terminator.ProbeAccess(child.Id));
        var result = await _terminator.TerminateAsync(child.Id, start);

        Assert.AreEqual(TerminationOutcome.Terminated, result.Outcome);
        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(child.Process.HasExited);
    }

    [TestMethod]
    public async Task Refuses_when_pid_was_reused_by_another_process()
    {
        using var child = new SleepingProcess();
        var wrongStart = ProcessInfoProvider.Inspect(child.Id).StartTime!.Value.AddSeconds(-30);

        var result = await _terminator.TerminateAsync(child.Id, wrongStart);

        Assert.AreEqual(TerminationOutcome.ProcessChanged, result.Outcome);
        Assert.IsFalse(child.Process.HasExited);
    }

    [TestMethod]
    public async Task Already_exited_process_is_not_an_error()
    {
        int pid;
        using (var child = new SleepingProcess())
        {
            pid = child.Id;
        }

        Thread.Sleep(100);
        var result = await _terminator.TerminateAsync(pid, null);

        Assert.IsTrue(result.Succeeded, $"Unexpected outcome {result.Outcome}");
        Assert.AreEqual(TerminationAccess.NotRunning, _terminator.ProbeAccess(pid));
    }

    [TestMethod]
    public async Task Refuses_to_terminate_system_and_self()
    {
        Assert.AreEqual(TerminationOutcome.Protected, (await _terminator.TerminateAsync(4, null)).Outcome);
        Assert.AreEqual(TerminationOutcome.Protected, (await _terminator.TerminateAsync(0, null)).Outcome);
        Assert.AreEqual(TerminationOutcome.Protected, (await _terminator.TerminateAsync(Environment.ProcessId, null)).Outcome);
        Assert.AreEqual(TerminationAccess.Protected, _terminator.ProbeAccess(4));
    }

    [TestMethod]
    public void Reports_elevation_requirement_for_processes_owned_by_the_system()
    {
        if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Assert.Inconclusive("Running elevated; access is not denied.");
        }

        // lsass and services always run as SYSTEM.
        var services = Process.GetProcessesByName("services").FirstOrDefault();
        Assert.IsNotNull(services);
        var access = _terminator.ProbeAccess(services.Id);

        Assert.AreNotEqual(TerminationAccess.Allowed, access);
    }
}
