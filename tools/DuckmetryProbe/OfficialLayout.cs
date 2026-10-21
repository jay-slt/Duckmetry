// The game's official shared-memory interface, transcribed from the Studio 397
// headers in log+example/SharedMemoryInterface/.
//
// This is a different object from the community plugin's block that ExtendedLayout
// describes, and the two must not be confused:
//
//   community plugin : "$LMU_SMMP_Extended$" / "Extended"  - 184-byte session-state struct
//   official (S397)  : "LMU_Data"                          - generic + paths + scoring + telemetry
//
// What we read here is only the PREFIX of "LMU_Data": SharedMemoryObjectOut begins with
// SharedMemoryGeneric, so the event queue and the application state are in the first ~100
// bytes. That is deliberate - it means the reader never has to reproduce the full layout
// (104 vehicles of TelemInfoV01 and ScoringInfoV01), which is where transcription errors
// would hide. Everything past byte 97 is left alone.
//
// Layout of the prefix, derived from the header declarations:
//
//   SharedMemoryGeneric {
//     SharedMemoryEvent events[SME_MAX];   // uint32_t[16]          0 .. 63
//     long  gameVersion;                   // (4 bytes on Windows)  64 .. 67
//     float FFBTorque;                     //                       68 .. 71
//     ApplicationStateV01 appInfo;         // 8-aligned -> 72, size 264
//   }
//
// Under ApplicationStateV01 (offset 72):
//     HWND          mAppWindow         72 .. 79   (8-byte pointer, so appInfo is 8-aligned)
//     unsigned long mWidth             80 .. 83
//     unsigned long mHeight            84 .. 87
//     unsigned long mRefreshRate       88 .. 91
//     unsigned long mWindowed          92 .. 95
//     unsigned char mOptionsLocation   96         <- 0 main UI, 1 loading, 2 monitor, 3 ON TRACK
//     char          mOptionsPage[31]   97 .. 127
//     unsigned char mExpansion[204]   128 .. 331
//
// The offsets are checked against the header itself by LayoutSelfCheck, so a change in the
// SDK shows up as a failed check rather than as plausible but wrong numbers.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DuckmetryProbe;

internal static class OfficialLayout
{
    /// <summary>The mapping's name. This is the official block, not the plugin's.</summary>
    public const string SharedMemoryName = "LMU_Data";

    /// <summary>The two events the game raises around this block, in the order to wait on.</summary>
    public const string HoldEventName = "LMU_Data_HoldEvent";
    public const string DataEventName = "LMU_Data_DataEvent";

    /// <summary>
    /// SharedMemoryEvent, in declaration order. Its last member is SME_MAX, which is the
    /// count and not an event, so there are 16 slots - not 17.
    /// </summary>
    public static readonly string[] EventNames =
    {
        "ENTER", "EXIT", "STARTUP", "SHUTDOWN", "LOAD", "UNLOAD",
        "START_SESSION", "END_SESSION",
        "ENTER_REALTIME", "EXIT_REALTIME",
        "UPDATE_SCORING", "UPDATE_TELEMETRY",
        "INIT_APPLICATION", "UNINIT_APPLICATION",
        "SET_ENVIRONMENT", "FFB",
    };

    public const int EventSlots = 16;
    public const int EventBytes = EventSlots * 4;          // 64

    public const int GameVersionOffset = 64;               // long, 4 bytes
    public const int FfbTorqueOffset = 68;                 // float, 4 bytes
    public const int AppInfoOffset = 72;                   // 8-aligned, no padding needed

    public const int AppWindowOffset = 72;                 // HWND
    public const int WidthOffset = 80;
    public const int HeightOffset = 84;
    public const int RefreshRateOffset = 88;
    public const int WindowedOffset = 92;
    public const int OptionsLocationOffset = 96;           // unsigned char
    public const int OptionsPageOffset = 97;               // char[31]
    public const int ExpansionOffset = 128;                // unsigned char[204]
    public const int AppInfoSize = 264;                    // 260, padded to the HWND's 8
    public const int GenericSize = AppInfoOffset + AppInfoSize;

    /// <summary>The shortest mapping that still contains mOptionsLocation.</summary>
    public const int MinPrefixLength = OptionsLocationOffset + 1;

    /// <summary>The event value the game raises when a session ends. <c>SME_END_SESSION</c>.</summary>
    public const uint EventEndSession = 7;

    /// <summary>The event the game raises while the car is drivable. <c>SME_ENTER_REALTIME</c>.</summary>
    public const uint EventEnterRealtime = 8;

    public static string EventName(uint value) =>
        value < EventNames.Length ? EventNames[value] : $"UNKNOWN({value})";

