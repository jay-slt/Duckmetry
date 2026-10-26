using System;

namespace Duckmetry.Core.Models;

/// <summary>
/// Holds session header and car setup information extracted from the the game DuckDB database.
/// </summary>
public class SessionMetadata
{
    public string Driver { get; set; } = "Telemetry Driver";
    public string Vehicle { get; set; } = "Telemetry Car";
    public string Venue { get; set; } = "Telemetry Circuit";
    public string Event { get; set; } = "Telemetry Session";
    public string SessionType { get; set; } = "Practice";
    public string ShortComment { get; set; } = "Converted from DuckDB";
    public DateTime SessionDateTime { get; set; } = DateTime.Now;
    public double TrackLengthMeters { get; set; }
    public double AmbientTempCelsius { get; set; }
    public double TrackTempCelsius { get; set; }
    public string? RawSetupJson { get; set; }
    public int TotalLaps { get; set; }
    public double DurationSeconds { get; set; }

    public string BareCarName => StripCarClass(Vehicle);

    /// <summary>
    /// The class the game recorded for the car, in the game's own vocabulary
    /// (a real session wrote "Hyper" for a Hypercar). Set from the <c>CarClass</c>
    /// metadata key; may be null for sessions that recorded none.
    /// </summary>
    public string? CarClass { get; set; }

    /// <summary>
    /// The class token the session records, wherever it lives: the CarClass key, else
    /// the "(CLASS)" tail the game sometimes folds into the vehicle name. Null when the
    /// session recorded no class at all. The input to <see cref="CarClassNormalizer"/>.
    /// </summary>
    public string? RecordedClass =>
        !string.IsNullOrWhiteSpace(CarClass) ? CarClass : TailClass(Vehicle);

    /// <summary>The "(CLASS)" tail of a vehicle string, or null.</summary>
    public static string? TailClass(string? vehicle)
    {
        if (string.IsNullOrWhiteSpace(vehicle)) return null;
        int open = vehicle.LastIndexOf('(');
        int close = vehicle.LastIndexOf(')');
        if (open <= 0 || close < open) return null;
        string tail = vehicle[(open + 1)..close].Trim();
        return tail.Length == 0 ? null : tail;
    }

    /// <summary>
    /// Drops a class suffix: "Proton Competition 2025 #88:LM (GT3)" becomes
    /// "Proton Competition 2025 #88:LM".
    /// </summary>
    /// <remarks>
    /// The game records the name and the class separately and this project folds them
    /// into <see cref="Vehicle"/> as "&lt;name&gt; (&lt;class&gt;)". i2 matches its own car
    /// database on the game's bare name, so the class suffix must not reach the .ld -
    /// with it, i2 shows no model beside the driver. The output filename uses the same
    /// name, so a converted file and the session's banner agree.
    /// </remarks>
    public static string StripCarClass(string? vehicle)
    {
        if (string.IsNullOrWhiteSpace(vehicle))
        {
            return string.Empty;
        }

        int open = vehicle.LastIndexOf('(');
        int close = vehicle.LastIndexOf(')');
        if (open <= 0 || close < open)
        {
            return vehicle.Trim();
        }

        return vehicle[..open].Trim();
    }
}
