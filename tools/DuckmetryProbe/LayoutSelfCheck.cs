// A self-check for the layout arithmetic, runnable on any platform - including Linux,
// where the probe itself cannot run.
//
// The offsets were transcribed from the community plugin's LMU_State.h. If pack(4) or the
// field order is misread, the probe would report plausible but wrong numbers, which is
// exactly the failure mode this exists to rule out. So the offsets are checked here against
// hand-computed values, and against a synthetic struct built by writing known values at
// those offsets and reading them back.
//
// Run with:  DuckmetryProbe.exe --selfcheck

using System.Buffers.Binary;
using System.Text;

namespace DuckmetryProbe;

internal static class LayoutSelfCheck
{
    public static int Run()
    {
        int fails = 0;

        void Check(bool ok, string what, string? detail = null)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}" +
                              (detail is null ? "" : $" -- {detail}"));
            if (!ok) fails++;
        }

        Console.WriteLine("Extended-block layout self-check");
        Console.WriteLine($"  sizeof(extended block) computed = {ExtendedLayout.StructSize}");
        Console.WriteLine($"  fields declared                = {ExtendedLayout.Fields.Count}");
        Console.WriteLine();

        Console.WriteLine("=== offsets hand-computed from the field list under pack(4) ===");
        (int Offset, string Name)[] expected =
        {
            (0, "mVersion"),                    // char[12]
            (12, "is64bit"),                    // bool
            (13, "mInRealtimeFC"),              // bool
            (14, "mSessionStarted"),            // bool, then pad to 16
            (16, "mTicksSessionStarted"),       // ulong
            (24, "mTicksSessionEnded"),         // ulong
            (32, "mDirectMemoryAccessEnabled"), // bool, then pad to 36
            (36, "mUnsubscribedBuffersMask"),   // int
            (40, "mpBrakeMigration"),
            (44, "mpBrakeMigrationMax"),
            (48, "mpTractionControl"),
            (52, "mpMotorMap"),                 // char[16]
            (68, "mChangedParamType"),
            (72, "mChangedParamValue"),         // char[16]
            (88, "mFront_ABR"),
            (92, "mRear_ABR"),
            (96, "mPenaltyType"),
            (100, "mPenaltyCount"),
            (104, "mPenaltyLeftLaps"),
            (108, "mPendingPenaltyType1"),
            (112, "mPendingPenaltyType2"),
            (116, "mPendingPenaltyType3"),
            (120, "mCuts"),                     // float
            (124, "mCutsPoints"),
            (128, "mCurrentBatteryValue"),      // double
            (136, "mMaxBatteryValue"),
            (144, "mCurrentEnergyValue"),
            (152, "mMaxEnergyValue"),
            (160, "mCurrentFuelValue"),
            (168, "mMaxFuelValue"),
            (176, "mEnergyLastLap"),            // float
            (180, "mFuelLastLap"),
        };

        foreach (var (offset, name) in expected)
        {
            int actual = ExtendedLayout.OffsetOf(name);
            Check(actual == offset, $"{name,-26} @ {offset}", $"got {actual}");
        }

        Console.WriteLine();
        Console.WriteLine("=== round-trip: write known values, read them back ===");
        const int structOffset = ExtendedLayout.StructOffset;
        int size = ExtendedLayout.StructSize;

        var mem = new byte[structOffset + size + 16];
        // A consistent version block: begin == end means not mid-write.
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(0), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(4), 7);

        int S(int off) => structOffset + off;

        Encoding.ASCII.GetBytes("4.0.16.6\0\0").CopyTo(mem, S(0));
        mem[S(12)] = 1;    // is64bit
        mem[S(13)] = 1;    // mInRealtimeFC
        mem[S(14)] = 1;    // mSessionStarted
        BinaryPrimitives.WriteUInt64LittleEndian(mem.AsSpan(S(16)), 1234567890123UL);
        BinaryPrimitives.WriteUInt64LittleEndian(mem.AsSpan(S(24)), 0UL);
        mem[S(32)] = 1;    // direct memory access
        BinaryPrimitives.WriteInt32LittleEndian(mem.AsSpan(S(36)), 160);
        BinaryPrimitives.WriteDoubleLittleEndian(mem.AsSpan(S(128)), 12.5);
        BinaryPrimitives.WriteDoubleLittleEndian(mem.AsSpan(S(160)), 44.25);

        unsafe
        {
            fixed (byte* p = mem)
            {
                var st = ExtendedReader.Read((IntPtr)p, mem.Length);

                Check(st.Version == "4.0.16.6", "version string reads back", st.Version);
                Check(st.VersionLooksRight, "version recognised as the plugin's");
                Check(!st.Torn, "version block consistent, so not a torn frame");
                Check(st.Is64Bit, "is64bit");
                Check(st.InRealtime, "mInRealtimeFC");
                Check(st.SessionStarted, "mSessionStarted");
                Check(st.SessionStartedTicks == 1234567890123UL, "mTicksSessionStarted",
                      st.SessionStartedTicks.ToString());
                Check(st.DirectMemoryAccess, "mDirectMemoryAccessEnabled");
                Check(st.UnsubscribedBuffersMask == 160, "mUnsubscribedBuffersMask",
                      st.UnsubscribedBuffersMask.ToString());
                // Doubles are checked through the reported field list, which is the same
                // path the probe prints, so this tests what a reader would actually see.
                // Array fields are reported as "name[16]", so match the prefix.
                string Field(string name) =>
                    st.AllFields.First(f =>
                        f.Name == name || f.Name.StartsWith(name + "[", StringComparison.Ordinal)).Item2;

                Check(Field("mCurrentBatteryValue") == "12.5",
                      "mCurrentBatteryValue", Field("mCurrentBatteryValue"));
                Check(Field("mCurrentFuelValue") == "44.25",
                      "mCurrentFuelValue", Field("mCurrentFuelValue"));
                Check(Field("mMaxFuelValue") == "0",
                      "mMaxFuelValue is zero when unset", Field("mMaxFuelValue"));
                Check(Field("mpMotorMap") == "",
                      "mpMotorMap reads as an empty string", $"'{Field("mpMotorMap")}'");

                // The version block's whole purpose: detect a frame mid-write.
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(4), 8);
                var torn = ExtendedReader.Read((IntPtr)p, mem.Length);
                Check(torn.Torn, "a half-written frame is reported as torn");
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(4), 7);

                // A short mapping must be refused, not misread.
                bool threw = false;
                try { ExtendedReader.Read((IntPtr)p, 10); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "a short mapping is refused rather than decoded");

                Check(st.AllFields.Count == ExtendedLayout.Fields.Count,
                      "every declared field is reported", st.AllFields.Count.ToString());
            }
        }

        fails += OfficialLayoutSelfCheck.Run();

        Console.WriteLine();
        Console.WriteLine($"  {(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} FAILED")}");
        return fails == 0 ? 0 : 1;
    }
}