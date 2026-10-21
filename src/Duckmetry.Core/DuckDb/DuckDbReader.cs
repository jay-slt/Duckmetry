using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using DuckDB.NET.Data;
using Duckmetry.Core.Models;
using Duckmetry.Core.Ld;

namespace Duckmetry.Core.DuckDb;

/// <summary>
/// Reads and extracts metadata, laps, car setup, and channel time-series from an the game DuckDB file.
/// </summary>
public class DuckDbReader : IDisposable
{
    private readonly string _filePath;
    private DuckDBConnection? _connection;

    public DuckDbReader(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>
    /// Reads the <c>channelsList</c> table: each telemetry channel's name and the rate
    /// the game recorded it at. Used by the Pro per-channel rate editor. Tolerant —
    /// a missing or unreadable <c>channelsList</c> yields an empty dictionary, never an
    /// exception (the file may be locked by the game).
    /// </summary>
    public Dictionary<string, int> ReadChannelFrequencies()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnsureOpen();

            bool hasTable;
            using (var probe = _connection!.CreateCommand())
            {
                probe.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema='main' AND table_name='channelsList';";
                hasTable = Convert.ToInt64(probe.ExecuteScalar()) > 0;
            }

            if (!hasTable) return result;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT channelName, frequency FROM " + QuoteTableName("channelsList") + ";";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                int freq = reader.GetInt32(1);
                if (freq > 0)
                {
                    result[name] = freq;
                }
            }
        }
        catch
        {
            // Locked or malformed — the caller shows what it could read.
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>
    /// Safely quotes a table name for use in DuckDB SQL.
    /// Table names with spaces must be wrapped in double quotes.
    /// </summary>
    private static string QuoteTableName(string tableName)
    {
        // Escape any existing double-quotes by doubling them, then wrap in double quotes
        return "\"" + tableName.Replace("\"", "\"\"") + "\"";
    }

    public void Open()
    {
        if (_connection != null) return;

        // READ_ONLY: (1) the scanner's and converter's SessionDateTime fallback for the
        // output name is File.GetLastWriteTime() - if opening this database touched the
        // file, the two sides could derive names differing by a second and a converted
        // session would be offered again as pending (a reproducible flake); (2) the
        // converter must never modify the game's own file.
        var connStr = $"Data Source={_filePath};access_mode=READ_ONLY";
        _connection = new DuckDBConnection(connStr);
        _connection.Open();
    }

    /// <summary>
    /// Gets all user table names present in the database.
    /// </summary>
    public List<string> GetTableNames()
    {
        EnsureOpen();
        var tables = new List<string>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema='main';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    /// <summary>
    /// Extracts session metadata (driver, vehicle, track, date, car setup JSON).
    /// </summary>
    public SessionMetadata ReadMetadata()
    {
        EnsureOpen();
        var meta = new SessionMetadata();
        var tables = GetTableNames();

        if (tables.Contains("metadata", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT * FROM " + QuoteTableName("metadata") + ";";
                using var reader = cmd.ExecuteReader();

                int kIdx = FindColumnIndex(reader, "key", "name", "k");
                int vIdx = FindColumnIndex(reader, "value", "val", "v");

                if (kIdx >= 0 && vIdx >= 0)
                {
                    while (reader.Read())
                    {
                        var k = reader.IsDBNull(kIdx) ? string.Empty : reader.GetValue(kIdx).ToString() ?? string.Empty;
                        var v = reader.IsDBNull(vIdx) ? string.Empty : reader.GetValue(vIdx).ToString() ?? string.Empty;
                        ApplyMetadataKeyValue(meta, k, v);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed reading metadata table: {ex.Message}");
            }
        }

        // Try reading temperatures if available
        if (tables.Contains("Track Temperature", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT AVG(value) FROM " + QuoteTableName("Track Temperature") + ";";
                var res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value) meta.TrackTempCelsius = Convert.ToDouble(res);
            }
            catch { }
        }

        if (tables.Contains("Ambient Temperature", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT AVG(value) FROM " + QuoteTableName("Ambient Temperature") + ";";
                var res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value) meta.AmbientTempCelsius = Convert.ToDouble(res);
            }
            catch { }
        }

        // Fallback: Infer driver / track / car from filename if empty
        var fileName = Path.GetFileNameWithoutExtension(_filePath);
        if (string.IsNullOrWhiteSpace(meta.Venue) || meta.Venue == "Telemetry Circuit")
        {
            var parts = fileName.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1) meta.Venue = parts[0].Replace(".", " ");
            if (parts.Length >= 2 && meta.Venue == parts[0]) meta.Venue = parts[0].Replace(".", " ");
        }

        if (meta.SessionDateTime == default)
        {
            meta.SessionDateTime = File.GetLastWriteTime(_filePath);
        }

        return meta;
    }

    private static void ApplyMetadataKeyValue(SessionMetadata meta, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        // Normalize the key: lowercase, remove underscores and spaces
        var k = key.ToLowerInvariant().Replace("_", "").Replace(" ", "");

        if (k == "drivername" || k == "driver")
        {
            meta.Driver = value;
        }
        else if (k == "carname" || k == "car" || k == "vehicle")
        {
            meta.Vehicle = value;
        }
        else if (k == "trackname" || k == "venue" || k == "track" || k == "circuit")
        {
            meta.Venue = value;
        }
        else if (k == "tracklayout")
        {
            if (!string.IsNullOrWhiteSpace(meta.Venue) && !meta.Venue.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                meta.Venue = $"{meta.Venue} - {value}";
            }
        }
        else if (k == "sessiontype" || k == "session")
        {
            meta.SessionType = value;
        }
        else if (k == "carsetup" || k == "setup" || k == "setupjson" || k == "setup_json")
        {
            meta.RawSetupJson = value;
        }
        else if (k == "recordingtime" || k == "datetime" || k == "date")
        {
            // Parse ISO string like "2026-06-02T20_04_44Z" or "2026-06-02T20:04:44Z"
            var cleanIso = value.Replace("_", ":");
            if (DateTime.TryParse(cleanIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                meta.SessionDateTime = dt;
            }
        }
        else if (k == "tracktemp" || k == "tracktemperature" || k == "tracktempc")
        {
            if (double.TryParse(value, out var tt)) meta.TrackTempCelsius = tt;
        }
        else if (k == "ambienttemp" || k == "ambienttemperature" || k == "ambienttempc")
        {
            if (double.TryParse(value, out var at)) meta.AmbientTempCelsius = at;
        }
        else if (k == "sessiontime" || k == "sessiontime")
        {
            if (double.TryParse(value, out var st)) meta.DurationSeconds = st;
        }
        else if (k == "sessiontime" || k == "totaldist" || k == "totdist")
        {
            // Track total distance
        }
        else if (k == "version")
        {
            // Store version info for debugging
        }
        else if (k == "steamid")
        {
            // Not needed for output but useful for debugging
        }
        else if (k == "carclass")
        {
            // Store car class info
            meta.CarClass = value;
            if (!string.IsNullOrWhiteSpace(meta.Vehicle))
            {
                meta.Vehicle = $"{meta.Vehicle} ({value})";
            }
        }
        else if (k == "weatherconditions")
        {
            meta.ShortComment = value;
        }
    }

    /// <summary>
    /// Reads lap markers and beacons from the database.
    /// </summary>
    public List<LapBeacon> ReadLapBeacons()
    {
        EnsureOpen();
        var beacons = new List<LapBeacon>();
        var tables = GetTableNames();

        double sessionStartTs = 0.0;
        double sessionEndTs = 0.0;

        // Get session start and end time from GPS Time if available
        if (tables.Contains("GPS Time", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT MIN(value), MAX(value) FROM " + QuoteTableName("GPS Time") + ";";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    sessionStartTs = reader.GetDouble(0);
                    sessionEndTs = reader.GetDouble(1);
                }
            }
            catch { }
        }

        // Check Lap table
        var lapTable = tables.FirstOrDefault(t => t.Equals("Lap", StringComparison.OrdinalIgnoreCase));
        if (lapTable != null)
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT * FROM " + QuoteTableName(lapTable) + ";";
                using var reader = cmd.ExecuteReader();

                var lapRows = new List<(double Ts, int LapNum)>();
                while (reader.Read())
                {
                    double ts = Convert.ToDouble(reader.GetValue(0));
                    int lapNum = Convert.ToInt32(reader.GetValue(1));
                    lapRows.Add((ts, lapNum));
                }

// The Lap table's timestamps sit on the game's 10 ms tick, so lap times taken
// straight from them are quantised to 10 ms: i2 renders 1:25.680 where the
// genuine log of the same session says 1:25.684. The Lap Time table holds the
// same laps at full float precision - 85.68377685546875 is the genuine log's
// 1:25.684 - so crossings are placed from those durations instead. The first
// line keeps its tick-resolution anchor; every later crossing is the previous
// one plus the precise duration. The differences are then exact, and the
// anchor's own tick error moves all of them together, so it cancels.
var preciseLapTimes = new Dictionary<long, double>();
var lapTimeTable = tables.FirstOrDefault(
    t => t.Equals("Lap Time", StringComparison.OrdinalIgnoreCase));
if (lapTimeTable != null)
{
    try
    {
        using var ltCmd = _connection!.CreateCommand();
        ltCmd.CommandText = "SELECT * FROM " + QuoteTableName(lapTimeTable) + ";";
        using var ltReader = ltCmd.ExecuteReader();
        while (ltReader.Read())
        {
            double lts = Convert.ToDouble(ltReader.GetValue(0));
            double lval = Convert.ToDouble(ltReader.GetValue(1));
            preciseLapTimes[(long)Math.Round(lts * 1000.0)] = lval;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Warning] Failed reading {lapTimeTable}: {ex.Message}");
    }
}

var placed = new double[lapRows.Count];
if (lapRows.Count > 0) placed[0] = lapRows[0].Ts;
for (int i = 1; i < lapRows.Count; i++)
{
    if (preciseLapTimes.TryGetValue((long)Math.Round(lapRows[i].Ts * 1000.0),
                                    out double precise)
        && precise > 0.0)
        placed[i] = placed[i - 1] + precise;
    else
        placed[i] = placed[i - 1] + (lapRows[i].Ts - lapRows[i - 1].Ts);
}

if (lapRows.Count > 0)
{
    if (sessionStartTs <= 0.0) sessionStartTs = placed[0];
    if (sessionEndTs <= 0.0) sessionEndTs = placed[^1];

    for (int i = 0; i < lapRows.Count; i++)
    {
        var cur = lapRows[i];
        // ABSOLUTE session time. A genuine i2 log of the same session stores the
        // raw DuckDB ts here (224, 310, 395, 480, 565), not ts - sessionStart.
        // Making it relative put every lap boundary 92.71 s early, which is why
        // the trailing in-lap absorbed the whole session.
        double relStart = Math.Max(0.0, placed[i]);

        // A lap's duration is the gap to the NEXT line crossing. The last
        // crossing has no successor, so there is no lap after it - the
        // remaining time is a partial in-lap and must NOT be recorded as a
        // lap time. Treating it as one made it look like the fastest lap
        // (67.585s) when it is really an incomplete lap.
        double lapTime = 0.0;
        if (i + 1 < lapRows.Count)
        {
            lapTime = Math.Max(0.0, placed[i + 1] - placed[i]);
        }

        string lapName = cur.LapNum == 0 ? "Out" : $"Lap {cur.LapNum}";

        beacons.Add(new LapBeacon(cur.LapNum, lapName, relStart, lapTime));
    }

    AttachSectorTimes(beacons, tables);

    // The time after the final crossing is a partial in-lap. Record it so
    // the caller can tell "3 completed laps + an in-lap" apart, but never
    // let it reach the lap-time or fastest-lap calculations.
    if (sessionEndTs > placed[^1])
    {
        beacons.Add(new LapBeacon(
        LapBeacon.InLapLapNumber,
        "In",
        Math.Max(0.0, placed[^1]),
        sessionEndTs - placed[^1]));
    }
}
}
catch (Exception ex)
{
    Console.WriteLine($"[Warning] Failed reading lap beacons from {lapTable}: {ex.Message}");
}
}

