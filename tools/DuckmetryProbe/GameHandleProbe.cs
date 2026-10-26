// The last-resort route to the the game blocks: duplicate handles out of the GAME's own process
// table.
//
// The by-name open fails with ERROR_FILE_NOT_FOUND for every the game object even though the
// namespace lists them in the probe's own session - so name resolution, prefixes and
// invisible characters are all ruled out, and an ACL problem would return ACCESS_DENIED.
// Whatever hides the objects from by-name opens, the game process itself holds real handles
// to them, and a handle duplicated with DUPLICATE_SAME_ACCESS carries the game's own access
// rights - name AND a readable view come with it, no name resolution involved.
//
// The game's pid is discovered two ways: a process named "Le Mans Ultimate" (the game's
// own process name — functional), and the pid in the GAME_ID_RECORD_&lt;hex&gt; file.
// "GAME_ID_RECORD_<hex>" object the game itself publishes in the namespace (the hex IS its
// pid).

using System.Runtime.InteropServices;

namespace DuckmetryProbe;

internal static class GameHandleProbe
{
    private const int ProcessHandleInformation = 51;
    private const int ObjectNameInformation = 1;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 0x0002;
    private const uint FILE_MAP_READ = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_HANDLE_TABLE_ENTRY_INFO
    {
        public ushort HandleValue;
        public ushort HandleCount;
        public ushort PointerCount;
        public uint GrantedAccess;
        public uint ObjectTypeIndex;
        public uint Attributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_HANDLE_SNAPSHOT_INFORMATION
    {
        public nuint NumberOfHandles;
        public PROCESS_HANDLE_TABLE_ENTRY_INFO HandleList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nuint process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(
        nuint handle, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        nuint sourceProcess, nuint sourceHandle, nuint targetProcess,
        out nuint targetHandle, uint desiredAccess, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nuint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint MapViewOfFile(
        nuint fileMappingObject, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr baseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(
        IntPtr address, out MEMORY_BASIC_INFORMATION buffer, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <summary>
    /// Find the game's pid: a "Le Mans Ultimate" process, or the pid in the GAME_ID_RECORD_&lt;hex&gt;
    /// object the game publishes. Returns 0 when the game is not running.
    /// </summary>
    public static uint FindGamePid(string[] listedNames)
    {
        foreach (System.Diagnostics.Process proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                string n = proc.ProcessName;
                if (n.Contains("Le Mans Ultimate", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("LMU", StringComparison.OrdinalIgnoreCase))
                    return (uint)proc.Id;
            }
            catch { /* process vanished while listing */ }
        }
        foreach (string nm in listedNames)
        {
            if (nm.StartsWith("GAME_ID_RECORD_", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(nm.Substring("GAME_ID_RECORD_".Length),
                    System.Globalization.NumberStyles.HexNumber, null, out uint pid))
                return pid;
        }
        return 0;
    }

    /// <summary>
    /// Walk the game's handle table, duplicate every handle whose object name mentions the game,
    /// and return the sections with a read view already mapped.
    /// </summary>
    public static List<(string Name, nuint View, nuint Size)> DuplicateFromPid(uint pid)
    {
        var opened = new List<(string Name, nuint View, nuint Size)>();
        nuint hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, pid);
        if (hProc == 0)
        {
            Console.WriteLine($"  OpenProcess({pid}) failed - Win32 {Marshal.GetLastWin32Error()}");
            return opened;
        }

        try
        {
            int len = 1 << 16;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int status;
                do
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal(len);
                    status = NtQueryInformationProcess(hProc, ProcessHandleInformation, buffer, len, out _);
                    if (status == STATUS_INFO_LENGTH_MISMATCH) len += (1 << 16);
                } while (status == STATUS_INFO_LENGTH_MISMATCH);

                if (status != 0)
                {
                    Console.WriteLine($"  NtQueryInformationProcess failed 0x{status:X8}");
                    return opened;
                }

                nuint count = (nuint)Marshal.ReadIntPtr(buffer, 0);
                int entrySize = Marshal.SizeOf<PROCESS_HANDLE_TABLE_ENTRY_INFO>();
                IntPtr first = IntPtr.Add(buffer, IntPtr.Size); // skip NumberOfHandles

                for (ulong i = 0; i < (ulong)count; i++)
                {
                    var e = Marshal.PtrToStructure<PROCESS_HANDLE_TABLE_ENTRY_INFO>(
                        IntPtr.Add(first, checked((int)i * entrySize)));
                    nuint dup;
                    if (!DuplicateHandle(hProc, (nuint)e.HandleValue, GetCurrentProcess(),
                                         out dup, 0, false, DUPLICATE_SAME_ACCESS))
                        continue;
                    try
                    {
                        string name = ReadObjectName(dup);
                        if (string.IsNullOrEmpty(name)) continue;
                        bool isSection = (e.GrantedAccess & 0x000F) != 0; // section access bits
                        if (!name.Contains("LMU", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (name.Contains("LMU", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isSection)
                            {
                                nuint view = MapViewOfFile(dup, FILE_MAP_READ, 0, 0, 0);
                                nuint size = 0;
                                if (view != 0)
                                {
                                    if (VirtualQuery((IntPtr)(ulong)view, out var mbi,
                                                     (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
                                        size = mbi.RegionSize;
                                }
                                else
                                {
                                    Console.WriteLine($"  {name}: duplicated but MapViewOfFile failed - Win32 {Marshal.GetLastWin32Error()}");
                                }
                                Console.WriteLine($"  {name}: via game pid {pid}, {size:N0} bytes" +
                                                  (view == 0 ? " (no view)" : " (view mapped)"));
                                if (view != 0)
                                    opened.Add((name, view, size));
                            }
                            else
                            {
                                Console.WriteLine($"  {name}: duplicated (non-section object, access 0x{e.GrantedAccess:X})");
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(dup);
                    }
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(hProc);
        }
        return opened;
    }

    private static nuint GetCurrentProcess() =>
        (nuint)System.Diagnostics.Process.GetCurrentProcess().Handle;

    private static string ReadObjectName(nuint handle)
    {
        int size = 512;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int needed;
            while (true)
            {
                int status = NtQueryObject(handle, ObjectNameInformation, buffer, size, out needed);
                if (status == 0) break;
                if (needed <= 0 || needed > (1 << 20)) return string.Empty;
                Marshal.FreeHGlobal(buffer);
                size = needed + 256;
                buffer = Marshal.AllocHGlobal(size);
            }
            ushort length = (ushort)Marshal.ReadInt16(buffer, 0);
            if (length == 0 || length > 2048) return string.Empty;
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4), length / 2) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}