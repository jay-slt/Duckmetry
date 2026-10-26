using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Duckmetry.Core.Models;

namespace Duckmetry.Core.Ld;

/// <summary>
/// Writes telemetry data into the i2 .ld binary log format.
/// </summary>
/// <remarks>
/// The layout is reverse-engineered from a genuine i2-written log that is
/// confirmed to render both laps and the track report:
/// <c>log+example/2026-08-03 - 20-38-46 - WeatherTech Raceway Laguna Seca - P1.ld</c>.
///
/// That file supersedes an earlier sample (<c>Friss/i3rs VIR_LAP.ld</c>) which used a
/// different convention for the 0x40 channel field. The two disagree, and following
/// the wrong one broke the track report, so the user-supplied file is the authority.
///
/// Key facts, all verified against that file:
/// <list type="bullet">
///   <item><description>ldHead is 1890 bytes; 0x24 holds the event pointer and 0x0C
///   the first channel header.</description></item>
///   <item><description>Channel headers are 124 bytes and form a doubly-linked list via
///   prev/next at 0x00/0x04. The first header's prev is 0 and the last header's next
///   is 0 - that is how the list is bounded.</description></item>
///   <item><description>0x20 is the 32-byte full channel name, 0x40 is an 8-byte
///   SHORT NAME, and 0x48 is a 12-byte UNIT. This is the layout in the reference
///   parser (github.com/gotzl/ldparser), whose ldChan.fmt is
///   <c>'&lt;IIII H HHH hhhh 32s 8s 12s 40x'</c>. Verified against two genuine
///   i2-written logs: 0x40 holds 'Grd Spd'/'LapET'/'LastLap' and 0x48 holds
///   'km/h'/'s'/'%'/'C'. Writing the short name as 32 bytes overran the unit.</description></item>
///   <item><description>Samples are integer, never float: dtype 3 = int16, dtype 5 =
///   int32, dtype 0 = 2-byte with element size 2. There is no dtype 7 in the
///   reference.</description></item>
///   <item><description>int16 samples decode as
///   <c>value = (raw + shift) * multiplier / 10^decimals</c>. The shift is essential -
///   without it values are wildly negative. Lap channels use shift 32760.</description></item>
/// </list>
/// </remarks>
public static class LdWriter
{
    private const uint EventPtr = 1890;
    /// <summary>
    /// The value written at 0x24. Every genuine i2 log holds <b>1762</b> here - its
    /// own header size - and i2's lap rendering depends on it. Writing 1890 (our
    /// layout's size) made i2 treat every Beacon crossing as a lap: 13 laps for a 4-lap
    /// session. Proven by transplanting a genuine header into our file, which fixed it.
    /// </summary>
    private const uint HeaderSizeField = 1762;
    private const uint VenuePtr = 3044;          // 1890 + 1154
    private const uint VehiclePtr = 4144;        // 3044 + 1100
    private const uint FirstChannelMetaPtr = 5000;
    private const int ChannelHeaderSize = 124;

    /// <summary>
    /// Device serial written at 0x46. <b>12007 in all three genuine i2 logs.</b>
    /// </summary>
    private const uint DeviceSerial = 12007;

    /// <summary>dtype code for 16-bit signed integer samples.</summary>
    public const ushort DTypeInt16 = 3;

    /// <summary>dtype code for 32-bit signed integer samples.</summary>
    public const ushort DTypeInt32 = 5;

    /// <summary>
    /// Value written at header offset 0x5DE to mark the log as "Pro logging enabled".
    /// i2 refuses to open a file lacking this flag.
    /// </summary>
    /// <remarks>
    /// <b>0x00D20822, the value all three genuine i2 logs carry.</b> An earlier
    /// release wrote 0x000C81A4, found by trial: it satisfies i2's open check, but it
    /// does not match the rest of a genuine header, and files carrying it render every
    /// Beacon crossing as a lap. Changing this word alone is not enough - i2 then
    /// rejects the file - so it has to move together with the header constants above.
    /// </remarks>
    public const uint ProLoggingMagic = 0x00D20822;

    public static void WriteFile(string filePath, SessionMetadata metadata, IReadOnlyList<TelemetryChannel> channels)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        int channelCount = channels.Count;
        uint firstChannelDataPtr = FirstChannelMetaPtr + (uint)(channelCount * ChannelHeaderSize);