// Fallback: Check eventsList or laps table
if (beacons.Count == 0)
{
    var eventTable = tables.FirstOrDefault(t =>
    t.Equals("eventsList", StringComparison.OrdinalIgnoreCase) ||
    t.Equals("events", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("laps", StringComparison.OrdinalIgnoreCase));

            if (eventTable != null)
            {
                try
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = "SELECT * FROM " + QuoteTableName(eventTable) + ";";
                    using var reader = cmd.ExecuteReader();

                    int timeCol = FindColumnIndex(reader, "time", "timestamp", "start_time", "start_time_s", "timestamp_ns", "ts");
                    int lapCol = FindColumnIndex(reader, "lap", "lap_number", "lap_num", "lap_index", "value");
                    int lapTimeCol = FindColumnIndex(reader, "lap_time", "duration", "lap_time_s", "laptime");

                    int currentLap = 1;
                    while (reader.Read())
                    {
                        double startTime = 0;
                        if (timeCol >= 0 && !reader.IsDBNull(timeCol))
                        {
                            var rawVal = Convert.ToDouble(reader.GetValue(timeCol));
                            if (rawVal > 1_000_000_000_000) startTime = rawVal / 1_000_000_000.0;
                            else if (rawVal > 1_000_000_000) startTime = rawVal / 1_000_000.0;
                            else if (rawVal > 1_000_000) startTime = rawVal / 1_000.0;
                            else startTime = rawVal;
                        }

                        int lapNum = lapCol >= 0 && !reader.IsDBNull(lapCol) ? Convert.ToInt32(reader.GetValue(lapCol)) : currentLap++;
                        double lapTime = lapTimeCol >= 0 && !reader.IsDBNull(lapTimeCol) ? Convert.ToDouble(reader.GetValue(lapTimeCol)) : 0;

                        beacons.Add(new LapBeacon(lapNum, $"Lap {lapNum}", startTime, lapTime));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed reading lap beacons from {eventTable}: {ex.Message}");
                }
            }
        }

        // If no beacons were parsed, create a default session beacon starting at 0.0s
        if (beacons.Count == 0)
        {
            beacons.Add(new LapBeacon(1, "Lap 1", 0.0, 0.0));
        }

        return beacons;
    }

/// <summary>
/// Attaches each lap's sector split times, relative to that lap's start, to its beacon.
/// </summary>
/// <remarks>
/// the game records sector boundaries in the "Current Sector" event table, which steps 0 to 1 at
/// the start of a lap, 1 to 2 at the first split and 2 back to 0 at the second. Only the
/// transitions matter: the rise to 1 coincides with the line crossing and is already
/// carried by the lap beacon, so the two falls are the splits that i2 pulses at.
/// </remarks>
private void AttachSectorTimes(List<LapBeacon> beacons, List<string> tables)
{
    var sectorTable = tables.FirstOrDefault(t =>
        t.Equals("Current Sector", StringComparison.OrdinalIgnoreCase));
    if (sectorTable == null) return;

    var transitions = new List<(double Ts, int Value)>();
    try
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT ts, value FROM " + QuoteTableName(sectorTable) +
                           " ORDER BY ts;";
        using var reader = cmd.ExecuteReader();
        int tsCol = FindColumnIndex(reader, "ts", "time", "timestamp");
        int valCol = FindColumnIndex(reader, "value", "sector");
        if (tsCol < 0 || valCol < 0) return;

        while (reader.Read())
        {
            if (reader.IsDBNull(tsCol) || reader.IsDBNull(valCol)) continue;
            transitions.Add((Convert.ToDouble(reader.GetValue(tsCol)),
                             Convert.ToInt32(reader.GetValue(valCol))));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Warning] Failed reading sector times: {ex.Message}");
        return;
    }

    if (transitions.Count < 2) return;

    // Both transitions are splits: the rise from one to two, and the fall back to zero.
    // The rise to one coincides with the line crossing, which the lap beacon already
    // carries, so it is skipped - a transition is a split when the sector number changes
    // but is not the entry into sector one.
    var splits = new List<double>();
    for (int i = 1; i < transitions.Count; i++)
    {
        if (transitions[i].Value != transitions[i - 1].Value &&
            transitions[i].Value != 1)
        {
            splits.Add(transitions[i].Ts);
        }
    }
    if (splits.Count == 0) return;

    // Each split belongs to the lap whose span contains it, and is stored relative to that
    // lap's start. Attaching every later split to every earlier lap would give the first lap
    // all of them and the last none.
    //
    // The FINAL lap is included as well. Its span has no next crossing, so it runs to the end
    // of the session. Excluding it is what left the Beacon a pulse short of the reference
    // (13 against 14) and the sector-time channels a value short: the DuckDB does record that
    // last lap's first split, and it is the "Cur Sector 1" run of 32.3 in a genuine log.
    var laps = beacons.Where(b => !b.IsInLap).ToList();
    for (int lap = 0; lap < laps.Count; lap++)
    {
        double from = laps[lap].StartTimeSeconds;
        double to = lap + 1 < laps.Count ? laps[lap + 1].StartTimeSeconds : double.MaxValue;
        foreach (double split in splits)
        {
            if (split > from && split <= to)
            {
                laps[lap].SectorTimesSeconds.Add(split - from);
            }
        }
    }

    // The final lap has no successor, and the genuine log's LAST transition carries no
    // beacon pulse and no sector-time value: that lap contributes only up to its first
    // split. Dropping its last split here keeps the Beacon and the sector-time channels
    // consistent by construction - they read the same list.
    var finalLap = laps[^1];
    if (finalLap.SectorTimesSeconds.Count > 1)
        finalLap.SectorTimesSeconds.RemoveAt(finalLap.SectorTimesSeconds.Count - 1);
}

