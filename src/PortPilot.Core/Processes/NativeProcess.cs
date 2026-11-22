using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PortPilot.Core.Interop;

namespace PortPilot.Core.Processes;

/// <summary>
/// Thin, allocation-conscious helpers over the Win32 process APIs. Every method tolerates the
/// process exiting underneath it.
/// </summary>
internal static unsafe class NativeProcess
{
    public readonly record struct OpenResult(SafeProcessHandle? Handle, int Error)
    {
        public bool Success => Handle is { IsInvalid: false };
        public bool AccessDenied => Error == Kernel32.ERROR_ACCESS_DENIED;
        public bool NotFound => Error == Kernel32.ERROR_INVALID_PARAMETER;
    }

    public static OpenResult Open(int pid, uint access)
    {
        var handle = Kernel32.OpenProcess(access, false, pid);
        if (!handle.IsInvalid)
        {
            return new OpenResult(handle, 0);
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return new OpenResult(null, error);
    }

    public static string? GetImagePath(SafeProcessHandle handle)
    {
        Span<char> buffer = stackalloc char[1024];
        var size = buffer.Length;
        fixed (char* p = buffer)
        {
            return Kernel32.QueryFullProcessImageName(handle, 0, p, ref size) ? new string(p, 0, size) : null;
        }
    }

    public static DateTimeOffset? GetStartTime(SafeProcessHandle handle)
    {
        if (!Kernel32.GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            return null;
        }

        var ticks = creation.ToInt64();
        return ticks <= 0 ? null : DateTimeOffset.FromFileTime(ticks);
    }

    public static bool HasExited(SafeProcessHandle handle) =>
        Kernel32.WaitForSingleObject(handle, 0) == Kernel32.WAIT_OBJECT_0;

    public static string? GetCommandLine(SafeProcessHandle handle)
    {
        var size = 2048;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = NativeMemory.Alloc((nuint)size);
            try
            {
                var status = NtDll.NtQueryInformationProcess(handle, NtDll.ProcessCommandLineInformation, buffer, size, out var needed);
                if (status == NtDll.STATUS_INFO_LENGTH_MISMATCH && needed > size)
                {
                    size = needed;
                    continue;
                }

                if (status < 0)
                {
                    return null;
                }

                var str = (NtDll.UNICODE_STRING*)buffer;
                if (str->Buffer is null || str->Length == 0)
                {
                    return null;
                }

                return new string(str->Buffer, 0, str->Length / sizeof(char));
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    public static bool? IsCritical(SafeProcessHandle handle) =>
        Kernel32.IsProcessCritical(handle, out var critical) ? critical : null;

    /// <summary>Lists every process name and parent without needing access to the processes themselves.</summary>
    public static Dictionary<int, (string Name, int ParentId)> SnapshotProcessNames()
    {
        var result = new Dictionary<int, (string, int)>(512);
        using var snapshot = Kernel32.CreateToolhelp32Snapshot(Kernel32.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            return result;
        }

        var entry = new Kernel32.PROCESSENTRY32W { dwSize = (uint)sizeof(Kernel32.PROCESSENTRY32W) };
        if (!Kernel32.Process32FirstW(snapshot, &entry))
        {
            return result;
        }

        do
        {
            var name = new string(entry.szExeFile);
            result[(int)entry.th32ProcessID] = (name, (int)entry.th32ParentProcessID);
        }
        while (Kernel32.Process32NextW(snapshot, &entry));

        return result;
    }
}
