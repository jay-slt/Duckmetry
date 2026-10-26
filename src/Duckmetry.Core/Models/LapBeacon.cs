using System;
using System.Collections.Generic;

namespace Duckmetry.Core.Models;

/// <summary>
/// Represents a lap beacon / marker in i2.
/// </summary>
public class LapBeacon
{
    /// <summary>
    /// Sentinel <see cref="LapNumber"/> for the partial in-lap that follows the final line
    /// crossing. It exceeds any real lap number, so the writers filter it out explicitly.
    /// </summary>
    public const int InLapLapNumber = 1000;

    public int LapNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public double StartTimeSeconds { get; set; }
    public double ElapsedTimeSeconds { get; set; }

    /// <summary>
    /// Duration of this lap in seconds. Zero for the final line crossing, because no lap
    /// follows it - the remaining time is a partial in-lap, not a lap.
    /// </summary>
    public double LapTimeSeconds { get; set; }

    public List<double> SectorTimesSeconds { get; set; } = new();

    public LapBeacon() { }

    public LapBeacon(int lapNumber, string name, double startTimeSeconds, double lapTimeSeconds)
    {
        LapNumber = lapNumber;
        Name = name;
        StartTimeSeconds = startTimeSeconds;
        LapTimeSeconds = lapTimeSeconds;
    }

    /// <summary>
    /// True for the synthetic partial in-lap appended after the final line crossing.
    /// It must be excluded from lap counts, lap times and the fastest-lap calculation.
    /// </summary>
    public bool IsInLap => LapNumber >= InLapLapNumber;
}