/// <summary>
/// Builds "Last Laptime": the duration of the lap that most recently completed,
/// held until the next crossing.
/// </summary>
/// <remarks>
/// Transcribed from the genuine i2 log (log+example/Same data/Duckdb,
/// ID 2209), whose runs are <c>0 -> -1 -> 85.7 -> 85.3 -> 84.8 -> 84.7</c>:
/// <list type="bullet">
///   <item><description><b>0</b> until the FIRST line crossing - the out-lap is still
///   running and no lap has completed.</description></item>
///   <item><description><b>-1</b> from that first crossing until the first <em>timed</em>
///   lap completes. The out-lap's own duration is deliberately NOT written; that whole
///   interval IS the -1 region.</description></item>
///   <item><description>the duration of the lap that just completed, from each later
///   crossing onwards.</description></item>
/// </list>
/// The trailing partial (the in-lap, which has no successor) never contributes. An
/// earlier reading of the format had the out-lap's duration written at the first
/// crossing (92.71 -> 224.14 s); the reference bytes say otherwise, so it is not.
/// </remarks>
private float[]? BuildLastLapTimeChannel(IReadOnlyList<LapBeacon> beacons, int totalSamples,
double timeStep, double sessionStartSeconds)
{
    // Completed *timed* laps only - the out-lap (LapNumber 0) is excluded, because the
    // reference represents the entire out-lap as the -1 region described above.
    var completed = CompletedLapDurations(beacons);
    if (completed.Count == 0) return null;

    // The first line crossing, whatever closes it - the out-lap's completion time is
    // where the channel leaves 0 and becomes -1.
    var crossings = beacons.Where(b => !b.IsInLap && b.LapTimeSeconds > 0.0)
    .Select(b => b.StartTimeSeconds + b.LapTimeSeconds)
    .ToList();
    if (crossings.Count == 0) return null;
    double firstCrossing = crossings.Min();

    // (time at which the lap completes, duration of that lap)
    var events = new List<(double At, double Duration)>();
    foreach (var (start, duration) in completed)
    events.Add((start + duration, duration));
    events.Sort((a, b) => a.At.CompareTo(b.At));

    var samples = new float[totalSamples];
    int idx = 0;
    for (int s = 0; s < totalSamples; s++)
    {
        double t = sessionStartSeconds + s * timeStep;
        while (idx < events.Count && events[idx].At <= t) idx++;
        samples[s] = t < firstCrossing
        ? 0f                                  // out-lap still running
        : idx == 0 ? -1f                      // no completed timed lap yet
        : (float)events[idx - 1].Duration;
    }

    return samples;
}

/// <summary>
/// Builds "Best Laptime": the fastest completed lap so far, held for the session.
/// </summary>
private float[]? BuildBestLapTimeChannel(IReadOnlyList<LapBeacon> beacons, int totalSamples,
double timeStep, double sessionStartSeconds)
{
    // Only real laps count towards a best: the reference's "Best Laptime" steps 4
    // times (once per completed lap) against "Last Laptime"'s 5, so the out-lap is
    // excluded here even though Last Laptime includes it.
    var held = CompletedLapDurations(beacons);
    if (held.Count == 0) return null;

    var events = new List<(double At, double Best)>();
    double best = double.MaxValue;
    foreach (var (start, duration) in held)
    {
        if (duration < best) best = duration;
        events.Add((start + duration, best));
    }
    events.Sort((a, b) => a.At.CompareTo(b.At));

    var samples = new float[totalSamples];
    int idx = 0;
    for (int s = 0; s < totalSamples; s++)
    {
        double t = sessionStartSeconds + s * timeStep;
        while (idx < events.Count && events[idx].At <= t) idx++;
        samples[s] = idx == 0 ? -1f : (float)events[idx - 1].Best;
    }

    return samples;
}

/// <summary>The DuckDB tables holding the game's own recorded sector times.</summary>
private static readonly HashSet<string> SectorTimeTables = new(StringComparer.OrdinalIgnoreCase)
{
    "Current Sector1", "Current Sector2",
    "Last Sector1", "Last Sector2",
    "Best Sector1", "Best Sector2",
};

/// <summary>
/// Builds "Cur Sector N": the current lap's sector-N time, once that sector has completed.
/// </summary>
/// <remarks>
/// Transcribed from the genuine i2 log (IDs 2210/2211). The reference is a PULSE, not a
/// staircase: it holds the time from the moment the sector completes until the next lap
/// line, then drops to -1 for the part of the new lap in which that sector is still running.
/// Measured runs for "Cur Sector 1": -1, 32.7, -1, 32.5, -1, 32.4, -1, 32.1, -1, 32.3.
/// The out-lap carries no sector of its own and reads -1 throughout.
/// </remarks>
private float[]? BuildCurrentSectorChannel(IReadOnlyList<LapBeacon> beacons,
    int sectorIndex, float[]? source, int totalSamples, double timeStep, double sessionStartSeconds)
{
    if (source == null || source.Length < totalSamples) return null;

    // Only timed laps have a sector time. The out-lap (LapNumber 0) does not, and the
    // synthetic in-lap shares its start with the final lap, so it is excluded too.
    var laps = beacons.Where(b => !b.IsInLap && b.LapNumber > 0)
        .OrderBy(b => b.StartTimeSeconds).ToList();
    if (laps.Count == 0) return null;

    // Each lap's sector-N time is visible from the moment that sector completes until the lap
    // ends, and -1 for the part of the next lap in which the sector is still running.
    var marks = new List<(double From, double To, float Value)>();
    for (int i = 0; i < laps.Count; i++)
    {
        var lap = laps[i];
        if (lap.SectorTimesSeconds.Count <= sectorIndex) continue;
        double from = lap.StartTimeSeconds + lap.SectorTimesSeconds[sectorIndex];
        double to = i + 1 < laps.Count ? laps[i + 1].StartTimeSeconds : double.PositiveInfinity;
        marks.Add((from, to, RecordedValueAt(source, (int)Math.Round((from - sessionStartSeconds) / timeStep),
            (int)Math.Round(1.0 / timeStep))));
    }
    if (marks.Count == 0) return null;
    marks.Sort((a, b) => a.From.CompareTo(b.From));

    var samples = new float[totalSamples];
    int mi = -1;
    for (int s = 0; s < totalSamples; s++)
    {
        double t = sessionStartSeconds + s * timeStep;
        while (mi + 1 < marks.Count && marks[mi + 1].From <= t) mi++;
        samples[s] = mi >= 0 && t < marks[mi].To ? marks[mi].Value : -1f;
    }
    return samples;
}

/// <summary>
/// Builds "Last Sector N": the sector-N time of the most recently completed timed lap.
/// </summary>
/// <remarks>
/// The same shape as "Last Laptime": 0 until the first line crossing, -1 from that crossing
/// until the first timed lap completes, then the completed lap's time. Measured runs for
/// "Last Sector 1": 0, -1, 32.7, 32.5, 32.4, 32.1.
/// </remarks>
private float[]? BuildLastSectorChannel(IReadOnlyList<LapBeacon> beacons,
    int sectorIndex, float[]? source, int totalSamples, double timeStep, double sessionStartSeconds)
{
    if (source == null || source.Length < totalSamples) return null;

    var laps = beacons.Where(b => !b.IsInLap && b.LapNumber > 0)
        .OrderBy(b => b.StartTimeSeconds).ToList();
    if (laps.Count == 0) return null;

    // A lap completes at the next timed lap's start; the final lap never completes, so its
    // sector is never a "last" one.
    var completions = new List<(double At, float Value)>();
    for (int i = 0; i + 1 < laps.Count; i++)
    {
        if (laps[i].SectorTimesSeconds.Count <= sectorIndex) continue;
        double at = laps[i + 1].StartTimeSeconds;
        completions.Add((at, RecordedValueAt(source, (int)Math.Round((at - sessionStartSeconds) / timeStep),
            (int)Math.Round(1.0 / timeStep))));
    }
    if (completions.Count == 0) return null;

    var crossings = beacons.Where(b => !b.IsInLap && b.LapTimeSeconds > 0.0)
        .Select(b => b.StartTimeSeconds + b.LapTimeSeconds).ToList();
    if (crossings.Count == 0) return null;
    double firstCrossing = crossings.Min();

    var samples = new float[totalSamples];
    int idx = 0;
    for (int s = 0; s < totalSamples; s++)
    {
        double t = sessionStartSeconds + s * timeStep;
        while (idx < completions.Count && completions[idx].At <= t) idx++;
        samples[s] = t < firstCrossing ? 0f : idx == 0 ? -1f : completions[idx - 1].Value;
    }
    return samples;
}

