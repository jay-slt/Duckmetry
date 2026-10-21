// Verifies the OFFICIAL layout constants in OfficialLayout.cs against the Studio 397
// headers themselves, rather than against a hand-computed table.
//
// The community layout (ExtendedLayout) can only be checked against arithmetic, because
// its source was a plugin's own header read from outside the repo. This one is better: the
// official headers ship in log+example/SharedMemoryInterface/, so the field order, the event
// list and the sizes can all be read and the offsets re-derived from them. If Studio 397
// changes the struct, this fails loudly instead of the probe printing plausible wrong numbers.
//
// Runs on any platform. If the headers are not found it reports SKIP and only the internal
// consistency checks run.

using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace DuckmetryProbe;

internal static class OfficialLayoutSelfCheck
{
    /// <summary>Runs the check, returning how many checks failed.</summary>
    public static int Run()
    {
        int fails = 0;

        void Check(bool ok, string what, string? detail = null)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}" +
                              (detail is null ? "" : $" -- {detail}"));
            if (!ok) fails++;
        }

        Console.WriteLine();
        Console.WriteLine("=== official LMU_Data layout (Studio 397 headers) ===");

        string? smiPath = FindUpwards("log+example/SharedMemoryInterface/SharedMemoryInterface.hpp");
        string? pluginPath = FindUpwards("log+example/SharedMemoryInterface/InternalsPlugin.hpp");
        if (smiPath is null || pluginPath is null)
        {
            Console.WriteLine("  [SKIP] the Studio 397 headers are not beside this build;");
            Console.WriteLine("         offsets cannot be re-derived from source here.");
            return CheckInternally(Check);
        }

        Console.WriteLine($"  SharedMemoryInterface.hpp  {smiPath}");
        Console.WriteLine($"  InternalsPlugin.hpp        {pluginPath}");
        Console.WriteLine();

        string smi = File.ReadAllText(smiPath);
        string plugin = File.ReadAllText(pluginPath);

        // ---- the event enum ---------------------------------------------------------
        var events = ParseEnumMembers(smi, "enum SharedMemoryEvent");
        Check(events.Count > 0, "parsed the SharedMemoryEvent enum", $"{events.Count} members");
        // The last member is SME_MAX, which is the count and not an event.
        var declared = events.Take(Math.Max(0, events.Count - 1)).ToArray();
        Check(declared.Length == OfficialLayout.EventSlots,
              $"enum holds {OfficialLayout.EventSlots} events before SME_MAX",
              $"header declares {declared.Length}");
        Check(declared.SequenceEqual(OfficialLayout.EventNames),
              "event names and order match the header",
              string.Join(",", declared));
        Check(events.LastOrDefault() == "MAX",
              "the enum ends with SME_MAX", events.LastOrDefault() ?? "(none)");
        Check(Array.IndexOf(OfficialLayout.EventNames, "END_SESSION") == OfficialLayout.EventEndSession,
              $"END_SESSION sits at {OfficialLayout.EventEndSession}");
        Check(Array.IndexOf(OfficialLayout.EventNames, "ENTER_REALTIME") == OfficialLayout.EventEnterRealtime,
              $"ENTER_REALTIME sits at {OfficialLayout.EventEnterRealtime}");

        // ---- ApplicationStateV01 ----------------------------------------------------
        var appFields = ParseStructFields(plugin, "struct ApplicationStateV01");
        Check(appFields.Count == 8, "parsed ApplicationStateV01", $"{appFields.Count} fields");

        var (appOffsets, appSize, appAlign) = ComputeLayout(appFields);
        Check(appAlign == 8, "ApplicationStateV01 is 8-aligned (it holds an HWND)", $"align {appAlign}");

        (int Offset, string Name)[] expectedApp =
        {
            (OfficialLayout.AppWindowOffset, "mAppWindow"),
            (OfficialLayout.WidthOffset, "mWidth"),
            (OfficialLayout.HeightOffset, "mHeight"),
            (OfficialLayout.RefreshRateOffset, "mRefreshRate"),
            (OfficialLayout.WindowedOffset, "mWindowed"),
            (OfficialLayout.OptionsLocationOffset, "mOptionsLocation"),
            (OfficialLayout.OptionsPageOffset, "mOptionsPage"),
            (OfficialLayout.ExpansionOffset, "mExpansion"),
        };
        // ComputeLayout returns offsets relative to the struct; the constants are absolute
        // within the mapping. The struct's own base is added back before comparing.
        foreach (var (offset, name) in expectedApp)
        {
            bool found = appOffsets.TryGetValue(name, out int relative);
            int absolute = found ? OfficialLayout.AppInfoOffset + relative : -1;
            Check(found && absolute == offset, $"{name,-26} @ {offset}",
                  found ? $"header gives {absolute}" : "not in the header");
        }
        Check(appSize == OfficialLayout.AppInfoSize,
              $"ApplicationStateV01 size {OfficialLayout.AppInfoSize}", $"header gives {appSize}");

        // ---- the prefix as a whole --------------------------------------------------
        // Names only here: `events` is typed with the enum's own name, which is not a type
        // this parser knows, and all this check needs is the declaration order.
        var genericNames = ParseFieldNames(smi, "struct SharedMemoryGeneric");
        Check(genericNames.Count == 4, "parsed SharedMemoryGeneric", $"{genericNames.Count} fields");
        Check(genericNames.SequenceEqual(new[] { "events", "gameVersion", "FFBTorque", "appInfo" }),
              "SharedMemoryGeneric order is events, gameVersion, FFBTorque, appInfo",
              string.Join(", ", genericNames));

        // Re-derive the prefix with appInfo treated as the struct we just measured.
        var prefix = new List<FieldSpec>
        {
            new("events", "uint32[]", OfficialLayout.EventBytes),
            new("gameVersion", "long", 4),
            new("FFBTorque", "float", 4),
            new("appInfo", "struct", appSize, appAlign),
        };
        var (prefixOffsets, prefixSize, _) = ComputeLayout(prefix);
        Check(prefixOffsets.GetValueOrDefault("events") == 0, "events[] is first (offset 0)");
        Check(prefixOffsets.GetValueOrDefault("gameVersion") == OfficialLayout.GameVersionOffset,
              $"gameVersion @ {OfficialLayout.GameVersionOffset}",
              $"re-derived {prefixOffsets.GetValueOrDefault("gameVersion")}");
        Check(prefixOffsets.GetValueOrDefault("FFBTorque") == OfficialLayout.FfbTorqueOffset,
              $"FFBTorque @ {OfficialLayout.FfbTorqueOffset}",
              $"re-derived {prefixOffsets.GetValueOrDefault("FFBTorque")}");
        Check(prefixOffsets.GetValueOrDefault("appInfo") == OfficialLayout.AppInfoOffset,
              $"appInfo @ {OfficialLayout.AppInfoOffset}",
              $"re-derived {prefixOffsets.GetValueOrDefault("appInfo")}");
        Check(prefixSize == OfficialLayout.GenericSize,
              $"SharedMemoryGeneric size {OfficialLayout.GenericSize}", $"re-derived {prefixSize}");
        Check(OfficialLayout.MinPrefixLength == OfficialLayout.OptionsLocationOffset + 1,
              "MinPrefixLength reaches exactly mOptionsLocation");

        return fails + CheckInternally(Check);
    }

    /// <summary>Checks that do not need the headers: the round trip and the sentinels.</summary>
    private static int CheckInternally(Action<bool, string, string?> check)
    {
        int fails = 0;
        void Check(bool ok, string what, string? detail = null)
        {
            check(ok, what, detail);
            if (!ok) fails++;
        }

        Console.WriteLine();
        Console.WriteLine("=== official prefix: write known values, read them back ===");

        var mem = new byte[OfficialLayout.GenericSize + 16];

        // events[]: put ENTER_REALTIME in slot 0 and END_SESSION in slot 1 - the two the
        // probe's verdict depends on.
        WriteU32(mem, 0, OfficialLayout.EventEnterRealtime);
        WriteU32(mem, 4, OfficialLayout.EventEndSession);
        WriteI32(mem, OfficialLayout.GameVersionOffset, 34);
        WriteF32(mem, OfficialLayout.FfbTorqueOffset, 1.25f);
        WriteI64(mem, OfficialLayout.AppWindowOffset, 0x1234);
        WriteU32(mem, OfficialLayout.WidthOffset, 2560);
        WriteU32(mem, OfficialLayout.HeightOffset, 1440);
        WriteU32(mem, OfficialLayout.RefreshRateOffset, 144);
        WriteU32(mem, OfficialLayout.WindowedOffset, 1);
        mem[OfficialLayout.OptionsLocationOffset] = 3;
        System.Text.Encoding.ASCII.GetBytes("monitor\0").CopyTo(mem, OfficialLayout.OptionsPageOffset);

        unsafe
        {
            fixed (byte* p = mem)
            {
                var st = OfficialReader.Read((IntPtr)p, mem.Length);
                Check(st.Events[0] == OfficialLayout.EventEnterRealtime, "events[0] round-trips",
                      st.Events[0].ToString());
                Check(st.Events[1] == OfficialLayout.EventEndSession, "events[1] round-trips",
                      st.Events[1].ToString());
                Check(st.InRealtime, "InRealtime reads true when ENTER_REALTIME is in the queue");
                Check(st.SessionEnded, "SessionEnded reads true when END_SESSION is in the queue");
                Check(st.GameVersion == 34, "gameVersion", st.GameVersion.ToString());
                Check(Math.Abs(st.FfbTorque - 1.25f) < 1e-6, "FFBTorque", st.FfbTorque.ToString("G6"));
                Check(st.Width == 2560 && st.Height == 1440, "width/height",
                      $"{st.Width}x{st.Height}");
                Check(st.OptionsLocation == 3 && st.Location == "ON TRACK",
                      "mOptionsLocation 3 reads as ON TRACK", st.Location);
                Check(st.OptionsPage == "monitor", "mOptionsPage", st.OptionsPage);
                Check(!st.Events.Contains(OfficialLayout.EventEndSession - 1),
                      "a slot left at 0 is reported as ENTER, not as END_SESSION");

                // A short mapping must be refused, not misread.
                bool threw = false;
                try { OfficialReader.Read((IntPtr)p, OfficialLayout.MinPrefixLength - 1); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "a mapping shorter than the prefix is refused rather than decoded");

                Check(st.AllFields.Count == OfficialLayout.EventSlots + 9,
                      "every prefix field is reported", st.AllFields.Count.ToString());
            }

            // The pinned scoring base (0x658, from the user's runs: "Test Driver" at
            // +124): plant the anchor and the session fields, and verify the probe's reads
            // line up exactly. An empty block must have no anchor.
            var big = new byte[327680];
            const int base_ = 0x658;
            System.Text.Encoding.ASCII.GetBytes("Test Driver").CopyTo(big, base_ + 124);
            WriteI32(big, base_ + 64, 3);                 // mSession: race
            WriteI64(big, base_ + 72, BitConverter.DoubleToInt64Bits(511.25)); // mCurrentET
            WriteI32(big, base_ + 88, 10);                // mMaxLaps
            WriteI64(big, base_ + 96, BitConverter.DoubleToInt64Bits(4898.0)); // mLapDist
            WriteI32(big, base_ + 112, 1);                // mNumVehicles
            big[base_ + 116] = 5;                         // mGamePhase: green
            big[base_ + 123] = 1;                         // mInRealtime
            fixed (byte* bp = big)
            {
                IntPtr v = (IntPtr)bp;
                Check(Program.PlayerNameAnchorOk(v),
                      "the pinned base (0x658) is recognised by the player-name anchor");
                Check(Program.ScoringPlayerName(v) == "Test Driver",
                      "the player name reads back at +124");
                Check(Marshal.ReadInt32(v, base_ + 112) == 1,
                      "mNumVehicles reads 1 at 0x6C8 (base+112)");
                Check(Marshal.ReadByte(v, base_ + 116) == 5 &&
                      Program.PhaseName(Marshal.ReadByte(v, base_ + 116)) == "5 green",
                      "mGamePhase reads green at 0x6CC (base+116)");
                Check(Marshal.ReadByte(v, base_ + 123) == 1,
                      "mInRealtime reads true at 0x6D3 (base+123)");
                var empty = new byte[327680];
                fixed (byte* ep = empty)
                {
                    Check(!Program.PlayerNameAnchorOk((IntPtr)ep),
                          "an empty block has no player-name anchor");
                }
            }
        }

        return fails;
    }

    private static void WriteU32(byte[] b, int off, uint v) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off, 4), v);
    private static void WriteI32(byte[] b, int off, int v) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off, 4), v);
    private static void WriteF32(byte[] b, int off, float v) =>
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(off, 4), v);
    private static void WriteI64(byte[] b, int off, long v) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(off, 8), v);

    /// <summary>A field as declared, before layout.</summary>
    private readonly record struct FieldSpec(string Name, string Type, int Size, int Align = -1);

    /// <summary>Walks up from the build output (and the cwd) looking for a repo-relative path.</summary>
    private static string? FindUpwards(string relative)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }

    /// <summary>Members of a C++ enum, with the SME_ prefix stripped.</summary>
    private static List<string> ParseEnumMembers(string text, string declaration)
    {
        int start = text.IndexOf(declaration, StringComparison.Ordinal);
        if (start < 0) return new List<string>();
        int open = text.IndexOf('{', start);
        int close = text.IndexOf("};", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) return new List<string>();
        return text[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.StartsWith("SME_", StringComparison.Ordinal) ? s[4..] : s)
            .ToList();
    }

    private static readonly Dictionary<string, (int Size, int Align)> TypeMap = new()
    {
        ["char"] = (1, 1), ["signed char"] = (1, 1), ["unsigned char"] = (1, 1),
        ["bool"] = (1, 1),
        ["short"] = (2, 2), ["unsigned short"] = (2, 2),
        ["int"] = (4, 4), ["unsigned int"] = (4, 4), ["long"] = (4, 4),
        ["unsigned long"] = (4, 4), ["float"] = (4, 4),
        ["double"] = (8, 8), ["ulong"] = (8, 8), ["unsigned long long"] = (8, 8),
        ["size_t"] = (8, 8), ["HWND"] = (8, 8),
    };

    /// <summary>Just the member names of a C++ struct, in declaration order.</summary>
    private static List<string> ParseFieldNames(string text, string declaration)
    {
        var result = new List<string>();
        int start = text.IndexOf(declaration, StringComparison.Ordinal);
        if (start < 0) return result;
        int open = text.IndexOf('{', start);
        int close = text.IndexOf("};", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) return result;

        var body = Regex.Replace(text[(open + 1)..close], @"//[^\n]*", "");
        foreach (Match m in Regex.Matches(body,
                     @"(?m)^\s*[A-Za-z_][A-Za-z0-9_: ]*?\s+(?<name>[A-Za-z_]\w*)\s*" +
                     @"(?:\[\s*[^\]]*\s*\])?\s*;"))
        {
            result.Add(m.Groups["name"].Value);
        }
        return result;
    }

    /// <summary>The fields of a C++ struct, in declaration order.</summary>
    private static List<FieldSpec> ParseStructFields(string text, string declaration)
    {
        var result = new List<FieldSpec>();
        int start = text.IndexOf(declaration, StringComparison.Ordinal);
        if (start < 0) return result;
        int open = text.IndexOf('{', start);
        int close = text.IndexOf("};", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) return result;

        // Drop the trailing comment before parsing, since members are declared one per line.
        var body = Regex.Replace(text[(open + 1)..close], @"//[^\n]*", "");
        foreach (Match m in Regex.Matches(body,
                     @"(?m)^\s*(?<type>[A-Za-z_][A-Za-z0-9_ ]*?)\s+(?<name>[A-Za-z_]\w*)\s*" +
                     @"(?:\[\s*(?<count>\d+)\s*\])?\s*;"))
        {
            string type = m.Groups["type"].Value.Trim();
            string name = m.Groups["name"].Value;
            int count = m.Groups["count"].Success ? int.Parse(m.Groups["count"].Value) : 1;
            if (!TypeMap.TryGetValue(type, out var ta)) continue;
            result.Add(new FieldSpec(name, type, ta.Size * count, ta.Align));
        }
        return result;
    }

    /// <summary>Natural-alignment layout, the same rules the C++ compiler applies.</summary>
    private static (Dictionary<string, int> Offsets, int Size, int Align) ComputeLayout(
        IReadOnlyList<FieldSpec> fields)
    {
        var offsets = new Dictionary<string, int>();
        int offset = 0, maxAlign = 1;
        foreach (var f in fields)
        {
            int align = f.Align > 0 ? f.Align : 1;
            if (offset % align != 0) offset += align - (offset % align);
            offsets[f.Name] = offset;
            offset += f.Size;
            maxAlign = Math.Max(maxAlign, align);
        }
        if (offset % maxAlign != 0) offset += maxAlign - (offset % maxAlign);
        return (offsets, offset, maxAlign);
    }
}
