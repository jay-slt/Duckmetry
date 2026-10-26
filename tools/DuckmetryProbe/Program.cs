// DuckmetryProbe - a read-only look at the the game shared-memory block.
//
// What it does, in order:
//   1. lists every named file-mapping object this process can see
//   2. picks out the ones that look like the the game block
//   3. maps it read-only and decodes the session state
//   4. prints a verdict on whether the layout matches
//
// It writes nothing, opens nothing for writing, and holds the mapping only while reading.
// It cannot affect the game or its telemetry.
//
// Usage:
//   DuckmetryProbe.exe            enumerate and try once
//   DuckmetryProbe.exe --watch    re-read every 500 ms, to watch a session change
//   DuckmetryProbe.exe --seconds 20   how long --watch runs for (default 120)

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DuckmetryProbe;

internal static class Program
{
    private const uint FILE_MAP_READ = 0x0004;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nuint OpenEventW(uint desiredAccess, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nuint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nuint handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    /// <summary>How long the fallback system-wide handle scan may run before it is cut short.</summary>
    private const int EnumBudgetSeconds = 25;

    // ScoringInfoV01 field offsets RELATIVE to the scoring section base, as declared in the
    // Studio 397 headers. These are pack-independent (no alignment surprises before 124):
    // mTrackName[64]@0, mSession@64, mCurrentET@72, mMaxLaps@88, mLapDist@96,
    // mNumVehicles@112, mGamePhase@116, mStartLight@121, mNumRedLights@122,
    // mInRealtime@123, mPlayerName[32]@124.
    private const int ScoringSessionOffset = 64;
    private const int ScoringCurrentEtOffset = 72;
    private const int ScoringMaxLapsOffset = 88;
    private const int ScoringLapDistOffset = 96;
    private const int ScoringNumVehiclesOffset = 112;
    private const int ScoringGamePhaseOffset = 116;     // unsigned char 0..9
    private const int ScoringInRealtimeOffset = 123;    // bool
    private const int ScoringPlayerNameOffset = 124;    // char[32]

    // The scoring section base, pinned EMPIRICALLY from the user's probe runs: the
    // player-name string appears at 0x6D4 in the live block ("Test Driver" in the
    // 2026-10-14 run, with mNumRedLights=5 at 0x6D2 during the start sequence), so
    // base = 0x6D4 - 124 = 0x658. The header-chain math (generic+paths sizes) and the
    // content locator both proved unreliable against this game build; the player name is
    // the anchor that cannot be mistaken (the block also carries it nowhere else as ASCII).
    private const long ScoringBase = 0x658;

    /// <summary>True when the pinned base still carries the player-name anchor.</summary>
    internal static bool PlayerNameAnchorOk(IntPtr view)
    {
        int printable = 0;
        for (int i = 0; i < 32; i++)
        {
            byte b = Marshal.ReadByte(view, (int)ScoringBase + ScoringPlayerNameOffset + i);
            if (b >= 32 && b <= 126) printable++;
        }
        return printable >= 4;
    }

    /// <summary>Reads the player name at the pinned base (or "(none)").</summary>
    internal static string ScoringPlayerName(IntPtr view)
    {
        var sb = new System.Text.StringBuilder(32);
        for (int i = 0; i < 32; i++)
        {
            byte b = Marshal.ReadByte(view, (int)ScoringBase + ScoringPlayerNameOffset + i);
            if (b == 0) break;
            if (b >= 32 && b <= 126) sb.Append((char)b);
        }
        return sb.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nuint OpenFileMappingW(uint desiredAccess, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint MapViewOfFile(
        nuint fileMappingObject, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr baseAddress);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION mbi, nuint len);

    private static int Main(string[] args)
    {
        // The layout self-check runs anywhere, including Linux, so it is checked first.
        if (args.Contains("--selfcheck", StringComparer.OrdinalIgnoreCase))
        {
            return LayoutSelfCheck.Run();
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("This probe needs Windows: it maps memory and enumerates handles.");
            Console.WriteLine("The layout self-check does not, and is the part worth running here:");
            Console.WriteLine("    dotnet run -c Release -- --selfcheck");
            return 2;
        }

        bool watch = args.Contains("--watch", StringComparer.OrdinalIgnoreCase);
        int seconds = 120;
        int i = Array.IndexOf(args, "--seconds");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int s)) seconds = s;

        Console.WriteLine("the game shared-memory probe");
        Console.WriteLine($"  pid            {Environment.ProcessId}");
        uint probeSession = 0;
        ProcessIdToSessionId(GetCurrentProcessId(), out probeSession);
        Console.WriteLine($"  session       {probeSession}");
        Console.WriteLine($"  elevated       {IsElevated()}");
        Console.WriteLine($"  .NET           {Environment.Version}");
        Console.WriteLine($"  sizeof(Extended) would be {ExtendedLayout.StructSize} bytes " +
                          $"(+ {ExtendedLayout.VersionBlockSize} version block = " +
                          $"{ExtendedLayout.StructSize + ExtendedLayout.VersionBlockSize})");
        Console.WriteLine();

        int needed = ExtendedLayout.StructOffset + ExtendedLayout.StructSize;

        // === 1. the documented names open INSTANTLY - try them first ===
        //
        // This order matters. The v2.24.x probe ran the system-wide handle scan BEFORE
        // opening anything: it printed "=== 1. named file-mapping objects ===" and then sat
        // inside the scan, which on a busy elevated machine is one OpenProcess per section
        // handle and takes minutes. "Nothing happened" was the scan, not the game. The scan
        // exists to find blocks whose names we do not know; the names below we DO know, so
        // they are tried directly - if one opens, the answer is there within seconds.
        //
        // The community plugin's declaration (for reference):
        //     char const* const MM_EXTENDED_FILE_NAME = "$LMU_SMMP_Extended$";
        // The rF2 plugin this descends from maps nine, under the $rFactor2SMMP_*$ names; the
        // bare names are what those mappings are actually created as on a non-dedicated server.
        Console.WriteLine("=== 1. opening the documented names directly ===");
        var order = new List<string>
        {
            OfficialLayout.SharedMemoryName,
            // The community-style family this game actually publishes (found by the scan):
            // the SDK-style names are versioned with the interface version, 3.8 on this
            // machine. The core names from the header are kept too, for older game builds.
            "$LMU.3.8.SMMP_Extended$",
            "$LMU.3.8.SMMP_Telemetry$",
            "$LMU.3.8.SMMP_Scoring$",
            "$LMU.3.8.SMMP_Graphics$",
            "$LMU.3.8.SMMP_Weather$",
            "$LMU_SMMP_Extended$",
            "$rFactor2SMMP_Extended$",
            "$rFactor2SMMP_Telemetry$",
            "$rFactor2SMMP_Scoring$",
            "Extended",
            "Telemetry",
            "Scoring",
        };

        var opened = TryOpenNames(order, needed, probeSession);

        int sectionsFound = 0;
        string[] scanNames = Array.Empty<string>();

        // The official interface's events are separate objects - often more permissive than
        // the section. If they open, the probe can wait on them even when the data block
        // itself stays closed, and "signaled right now" proves the game is actively writing.
        Console.WriteLine();
        Console.WriteLine("=== 1b. the interface's events ===");
        var eventNames = new[]
        {
            "LMU_Data_HoldEvent", "LMU_Data_DataEvent", "LMU_Data_Event", "LMU_SharedMemoryLockEvent",
        };
        foreach (string ev in eventNames)
        {
            nuint hev = OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, false, ev);
            if (hev == 0)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"  {ev}: refused - Win32 {err} " +
                                  $"({new System.ComponentModel.Win32Exception(err).Message})");
                continue;
            }
            uint state = WaitForSingleObject(hev, 0);
            string verdict = state == WAIT_OBJECT_0 ? "SIGNALED right now (the game is writing)"
                             : state == WAIT_TIMEOUT ? "open, not signaled at this instant"
                             : $"open, WaitForSingleObject returned 0x{state:X8}";
            Console.WriteLine($"  {ev}: OPENED - {verdict}");
            CloseHandle(hev);
        }

        if (opened.Count == 0)
        {
            // === 2. nothing by name - enumerate EVERY named mapping, under a time budget ===
            Console.WriteLine();
            Console.WriteLine("=== 2. no documented name opened - scanning for every named file-mapping ===");
            Console.WriteLine("     (budgeted: the scan stops after " + EnumBudgetSeconds + " s even if incomplete)");
            EnumerationResult enumerated;
            try
            {
                enumerated = HandleEnumerator.Enumerate(EnumBudgetSeconds,
                    (scanned, found) => Console.WriteLine(
                        $"    ... scanning: {scanned:,} handles, {found} named sections so far"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  enumeration failed: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }

            var regions = enumerated.Regions;
            sectionsFound = regions.Count;
            scanNames = enumerated.AllEntries.Select(e => e.Name).ToArray();
            Console.WriteLine($"  found {sectionsFound} named sections" +
                              (enumerated.Truncated
                                  ? "  (TRUNCATED - the time budget ran out; the listing is partial)"
                                  : ""));
            foreach (var r in regions.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    {EscapeName(r.Name)}   " +
                                  $"{(r.Size == 0 ? "size unknown" : r.Size.ToString("N0") + " bytes")}");
            }

            var gameEntries = enumerated.AllEntries
                .Where(e => e.Name.Contains("LMU", StringComparison.OrdinalIgnoreCase))
                .Select(e => $"{EscapeName(e.Name)} ({e.Root})").ToList();
            Console.WriteLine(gameEntries.Count == 0
                ? "  (no enumerated object name contains \"the game\")"
                : "  the game-looking objects: " + string.Join(", ", gameEntries));

            Console.WriteLine();
            Console.WriteLine("=== 3. retrying with the enumerated names ===");
            opened = TryOpenNames(regions.Select(r => r.Name)
                .Where(n => !n.Equals("Extended", StringComparison.OrdinalIgnoreCase)).ToList(),
                needed, probeSession);
        }
        else
        {
            Console.WriteLine("  (the block opened by name, so the system-wide scan is skipped -");
            Console.WriteLine("   it runs only when no documented name opens)");
        }

        // The by-name opens fail with Win32 2 for every the game object despite the namespace
        // listing them - so the last route is the game's OWN handle table: duplicated
        // handles carry the game's access and need no name resolution at all.
        if (opened.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== 4. the game's own handle table (bypasses name resolution) ===");
            uint gamePid = GameHandleProbe.FindGamePid(scanNames);
            if (gamePid == 0)
                Console.WriteLine("  (no game process found - is Le Mans Ultimate running?)");
            else
                opened = GameHandleProbe.DuplicateFromPid(gamePid);
        }

        if (opened.Count == 0 && watch)
        {
            // The user may start the probe before the game. Keep trying the documented names
            // for the whole watch window instead of giving up, so the ORDER of starting the
            // two programs stops mattering.
            Console.WriteLine();
            Console.WriteLine($"  none yet - and --watch is set, so the probe keeps trying the documented");
            Console.WriteLine($"  names every 2 s for up to {seconds} s. Start the game and get into a");
            Console.WriteLine($"  session; the block is picked up the moment it appears.");
            var waiter = Stopwatch.StartNew();
            double lastNote = 0;
            while (opened.Count == 0 && waiter.Elapsed.TotalSeconds < (double)seconds)
            {
                Thread.Sleep(2000);
                if (waiter.Elapsed.TotalSeconds - lastNote >= 10)
                {
                    int gamePresent = HandleEnumerator.Enumerate(5, null).AllEntries
                        .Count(e => e.Name.Contains("LMU", StringComparison.OrdinalIgnoreCase));
                    Console.WriteLine($"  [{waiter.Elapsed.TotalSeconds,3:F0}s] still waiting - " +
                                      $"the game-named objects in the namespace right now: {gamePresent}");
                    lastNote = waiter.Elapsed.TotalSeconds;
                }
                opened = TryOpenNames(order, needed, probeSession);
            }
        }

        if (opened.Count == 0)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine();
            Console.WriteLine("  No shared-memory block opened under any name.");
            Console.WriteLine($"    last error {err}: {new System.ComponentModel.Win32Exception(err).Message}");
            Console.WriteLine();
            Console.WriteLine("  Things to check:");
            Console.WriteLine("    1. is the game running and in a SESSION? The official LMU_Data block is");
            Console.WriteLine("       created by the game's core application, so the game must be up.");
            Console.WriteLine("    2. elevated?  Windows hides other processes' shared memory otherwise.");
            Console.WriteLine($"    3. if the run found {sectionsFound} named sections but none opened,");
            Console.WriteLine("       that is the elevation/ACL problem, not \"nothing is published\".");
            Console.WriteLine("    4. (community plugin only) CustomPluginVariables.JSON needs \"Enabled\": 1");
            Console.WriteLine("       for LMU_SharedMemoryMapPlugin64.dll, loaded in a session.");
            Console.WriteLine();
            Console.WriteLine("  If step 1 listed sections but none opened, that means this process");
            Console.WriteLine("  cannot see them - almost always the elevation problem.");
            return 1;
        }

        // Decode the first block big enough to hold the struct; report on any that is not.
        var target = opened.FirstOrDefault(o => o.Size >= (nuint)needed);
        if (target.View == 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Nothing was large enough for the {needed}-byte struct:");
            foreach (var (nm, _, sz) in opened)
                Console.WriteLine($"    {nm}: {sz:N0} bytes");
            Console.WriteLine();
            Console.WriteLine("  Send this listing. The sizes are what identify the layout.");
            foreach (var (nm, v, _) in opened) UnmapViewOfFile((IntPtr)(ulong)v);
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine($"  decoding \"{target.Name}\" ({target.Size:N0} bytes)");
        Console.WriteLine();

        // Compare by name, not ReferenceEquals: these are value tuples, so ReferenceEquals
        // boxes and is always false, which would have listed the block we just decoded as
        // one of the "others" - including unmapping it out from under us.
        var others = opened.Where(o => !string.Equals(o.Name, target.Name,
            StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
        {
            Console.WriteLine("  other sections this process can open, for reference:");
            foreach (var (nm, v, sz) in others)
            {
                Console.WriteLine($"    {nm,-44} {sz,12:N0} B");
                UnmapViewOfFile((IntPtr)(ulong)v);
            }
            Console.WriteLine();
        }

        IntPtr watchView = (IntPtr)(ulong)target.View;
        bool official = IsOfficial(target.Name);

        // The official prefix's mOptionsLocation and events[] did not move during the user's
        // runs even with the car on track, and the community block's session flags neither -
        // so instead of transcribing deep telemetry offsets blind, the watch latches EVERY
        // byte of LMU_Data and reports only the ones that flip and hold for 5 s: telemetry
        // churn is transient and silent, a session-state flag holds and gets reported. The
        // offsets it finds are then named from the header.
        FlipLatch? latch = null;
        long scoringBase = -1;      // ScoringBase once the player-name anchor is seen
        double lastLocate = -10;    // re-check the anchor every 10 s until it appears
        if (official)
        {
            latch = new FlipLatch(checked((int)target.Size));
            Console.WriteLine("  whole-block flip latch armed: any offset that leaves the baseline");
            Console.WriteLine("  for 5 s (or returns to it from a held state) is reported by offset.");
            if (PlayerNameAnchorOk(watchView))
            {
                scoringBase = ScoringBase;
                Console.WriteLine($"  scoring section at the pinned base 0x{ScoringBase:X} - player \"{ScoringPlayerName(watchView)}\" (anchor OK)");
            }
            else
            {
                Console.WriteLine($"  scoring anchor not visible yet at 0x{ScoringBase:X} (the player name");
                Console.WriteLine("  appears once a session loads; re-checked every 10 s).");
            }
        }

        Console.WriteLine($"  layout: {(official
            ? "OFFICIAL Studio 397 (LMU_Data prefix)"
            : "community plugin (LMU_Extended)")}");
        Console.WriteLine($"=== 4. watching {target.Name} for {seconds}s ===");
        Console.WriteLine("  changes print as they happen; a heartbeat every 10 s shows it is");
        Console.WriteLine("  still alive. While you drive, expect little or no change - the signal");
        Console.WriteLine("  to catch is the RETURN TO THE GARAGE, not the driving itself.");
        var sw = Stopwatch.StartNew();
        var previous = new List<(string, string)>();
        double lastBeat = 0;
        double lastFailureAt = -5;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double t = sw.Elapsed.TotalSeconds;
            var now = ReadFields(watchView, official);
            if (now is not null && official)
            {
                if (scoringBase < 0 && t - lastLocate >= 10)
                {
                    lastLocate = t;
                    if (PlayerNameAnchorOk(watchView))
                    {
                        scoringBase = ScoringBase;
                        Console.WriteLine($"  scoring anchor appeared at 0x{ScoringBase:X} - player \"{ScoringPlayerName(watchView)}\"");
                    }
                }
                // The named session-state fields, read DIRECTLY every tick - unlike the
                // flip latch they report even sub-5-second changes. The base is pinned
                // empirically (player-name anchor), the offsets are header-declared.
                if (scoringBase >= 0)
                {
                    now.Add(("session.vehicles",
                        Marshal.ReadInt32(watchView, checked((int)scoringBase) + ScoringNumVehiclesOffset).ToString()));
                    now.Add(("session.phase", PhaseName(Marshal.ReadByte(watchView, checked((int)scoringBase) + ScoringGamePhaseOffset))));
                    now.Add(("session.inRealtime",
                        Marshal.ReadByte(watchView, checked((int)scoringBase) + ScoringInRealtimeOffset) != 0 ? "True" : "False"));
                }
            }
            if (now is null)
            {
                if (t - lastFailureAt >= 5)
                {
                    Console.WriteLine($"[{t,6:F1}s] could not read the block - it may have just been taken away");
                    lastFailureAt = t;
                }
                Thread.Sleep(500);
                continue;
            }

            var changed = Diff(previous, now);
            if (changed.Count > 0)
            {
                Console.WriteLine($"[{t,6:F1}s] {(previous.Count == 0 ? "INITIAL STATE" : "changed")}");
                foreach (var (k, v) in changed)
                    Console.WriteLine($"      {k,-28} {v}");
                previous = now;
                lastBeat = t;
            }
            else if (t - lastBeat >= 10)
            {
                Console.WriteLine($"[{t,6:F1}s] alive - {Summary(watchView, official, scoringBase)}" +
                                              (latch is null ? "" : $"  held-flips={latch.Flips}"));
                lastBeat = t;
            }
            latch?.Tick(t, watchView, (off, from, to) =>
            {
                string known = off switch
                {
                    var o when o < OfficialLayout.GameVersionOffset => $"events[{o / 4}]",
                    OfficialLayout.GameVersionOffset => "gameVersion",
                    OfficialLayout.FfbTorqueOffset => "FFBTorque",
                    OfficialLayout.OptionsLocationOffset => "app.mOptionsLocation",
                    _ => "?",
                };
                Console.WriteLine($"      [flip] 0x{off:X4} ({known}): 0x{from:X2} -> 0x{to:X2}  (held 5 s)");
            }, _ => { });
            Thread.Sleep(500);
        }

        Console.WriteLine();
        Console.WriteLine("=== final state ===");
        int rc = Report(watchView, target.Name, official);
        if (latch is not null)
        {
            Console.WriteLine();
            if (scoringBase >= 0)
            {
                Console.WriteLine($"  session-state at the end (base 0x{scoringBase:X}, player \"{ScoringPlayerName(watchView)}\"):");
                Console.WriteLine($"    mNumVehicles   {Marshal.ReadInt32(watchView, checked((int)scoringBase) + ScoringNumVehiclesOffset)}" +
                                  $"   mGamePhase {PhaseName(Marshal.ReadByte(watchView, checked((int)scoringBase) + ScoringGamePhaseOffset))}" +
                                  $"   mInRealtime {Marshal.ReadByte(watchView, checked((int)scoringBase) + ScoringInRealtimeOffset) != 0}");
            }
            else
            {
                Console.WriteLine("  session-state: scoring section was never located in this run");
            }
            Console.WriteLine($"  persistent flips during the watch: {latch.Flips}");
            Console.WriteLine("  (each [flip] line above is an offset this game build actually moves on");
            Console.WriteLine("   a session boundary - the header names those offsets)");
        }
        // Only the watched view is still mapped here: the "other sections" above were
        // already unmapped in their loop, and unmapping them again at exit crashed the
        // probe (double-unmap, fatal 0x80131506) right after the final state printed.
        UnmapViewOfFile((IntPtr)(ulong)target.View);
        return rc;
    }

    private static List<(string Name, nuint View, nuint Size)> TryOpenNames(
        IReadOnlyList<string> names, int needed, uint sessionId)
    {
        var opened = new List<(string Name, nuint View, nuint Size)>();
        foreach (string nm in names)
        {
            nuint h = OpenFileMappingW(FILE_MAP_READ, false, nm);
            if (h == 0)
            {
                // The sections of the the game interface exist but refused to open on the user's
                // machine. Every refusal matters for the diagnosis: the Win32 code separates
                // "access denied" (DACL) from "does not exist" (name/session mismatch).
                if (nm.Contains("LMU", StringComparison.OrdinalIgnoreCase))
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"  {nm}: refused - Win32 {err} " +
                                      $"({new System.ComponentModel.Win32Exception(err).Message})");
                    // The namespace scan lists these objects, so a NOT FOUND here means the
                    // by-name open looks somewhere else: test the Global\ and Local\ prefixes
                    // and the probe's own session root explicitly.
                    if (err == 2)
                    {
                        var variants = new[]
                        {
                            $"Global\\{nm}", $"Local\\{nm}",
                            $"\\Sessions\\{sessionId}\\BaseNamedObjects\\{nm}",
                        };
                        foreach (string variant in variants)
                        {
                            nuint hv = OpenFileMappingW(FILE_MAP_READ, false, variant);
                            if (hv == 0)
                            {
                                int verr = Marshal.GetLastWin32Error();
                                Console.WriteLine($"      {variant}: Win32 {verr}");
                                continue;
                            }
                            nuint vv = MapViewOfFile(hv, FILE_MAP_READ, 0, 0, 0);
                            nuint vsz = 0;
                            if (vv != 0)
                            {
                                if (VirtualQuery((IntPtr)(ulong)vv, out var vmbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
                                    vsz = vmbi.RegionSize;
                            }
                            string logical = variant.Substring(variant.LastIndexOf('\\') + 1);
                            Console.WriteLine($"      {variant}: OPENED, {vsz:N0} bytes (recorded as '{logical}')");
                            opened.Add((logical, vv, vsz));
                            break;
                        }
                    }
                }
                continue;
            }
            nuint v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, 0);
            if (v == 0) { Console.WriteLine($"  {nm}: opened but MapViewOfFile failed"); continue; }
            nuint sz = 0;
            if (VirtualQuery((IntPtr)(ulong)v, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
                sz = mbi.RegionSize;
            Console.WriteLine($"  {nm}: opened, {sz:N0} bytes" +
                              (sz < (nuint)needed ? $"   (smaller than the {needed} needed)" : ""));
            opened.Add((nm, v, sz));
        }
        return opened;
    }

    private static string EscapeName(string s)
    {
        // The namespace may contain names with invisible characters (trailing spaces,
        // control characters) that would defeat any by-name open while LOOKING identical.
        // Print those escaped so the file shows exactly what the kernel reported.
        bool needsEscaping = s.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c > 0x7F);
        if (!needsEscaping) return s;
        return string.Concat(s.Select(c =>
            c <= 0x7F && !char.IsWhiteSpace(c) ? c.ToString() : $@"\x{(int)c:X2}"));
    }

    private static bool IsOfficial(string name) =>
        name.Equals(OfficialLayout.SharedMemoryName, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("\\" + OfficialLayout.SharedMemoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The whole field list for whichever block is being watched.</summary>
    private static List<(string, string)>? ReadFields(IntPtr view, bool official) =>
        official
            ? OfficialReader.TryRead(view, OfficialLayout.MinPrefixLength)?.AllFields.ToList()
            : TryRead(view)?.AllFields.ToList();

    /// <summary>
    /// A one-line summary of the decisive fields, for the watch heartbeat. The point of the
    /// heartbeat is that silence is ambiguous — it must read as "alive and unchanged", not as
    /// "not working", which is exactly the confusion a quiet watch run creates.
    /// </summary>
    private static string Summary(IntPtr view, bool official, long scoringBase)
    {
        if (!official)
        {
            var s = TryRead(view);
            return s is null ? "could not read" :
                $"mInRealtimeFC={s.InRealtime}  mSessionStarted={s.SessionStarted}";
        }
        var o = OfficialReader.TryRead(view, OfficialLayout.MinPrefixLength);
        string session = scoringBase < 0
            ? "  session: (scoring section not located yet)"
            : $"  session: vehicles={Marshal.ReadInt32(view, checked((int)scoringBase) + ScoringNumVehiclesOffset)}  " +
              $"phase={PhaseName(Marshal.ReadByte(view, checked((int)scoringBase) + ScoringGamePhaseOffset))}  " +
              $"inRealtime={Marshal.ReadByte(view, checked((int)scoringBase) + ScoringInRealtimeOffset) != 0}";
        return o is null ? "could not read" :
            $"mOptionsLocation={o.OptionsLocation} ({o.Location})  " +
            $"END_SESSION raised={o.SessionEnded}  IN_REALTIME={o.InRealtime}{session}";
    }

    /// <summary>The documented ScoringInfoV01.mGamePhase values.</summary>
    internal static string PhaseName(int p) => p switch
    {
        0 => "0 before-session",
        1 => "1 recon",
        2 => "2 grid-walk",
        3 => "3 formation",
        4 => "4 start-lights",
        5 => "5 green",
        6 => "6 yellow",
        7 => "7 stopped",
        8 => "8 session-over",
        9 => "9 paused",
        _ => p.ToString(),
    };

    /// <summary>
    /// The scoring section base, pinned EMPIRICALLY from the user's probe runs...
    /// </summary>
    internal static long PinnedScoringBaseForTests => ScoringBase;

    /// <summary>
    /// The official block's report. Unlike the community struct there is no version string
    /// to validate against, so the evidence is the event queue and the application state
    /// changing in the way a session does.
    /// </summary>
    private static int ReportOfficial(IntPtr view, string name)
    {
        OfficialState st;
        try
        {
            st = OfficialReader.Read(view, OfficialLayout.MinPrefixLength);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  could not decode: {ex.Message}");
            return 3;
        }

        Console.WriteLine($"=== decoded the OFFICIAL session state from \"{name}\" ===");
        Console.WriteLine($"  gameVersion        {st.GameVersion}");
        Console.WriteLine($"  FFBTorque          {st.FfbTorque:G6}");
        Console.WriteLine();
        Console.WriteLine("  events[] - the game's own event queue, all 16 slots:");
        for (int i = 0; i < st.Events.Length; i++)
            Console.WriteLine($"    [{i,2}] {st.Events[i],10}  {OfficialLayout.EventName(st.Events[i])}");
        Console.WriteLine();
        Console.WriteLine($"  END_SESSION (7) raised  {st.SessionEnded}");
        Console.WriteLine($"  ENTER_REALTIME (8) raised {st.InRealtime}   <- the car is drivable");
        Console.WriteLine();
        Console.WriteLine("  application state:");
        Console.WriteLine($"    mOptionsLocation   {st.OptionsLocation} ({st.Location})   <- 3 is on track");
        Console.WriteLine($"    mOptionsPage       \"{st.OptionsPage}\"");
        Console.WriteLine($"    mWidth x mHeight   {st.Width} x {st.Height} @ {st.RefreshRate} Hz");
        Console.WriteLine($"    mWindowed          {st.Windowed}");
        Console.WriteLine($"    mAppWindow         0x{st.AppWindow:X}");
        Console.WriteLine();
        Console.WriteLine("  every prefix field, as read:");
        foreach (var (fieldName, value) in st.AllFields)
            Console.WriteLine($"    {fieldName,-30} {value}");

        Console.WriteLine();
        Console.WriteLine("  What to look for while a session ends:");
        Console.WriteLine("    * a slot of events[] taking 7 (END_SESSION), or 8/9 (ENTER/EXIT_REALTIME),");
        Console.WriteLine("      at the moment you return to the garage");
        Console.WriteLine("    * mOptionsLocation leaving 3 (on track) for 0 (main UI) or 2 (monitor)");
        Console.WriteLine("    * whether the slots hold their own index or a rolling set - the header");
        Console.WriteLine("      does not document the discipline, so a real run is what settles it");
        return 0;
    }

    private static bool LooksLikeGameObject(string name) =>
        name.Contains("Extended", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("rFactor2SMM", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("LMU", StringComparison.OrdinalIgnoreCase);

    private static SessionState? TryRead(IntPtr view)
    {
        try
        {
            return ExtendedReader.Read(view,
                ExtendedLayout.StructOffset + ExtendedLayout.StructSize);
        }
        catch
        {
            return null;
        }
    }

    private static List<(string, string)> Diff(
        List<(string, string)> before, List<(string, string)> after)
    {
        var b = before.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var changed = new List<(string, string)>();
        foreach (var (k, v) in after)
        {
            if (!b.TryGetValue(k, out string? old) || old != v) changed.Add((k, v));
        }
        return changed;
    }

    private static int Report(IntPtr view, string name, bool official)
    {
        if (official) return ReportOfficial(view, name);

        SessionState st;
        try
        {
            st = ExtendedReader.Read(view,
                ExtendedLayout.StructOffset + ExtendedLayout.StructSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  could not decode: {ex.Message}");
            return 3;
        }

        Console.WriteLine($"=== decoded session state from \"{name}\" ===");
        Console.WriteLine($"  version            \"{st.Version}\"   " +
                          $"{(st.VersionLooksRight ? "looks right" : "UNEXPECTED")}");
        Console.WriteLine($"  version block      begin={st.VersionBegin} end={st.VersionEnd}  " +
                          $"{(st.Torn ? "TORN FRAME - being written now" : "consistent")}");
        Console.WriteLine($"  is64bit            {st.Is64Bit}");
        Console.WriteLine($"  mInRealtimeFC      {st.InRealtime}      <- in realtime, the car is drivable");
        Console.WriteLine($"  mSessionStarted    {st.SessionStarted}    <- a session is live");
        Console.WriteLine($"  session start tick {st.SessionStartedTicks}");
        Console.WriteLine($"  session end tick   {st.SessionEndedTicks}");
        Console.WriteLine($"  direct mem access  {st.DirectMemoryAccess}");
        Console.WriteLine($"  unsub buffers mask {st.UnsubscribedBuffersMask}");
        Console.WriteLine($"  fuel               {st.CurrentFuel:F2} / {st.MaxFuel:F2}");
        Console.WriteLine($"  energy             {st.CurrentEnergy:F2} / {st.MaxEnergy:F2}");

        Console.WriteLine();
        Console.WriteLine("  every field, as read:");
        foreach (var (fieldName, value) in st.AllFields)
            Console.WriteLine($"    {fieldName,-30} {value}");

        Console.WriteLine();
        bool layoutOk = st.VersionLooksRight && !st.Torn;
        Console.WriteLine(layoutOk
            ? "  VERDICT: the layout matches. This is usable for auto mode."
            : "  VERDICT: layout or state is unexpected - send the output above.");

        Console.WriteLine();
        Console.WriteLine("  NOTE: this struct carries no telemetry channels. It carries session");
        Console.WriteLine("  state, which is what tells the converter when a session ended.");
        return layoutOk ? 0 : 3;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}