/// <summary>
/// The value a recorded table settles on at or just after sample <paramref name="at"/>.
/// </summary>
/// <remarks>
/// The sector-time tables step a fraction of a second after the crossing whose time the
/// model uses, so reading the sample exactly at the split returns the previous value.
/// Scanning a short window picks up the value the table actually holds for that sector.
/// </remarks>
private static float RecordedValueAt(float[] source, int at, int window)
{
    if (at < 0 || at >= source.Length) return 0f;
    float first = source[at];
    int end = Math.Min(source.Length - 1, at + window);
    for (int j = at; j <= end; j++)
        if (source[j] != first) return source[j];
    return first;
}

/// <summary>
/// Builds "Best Sector N": the fastest sector-N time so far.
/// </summary>
/// <remarks>
/// -1 until the first timed lap's sector completes. The reference updates at the SECTOR
/// split, not at the lap line: measured runs for "Best Sector 1" are -1, 32.7, 32.5, 32.4,
/// 32.1, i.e. a new value at each lap's sector-1 split.
/// </remarks>
private float[]? BuildBestSectorChannel(IReadOnlyList<LapBeacon> beacons,
    int sectorIndex, float[]? source, int totalSamples, double timeStep, double sessionStartSeconds)
{
    if (source == null || source.Length < totalSamples) return null;

    var laps = beacons.Where(b => !b.IsInLap && b.LapNumber > 0)
        .OrderBy(b => b.StartTimeSeconds).ToList();
    if (laps.Count == 0) return null;

    var events = new List<(double At, double Value)>();
    double best = double.MaxValue;
    foreach (var lap in laps)
    {
        if (lap.SectorTimesSeconds.Count <= sectorIndex) continue;
        double at = lap.StartTimeSeconds + lap.SectorTimesSeconds[sectorIndex];
        double value = RecordedValueAt(source, (int)Math.Round((at - sessionStartSeconds) / timeStep),
            (int)Math.Round(1.0 / timeStep));
        if (value < best) best = value;
        events.Add((at, best));
    }
    if (events.Count == 0) return null;
    events.Sort((a, b) => a.At.CompareTo(b.At));

    var samples = new float[totalSamples];
    int idx = 0;
    for (int s = 0; s < totalSamples; s++)
    {
        double t = sessionStartSeconds + s * timeStep;
        while (idx < events.Count && events[idx].At <= t) idx++;
        samples[s] = idx == 0 ? -1f : (float)events[idx - 1].Value;
    }
    return samples;
}

/// <summary>
/// Completed laps as (start time, duration): the out-lap, and any crossing with no
/// successor (which starts the in-lap rather than completing a lap), are excluded.
/// </summary>
private static List<(double Start, double Duration)> CompletedLapDurations(
IReadOnlyList<LapBeacon> beacons) =>
beacons.Where(b => !b.IsInLap && b.LapNumber > 0 && b.LapTimeSeconds > 0.0)
.Select(b => (b.StartTimeSeconds, b.LapTimeSeconds))
.OrderBy(x => x.Item1)
.ToList();

/// <summary>
/// Builds a "Lap Number" channel: an integer counter that steps up at each line
/// crossing and holds until the next one.
/// </summary>
/// <remarks>
/// A genuine i2 log carries "Lap Number" (dtype 5, integer) and that is what
/// changes to define a lap boundary. It steps 2 -&gt; 3 -&gt; 4 across a 5-lap
/// session's active portion. "Lap Time Running" alone left i2 showing the whole
/// session as one lap, so both are emitted.
/// </remarks>
private float[]? BuildLapNumberChannel(IReadOnlyList<LapBeacon> beacons, int totalSamples,
double timeStep, double sessionStartSeconds)
{
    if (beacons.Count == 0) return null;

    // The same boundaries the running timer uses: each real line crossing, the
    // synthetic in-lap excluded.
    var boundaries = beacons.Where(b => !b.IsInLap)
    .Select(b => b.StartTimeSeconds)
    .ToList();
    if (boundaries.Count == 0) return null;

    return BuildSteppedChannel(boundaries, totalSamples, timeStep, invert: false,
    sessionStartSeconds);
}

/// <summary>
/// Builds "Lap Start Elapsed Time": the session time at which the current lap
/// began, held until the next line crossing.
/// </summary>
/// <remarks>
/// This is the channel i2 differences to obtain lap times:
/// <c>lap_time[n] = LapET[n] - LapET[n-1]</c>. Verified against the reference log,
/// and against a genuine log recorded from the same DuckDB session, whose steps
/// of 224, 310, 395, 480, 565 yield lap times of 86, 85, 85, 85 s against a .ldx
/// fastest time of 84.708 s.
///
/// The values are ABSOLUTE session times, matching "Session Elapsed Time". The
/// sample index must therefore be compared against the same absolute clock:
/// <paramref name="sessionStartSeconds"/> is the time of the first sample.
/// </remarks>
private float[]? BuildLapStartElapsedChannel(
IReadOnlyList<LapBeacon> beacons, int totalSamples, double timeStep,
double sessionStartSeconds)
{
    if (beacons.Count == 0) return null;

    // i2 stores 0 until the FIRST completed lap begins. A genuine log of a
    // 4-lap session has LapET distinct values 0, 224, 310, 395, 480, 565 - the
    // leading 0 is not a 92.71 s segment, it is "no lap yet". Emitting the out-lap
    // start as its own boundary added a phantom short lap and inflated the count.
    // So: 0 until the first crossing, then each subsequent crossing.
    var boundaries = beacons.Where(b => !b.IsInLap)
    .Select(b => b.StartTimeSeconds)
    .ToList();
    if (boundaries.Count == 0) return null;

    // The first crossing ends the out-lap; no lap has started yet, so it reads 0.
    if (boundaries.Count >= 1) boundaries[0] = 0.0;

    var samples = new float[totalSamples];
    int lapIndex = 0;
    for (int s = 0; s < totalSamples; s++)
    {
        double t = sessionStartSeconds + s * timeStep;
        while (lapIndex + 1 < boundaries.Count && boundaries[lapIndex + 1] <= t) lapIndex++;
        samples[s] = (float)boundaries[lapIndex];
    }

    return samples;
}

