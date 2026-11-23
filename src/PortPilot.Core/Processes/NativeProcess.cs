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

    /// <summary>
    /// Reads a process's current directory from its PEB (RTL_USER_PROCESS_PARAMETERS.CurrentDirectory).
    /// Needs PROCESS_VM_READ, so it works for the user's own processes; 64-bit PortPilot, 64-bit targets only.
    /// </summary>
    public static string? GetCurrentDirectory(int pid)
    {
        if (!Environment.Is64BitProcess)
        {
            return null;
        }

        var open = Open(pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION | Kernel32.PROCESS_VM_READ);
        if (!open.Success)
        {
            return null;
        }

        using var handle = open.Handle!;
        if (Kernel32.IsWow64Process(handle, out var wow64) && wow64)
        {
            return null;
        }

        NtDll.PROCESS_BASIC_INFORMATION info;
        if (NtDll.NtQueryInformationProcess(handle, NtDll.ProcessBasicInformation, &info, sizeof(NtDll.PROCESS_BASIC_INFORMATION), out _) < 0 || info.PebBaseAddress == 0)
        {
            return null;
        }

        // x64 / ARM64 layouts: PEB.ProcessParameters at 0x20; CurrentDirectory.DosPath at 0x38.
        nint parameters;
        if (!Kernel32.ReadProcessMemory(handle, info.PebBaseAddress + 0x20, &parameters, (nuint)sizeof(nint), out _) || parameters == 0)
        {
            return null;
        }

        NtDll.UNICODE_STRING path;
        if (!Kernel32.ReadProcessMemory(handle, parameters + 0x38, &path, (nuint)sizeof(NtDll.UNICODE_STRING), out _) || path.Length == 0 || path.Buffer is null)
        {
            return null;
        }

        var chars = new char[path.Length / 2];
        fixed (char* buffer = chars)
        {
            if (!Kernel32.ReadProcessMemory(handle, (nint)path.Buffer, buffer, path.Length, out _))
            {
                return null;
            }
        }

        return new string(chars).TrimEnd('\\') is { Length: > 0 } dir && dir[^1] == ':' ? dir + "\\" : new string(chars).TrimEnd('\\');
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
