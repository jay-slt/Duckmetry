// The LMU_Extended layout, transcribed from the community plugin's LMU_State.h with its
// #pragma pack(4), and its interpretation.
//
// This is the session-state struct the shared-memory plugin publishes. It carries NO
// telemetry channels - not gear, not RPM, not position. What it does carry is exactly what
// the converter's auto mode needs and the DuckDB cannot provide: whether a session is live,
// when it started, and whether the game is still in it.
//
// Offsets are computed from the field list rather than hand-counted, so a change to the
// list shows up as a change here rather than as silently wrong numbers.

using System.Runtime.InteropServices;
using System.Text;

namespace DuckmetryProbe;

/// <summary>One field of the mapped struct.</summary>
internal sealed record Field(int Offset, string Type, string Name, int Size)
{
    public override string ToString() => $"0x{Offset:X4}  {Type,-9} {Name}";
}

/// <summary>
/// The mapped block: a version block for torn-frame detection, then LMU_Extended.
/// </summary>
/// <remarks>
/// The plugin maps the block with a size of
/// <c>sizeof(LMU_MappedBufferVersionBlock) + sizeof(LMU_Extended)</c>, and its read path
/// casts the base of the view to the version block and asserts the struct follows
/// immediately. So the layout is: two unsigned longs, then the struct at offset 8.
/// </remarks>
internal static class ExtendedLayout
{
    /// <summary>mVersionUpdateBegin / mVersionUpdateEnd, both unsigned long (4-byte pack).</summary>
    public const int VersionBlockSize = 8;

    /// <summary>Where LMU_Extended begins within the mapped view.</summary>
    public const int StructOffset = VersionBlockSize;

    /// <summary>
    /// The fields, in declaration order, from LMU_State.h. Types matter for offsets:
    /// under pack(4) an 8-byte type still aligns to 8, and a 1-byte bool is followed by
    /// padding before the next aligned member.
    /// </summary>
    public static readonly IReadOnlyList<Field> Fields = BuildFields();

    public const int StructSize = 184;   // sum of the fields below

    private static IReadOnlyList<Field> BuildFields()
    {
        (string Type, string Name, int Count)[] spec =
        {
            ("char",       "mVersion",                       12),
            ("bool",       "is64bit",                         1),
            ("bool",       "mInRealtimeFC",                   1),
            ("bool",       "mSessionStarted",                 1),
            ("ulong",      "mTicksSessionStarted",            1),
            ("ulong",      "mTicksSessionEnded",              1),
            ("bool",       "mDirectMemoryAccessEnabled",      1),
            ("int",        "mUnsubscribedBuffersMask",        1),
            ("int",        "mpBrakeMigration",                1),
            ("int",        "mpBrakeMigrationMax",             1),
            ("int",        "mpTractionControl",               1),
            ("char",       "mpMotorMap",                     16),
            ("int",        "mChangedParamType",               1),
            ("char",       "mChangedParamValue",             16),
            ("int",        "mFront_ABR",                      1),
            ("int",        "mRear_ABR",                       1),
            ("int",        "mPenaltyType",                    1),
            ("int",        "mPenaltyCount",                   1),
            ("int",        "mPenaltyLeftLaps",                1),
            ("int",        "mPendingPenaltyType1",            1),
            ("int",        "mPendingPenaltyType2",            1),
            ("int",        "mPendingPenaltyType3",            1),
            ("float",      "mCuts",                           1),
            ("int",        "mCutsPoints",                     1),
            ("double",     "mCurrentBatteryValue",            1),
            ("double",     "mMaxBatteryValue",                1),
            ("double",     "mCurrentEnergyValue",             1),
            ("double",     "mMaxEnergyValue",                 1),
            ("double",     "mCurrentFuelValue",               1),
            ("double",     "mMaxFuelValue",                   1),
            ("float",      "mEnergyLastLap",                  1),
            ("float",      "mFuelLastLap",                    1),
        };

        static int SizeOf(string t, int count) => t switch
        {
            "char" => count,
            "bool" => 1,
            "int" => 4,
            "float" => 4,
            "double" => 8,
            "ulong" => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(t), t, "unknown type")
        };

        static int AlignOf(string t) => t switch
        {
            "char" or "bool" => 1,
            "int" or "float" => 4,
            _ => 8      // 8-byte types keep their natural alignment under pack(4)
        };