        // 1. ldHead (1890 bytes)
        byte[] headBytes = new byte[EventPtr];
        using (var mem = new MemoryStream(headBytes))
        using (var hw = new BinaryWriter(mem, Encoding.ASCII))
        {
            hw.Write((uint)0x00000040);             // 0x00: ldmarker
            // 0x08 is the first channel header pointer, and it must be populated.
            // v2.4.0 zeroed it after I misread the reference: I read 0x08 and 0x04
            // as zeroed and concluded the field was unused, but the genuine file
            // holds its first header (0x3448) at 0x08. With it zeroed i2 showed
            // NO CHANNELS AT ALL while the track report - read from ldHead, not from
            // the channel list - kept working. Confirmed against three files:
            //   genuine reference  0x08 = 0x3448  (first channel header)   works
            //   v2.2.0             0x08 = 0x1388  (first channel header)   works
            //   v2.4.0..v2.6.0     0x08 = 0x0000                           no channels
            hw.Write((uint)0);                      // 0x04: reserved / zero
            hw.Write(FirstChannelMetaPtr);          // 0x08: first channel header
            hw.Write(firstChannelDataPtr);          // 0x0C: first channel data block

            mem.Seek(0x24, SeekOrigin.Begin);
            hw.Write(HeaderSizeField);              // 0x24: header size field (genuine 1762)

            // These header constants come from the three genuine i2 logs, which agree
            // byte for byte on all of them. The previous values came from a different
            // source file with a 1890-byte header: i2 opened those files happily, but it
            // also rendered every Beacon crossing as a lap - 13 laps for a 4-lap session.
            // Writing the genuine values reproduces the genuine lap count.
            mem.Seek(0x40, SeekOrigin.Begin);
            hw.Write((ushort)0);                    // 0x40: 0 in all three genuine logs
            hw.Write((ushort)0x4240);               // 0x42: static 0x4240
            hw.Write((ushort)0x000F);               // 0x44: static 0x000F
            hw.Write(DeviceSerial);                 // 0x46: device serial (12007)
            WriteStringFixed(mem, 0x4A, "ADL", 8);  // 0x4A: device type
            hw.Seek(0x52, SeekOrigin.Begin);
            hw.Write((ushort)200);                  // 0x52: 200 in all three genuine logs
            hw.Write((ushort)128);                  // 0x54: 128 in all three genuine logs
            hw.Write((uint)78);                     // 0x56: 78 in all three, NOT the
                                                    //       channel count
            hw.Seek(0x5A, SeekOrigin.Begin);
            hw.Write((byte)100);                    // 0x5A: 100 in all three genuine logs
            hw.Seek(0x5C, SeekOrigin.Begin);
            hw.Write((byte)1);                      // 0x5C: 1 in all three genuine logs

            // Date & Time (16 bytes each).
            //
            // InvariantCulture is load-bearing. In a .NET custom format string "/" means
            // the culture's date separator, so a German-locale Windows rendered
            // "01.10.2026" where the genuine log has "01/10/2026" - and i2 shows
            // "Invalid DateTime" for it. The separator has to be a literal slash on every
            // machine, which is exactly what InvariantCulture gives.
            //
            // The DuckDB records the session start in UTC (the telemetry filename ends in
            // Z). i2 and the game's own logs show local time, so convert. The offset
            // used is the converting machine's current one, which is an approximation
            // when converting a session logged in a different part of the year.
            var dt = metadata.SessionDateTime != default ? metadata.SessionDateTime : DateTime.Now;
            dt = dt.ToLocalTime();
            WriteStringAt(mem, 0x5E,
                          dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), 16);
            WriteStringAt(mem, 0x7E,
                          dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture), 16);

            // Metadata strings (64 bytes each).
            //
            // ONLY the driver and the venue go here. Both genuine i2-written logs
            // leave every other one of these slots EMPTY - 0xDE, 0x624, 0x6E2 and 0x722
            // are all zero in each - and the session details live in the .ldx, which is
            // where the user confirms they read correctly.
            //
            // Writing the vehicle here produced visible junk: i2 renders the bytes
            // following the driver name in its data menu, and the user reported "unknown
            // characters" right after "Test Driver". That was this string.
            WriteStringAt(mem, 0x9E, metadata.Driver, 64);
            WriteStringAt(mem, 0x15E, metadata.Venue, 64);

            // 0xDE vehicle, 0x624 short comment, 0x6E2 event, 0x722 session type:
            // deliberately left zeroed, matching the reference.

            mem.Seek(0x66C, SeekOrigin.Begin);
            hw.Write((byte)0x63);                   // 0x66C: 99 in all three genuine logs

            // "Pro logging enabled" flag. i2 refuses to open a log without it:
            //   "this version of i2 only supports pro logging enabled files"
            //
            // This value is NOT interchangeable in isolation. Writing the genuine
            // 0x00D20822 while the rest of the header still held the old constants made
            // i2 reject the file with that very message, yet the genuine header carrying
            // the same value opens fine. Flag and header constants must be genuine
            // together, which is what the writer now does.
            mem.Seek(0x5DE, SeekOrigin.Begin);
            hw.Write(ProLoggingMagic);
        }
        writer.Write(headBytes);

        // 2. ldEvent (1154 bytes)
        byte[] eventBytes = new byte[1154];
        using (var mem = new MemoryStream(eventBytes))
        using (var ew = new BinaryWriter(mem, Encoding.ASCII))
        {
            WriteStringAt(mem, 0x00, metadata.Event, 64);
            WriteStringAt(mem, 0x40, metadata.SessionType, 64);
            WriteStringAt(mem, 0x80, metadata.ShortComment, 1024);
            mem.Seek(0x480, SeekOrigin.Begin);
            ew.Write((ushort)VenuePtr);
        }
        writer.Write(eventBytes);

        // 3. ldVenue (1100 bytes)
        byte[] venueBytes = new byte[1100];
        using (var mem = new MemoryStream(venueBytes))
        using (var vw = new BinaryWriter(mem, Encoding.ASCII))
        {
            WriteStringAt(mem, 0x00, metadata.Venue, 64);
            mem.Seek(0x44A, SeekOrigin.Begin);
            vw.Write((ushort)VehiclePtr);
        }
        writer.Write(venueBytes);

        // 4. ldVehicle (260 bytes)
        //
        // The car NAME only, without the " (CLASS)" tail this project folds into
        // metadata.Vehicle for its own .svm and .ldx use. i2 looks the car up in its
        // own database to label the session: the genuine log carries
        // "Proton Competition 2025 #88:LM" and i2 shows "GT3" beside the driver, while
        // ours carried the same string plus " (GT3)" and i2 showed no model at all.
        // The lookup matches the game's own name, so the suffix has to go.
        byte[] vehicleBytes = new byte[260];
        using (var mem = new MemoryStream(vehicleBytes))
        using (var vehW = new BinaryWriter(mem, Encoding.ASCII))
        {
            WriteStringAt(mem, 0x00, BannerCarName(metadata), 64);
            mem.Seek(0xC0, SeekOrigin.Begin);
            vehW.Write((uint)0);              // weight
            WriteStringAt(mem, 0xC4, "", 32); // type
            WriteStringAt(mem, 0xE4, "", 32); // comment
        }
        writer.Write(vehicleBytes);

        // Pad up to the first channel header
        long currentPos = stream.Position;
        if (currentPos < FirstChannelMetaPtr)
        {
            writer.Write(new byte[FirstChannelMetaPtr - currentPos]);
        }

        // 5. Channel headers. Data offsets are computed first because a header is
        //    124 bytes and the data blobs follow all of them, so each channel's data
        //    pointer depends only on the channel index.
        uint currentDataOffset = firstChannelDataPtr;
        // 0x10 is i2's standard channel ID, not a sequence number. i2 drops
        // any channel whose ID it does not recognise, which is why the old
        // 0x2EE1 + i counter produced a file with no channels at all.
        // Resolve each channel's standard ID by its i2 IDENTITY, not its display name:
        // a DuckDB-name build renames 0x20 but must keep the standard ID behind it.
        var channelIds = ChannelIds.Assign(channels.Select(c => c.Identity).ToList());
        for (int i = 0; i < channelCount; i++)
        {
            var ch = channels[i];
            uint currentChannelOffset = FirstChannelMetaPtr + (uint)(i * ChannelHeaderSize);
            uint prevOffset = (i == 0) ? 0 : currentChannelOffset - ChannelHeaderSize;
            uint nextOffset = (i == channelCount - 1) ? 0 : currentChannelOffset + ChannelHeaderSize;
            uint sampleCount = (uint)ch.Samples.Length;

            var q = Quantise(ch);

            byte[] chHeaderBytes = new byte[ChannelHeaderSize];
            using (var chMem = new MemoryStream(chHeaderBytes))
            using (var chWriter = new BinaryWriter(chMem, Encoding.ASCII))
            {
                chWriter.Write(prevOffset);                   // 0x00: prev header ptr
                chWriter.Write(nextOffset);                   // 0x04: next header ptr
                chWriter.Write(currentDataOffset);            // 0x08: data ptr
                chWriter.Write(sampleCount);                  // 0x0C: sample count
                chWriter.Write(channelIds[i]);                // 0x10: Standard names channel ID
                // 0x12 dtype_a and 0x14 dtype, per ldparser:
                //   dtype_a 0x03 -> signed int, 0x05 -> signed int, 0x07 -> float
                //   dtype    = element width in bytes (2 = int16, 4 = int32)
                chWriter.Write(q.DType);                      // 0x12: dtype_a
                chWriter.Write((ushort)q.ElementSize);        // 0x14: element width
                chWriter.Write((ushort)ch.FrequencyHz);       // 0x16: sample rate (Hz)
                chWriter.Write((short)q.Shift);               // 0x18: shift
                chWriter.Write((short)q.Multiplier);          // 0x1A: multiplier
                chWriter.Write((short)q.Scale);               // 0x1C: scale
                chWriter.Write((short)q.Decimals);            // 0x1E: decimals

                // Field layout from the reference parser (github.com/gotzl/ldparser):
                //   ldChan.fmt = '<IIII H HHH hhhh 32s 8s 12s 40x'  (124 bytes)
                //     0x20 name (32B)   0x40 SHORT NAME (8B)   0x48 UNIT (12B)
                // The short name is 8 bytes and the unit is a separate 12-byte field.
                // Writing a 32-byte short name overran it and destroyed the unit, so
                // every channel showed a blank unit and i2 could not interpret the
                // scale. Confirmed against two genuine i2-written logs, where 0x48
                // holds 'km/h', '%', 'C', 'mm', 'N', 'm/s', 's', 'rad', 'kPa', 'rpm'.
                WriteStringAt(chMem, 0x20, ch.StandardName, 32);
                WriteStringAt(chMem, 0x40, ch.ShortName, 8);
                WriteStringAt(chMem, 0x48, ch.Units, 12);
                chMem.Seek(0x54, SeekOrigin.Begin);

                // 0x54: raw_max, 0x58: raw_min.
                // The genuine i2 file writes the actual min/max of the raw integer
                // samples here. Our previous version left these zeroed, which is the
                // byte-level difference between our output and a genuine file - and
                // likely why i2 displays lap counts, decimal precision and time
                // resolution differently.
                chWriter.Write((int)q.RawMax);             // 0x54: raw max
                chWriter.Write((int)q.RawMin);             // 0x58: raw min
                // 0x5C..0x7B remains zero (padding)
            }

            writer.Write(chHeaderBytes);
            currentDataOffset += sampleCount * q.ElementSize;
        }

        // 6. Channel data, quantised to the header's declared integer encoding
        foreach (var ch in channels)
        {
            var q = Quantise(ch);
            foreach (int v in q.Raw)
            {
                // The byte count MUST match the element size declared in the header.
                // i2 uses a 1-byte element for 19 of our channels (the sector and flap
                // flags), and writing those as int32 made the stream run 3 bytes ahead per
                // sample: 3 x 182,897 samples = 548,691 bytes of overrun, which pushed
                // every later data block out of place. That is what made Ground Speed,
                // Engine RPM and Throttle Pos decode to garbage - their blocks held
                // whichever channel's data had been shifted into them.
                if (q.ElementSize == 1) writer.Write((sbyte)v);
                else if (q.ElementSize == 2) writer.Write((short)v);
                else writer.Write(v);
            }
        }

        writer.Flush();
    }

    /// <summary>
    /// Chooses an integer encoding for a channel and computes the raw values that
    /// decode back to the originals.
    /// </summary>
    /// <remarks>
    /// i2 decodes int16 as <c>value = (raw + shift) * multiplier / 10^decimals</c>.
    /// We pick the encoding that represents the channel's value range with minimal
    /// precision loss, preferring int16 because that is what the reference uses for
    /// every lap and counter channel.
    ///
    /// The shift is chosen per channel rather than fixed. The reference uses
    /// shift 0 for counters and flags, and a shift near 32760 for the elapsed-time
    /// channels whose raw values sit negative. A blanket 32760 makes a counter's
    /// raw value ~-32760, which i2 may read as an out-of-range marker; the
    /// "Lap Number"-style channels must have shift 0.
    /// </remarks>
        /// <summary>
    /// i2's own quantisation per standard channel ID:
    /// (dtype_a, width, shift, multiplier, scale, decimals).
    /// </summary>
    /// <remarks>
    /// A stored sample decodes as
    ///     value = raw * Multiplier / 10^Decimals + Shift
    /// and i2 reads those numbers from the header, not from our intent. We were
    /// choosing them ourselves, which is why some values came out far too high: a
    /// channel i2 decodes at 10^-5 with a multiplier of 4 is not the same as one at
    /// 10^-2 with a multiplier of 1.
    ///
    /// There is no formula to derive - the shift is arbitrary, picked so the raw integers
    /// stay small - so this is transcribed from two genuine i2-written logs, which
    /// AGREE on all 238 IDs. For example:
    ///     ID 9   Ground Speed  (5, 4,  24400, 2, 1, 5)
    ///     ID 1   Brake Temp FL (5, 4, -16720, 4, 1, 5)
    ///     ID 5   Engine RPM    (5, 4,  31280, 2, 1, 4)
    /// Channels with no standard ID keep the computed fallback in <see cref="Quantise"/>.
    /// </remarks>
    private static readonly Dictionary<ushort, (ushort DType, ushort Width, short Shift,
        short Multiplier, short Scale, short Decimals)> QuantById = new()
    {
        [1] = (5, 4, -16720, 4, 1, 5),
        [2] = (5, 4, -16720, 4, 1, 5),
        [3] = (5, 4, -16720, 4, 1, 5),
        [4] = (5, 4, -16720, 4, 1, 5),
        [5] = (5, 4, 31280, 2, 1, 4),
        [6] = (5, 4, -16720, 4, 1, 5),
        [7] = (5, 4, -16720, 4, 1, 5),
        [8] = (5, 4, -16720, 4, 1, 5),
        [9] = (5, 4, 24400, 2, 1, 5),
        [10] = (5, 4, -16720, 4, 1, 5),
        [11] = (5, 4, -16720, 4, 1, 5),
        [12] = (5, 4, -16720, 4, 1, 5),
        [13] = (5, 4, 10, 1, 1, 7),
        [14] = (5, 4, 10, 1, 1, 7),
        [20] = (3, 2, 641, 2, 1, 2),
        [21] = (3, 2, 641, 2, 1, 2),
        [110] = (0, 2, 0, 1, 1, 0),
        [200] = (3, 2, -127, 1, 1, 2),
        [300] = (3, 2, 0, 1, 1, 0),
        [301] = (5, 4, 95, 1, 1, 7),
        [302] = (5, 4, 95, 1, 1, 7),
        [303] = (5, 4, 95, 1, 1, 7),
        [304] = (5, 4, 65, 1, 1, 7),
        [305] = (5, 4, 1780, 1, 1, 5),
        [310] = (3, 1, 0, 1, 1, 0),
        [400] = (5, 4, -16720, 4, 1, 5),
        [401] = (5, 4, -16720, 4, 1, 5),
        [402] = (5, 4, -16720, 4, 1, 5),
        [403] = (5, 4, -16720, 4, 1, 5),
        [500] = (5, 4, 95, 1, 1, 7),
        [501] = (5, 4, 95, 1, 1, 7),
        [502] = (5, 4, 95, 1, 1, 7),
        [503] = (5, 4, 95, 1, 1, 7),
        [504] = (5, 4, -16720, 4, 1, 5),
        [505] = (5, 4, -16720, 4, 1, 5),
        [506] = (5, 4, -16720, 4, 1, 5),
        [507] = (5, 4, -16720, 4, 1, 5),
        [508] = (5, 4, -16720, 4, 1, 5),
        [509] = (5, 4, -16720, 4, 1, 5),
        [510] = (5, 4, -16720, 4, 1, 5),
        [511] = (5, 4, -16720, 4, 1, 5),
        [512] = (5, 4, -16720, 4, 1, 5),
        [513] = (5, 4, -16720, 4, 1, 5),
        [514] = (5, 4, -16720, 4, 1, 5),
        [515] = (5, 4, -16720, 4, 1, 5),
        [516] = (5, 4, -16720, 4, 1, 5),
        [517] = (5, 4, -16720, 4, 1, 5),
        [518] = (5, 4, -16720, 4, 1, 5),
        [519] = (5, 4, -16720, 4, 1, 5),
        [700] = (3, 2, 35, 1, 50, 1),
        [701] = (3, 2, 35, 1, 50, 1),
        [1100] = (5, 4, -16720, 4, 1, 5),
        [1101] = (5, 4, -16720, 4, 1, 5),
        [1102] = (5, 4, -16720, 4, 1, 5),
        [1103] = (5, 4, -16720, 4, 1, 5),
        [1104] = (5, 4, -16720, 4, 1, 5),
        [1105] = (5, 4, -16720, 4, 1, 5),
        [1106] = (5, 4, 95, 1, 1, 7),
        [1107] = (5, 4, -16720, 4, 1, 5),
        [1108] = (5, 4, 6, 1, 1, 8),
        [1109] = (5, 4, 6, 1, 1, 8),
        [1200] = (5, 4, 95, 1, 1, 7),
        [1201] = (5, 4, 95, 1, 1, 7),
        [1202] = (5, 4, 95, 1, 1, 7),
        [1203] = (5, 4, 95, 1, 1, 7),
        [1204] = (5, 4, -16720, 4, 1, 5),
        [1205] = (3, 1, 0, 1, 1, 0),
        [1206] = (3, 1, 0, 1, 1, 0),
        [1207] = (3, 1, 0, 1, 1, 0),
        [1208] = (3, 1, 0, 1, 1, 0),
        [1209] = (3, 1, 0, 1, 1, 0),
        [1210] = (3, 1, 0, 1, 1, 0),
        [1211] = (3, 1, 0, 1, 1, 0),
        [1212] = (3, 1, 0, 1, 1, 0),
        [1213] = (3, 1, 0, 1, 1, 0),
        [1214] = (3, 1, 0, 1, 1, 0),
        [1215] = (5, 4, -16720, 4, 1, 5),
        [1216] = (5, 4, -16720, 4, 1, 5),
        [1217] = (5, 4, -16720, 4, 1, 5),
        [1218] = (5, 4, -16720, 4, 1, 5),
        [1219] = (5, 4, -16720, 4, 1, 5),
        [1220] = (3, 1, 128, 1, 1, 0),
        [1221] = (3, 1, 128, 1, 1, 0),
        [1222] = (3, 1, 128, 1, 1, 0),
        [1223] = (3, 1, 128, 1, 1, 0),
        [1224] = (3, 1, 128, 1, 1, 0),
        [1225] = (3, 1, 128, 1, 1, 0),
        [1226] = (3, 1, 128, 1, 1, 0),
        [1227] = (3, 1, 128, 1, 1, 0),
        [1228] = (5, 4, 65, 1, 1, 7),
        [1229] = (5, 4, 65, 1, 1, 7),
        [1230] = (5, 4, 65, 1, 1, 7),
        [1231] = (5, 4, 65, 1, 1, 7),
        [1232] = (5, 4, 65, 1, 1, 7),
        [1233] = (5, 4, 65, 1, 1, 7),
        [2100] = (3, 2, 32760, 1, 1, 0),
        [2101] = (3, 2, 32760, 1, 1, 0),
        [2102] = (3, 2, 32760, 1, 1, 0),
        [2103] = (3, 1, 0, 1, 1, 0),
        [2104] = (3, 1, 0, 1, 1, 0),
        [2105] = (3, 1, 128, 1, 1, 0),
        [2106] = (3, 2, 32760, 1, 1, 0),
        [2107] = (3, 2, 32760, 1, 1, 0),
        [2108] = (3, 2, 32760, 1, 1, 0),
        [2109] = (3, 2, 32760, 1, 1, 0),
        [2110] = (3, 1, 128, 1, 1, 0),
        [2200] = (3, 1, 0, 1, 1, 0),
        [2201] = (3, 2, 31780, 1, 1, 0),
        [2202] = (3, 2, -10, 1, 1, 2),
        [2203] = (3, 2, -10, 1, 1, 2),
        [2204] = (3, 2, 3029, 1, 1, 1),
        [2205] = (3, 2, 3029, 1, 1, 1),
        [2206] = (3, 2, 3029, 1, 1, 1),
        [2207] = (3, 2, 3029, 1, 1, 1),
        [2208] = (3, 2, 3029, 1, 1, 1),
        [2209] = (3, 2, 3029, 1, 1, 1),
        [2210] = (3, 2, 3029, 1, 1, 1),
        [2211] = (3, 2, 3029, 1, 1, 1),
        [2212] = (3, 1, 128, 1, 1, 0),
        [2213] = (3, 1, 128, 1, 1, 0),
        [2214] = (3, 1, 0, 1, 1, 0),
        [2215] = (3, 1, 0, 1, 1, 0),
        [2216] = (3, 1, 128, 1, 1, 0),
        [2217] = (3, 1, 128, 1, 1, 0),
        [2218] = (3, 1, 0, 1, 1, 0),
        [2219] = (3, 1, 0, 1, 1, 0),
        [2220] = (3, 1, 0, 1, 1, 0),
        [2221] = (3, 1, 0, 1, 1, 0),
        [2222] = (3, 1, 128, 1, 1, 0),
        [2223] = (3, 1, 128, 1, 1, 0),
        [3100] = (5, 4, 95, 1, 1, 7),
        [3101] = (5, 4, 95, 1, 1, 7),
        [3102] = (5, 4, 95, 1, 1, 7),
        [3103] = (5, 4, 65, 1, 1, 7),
        [3104] = (5, 4, -16720, 4, 1, 5),
        [3105] = (5, 4, 65, 1, 1, 7),
        [4100] = (5, 4, -16720, 4, 1, 5),
        [4101] = (5, 4, -16720, 4, 1, 5),
        [4102] = (5, 4, -16720, 4, 1, 5),
        [4103] = (5, 4, -16720, 4, 1, 5),
        [4104] = (5, 4, -16720, 4, 1, 5),
        [4105] = (5, 4, -16720, 4, 1, 5),
        [4200] = (5, 4, 10660, 5, 2, 4),
        [4201] = (5, 4, 10660, 5, 2, 4),
        [4202] = (5, 4, 10660, 5, 2, 4),
        [4203] = (5, 4, 10660, 5, 2, 4),
        [5100] = (5, 4, 95, 1, 1, 7),
        [5101] = (5, 4, 95, 1, 1, 7),
        [5102] = (5, 4, 95, 1, 1, 7),
        [5103] = (5, 4, 95, 1, 1, 7),
        [5104] = (5, 4, 10660, 5, 2, 4),
        [5105] = (5, 4, 10660, 5, 2, 4),
        [5106] = (5, 4, 10660, 5, 2, 4),
        [5107] = (5, 4, 10660, 5, 2, 4),
        [5108] = (5, 4, -16720, 4, 1, 5),
        [5109] = (5, 4, -16720, 4, 1, 5),
        [5110] = (5, 4, -16720, 4, 1, 5),
        [5111] = (5, 4, -16720, 4, 1, 5),
        [5112] = (5, 4, -16720, 4, 1, 5),
        [5113] = (5, 4, -16720, 4, 1, 5),
        [5114] = (5, 4, -16720, 4, 1, 5),
        [5115] = (5, 4, -16720, 4, 1, 5),
        [5116] = (5, 4, -16720, 4, 1, 5),
        [5117] = (5, 4, -16720, 4, 1, 5),
        [5118] = (5, 4, -16720, 4, 1, 5),
        [5119] = (5, 4, -16720, 4, 1, 5),
        [5120] = (5, 4, -16720, 4, 1, 5),
        [5121] = (5, 4, -16720, 4, 1, 5),
        [5122] = (5, 4, -16720, 4, 1, 5),
        [5123] = (5, 4, -16720, 4, 1, 5),
        [5200] = (5, 4, -16720, 4, 1, 5),
        [5201] = (5, 4, -16720, 4, 1, 5),
        [5202] = (5, 4, -16720, 4, 1, 5),
        [5203] = (5, 4, -16720, 4, 1, 5),
        [5204] = (5, 4, -16720, 4, 1, 5),
        [5205] = (5, 4, -16720, 4, 1, 5),
        [5206] = (5, 4, -16720, 4, 1, 5),
        [5207] = (5, 4, -16720, 4, 1, 5),
        [5208] = (5, 4, -16720, 4, 1, 5),
        [5209] = (5, 4, -16720, 4, 1, 5),
        [5210] = (5, 4, -16720, 4, 1, 5),
        [5211] = (5, 4, -16720, 4, 1, 5),
        [5212] = (5, 4, -16720, 4, 1, 5),
        [5213] = (5, 4, -16720, 4, 1, 5),
        [5214] = (5, 4, -16720, 4, 1, 5),
        [5215] = (5, 4, -16720, 4, 1, 5),
        [5216] = (3, 1, 128, 1, 1, 0),
        [5217] = (3, 1, 128, 1, 1, 0),
        [5218] = (3, 1, 128, 1, 1, 0),
        [5219] = (3, 1, 128, 1, 1, 0),
        [5220] = (3, 1, 0, 1, 1, 0),
        [5221] = (3, 1, 0, 1, 1, 0),
        [5222] = (3, 1, 0, 1, 1, 0),
        [5223] = (3, 1, 0, 1, 1, 0),
        [5224] = (3, 1, 0, 1, 1, 0),
        [5225] = (3, 1, 0, 1, 1, 0),
        [5226] = (3, 1, 0, 1, 1, 0),
        [5227] = (3, 1, 0, 1, 1, 0),
        [5228] = (5, 4, -16720, 4, 1, 5),
        [5229] = (5, 4, -16720, 4, 1, 5),
        [5230] = (5, 4, -16720, 4, 1, 5),
        [5231] = (5, 4, -16720, 4, 1, 5),
        [6100] = (5, 4, -16720, 4, 1, 5),
        [6101] = (5, 4, -16720, 4, 1, 5),
        [6102] = (5, 4, -16720, 4, 1, 5),
        [6103] = (5, 4, -25440, 8, 1, 5),
        [6104] = (5, 4, 10660, 5, 2, 4),
        [6105] = (5, 4, 10660, 5, 2, 4),
        [7100] = (3, 2, 50, 2, 1, 3),
        [7101] = (3, 2, 50, 2, 1, 3),
        [7102] = (3, 2, 50, 2, 1, 3),
        [7103] = (3, 2, 0, 6, 1, 5),
        [7104] = (3, 2, 50, 2, 1, 3),
        [7105] = (3, 2, 50, 2, 1, 3),
        [8100] = (5, 4, -16720, 4, 1, 5),
        [8101] = (5, 4, -16720, 4, 1, 5),
        [8102] = (5, 4, -16720, 4, 1, 5),
        [8103] = (5, 4, -16720, 4, 1, 5),
        [8104] = (5, 4, -16720, 4, 1, 5),
        [8105] = (5, 4, -16720, 4, 1, 5),
        [8106] = (5, 4, -16720, 4, 1, 5),
        [8107] = (5, 4, -16720, 4, 1, 5),
        [8108] = (5, 4, -16720, 4, 1, 5),
        [8109] = (5, 4, -16720, 4, 1, 5),
        [8110] = (5, 4, -16720, 4, 1, 5),
        [8111] = (5, 4, -16720, 4, 1, 5),
        [8200] = (5, 4, -5, 1, 1, 8),
        [8201] = (5, 4, -5, 1, 1, 8),
        [8202] = (5, 4, -5, 1, 1, 8),
        [8203] = (5, 4, -5, 1, 1, 8),
        [8204] = (3, 1, 0, 1, 1, 0),
        [8205] = (3, 1, 0, 1, 1, 0),
        [8206] = (3, 1, 0, 1, 1, 0),
        [8207] = (3, 1, 0, 1, 1, 0),
        [8208] = (5, 4, -16720, 4, 1, 5),
        [8209] = (5, 4, -16720, 4, 1, 5),
        [8210] = (5, 4, -16720, 4, 1, 5),
        [8211] = (5, 4, -16720, 4, 1, 5),
    };

