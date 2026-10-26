using System;
using System.Collections.Generic;

namespace Duckmetry.Core.Models;

/// <summary>
/// Represents a single telemetry channel (e.g. Throttle, Speed, Suspension).
/// </summary>
public class TelemetryChannel
{
    public int Id { get; set; }
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>The name actually written to the .ld (header offset 0x20).</summary>
    public string StandardName { get; set; } = string.Empty;

    /// <summary>
    /// The i2-standard name this channel resolves its standard ID, sample rate and
    /// quantisation by. Normally identical to <see cref="StandardName"/>; a DuckDB-name build
    /// (see <c>ConversionOptions.UseDuckDbChannelNames</c>) makes the two differ - the
    /// display name is swapped for the DuckDB spelling while every lookup still uses this.
    /// Empty means "same as <see cref="StandardName"/>".
    /// </summary>
    public string IdName { get; set; } = string.Empty;

    /// <summary>
    /// The name to resolve IDs / rates / quantisation / de-duplication against: the
    /// standard name where one was recorded, else the display name. Every lookup in the
    /// pipeline goes through here so a renamed channel cannot silently lose its ID.
    /// </summary>
    public string Identity => string.IsNullOrEmpty(IdName) ? StandardName : IdName;

    public string ShortName { get; set; } = string.Empty;
    public string Units { get; set; } = string.Empty;
    public int FrequencyHz { get; set; } = 50;

    /// <summary>
    /// Synchronized sample values at constant sample rate.
    /// </summary>
    public float[] Samples { get; set; } = Array.Empty<float>();

    public TelemetryChannel() { }

    public TelemetryChannel(string originalName, string channelName, string shortName, string units, int frequencyHz)
    {
        OriginalName = originalName;
        StandardName = channelName;
        IdName = channelName;
        ShortName = shortName;
        Units = units;
        FrequencyHz = frequencyHz;
    }
}

/// <summary>
/// A single raw sample point from DuckDB before resampling.
/// </summary>
public readonly struct RawDataPoint
{
    public double TimeSeconds { get; }
    public float Value { get; }

    public RawDataPoint(double timeSeconds, float value)
    {
        TimeSeconds = timeSeconds;
        Value = value;
    }
}
