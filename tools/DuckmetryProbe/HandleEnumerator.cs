// Lists the named objects in this session's object namespace, which is where named
// file-mapping objects (sections), events and mutexes live.
//
// The earlier implementation enumerated every HANDLE on the machine and duplicated the
// section ones to learn their names. That works in general, but on a machine running an
// anti-cheat-protected game it is pathological: OpenProcess against the protected process
// takes seconds per handle, so the scan stalled mid-way and the probe looked dead. This
// version never touches another process. It queries the object manager's directory
// (NtOpenDirectoryObject / NtQueryDirectoryObject on \Sessions\<n>\BaseNamedObjects and
// \BaseNamedObjects), which the kernel answers in milliseconds, and reports the TYPE of
// every object - sections (named file mappings), events, mutexes, ... - so the probe can
// see that "LMU_Data" exists even when its ACL would refuse opening it.

using System.Runtime.InteropServices;

namespace DuckmetryProbe;

/// <summary>One named file-mapping object that exists in this session's namespace.</summary>
internal sealed record MappedRegion(string Name, int Pid, int OwnerPid, nuint Address, nuint Size)
{
    public override string ToString() =>
        $"{Name}  size={(Size == 0 ? "?" : Size.ToString("N0"))}";
}

/// <summary>A namespace scan's outcome: the sections, whether the scan finished, and every
/// object name seen (sections AND events AND mutexes), so callers can look for names like
/// "LMU_Data_HoldEvent" even when the section itself refuses to open.</summary>
internal sealed record EnumerationResult(
    IReadOnlyList<MappedRegion> Regions, bool Truncated,
    IReadOnlyList<(string Name, string Root)> AllEntries);

internal static class HandleEnumerator
{
    private const uint DIRECTORY_QUERY = 0x0001;
    private const uint FILE_MAP_READ = 0x0004;

    private const int STATUS_MORE_ENTRIES = unchecked((int)0x00000105);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_DIRECTORY_INFORMATION
    {
        public UNICODE_STRING Name;
        public UNICODE_STRING TypeName;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtOpenDirectoryObject(
        out IntPtr handle, uint access, ref OBJECT_ATTRIBUTES attributes);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryObject(
        IntPtr directoryHandle, IntPtr buffer, int length, bool bSingleEntry,
        bool bRestart, out uint context, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nuint OpenFileMappingW(uint desiredAccess, bool inherit, string name);

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
    /// Every named file-mapping object (and every other named object) this session can see.
    /// Fast and side-effect free: no opening of other processes' handles.
    /// </summary>
    public static EnumerationResult Enumerate(
        int budgetSeconds = 25, Action<int, int>? onProgress = null)
    {
        var sections = new List<MappedRegion>();
        var allEntries = new List<(string Name, string Root)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        uint sessionId = 0;
        ProcessIdToSessionId(GetCurrentProcessId(), out sessionId);

        var roots = new (string Path, string Label)[]
        {
            ($"\\Sessions\\{sessionId}\\BaseNamedObjects", $"session-{sessionId}"),
            ("\\BaseNamedObjects", "global"),
        };
        foreach (var (root, label) in roots)
        {
            var (names, types) = ListDirectory(root);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                string type = types[i];
                if (!seen.Add(name)) continue;
                allEntries.Add((name, label));
                if (!type.Equals("Section", StringComparison.OrdinalIgnoreCase)) continue;

                // Named file mapping. Try to open it read-only to learn its size; the name
                // already told us it exists, so a refusal only loses the size.
                nuint size = 0;
                nuint h = OpenFileMappingW(FILE_MAP_READ, false, name);
                if (h != 0)
                {
                    nuint v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, 0);
                    if (v != 0)
                    {
                        if (VirtualQuery((IntPtr)(ulong)v, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
                            size = mbi.RegionSize;
                        UnmapViewOfFile((IntPtr)(ulong)v);
                    }
                    CloseHandle((IntPtr)(ulong)h);
                }
                sections.Add(new MappedRegion(name, 0, 0, 0, size));
            }
        }

        onProgress?.Invoke(allEntries.Count, sections.Count);
        return new EnumerationResult(sections, Truncated: false, allEntries);
    }

    private static (List<string> Names, List<string> Types) ListDirectory(string root)
    {
        var names = new List<string>();
        var types = new List<string>();

        // The UNICODE_STRING must OWN its buffer: RtlInitUnicodeString would point at the
        // marshalled copy of a C# string, which is only valid for the duration of that call.
        IntPtr wide = Marshal.StringToHGlobalUni(root);
        var name = new UNICODE_STRING
        {
            Length = (ushort)(root.Length * 2),
            MaximumLength = (ushort)(root.Length * 2 + 2),
            Buffer = wide,
        };
        var attr = new OBJECT_ATTRIBUTES
        {
            Length = Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
            ObjectName = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>()),
            Attributes = 0x40 /* OBJ_CASE_INSENSITIVE */,
        };
        try
        {
            Marshal.StructureToPtr(name, attr.ObjectName, false);

            IntPtr dir;
            int status = NtOpenDirectoryObject(out dir, DIRECTORY_QUERY, ref attr);
            if (status != 0)
                return (names, types); // not accessible (e.g. non-existent session tree) - not fatal
            try
            {
                const int bufSize = 1 << 16;
                IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                byte[] zeros = new byte[bufSize];
                try
                {
                    uint context = 0;
                    bool restart = true;
                    while (true)
                    {
                        // Zero first: on STATUS_MORE_ENTRIES the buffer is full of entries
                        // with NO terminator, and the parse loop needs the terminator to stop
                        // instead of reading garbage lengths past the last entry.
                        Marshal.Copy(zeros, 0, buffer, bufSize);
                        status = NtQueryDirectoryObject(dir, buffer, bufSize, false, restart, out context, out _);
                        restart = false;
                        if (status == 0 || status == STATUS_MORE_ENTRIES)
                        {
                            int off = 0;
                            while (off + Marshal.SizeOf<OBJECT_DIRECTORY_INFORMATION>() <= bufSize)
                            {
                                var e = Marshal.PtrToStructure<OBJECT_DIRECTORY_INFORMATION>(
                                    IntPtr.Add(buffer, off));
                                if (e.Name.Length == 0 || e.Name.Buffer == IntPtr.Zero) break;
                                if (e.Name.Length > 2048 || e.TypeName.Length > 2048) break;
                                string nm = Marshal.PtrToStringUni(e.Name.Buffer, e.Name.Length / 2) ?? string.Empty;
                                string ty = e.TypeName.Length == 0 || e.TypeName.Buffer == IntPtr.Zero
                                    ? string.Empty
                                    : Marshal.PtrToStringUni(e.TypeName.Buffer, e.TypeName.Length / 2) ?? string.Empty;
                                if (nm.Length > 0)
                                {
                                    names.Add(nm);
                                    types.Add(ty);
                                }
                                off += Marshal.SizeOf<OBJECT_DIRECTORY_INFORMATION>();
                            }
                        }
                        if (status == 0) break;      // enumeration reached the end
                        if (status != STATUS_MORE_ENTRIES) break; // unexpected - stop, keep what we have
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(dir);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attr.ObjectName);
            Marshal.FreeHGlobal(wide);
        }

        return (names, types);
    }
}