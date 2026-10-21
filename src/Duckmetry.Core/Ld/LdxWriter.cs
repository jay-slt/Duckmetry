using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Duckmetry.Core.Models;

namespace Duckmetry.Core.Ld;

/// <summary>
/// Writes companion i2 .ldx XML files with session details and car setup.
/// </summary>
/// <remarks>
/// The output is generated as text rather than through <see cref="System.Xml.XmlWriter"/>
/// because i2's own writer emits a very specific byte layout that XmlWriter cannot
/// reproduce. Verified byte-for-byte against the genuine reference at
/// log+example/WeatherTech.Raceway.Laguna.Seca/*.ldx:
///
///   - no UTF-8 byte-order mark (XmlWriter emits one for UTF8Encoding)
///   - CRLF line endings
///   - exactly one space of indentation per nesting level
///   - a bare '&lt;?xml version="1.0"?&gt;' declaration with NO encoding attribute
///   - empty elements closed tight: &lt;String .../&gt; with no space before the slash
///   - a blank line before the closing &lt;/Details&gt;
///
/// Structure follows the reference writer in Aim_2_i2 (ldParser.cs), a working,
/// i2-verified producer of .ld/.ldx pairs:
///
///   &lt;LDXFile Locale=... DefaultLocale="C" Version="1.6"&gt;
///    &lt;Layers&gt;
///     &lt;Layer&gt;
///      &lt;MarkerBlock&gt;
///       &lt;MarkerGroup Name="Beacons" Index="3"&gt;
///        &lt;Marker Version="100" ClassName="BCN" Name="Lap 1" Flags="0" Time="1.23E+08"/&gt;
///       &lt;/MarkerGroup&gt;
///      &lt;/MarkerBlock&gt;
///      &lt;RangeBlock/&gt;
///     &lt;/Layer&gt;
///     &lt;Details&gt;...&lt;/Details&gt;
///    &lt;/Layers&gt;
///   &lt;/LDXFile&gt;
///
/// Laps ARE the &lt;Marker&gt; elements: i2 models a lap as an IMtcRange spanning two
/// IMtcMarker objects, and the circuit-lap marker class is "BCN". Marker.Time is in
/// microseconds. Note that &lt;Details&gt; is a SIBLING of &lt;Layer&gt;, not a child - the
/// session summary strings and setup do not live inside the marker layer.
/// </remarks>
public static class LdxWriter
{
    private const string CrLf = "\r\n";
    private const string Indent1 = " ";
    private const string Indent2 = "  ";
    private const string Indent3 = "   ";
    private const string Indent4 = "    ";
    private const string Indent5 = "     ";