private readonly record struct Quantisation(
        ushort DType, ushort ElementSize, short Shift, short Multiplier, short Scale,
        short Decimals, int[] Raw, int RawMax, int RawMin);

    /// <summary>
    /// Encodes samples using i2's own quantisation - the exact inverse of its decode.
    /// </summary>
    /// <remarks>
    /// The decode is  value = raw * multiplier / 10^decimals + shift  and the shift is
    /// added AFTER the scaling, not before. That ordering is the whole thing: reading it
    /// as (raw + shift) * multiplier / 10^decimals puts the genuine log's Ground Speed
    /// samples at -24,400 km/h instead of 0. Verified against the real DuckDB ranges for
    /// speed, both brake temperatures, engine RPM and throttle.
    ///
    /// Raw samples are clamped to the channel's declared width rather than wrapped: a
    /// wrapped sample decodes as a wildly wrong value, which is worse than a saturated
    /// one. Width 1 is int8, 2 is int16, 4 is int32.
    /// </remarks>
    private static int[] EncodeSamples(
        TelemetryChannel ch, ushort width, short shift, short multiplier, short scale, short decimals)
    {
        // scale divides the decoded value in i2's model, so it belongs in the divisor.
        double perRaw = multiplier / (Math.Pow(10, decimals) * (scale == 0 ? 1 : (int)scale));
        int lo = width switch { 1 => sbyte.MinValue, 4 => int.MinValue, _ => short.MinValue };
        int hi = width switch { 1 => sbyte.MaxValue, 4 => int.MaxValue, _ => short.MaxValue };

        var raw = new int[ch.Samples.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double v = (ch.Samples[i] - shift) / perRaw;
            double r = Math.Round(v, MidpointRounding.AwayFromZero);
            if (r > hi) r = hi;
            if (r < lo) r = lo;
            raw[i] = (int)r;
        }
        return raw;
    }

    private static Quantisation Quantise(TelemetryChannel ch)
    {
        // Prefer i2's own quantisation for this channel, keyed by standard ID. The
        // computed fallback below is only for channels i2 has no standard ID for -
        // those land on a synthetic ID and have no reference triple to copy.
        if (ch.Samples.Length > 0 &&
                    ChannelIds.Lookup(ch.Identity) is ushort id &&
                    QuantById.TryGetValue(id, out var mo))
                {
                    var encoded = EncodeSamples(ch, mo.Width, mo.Shift, mo.Multiplier,
                        mo.Scale, mo.Decimals);
                    int encMax = encoded.Length > 0 ? encoded.Max() : 0;
                    int encMin = encoded.Length > 0 ? encoded.Min() : 0;
                    var q = new Quantisation(mo.DType, mo.Width, mo.Shift, mo.Multiplier,
                        mo.Scale, mo.Decimals, encoded, encMax, encMin);
                    return q;
                }

        if (ch.Samples.Length == 0)
        {
            return new Quantisation(DTypeInt16, 2, 0, 1, 1, 0, Array.Empty<int>(), 0, 0);
        }

        float min = ch.Samples.Min();
        float max = ch.Samples.Max();
        float range = max - min;

        // Enough decimals to keep the value within a small absolute error, but no
        // more than int16 can carry after the shift.
        int decimals = range <= 0.0001f ? 0 : (range < 10f ? 3 : range < 1000f ? 2 : 1);
        double scale = Math.Pow(10, decimals);

        // Choose the smallest signed width that holds the scaled range.
        double scaledMin = min * scale;
        double scaledMax = max * scale;
        double span = scaledMax - scaledMin;

        // Shift is per channel, matching how the reference encodes each kind:
        //   - counters/flags  -> shift 0, raw == value
        //     (Lap Number, Lap Beacon Number, Marker, Sector Flag N, ...)
        //   - timing channels -> a shift that centres the raw values, which is why
        //     the reference's elapsed-time channels use ~32760 and their raw
        //     samples sit near -32244.
        // A single fixed shift of 32760 gave every counter a raw value near -32760,
        // which i2 appears to read as an out-of-range marker; that produced
        // 11 phantom "laps" with zero duration.
        const int RawHeadroom = 32760;

        bool isCounter = IsDiscreteCounter(ch);

        // For a counter, raw = value exactly (shift 0). For a continuous channel
        // pick the smallest shift that keeps raw inside int16 with headroom.
        double needed = isCounter ? Math.Max(Math.Abs(scaledMin), Math.Abs(scaledMax))
                              : span;

        ushort dtype = DTypeInt16;
        ushort elementSize = 2;
        // i2's shift is a VALUE, added after the raw is divided back out by 10^decimals:
        //   value = raw / (scale * 10^decimals) * multiplier + shift
        // This used to be the scaled midpoint, so the header announced a shift of 3599
        // for a channel whose values are 3.6, and i2 decoded it as 3599. Instead of
        // the value, the shift is the unscaled midpoint and the raw is scaled separately.
        double shift = 0;

        if (needed > RawHeadroom)
        {
            // Too wide for int16 even with no shift; fall back to int32.
            dtype = DTypeInt32;
            elementSize = 4;
        }
        else if (!isCounter)
        {
            shift = (min + max) / 2.0;
        }

        var raw = new int[ch.Samples.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            // Bring the sample to the same units the header describes, then centre it.
            double v = (ch.Samples[i] - shift) * scale;
            double r = Math.Round(v, MidpointRounding.AwayFromZero);
            // Clamp rather than wrap: a wrapped sample would be a wildly wrong value.
            if (dtype == DTypeInt16)
            {
                if (r > short.MaxValue) r = short.MaxValue;
                if (r < short.MinValue) r = short.MinValue;
            }
            raw[i] = (int)r;
        }

        // Scale 1 here is correct and not a placeholder: EncodeSamples above already
        // applies this channel's own 10^decimals, so the header's divisor stays 1.
        int rawMax = raw.Length > 0 ? raw.Max() : 0;
        int rawMin = raw.Length > 0 ? raw.Min() : 0;
        return new Quantisation(dtype, elementSize, (short)Math.Round(shift),
            1, 1, (short)decimals, raw, rawMax, rawMin);
    }

    /// <summary>
    /// True for channels that are a small non-negative integer state rather than a
    /// measured value: lap counters, beacon numbers, and boolean/enum flags.
    /// </summary>
    /// <remarks>
    /// These are encoded with shift 0, exactly as the reference does. Applying the
    /// timing-channel shift to a counter pushed its raw value to about -32760, which
    /// i2 appeared to read as an out-of-range marker and turned into phantom
    /// zero-length laps. A channel qualifies when every sample is a whole number in
    /// a small non-negative range - a measured value like throttle (0..1) or a
    /// timing in seconds does not, unless it happens to be a clean small integer.
    /// </remarks>
    private static bool IsDiscreteCounter(TelemetryChannel ch)
    {
        // Only channels with a plausible counter name are candidates; a continuous
        // channel that happens to be integral (e.g. constant 0) must not qualify.
        string n = ch.Identity;
        bool counterish =
            n.Contains("Lap Number", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Beacon Number", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Marker", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Flag", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Laps Behind", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Current Sector", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("State", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("Status", StringComparison.OrdinalIgnoreCase);

        if (!counterish) return false;

        // And it must actually be a small non-negative integer series.
        foreach (float s in ch.Samples)
        {
            if (s < 0 || s > 32760) return false;
            if (MathF.Abs(s - MathF.Round(s)) > 0.0001f) return false;
        }
        return true;
    }

    /// <summary>
    /// The car name shown in the .ld banner: the bare name plus the class when one can
    /// be resolved ("Toyota Racing 2026 #7:LM (Hypercar)") - i2 shows it beside the
    /// driver's name. Bare name only when the session records no class anywhere.
    /// </summary>
    public static string BannerCarName(SessionMetadata metadata)
    {
        string bare = SessionMetadata.StripCarClass(metadata.Vehicle);
        string? carClass = CarClassNormalizer.BannerClass(metadata.RecordedClass, metadata.Vehicle);
        return carClass is null ? bare : $"{bare} ({carClass})";
    }

    private static void WriteStringAt(Stream stream, long offset, string? value, int maxBytes)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        if (string.IsNullOrEmpty(value)) return;

        var bytes = Encoding.ASCII.GetBytes(value);

        // A silently truncated name mislabels the channel, so refuse instead.
        if (bytes.Length > maxBytes - 1)
        {
            throw new InvalidOperationException(
                $"Value '{value}' is {bytes.Length} bytes but the field holds {maxBytes - 1}. " +
                "Shorten it rather than have it truncated in the .ld.");
        }

        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0); // Null terminator
    }

    private static void WriteStringFixed(Stream stream, long offset, string value, int length)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = Encoding.ASCII.GetBytes(value);
        var buffer = new byte[length];
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, length));
        stream.Write(buffer, 0, length);
    }
}