/// <summary>
/// Builds i2's "Beacon" channel (ID 110) from the lap and sector crossings.
/// </summary>
/// <remarks>
/// This is the channel i2 reads laps from. Its structure, transcribed from two genuine
/// i2-written logs covering 31 pulses between them:
///
///   three samples per crossing, at 5 Hz, in this fixed frame:
///     sample 0 = <see cref="PulseMarker"/>, a constant in both references;
///     sample 1 = a counter starting at -32767, rising by one per pulse and by two at the
///                sector-two line;
///     sample 2 = an unaccounted value, 16421 to 17382 across the two files, with no
///                relation to the crossing time, the lap, or the sector;
///   and then the channel settles, holding <see cref="LapSettleValue"/> until a lap line or
///   <see cref="SectorSettleValue"/> until a sector split.
///
/// The settle value is the discriminator: 100 after a lap line, 56 after a sector split,
/// unambiguous across all 31 pulses. Before the first crossing it holds 0.
///
/// Pulse <em>times</em> were verified against the DuckDB's own lap and sector events. With
/// the 92.71 s offset of that session, all 14 genuine pulses match an event to within 0.7 s
/// and each lands on the correct kind: the five that settle at 100 are the five lap lines,
/// the nine that settle at 56 are the nine sector splits. The Sector3 Flag events at
/// 126.17 s and 144.69 s correctly produce no pulse.
///
/// The third sample is the crossing's sub-second time - see <see cref="PulsePayloadBase"/>.
/// It is computed from the crossing time, not read from the game, and i2 does depend on
/// it: held at a constant, lap times collapse to whole seconds.
/// </remarks>
private float[]? BuildBeaconChannel(IReadOnlyList<LapBeacon> beacons, int totalSamples,
    double timeStep, double sessionStartSeconds, RatePolicy policy, int targetFrequencyHz)
{
    if (beacons.Count == 0) return null;

    // The pulse is three samples of the channel's OWN rate, so it has to be built on that
    // grid. Built on the 100 Hz resample grid and then decimated to 5 Hz it disappears: the
    // decimation keeps every twentieth sample, so a pulse 0.03 s wide is never sampled.
    // The genuine log shows the pulse occupying three consecutive 5 Hz samples, 0.6 s in all.
    int outHz = policy.Resolve("Beacon", ChannelIds.Lookup("Beacon"), targetFrequencyHz);
    if (outHz <= 0) outHz = targetFrequencyHz;
    double sessionSeconds = (totalSamples - 1) * timeStep;
    int outCount = Math.Max(1, (int)Math.Round(sessionSeconds * outHz) + 1);
    double outStep = 1.0 / outHz;

    // Every crossing i2 pulses at, in the order it pulses at them: a lap line, then that
    // lap's two sector splits, then the next lap line, and so on.
    //
    // Verified against the genuine log for the paired session. With that log's 92.71 s
    // offset, 13 of its 14 pulses land on one of these boundaries to within 0.13 s:
    //
    //   224.14 lap   256.82 rise   290.88 fall    <- lap 1
    //   309.82 lap   342.30 rise   375.96 fall    <- lap 2
    //   395.10 lap   427.50 rise   461.22 fall    <- lap 3
    //   479.88 lap   512.02 rise   545.64 fall    <- lap 4
    //   564.58 lap   596.91 rise                  <- lap 5, still in progress
    //
    // The out-lap contributes nothing - neither its line at 92.71 s nor its sector splits
    // at 171.36 s and 205.14 s. That matches the .ldx, which does not count the out-lap as
    // a completed lap.
    var pulses = new List<(double Time, bool IsLap)>();
    foreach (var b in beacons)
    {
        // The out-lap is the session's first segment rather than a completed lap, and the
        // trailing in-lap is a partial one. Neither pulses.
        if (b.IsInLap || b.LapNumber <= 0) continue;

        pulses.Add((b.StartTimeSeconds, true));

        // SectorTimesSeconds holds this lap's split times relative to its start. The two
        // kinds - the rise to sector two and the fall back to zero - alternate, and the
        // counter steps by two after the rise, so they are kept in time order.
        foreach (double sector in b.SectorTimesSeconds.OrderBy(s => s))
        {
            pulses.Add((b.StartTimeSeconds + sector, false));
        }
    }
    if (pulses.Count == 0) return null;
    pulses.Sort((a, b) => a.Time.CompareTo(b.Time));

    // The counter in the pulse's second sample starts at -32767 and rises by one per pulse,
    // except after the rise to sector two, where it rises by two. Observed deltas across the
    // genuine log's 14 pulses: +1, +2, +1, +1, +2, +1, +1, +2, +1, +1, +2, +1, +1.
    var counters = new int[pulses.Count];
    counters[0] = (int)PulseCounterStartValue;
    for (int i = 1; i < pulses.Count; i++)
    {
        // A rise is a sector pulse whose predecessor was the lap line itself.
        bool afterRise = !pulses[i].IsLap && i >= 2 && pulses[i - 1].IsLap == false
                         && pulses[i - 2].IsLap;
        counters[i] = counters[i - 1] + (afterRise ? 2 : 1);
    }

    var samples = new float[outCount];
    int pulseIndex = 0;

    for (int s = 0; s < outCount; s++)
    {
        double t = sessionStartSeconds + s * outStep;

        // A pulse occupies three consecutive samples; anything older has expired.
        while (pulseIndex < pulses.Count && pulses[pulseIndex].Time + 3 * outStep <= t)
        {
            pulseIndex++;
        }

        if (pulseIndex < pulses.Count)
        {
            int slot = (int)Math.Floor((t - pulses[pulseIndex].Time) / outStep + 1e-9);

            // A negative slot means the sample precedes the next pulse entirely, so it holds
            // whatever was settled by the previous one - or 0 before any pulse has happened.
            // Falling through to the default arm here instead made the channel read as though
            // the first pulse had already occurred, and inverted every lap/sector flag after.
            samples[s] = slot switch
            {
                0 => PulseMarkerValue,
                1 => counters[pulseIndex],
                2 => (float)(PulsePayloadBase + Math.Round(PulsePayloadScale
                        * Frac(pulses[pulseIndex].Time - sessionStartSeconds))),
                _ => slot >= 3
                    ? (pulses[pulseIndex].IsLap ? LapSettleValue : SectorSettleValue)
                    : SettledValue(pulses, pulseIndex),
            };
            continue;
        }

        // After the last pulse has settled, hold its value for the rest of the session.
        samples[s] = SettledValue(pulses, pulseIndex);
    }

    return samples;
}

/// <summary>
/// The value the channel holds between pulses: the previous pulse's settle value, or 0
/// before any pulse has occurred.
/// </summary>
private static float SettledValue(List<(double Time, bool IsLap)> pulses, int pulseIndex) =>
    pulseIndex > 0
        ? (pulses[pulseIndex - 1].IsLap ? LapSettleValue : SectorSettleValue)
        : 0f;

/// <summary>Constant first sample of every pulse; identical in both references.</summary>
private const float PulseMarkerValue = -8192f;

/// <summary>The counter's first value; identical in both references.</summary>
private const float PulseCounterStartValue = -32767f;

/// <summary>
/// Base added to the pulse's third sample: the crossing's sub-second time, in milliseconds.
/// </summary>
/// <remarks>
/// The payload is the crossing's sub-second time in milliseconds, offset by 0x4000:
///
///     payload = 0x4000 + round(1000 * frac(t))
///
/// where t is the crossing's time on the file's own time axis - the Beacon channel's first
/// sample is 0, so t is the session time minus the session's start offset.
///
/// Verified exactly against the genuine log's five lap-line crossings, whose times are
/// known to the millisecond from its own displayed lap times:
///
///     t = 131.302 -&gt; 0x4000 + 302 = 16686   (genuine 16686)
///     t = 216.986 -&gt; 0x4000 + 986 = 17370   (genuine 17370)
///     t = 302.263 -&gt; 0x4000 + 263 = 16647   (genuine 16647)
///     t = 387.049 -&gt; 0x4000 +  49 = 16433   (genuine 16433)
///     t = 471.757 -&gt; 0x4000 + 757 = 17141   (genuine 17141)
///
/// All fourteen crossings agree to within the 0.02 s at which crossing times can be read
/// from the log's Current Sector channel.
/// <para>
/// An earlier version held this slot at a constant 16900, having judged the value an RF
/// artefact: it correlates with the crossing time only once that time is taken modulo one
/// second, and the earlier searches correlated against crossing times quantised to the
/// 0.2 s Beacon sample grid, which discards exactly the sub-second information this field
/// carries. A constant payload costs i2 its sub-sample resolution, and lap times then
/// collapse to whole seconds.
/// </para>
/// </remarks>
private const float PulsePayloadBase = 0x4000;

/// <summary>Units of the payload's sub-second time: milliseconds per second.</summary>
private const double PulsePayloadScale = 1000.0;

/// <summary>Fractional part of a value, always in [0, 1).</summary>
private static double Frac(double v) => v - Math.Floor(v);

/// <summary>Value the channel holds after a lap line.</summary>
private const float LapSettleValue = 100f;

/// <summary>Value the channel holds after a sector split.</summary>
private const float SectorSettleValue = 56f;

/// <summary>
/// Builds "Session Elapsed Time": whole seconds since the session start.
/// </summary>
/// <remarks>
/// The value is ABSOLUTE session time, not time since the first sample. Verified
/// against a genuine i2 log recorded from the same DuckDB session
/// (log+example/Same data/Duckdb):
///     DuckDB first row ts      =  92.71 s
///     genuine Session Elapsed Time starts at 93.0 and steps once per second
/// Our previous version started at 0, i.e. 92.71 s behind. i2 aligns the lap
/// channels against this one, so every lap boundary landed at the wrong instant
/// and the trailing in-lap absorbed the whole session.
/// </remarks>
private float[]? BuildSessionElapsedChannel(int totalSamples, double timeStep,
double sessionStartSeconds)
{
    if (totalSamples <= 0) return null;

    var samples = new float[totalSamples];
    for (int s = 0; s < totalSamples; s++)
    {
        samples[s] = (float)Math.Floor(sessionStartSeconds + s * timeStep);
    }

    return samples;
}

/// <summary>
/// Shared sampler for the two synthesised lap channels.
/// </summary>
/// <param name="invert">
/// false: value counts up from the first boundary (Lap Number).
/// true: value is the elapsed time since the most recent boundary (Lap Time Running).
/// </param>
private float[] BuildSteppedChannel(
List<double> boundaries, int totalSamples, double timeStep, bool invert,
double sessionStartSeconds = 0.0)
{
    var samples = new float[totalSamples];
    int lapIndex = 0;

    for (int s = 0; s < totalSamples; s++)
    {
        double t = sessionStartSeconds + s * timeStep;

        // Advance to the most recent boundary at or before t.
        while (lapIndex + 1 < boundaries.Count && boundaries[lapIndex + 1] <= t) lapIndex++;

        samples[s] = invert
        ? (float)Math.Max(0.0, t - boundaries[lapIndex])
        : lapIndex;
    }

    return samples;
}