    public static void WriteFile(string filePath, SessionMetadata metadata, IReadOnlyList<LapBeacon> beacons)
    {
        var sb = new StringBuilder(16 * 1024);

        // Declaration exactly as i2 writes it: no encoding attribute, no BOM.
        sb.Append("<?xml version=\"1.0\"?>").Append(CrLf);
        sb.Append("<LDXFile Locale=\"English_United States.1252\" DefaultLocale=\"C\" Version=\"1.6\">").Append(CrLf);
        sb.Append(Indent1).Append("<Layers>").Append(CrLf);

        // ---- No <Layer> block ----------------------------------------------
        //
        // A genuine i2-written .ldx for a real session contains NO layer, no
        // <MarkerGroup>, no <Marker> and no <RangeBlock> at all - only <Details>. Its
        // "Marker" channel (ID 310) is entirely zero, i.e. i2 recorded no markers,
        // and the lap structure comes from the .ld's Lap Number / Lap Start Elapsed
        // Time / Last Laptime channels instead. A user test confirmed the behaviour:
        // looking up "Lap Start Elapsed Time" and "Session Elapsed Time" by hand shows
        // the lap structure, and the .ldx has nothing else.
        //
        // We were emitting six invented BCN markers here, describing a marker channel
        // our .ld does not even contain. That is a second, conflicting lap definition
        // for i2 to reconcile, and it disagreed with the .ld's own channels. Remove
        // it and match the reference: <Details> only.

        // ---- Details: session summary only (sibling of Layer) -------------
        //
        // No car setup here. It is written to its own .svm and _setup.json from the
        // DuckDB metadata, and the user confirmed this block is not needed. It also
        // kept _Setup_* entries out of a file i2 rewrites on load anyway.
        sb.Append(Indent1).Append("<Details>").Append(CrLf);
        WriteDetailStrings(sb, metadata, beacons);
        sb.Append(CrLf);
        sb.Append(Indent1).Append("</Details>").Append(CrLf);

        sb.Append(Indent1).Append("</Layers>").Append(CrLf);
        sb.Append("</LDXFile>").Append(CrLf);

        // UTF-8 without BOM. i2's own files start directly with '<?xml'.
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
/// Emits the session summary strings, matching the three that i2 itself writes.
/// </summary>
private static void WriteDetailStrings(StringBuilder sb, SessionMetadata metadata, IReadOnlyList<LapBeacon> beacons)
{
    // Completed laps only. A lap is completed when it has a duration: the leading
    // "Out" row opens the first range without closing one (LapNumber 0), and the
    // final crossing has no successor, so its LapTimeSeconds is 0 - it is an in-lap,
    // not a finished lap. Counting it inflated Total Laps by one (5 instead of 4 in
    // the session whose genuine log is paired with its DuckDB in
    // log+example/Same data/Duckdb).
    var completedLaps = beacons
    .Where(b => b.LapNumber > 0 && !b.IsInLap && b.LapTimeSeconds > 0.0)
    .ToList();
    int totalLaps = completedLaps.Count;
    if (totalLaps > 0)
    {
        AppendString(sb, "Total Laps", totalLaps.ToString(CultureInfo.InvariantCulture));

        // Only real completed laps can be the fastest. The in-lap is excluded above,
        // and a lap under ~20s is a formation/out artefact rather than a timed lap.
        var timedLaps = completedLaps.Where(b => b.LapTimeSeconds > 20.0).ToList();
            if (timedLaps.Count > 0)
            {
                var fastest = timedLaps.OrderBy(b => b.LapTimeSeconds).First();
                // Round to whole milliseconds first: a lap time like 111.83999999999997
                // (float noise from subtracting two timestamps) otherwise truncates to
                // 111.839 and i2 shows 1:51.839 for a 1:51.840 lap.
                var ts = TimeSpan.FromSeconds(Math.Round(fastest.LapTimeSeconds, 3));
                string formattedTime = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
                AppendString(sb, "Fastest Time", formattedTime);
                AppendString(sb, "Fastest Lap", fastest.LapNumber.ToString(CultureInfo.InvariantCulture));
            }
        }

        AppendString(sb, "Venue", metadata.Venue);
        AppendString(sb, "Vehicle", LdWriter.BannerCarName(metadata));
        AppendString(sb, "Driver", metadata.Driver);
        AppendString(sb, "Event", metadata.Event);

        // i2's Data tab renders the Session entry (and ignores the Vehicle entry), so the
        // car model + class ride along here: "Practice - Toyota Racing 2026 #7:LM (Hypercar)".
        // The separator is ASCII (the ldx is written without a BOM and i2's parser is
        // byte-layout specific); the same BannerCarName used by the .ld vehicle block.
        AppendString(sb, "Session",
            $"{metadata.SessionType} - {LdWriter.BannerCarName(metadata)}");

        AppendString(sb, "Short Comment", metadata.ShortComment);
    }

    private static void AppendString(StringBuilder sb, string id, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(Indent2)
          .Append("<String Id=\"").Append(Escape(id))
          .Append("\" Value=\"").Append(Escape(value)).Append("\"/>")
          .Append(CrLf);
    }

    /// <summary>
    /// XML-escapes a value for use inside a double-quoted attribute.
    /// </summary>
    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }
}