        var fields = new List<Field>();
        int offset = 0;
        foreach (var (type, name, count) in spec)
        {
            int align = AlignOf(type);
            if (offset % align != 0) offset += align - (offset % align);
            int size = SizeOf(type, count);
            fields.Add(new Field(offset, type, name + (count > 1 ? $"[{count}]" : ""), size));
            offset += size;
        }
        return fields;
    }

    public static int OffsetOf(string name)
    {
        var f = Fields.FirstOrDefault(x => x.Name == name || x.Name.StartsWith(name + "["));
        if (f is null) throw new ArgumentException($"no field named {name}", nameof(name));
        return f.Offset;
    }

    public static int SizeOf(string name) => Fields
        .First(x => x.Name == name || x.Name.StartsWith(name + "[")).Size;
}

/// <summary>A read of the mapped block, decoded into the fields the converter cares about.</summary>
internal sealed record SessionState(
    uint VersionBegin,
    uint VersionEnd,
    string Version,
    bool Is64Bit,
    bool InRealtime,
    bool SessionStarted,
    ulong SessionStartedTicks,
    ulong SessionEndedTicks,
    bool DirectMemoryAccess,
    int UnsubscribedBuffersMask,
    double CurrentFuel,
    double MaxFuel,
    double CurrentEnergy,
    double MaxEnergy,
    IReadOnlyList<(string Name, string Value)> AllFields)
{
    /// <summary>True while the plugin is mid-write, so this read may be torn.</summary>
    public bool Torn => VersionBegin != VersionEnd;

    /// <summary>Whether the block holds a version string that looks like the plugin's.</summary>
    public bool VersionLooksRight =>
        Version.StartsWith("4.", StringComparison.Ordinal) ||
        Version.StartsWith("3.", StringComparison.Ordinal);
}

internal static class ExtendedReader
{
    /// <summary>
    /// Decodes a mapped view. Requires the two version reads to agree; a torn frame is
    /// reported rather than silently misread, which is the whole point of the version block.
    /// </summary>
    public static SessionState Read(IntPtr view, int length)
    {
        if (length < ExtendedLayout.StructOffset + ExtendedLayout.StructSize)
        {
            throw new ArgumentException(
                $"mapped view is {length} bytes, need at least " +
                $"{ExtendedLayout.StructOffset + ExtendedLayout.StructSize}");
        }

        uint begin = unchecked((uint)Marshal.ReadInt32(view, 0));
        uint end = unchecked((uint)Marshal.ReadInt32(view, 4));

        // Copy the region out before decoding: the game writes to this memory
        // concurrently, so reading a snapshot is both simpler and more correct than
        // decoding fields one at a time from a moving target.
        int needed = ExtendedLayout.StructSize;
        var raw = new byte[needed];
        Marshal.Copy(view + ExtendedLayout.StructOffset, raw, 0, needed);

        int verOff = ExtendedLayout.OffsetOf("mVersion");
        int verLen = ExtendedLayout.SizeOf("mVersion");
        string version = Encoding.ASCII.GetString(raw, verOff, verLen).TrimEnd('\0');

        var all = new List<(string, string)>(ExtendedLayout.Fields.Count);
        foreach (var f in ExtendedLayout.Fields)
        {
            string text = f.Type switch
            {
                "char" => Encoding.ASCII.GetString(raw, f.Offset, f.Size).TrimEnd('\0'),
                "bool" => raw[f.Offset] != 0 ? "true" : "false",
                "int" => BitConverter.ToInt32(raw, f.Offset).ToString(),
                "float" => BitConverter.ToSingle(raw, f.Offset).ToString("G6"),
                "double" => BitConverter.ToDouble(raw, f.Offset).ToString("G6"),
                "ulong" => BitConverter.ToUInt64(raw, f.Offset).ToString(),
                _ => "?",
            };
            all.Add((f.Name, text));
        }

        double D(string n) => BitConverter.ToDouble(raw, ExtendedLayout.OffsetOf(n));
        bool B(string n) => raw[ExtendedLayout.OffsetOf(n)] != 0;
        ulong U(string n) => BitConverter.ToUInt64(raw, ExtendedLayout.OffsetOf(n));
        int I(string n) => BitConverter.ToInt32(raw, ExtendedLayout.OffsetOf(n));

        return new SessionState(
            begin, end, version,
            B("is64bit"), B("mInRealtimeFC"), B("mSessionStarted"),
            U("mTicksSessionStarted"), U("mTicksSessionEnded"),
            B("mDirectMemoryAccessEnabled"), I("mUnsubscribedBuffersMask"),
            D("mCurrentFuelValue"), D("mMaxFuelValue"),
            D("mCurrentEnergyValue"), D("mMaxEnergyValue"),
            all);
    }
}