/// <summary>
/// Reads all telemetry channels and resamples them onto a consistent target frequency grid.
/// </summary>
    public List<TelemetryChannel> ReadAndResampleChannels(
        int targetFrequencyHz = RatePolicy.ResampleTargetHz,
        IProgress<int>? progress = null,
        RatePolicy? ratePolicy = null,
        CancellationToken cancel = default,
        bool useDuckDbChannelNames = false)
{
    EnsureOpen();
    var tables = GetTableNames();
        // The policy chooses each channel's output rate; targetFrequencyHz is the
        // grid everything is interpolated onto before being decimated to it.
        var policy = ratePolicy ?? new RatePolicy();

    // 1. Read channelsList metadata (frequency & units)
        var channelsMeta = new Dictionary<string, (int Frequency, string Unit)>(StringComparer.OrdinalIgnoreCase);
        if (tables.Contains("channelsList", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT channelName, frequency, unit FROM " + QuoteTableName("channelsList") + ";";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var chName = reader.GetString(0);
                    int freq = reader.GetInt32(1);
                    var unit = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    channelsMeta[chName] = (freq, unit);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed reading channelsList: {ex.Message}");
            }
        }

        // 2. Determine session start timestamp & duration from GPS Time
        double sessionStartTs = 0.0;
        double sessionDuration = 0.0;

        if (tables.Contains("GPS Time", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT MIN(value), MAX(value) FROM " + QuoteTableName("GPS Time") + ";";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    sessionStartTs = reader.GetDouble(0);
                    sessionDuration = reader.GetDouble(1) - sessionStartTs;
                }
            }
            catch { }
        }

        // Fallback for duration if GPS Time missing
        if (sessionDuration <= 0.0)
        {
            foreach (var t in tables)
            {
                if (t.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("channelsList", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("eventsList", StringComparison.OrdinalIgnoreCase)) continue;

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM " + QuoteTableName(t) + ";";
                var cntObj = cmd.ExecuteScalar();
                if (cntObj != null && long.TryParse(cntObj.ToString(), out var count) && count > 1)
                {
                    int freq = channelsMeta.TryGetValue(t, out var meta) ? meta.Frequency : 50;
                    sessionDuration = Math.Max(sessionDuration, (count - 1) / (double)freq);
                }
            }
        }

        if (sessionDuration <= 0.0) sessionDuration = 1.0;

        int totalSamples = Math.Max(1, (int)Math.Round(sessionDuration * targetFrequencyHz) + 1);
        double timeStep = 1.0 / targetFrequencyHz;

        var resultChannels = new List<TelemetryChannel>();

        // Resampled samples for the six sector-time tables, captured as they are read so the
        // sector channels built lower down can use the game's own recorded sector times.
        // Those are marginally more accurate than the difference between two crossing
        // timestamps: lap 4's second sector is 65.76 by subtraction but 65.7 as recorded,
        // and the reference shows 65.7.
        var sectorTimeSources = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        var excludeTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "metadata", "channelsList", "eventsList", "events", "Lap"
        };

        var candidateTables = tables.Where(t => !excludeTables.Contains(t)).ToList();
        int channelId = 0;

        for (int tIdx = 0; tIdx < candidateTables.Count; tIdx++)
        {
            var tableName = candidateTables[tIdx];
            cancel.ThrowIfCancellationRequested();
            progress?.Report((int)((tIdx + 1) / (float)candidateTables.Count * 50));

            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT * FROM " + QuoteTableName(tableName) + ";";
                using var reader = cmd.ExecuteReader();

                var colNames = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++) colNames.Add(reader.GetName(i));

                bool hasTs = colNames.Any(c => c.Equals("ts", StringComparison.OrdinalIgnoreCase) || c.Equals("time", StringComparison.OrdinalIgnoreCase));
                channelsMeta.TryGetValue(tableName, out var nativeMeta);

                // Case A: Event / State Table with 'ts' column (e.g. Gear, TC, ABS, In Pits, Flags, etc.)
                if (hasTs)
                {
                    int tsCol = FindColumnIndex(reader, "ts", "time", "timestamp");
                    var valueCols = colNames.Select((name, index) => (name, index)).Where(c => c.index != tsCol).ToList();

                    var events = new List<(double Ts, float[] Values)>();
                    while (reader.Read())
                    {
                        double ts = Convert.ToDouble(reader.GetValue(tsCol));
                        var vals = new float[valueCols.Count];
                        for (int i = 0; i < valueCols.Count; i++)
                        {
                            vals[i] = reader.IsDBNull(valueCols[i].index) ? 0f : Convert.ToSingle(reader.GetValue(valueCols[i].index));
                        }
                        events.Add((ts, vals));
                    }

                    if (events.Count == 0) continue;

                    // Sort events by timestamp
                    events.Sort((a, b) => a.Ts.CompareTo(b.Ts));

                    for (int vIdx = 0; vIdx < valueCols.Count; vIdx++)
                    {
                        var colName = valueCols[vIdx].name;
                        var rawName = (valueCols.Count == 1) ? tableName : $"{tableName}_{colName}";

                        ChannelDictionary.ChannelMeta meta;
                        if (colName.StartsWith("value", StringComparison.OrdinalIgnoreCase) && colName.Length > 5)
                        {
                            var wheel = colName.ToLowerInvariant() switch
                            {
                                "value1" => "FL",
                                "value2" => "FR",
                                "value3" => "RL",
                                "value4" => "RR",
                                _ => colName
                            };
                            meta = ChannelDictionary.GetWheelChannelMetadata(
                                tableName, wheel, nativeMeta.Unit ?? "");
                        }
                        else
                        {
                            meta = ChannelDictionary.GetMetadata(rawName, nativeMeta.Unit ?? "");
                        }

                        var samples = new float[totalSamples];
                        int eventIdx = 0;

                        // Step / Hold-last-value interpolation
                        for (int s = 0; s < totalSamples; s++)
                        {
                            double targetTs = sessionStartTs + (s * timeStep);

                            while (eventIdx < events.Count - 1 && events[eventIdx + 1].Ts <= targetTs)
                            {
                                eventIdx++;
                            }

                            samples[s] = events[eventIdx].Values[vIdx] * meta.Multiplier;
                        }

                        // The DuckDB records sector boundaries as a state table whose first row is
                        // the first transition the game captured. For "Current Sector" that is
                        // the out-lap's 1->2 step, so hold-last-value fills the session's
                        // opening minutes with 1. The genuine DamPlugin log opens at 0: it
                        // carries the out-lap's 0->1 transition, which the DuckDB does not
                        // record.
                        //
                        // Verified against the genuine same-session log. At the same relative
                        // times the DuckDB's value is one step ahead of the genuine across all
                        // 17 transitions - it opens at 1 where the genuine opens at 0, and the
                        // two sequences stay in phase. The DuckDB's first recorded sample is
                        // after the 0->1 transition already occurred, so its whole channel is
                        // shifted by +1. Rolling every value back by one (mod 3) reproduces the
                        // genuine sequence without inventing a transition that never happened.
                        if (rawName.Equals("Current Sector", StringComparison.OrdinalIgnoreCase) &&
                            events.Count > 0 && events[0].Values[vIdx] != 0f)
                        {
                            for (int s = 0; s < totalSamples; s++)
                            {
                                samples[s] = ((int)samples[s] + 2) % 3;
                            }
                        }

                        // Keep the resampled samples for the sector-time tables. They are not
                        // emitted from this loop, but the sector channels built below read
                        // their values from here.
                        if (SectorTimeTables.Contains(rawName) && samples.Length == totalSamples)
                            sectorTimeSources[rawName] = samples;

                        // "Best Sector 1/2", "Last Sector 1/2" and "Cur Sector 1/2" are no
                        // longer emitted from here. Their DuckDB tables hold the right values,
                        // but a hold-last resample turns them into staircases, whereas the
                        // reference pulses them and drops to a sentinel between laps. They are
                        // rebuilt from the lap splits below (Build*SectorChannel) and removed
                        // from this batch by name, like the lap-time channels.

                        AddChannel(resultChannels, ref channelId, rawName, meta.StandardName, meta.ShortName,
                        meta.Units, targetFrequencyHz, samples, policy,
                              nativeMeta.Frequency, useDuckDbChannelNames: useDuckDbChannelNames);
                    }
                }
                // Case B: Continuous / Stream Table (e.g. Ground Speed, Engine RPM, Susp Pos, RideHeights, etc.)
                else
                {
                    // Check if multi-column (e.g. value1, value2, value3, value4 for wheels)
                    var isMultiWheel = colNames.Count == 4 &&
                    colNames.Contains("value1", StringComparer.OrdinalIgnoreCase) &&
                    colNames.Contains("value2", StringComparer.OrdinalIgnoreCase) &&
                    colNames.Contains("value3", StringComparer.OrdinalIgnoreCase) &&
                    colNames.Contains("value4", StringComparer.OrdinalIgnoreCase);

                    if (isMultiWheel)
                    {
                        var flList = new List<float>();
                        var frList = new List<float>();
                        var rlList = new List<float>();
                        var rrList = new List<float>();

                        int v1Idx = colNames.FindIndex(c => string.Equals(c, "value1", StringComparison.OrdinalIgnoreCase));
                        int v2Idx = colNames.FindIndex(c => string.Equals(c, "value2", StringComparison.OrdinalIgnoreCase));
                        int v3Idx = colNames.FindIndex(c => string.Equals(c, "value3", StringComparison.OrdinalIgnoreCase));
                        int v4Idx = colNames.FindIndex(c => string.Equals(c, "value4", StringComparison.OrdinalIgnoreCase));

                        while (reader.Read())
                        {
                            flList.Add(reader.IsDBNull(v1Idx) ? 0f : Convert.ToSingle(reader.GetValue(v1Idx)));
                            frList.Add(reader.IsDBNull(v2Idx) ? 0f : Convert.ToSingle(reader.GetValue(v2Idx)));
                            rlList.Add(reader.IsDBNull(v3Idx) ? 0f : Convert.ToSingle(reader.GetValue(v3Idx)));
                            rrList.Add(reader.IsDBNull(v4Idx) ? 0f : Convert.ToSingle(reader.GetValue(v4Idx)));
                        }

                        var wheelData = new[] { ("FL", flList), ("FR", frList), ("RL", rlList), ("RR", rrList) };
                        foreach (var (wheel, rawPoints) in wheelData)
                        {
                            if (rawPoints.Count == 0) continue;
                            var meta = ChannelDictionary.GetWheelChannelMetadata(
                                tableName, wheel, nativeMeta.Unit ?? "");
                            int nativeFreq = nativeMeta.Frequency > 0 ? nativeMeta.Frequency : 0;
                            var samples = ResampleStream(rawPoints, totalSamples, sessionDuration, nativeFreq, meta.Multiplier);

                            AddChannel(resultChannels, ref channelId, $"{tableName}_{wheel}", meta.StandardName, meta.ShortName,
                            meta.Units, targetFrequencyHz, samples, policy, nativeFreq, useDuckDbChannelNames: useDuckDbChannelNames);
                        }
                    }
                    else
                    {
                        // Single value column
                        int vIdx = FindColumnIndex(reader, "value", "val", "data", "sample");
                        if (vIdx < 0) vIdx = 0;

                        var rawPoints = new List<float>();
                        while (reader.Read())
                        {
                            rawPoints.Add(reader.IsDBNull(vIdx) ? 0f : Convert.ToSingle(reader.GetValue(vIdx)));
                        }

                        if (rawPoints.Count > 0)
                        {
                            var meta = ChannelDictionary.GetMetadata(tableName, nativeMeta.Unit ?? "");
                            int nativeFreq2 = nativeMeta.Frequency > 0 ? nativeMeta.Frequency : 0;
                            var samples = ResampleStream(rawPoints, totalSamples, sessionDuration, nativeFreq2, meta.Multiplier);

                            AddChannel(resultChannels, ref channelId, tableName, meta.StandardName, meta.ShortName,
                            meta.Units, targetFrequencyHz, samples, policy, nativeFreq2, useDuckDbChannelNames: useDuckDbChannelNames);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed reading channel table '{tableName}': {ex.Message}");
            }
        }

        // the game's "Lap Time" is a 2-row event table, not a time series, so resampling it
        // yields a flat curve with no lap information. Drop it and synthesise the
        // channels a genuine i2 log actually uses to define laps, taken from
        // log+example/2026-08-03 ... Laguna Seca - P1.ld:
        //
        //   "Lap Start Elapsed Time" (LapET) - session time at which the CURRENT lap
        //     began; -1 before the first crossing. This is the key one: i2 derives
        //     lap_time[n] = LapET[n] - LapET[n-1], so without it every lap reads 0.
        //   "Session Elapsed Time"  (SessET) - seconds since the session start.
        //   "Lap Number"             (Lap N)   - the lap counter.
        //
        // A user test showed i2 reads the .ld and then REWRITES the .ldx, so the
        // .ld channels are the input and the .ldx is only the summary.
        var beacons = ReadLapBeacons();
        var lapTimeMeta = ChannelDictionary.GetMetadata("Lap Time", "s");

        // Replace any the game-sourced "Lap Time" with our synthesised lap channels.
        int degenerate = resultChannels.FindIndex(c =>
        c.Identity.Equals(lapTimeMeta.StandardName, StringComparison.OrdinalIgnoreCase));
        if (degenerate >= 0) resultChannels.RemoveAt(degenerate);

        // Drop any the game-sourced channel whose i2 name we are about to synthesise.
        // "Best LapTime" maps to "Best Laptime", which we also build from the Lap events,
        // so without this the file carried Best Laptime (ID 2206) twice. A duplicated ID
        // means i2 resolves the channel by number and cannot tell the two apart.
        var synthesised = new[]
        {
            "Lap Start Elapsed Time", "Session Elapsed Time", "Lap Number",
            "Last Laptime", "Best Laptime",
            // The sector-time channels are rebuilt from the lap splits too. Their DuckDB
            // tables carry the right values but a hold-last resample leaves them as
            // staircases, where the reference pulses them and drops to a sentinel between
            // laps - see the Build*SectorChannel remarks.
            "Cur Sector 1", "Cur Sector 2", "Last Sector 1", "Last Sector 2",
            "Best Sector 1", "Best Sector 2",
        };
        resultChannels.RemoveAll(c => synthesised.Contains(c.Identity,
            StringComparer.OrdinalIgnoreCase));

        // Drop the game's own lap-time event channels: neither appears in a genuine i2
        // log, neither has a standard ID, and both are misleading.
        //   "Lap Time Running"  - a sawtooth that reaches the out-lap length and never
        //                          resets, so it reads as a signal that never ends.
        //   "Current LapTime"   - a running BEST-so-far, ascending (84.708, 84.786,
        //                          85.277, 85.684 on the paired session), so it gets
        //                          numerically worse as you drive faster laps.
        // i2 reads lap times from "Last Laptime" (2209) and the session best from
        // "Best Laptime" (2206), both synthesised above.
        resultChannels.RemoveAll(c =>
            c.Identity.Equals("Lap Time Running", StringComparison.OrdinalIgnoreCase) ||
            c.Identity.Equals("Current LapTime", StringComparison.OrdinalIgnoreCase));

        AddSynthesised(resultChannels, ref channelId, "Lap Start Elapsed Time",
            BuildLapStartElapsedChannel(beacons, totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Session Elapsed Time",
            BuildSessionElapsedChannel(totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Lap Number",
            BuildLapNumberChannel(beacons, totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
            // "Lap Time Running" is deliberately NOT emitted. It appears in neither genuine
            // i2 log, it has no standard channel ID (so it lands on a synthetic one), and
            // it is a sawtooth that runs to 131 s on this session - the out-lap - without ever
            // resetting at the end. A user test confirmed it shows up in the channel list as a
            // signal that never ends. i2 reads lap times from "Last Laptime" (2209), which
            // we do emit, so this channel only adds noise.
            AddSynthesised(resultChannels, ref channelId, "Last Laptime",
            BuildLastLapTimeChannel(beacons, totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Best Laptime",
            BuildBestLapTimeChannel(beacons, totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);

        // Sector times, rebuilt from the lap splits so they reproduce the reference's pulse
        // shape rather than a hold-last staircase. Sector 1 is the time to the first split,
        // sector 2 the time to the second (both measured from the lap's start).
        // The recorded sector times come from the sector-time tables. Best reads the
        // "Current" table because it needs each lap's own time, not the running best, and
        // the reference updates it at the split rather than at the lap line.
        float[]? Src(string key) =>
            sectorTimeSources.TryGetValue(key, out var v) ? v : null;

        AddSynthesised(resultChannels, ref channelId, "Current Sector1",
            BuildCurrentSectorChannel(beacons, 0, Src("Current Sector1"), totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Current Sector2",
            BuildCurrentSectorChannel(beacons, 1, Src("Current Sector2"), totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Last Sector1",
            BuildLastSectorChannel(beacons, 0, Src("Last Sector1"), totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Last Sector2",
            BuildLastSectorChannel(beacons, 1, Src("Last Sector2"), totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Best Sector1",
            BuildBestSectorChannel(beacons, 0, Src("Current Sector1"), totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);
        AddSynthesised(resultChannels, ref channelId, "Best Sector2",
            BuildBestSectorChannel(beacons, 1, Src("Current Sector2"), totalSamples, timeStep, sessionStartTs),
            targetFrequencyHz, policy);

        // "Beacon" (ID 110) is the channel i2 actually reads laps from. It is a
        // three-sample pulse at each lap line and each sector split, then settles at
        // 100 for a lap or 56 for a sector. See BuildBeaconChannel for how the frame
        // was transcribed, and for the one value in it that is not derived.
        AddSynthesised(resultChannels, ref channelId, "Beacon",
            BuildBeaconChannel(beacons, totalSamples, timeStep, sessionStartTs, policy,
                targetFrequencyHz),
            targetFrequencyHz, policy, alreadyAtOutputRate: true);

        return resultChannels;
    }

    /// <summary>
    /// Appends a channel, honouring i2's own per-channel sample rate.
    /// </summary>
    /// <remarks>
    /// Every channel this converter emits goes through here, so the rate rule cannot be
    /// applied to some and missed on others. A declared rate that disagrees with the
    /// sample count makes i2 read the channel at the wrong instants - which is
    /// indistinguishable, in the lap table, from a channel with no lap information.
    /// </remarks>
    private static void AddChannel(
        List<TelemetryChannel> channels, ref int channelId,
        string originalName, string channelName, string shortName, string units,
        int targetFrequencyHz, float[]? samples,
        RatePolicy policy, int sourceHz, bool alreadyAtOutputRate = false,
        bool useDuckDbChannelNames = false)
    {
        if (samples == null || samples.Length == 0) return;

        // i2 matches a channel by its standard ID (the field at 0x10) AND its name.
        // An the game-spelled name that carries a i2 ID is therefore only half resolved,
        // so apply the alias to the emitted name too. "Lap Dist" becomes "Lap Distance",
        // "FrontFlapActivated" becomes "Front Flap Activated".
        //
        // This resolved name is the channel's i2 IDENTITY (TelemetryChannel.IdName): every
        // lookup - standard ID, rate, quantisation, de-duplication - keys on it. A DuckDB-name
        // build changes only the DISPLAY name written at 0x20.
        string stdName = ChannelIds.StandardName(channelName);

        // The DuckDB-name build swaps the displayed name for the source spelling, EXCEPT for
        // the channels i2's built-in maths resolve by name, and i2's lap/sector machinery
        // (ChannelIds.KeepStandardNames) - renaming one of those reads as a missing channel
        // in i2 and breaks the CorrSpeed / CorrDist / GPS / Damper-Velocity maths. The short
        // name stays i2's: it is collision-free, where truncated DuckDB spellings would
        // clash ("Throttle Pos" vs "Throttle Pos Unfiltered" both truncate to "Throttl").
        bool rename = useDuckDbChannelNames
                      && !ChannelIds.KeepStandardNames.Contains(stdName)
                      && !string.IsNullOrWhiteSpace(originalName);
        // The DuckDB identity for a per-wheel / multi-column channel is built as
        // "Table_Wheel" (or "Table_valueN"); that underscore is our joining convention, not
        // the game's, so it is shown as a space: "Brakes Temp_FL" reads as "Brakes Temp FL".
        string displayName = rename ? originalName.Replace('_', ' ') : stdName;

        string brief = ChannelIds.StandardShortName(stdName) ?? shortName;

        // The output rate is the policy's choice, bounded by the rate the game recorded.
        ushort? stdId = ChannelIds.Lookup(stdName);
        int hz = policy.Resolve(stdName, stdId, sourceHz);

        // The sample count MUST match the declared rate, or i2 reads the channel at the
        // wrong instants. This used to run only when the rate was BELOW the resample grid,
        // so a channel asked to run at or above the grid kept the grid's full sample count
        // while declaring a higher rate - the single most damaging thing we can emit, and
        // invisible in a structural check that only looks at declared data length.
        float[] data = samples;
        if (!alreadyAtOutputRate && hz > 0 && hz != targetFrequencyHz && samples.Length > 1)
        {
            int keep = Math.Max(1, (int)Math.Round((double)samples.Length / targetFrequencyHz * hz));
            var reduced = new List<float>(keep);
            double step = (double)samples.Length / keep;
            for (int k = 0; k < keep; k++)
            {
                int idx = (int)Math.Floor(k * step);
                if (idx >= samples.Length) idx = samples.Length - 1;
                reduced.Add(samples[idx]);
            }
            data = reduced.ToArray();
        }

        channels.Add(new TelemetryChannel
        {
            Id = ++channelId,
            OriginalName = originalName,
            StandardName = displayName,
            IdName = stdName,
            ShortName = brief,
            Units = units,
            FrequencyHz = hz,
            Samples = data
        });
    }

    /// <summary>Appends a channel whose name is an the game table key, resolving its metadata.</summary>
    /// <param name="alreadyAtOutputRate">
    /// true when the caller built the samples directly on the rate the policy will emit at,
    /// so AddChannel must not decimate them again. "Beacon" needs this: its pulse is three
    /// samples of its own 5 Hz grid, which is far too narrow to survive decimation from 100 Hz.
    /// </param>
    private static void AddSynthesised(
        List<TelemetryChannel> channels, ref int channelId,
        string key, float[]? samples, int targetFrequencyHz,
        RatePolicy policy, bool alreadyAtOutputRate = false)
    {
        var meta = ChannelDictionary.GetMetadata(key, string.Empty);
        // A synthesised channel has no the game table behind it, so there is no source rate to
        // respect: it is built directly on the resample grid. i2's rate still applies,
        // and is what the reference uses for every lap and sector channel.
        AddChannel(channels, ref channelId, key, meta.StandardName, meta.ShortName,
            meta.Units, targetFrequencyHz, samples, policy, targetFrequencyHz,
            alreadyAtOutputRate, useDuckDbChannelNames: false);
    }

    /// <summary>
    /// i2's own sample rate for the lap-management channels, in Hz.
    /// </summary>
    /// <remarks>
    /// Transcribed from a genuine i2 log of a real session
    /// (log+example/Same data/Duckdb). i2 does NOT record these at a uniform
    /// rate: "Last Laptime" (2209), "Best Laptime" (2206), "Lap Distance" (2201),
    /// "Cur Sector 1/2" and "Sector Flag 1/2/3" are all 5 Hz, while "Lap Number" (2101),
    /// "Lap Start Elapsed Time" (2102), "Session Elapsed Time" (2100) and "Current
    /// Sector" (2103) are 50 Hz. Writing the 5 Hz channels at 50 Hz means i2 reads
    /// every tenth sample, so it sees the right values at the wrong instants.
    ///
    /// These are i2-native channels and appear nowhere in the game's config.json, so their
    /// rate cannot come from there - only from a log i2 wrote itself.
    /// </remarks>

    /// <summary>

    private static float[] ResampleStream(List<float> rawPoints, int totalSamples, double sessionDuration, int nativeFrequency, float multiplier)
    {
        var samples = new float[totalSamples];
        int count = rawPoints.Count;

        if (count == 0)
        {
            Array.Fill(samples, 0f);
            return samples;
        }

        if (count == 1)
        {
            Array.Fill(samples, rawPoints[0] * multiplier);
            return samples;
        }

        double nativeFreq = nativeFrequency > 0 ? nativeFrequency : (count - 1) / Math.Max(sessionDuration, 0.001);

        for (int s = 0; s < totalSamples; s++)
        {
            double sampleTime = s * sessionDuration / Math.Max(1, totalSamples - 1);
            double nativeIndex = sampleTime * nativeFreq;

            int idx0 = Math.Clamp((int)Math.Floor(nativeIndex), 0, count - 1);
            int idx1 = Math.Clamp(idx0 + 1, 0, count - 1);
            float frac = (float)(nativeIndex - idx0);

            float val = rawPoints[idx0] + frac * (rawPoints[idx1] - rawPoints[idx0]);
            samples[s] = val * multiplier;
        }

        return samples;
    }

    private static int FindColumnIndex(IDataRecord reader, params string[] candidateNames)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            foreach (var candidate in candidateNames)
            {
                if (colName.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private void EnsureOpen()
    {
        if (_connection == null || _connection.State != ConnectionState.Open)
        {
            Open();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