    public static string LocationName(byte value) => value switch
    {
        0 => "main UI",
        1 => "track loading",
        2 => "monitor",
        3 => "ON TRACK",
        _ => $"unknown({value})",
    };
}

/// <summary>A read of the official block's prefix.</summary>
internal sealed record OfficialState(
    uint[] Events,
    int GameVersion,
    float FfbTorque,
    long AppWindow,
    uint Width,
    uint Height,
    uint RefreshRate,
    uint Windowed,
    byte OptionsLocation,
    string OptionsPage,
    IReadOnlyList<(string Name, string Value)> AllFields)
{
    /// <summary>Whether the realtime event is currently raised - the car is drivable.</summary>
    public bool InRealtime => Events.Contains(OfficialLayout.EventEnterRealtime);

    /// <summary>Whether the session-ended event is currently raised.</summary>
    public bool SessionEnded => Events.Contains(OfficialLayout.EventEndSession);

    public string Location => OfficialLayout.LocationName(OptionsLocation);

    /// <summary>Events raised, as names, in slot order - the queue as the game left it.</summary>
    public string EventSummary =>
        Events.Length == 0
            ? "(none)"
            : string.Join(", ", Events.Select(OfficialLayout.EventName));
}

internal static class OfficialReader
{
    /// <summary>
    /// Decodes the prefix of a mapped view. Never reads past MinPrefixLength, so a mapping
    /// that is large enough for the prefix is enough for us - the full official block is
    /// very large and this deliberately does not depend on reproducing it.
    /// </summary>
    public static OfficialState Read(IntPtr view, int length)
    {
        if (length < OfficialLayout.MinPrefixLength)
        {
            throw new ArgumentException(
                $"mapped view is {length} bytes, need at least {OfficialLayout.MinPrefixLength}");
        }

        // Copy the prefix out before decoding: the game writes this memory concurrently,
        // and a snapshot is both simpler and more correct than reading a moving target.
        var raw = new byte[OfficialLayout.GenericSize];
        Marshal.Copy(view, raw, 0, raw.Length);

        var events = new uint[OfficialLayout.EventSlots];
        for (int i = 0; i < events.Length; i++)
        {
            events[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4 * i, 4));
        }

        var all = new List<(string, string)>();

        // The raw queue, one row per slot: all 16 values, named. Only the rows that change
        // are printed while watching, so this is cheap and shows the discipline directly
        // (e.g. whether a slot holds its own index, or the array is a rolling queue).
        for (int i = 0; i < events.Length; i++)
        {
            all.Add(($"events[{i}]", $"{events[i]} ({OfficialLayout.EventName(events[i])})"));
        }

        int gameVersion = BinaryPrimitives.ReadInt32LittleEndian(
            raw.AsSpan(OfficialLayout.GameVersionOffset, 4));
        float ffb = BinaryPrimitives.ReadSingleLittleEndian(
            raw.AsSpan(OfficialLayout.FfbTorqueOffset, 4));
        long appWindow = BinaryPrimitives.ReadInt64LittleEndian(
            raw.AsSpan(OfficialLayout.AppWindowOffset, 8));
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(OfficialLayout.WidthOffset, 4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(OfficialLayout.HeightOffset, 4));
        uint refresh = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(OfficialLayout.RefreshRateOffset, 4));
        uint windowed = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(OfficialLayout.WindowedOffset, 4));
        byte location = raw[OfficialLayout.OptionsLocationOffset];
        string page = Encoding.ASCII
            .GetString(raw, OfficialLayout.OptionsPageOffset, 31).TrimEnd('\0');

        all.Add(("gameVersion", gameVersion.ToString()));
        all.Add(("FFBTorque", ffb.ToString("G6")));
        all.Add(("app.mOptionsLocation", $"{location} ({OfficialLayout.LocationName(location)})"));
        all.Add(("app.mOptionsPage", page));
        all.Add(("app.mWidth", width.ToString()));
        all.Add(("app.mHeight", height.ToString()));
        all.Add(("app.mRefreshRate", refresh.ToString()));
        all.Add(("app.mWindowed", windowed.ToString()));
        all.Add(("app.mAppWindow", $"0x{appWindow:X}"));

        return new OfficialState(events, gameVersion, ffb, appWindow, width, height,
                                 refresh, windowed, location, page, all);
    }

    /// <summary>Reads, or returns null rather than throwing - for the watch loop.</summary>
    public static OfficialState? TryRead(IntPtr view, int length)
    {
        try
        {
            return Read(view, length);
        }
        catch
        {
            return null;
        }
    }
}
