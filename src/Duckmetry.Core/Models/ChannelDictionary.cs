using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Duckmetry.Core.Models;

/// <summary>
/// Mapping dictionary between the game's DuckDB channel names and the i2 standard channel names.
/// </summary>
public static class ChannelDictionary
{
    public record ChannelMeta(string StandardName, string ShortName, string Units, float Multiplier = 1.0f, int Decimals = 2);

    private static readonly Dictionary<string, (string BaseName, string ShortName, string Units, float Multiplier, int Decimals)> KnownBases = 
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Core Inputs
        { "Ground Speed", ("Ground Speed", "Grd Spd", "km/h", 1.0f, 1) },
        { "Speed", ("Ground Speed", "Grd Spd", "km/h", 1.0f, 1) },
        { "GPS Speed", ("GPS Speed", "GPSSpd", "km/h", 3.6f, 1) },
        { "Engine RPM", ("Engine RPM", "Eng RPM", "rpm", 1.0f, 0) },
        { "Throttle Pos", ("Throttle Pos", "Thrttle", "%", 1.0f, 1) },
        { "Throttle Pos Unfiltered", ("Throttle Pos Unfiltered", "ThrotUn", "%", 1.0f, 1) },
        { "Brake Pos", ("Brake Pos", "Brake", "%", 1.0f, 1) },
        { "Brake Pos Unfiltered", ("Brake Pos Unfiltered", "BrakeUn", "%", 1.0f, 1) },
        { "Clutch Pos", ("Clutch Pos", "Clutch", "%", 1.0f, 1) },
        { "Clutch Pos Unfiltered", ("Clutch Pos Unfiltered", "ClutchU", "%", 1.0f, 1) },
        { "Clutch RPM", ("Clutch RPM", "ClutchR", "rpm", 1.0f, 0) },
        { "Steering Pos", ("Steering", "Steer", "%", 1.0f, 1) },
        { "Steering Pos Unfiltered", ("Steering Unfiltered", "SteerUn", "%", 1.0f, 1) },
        { "Steering Shaft Torque", ("Steering Shaft Torque", "Str Tq", "N.m", 1.0f, 1) },
        { "FFB Output", ("FFB Output", "FFBOut", "%", 1.0f, 1) },
        { "Gear", ("Gear", "Gear", "", 1.0f, 0) },
        { "Brake Bias Rear", ("Brake Bias Rear", "BBias R", "%", 1.0f, 1) },
        { "Brake Migration", ("Brake Migration", "BMigrat", "%", 1.0f, 1) },

        // Dynamics & G-Forces
        { "G Force Lat", ("G Force Lat", "G Lat", "G", 1.0f, 3) },
        { "G Force Long", ("G Force Long", "G Long", "G", 1.0f, 3) },
        { "G Force Vert", ("G Force Vert", "G Vert", "G", 1.0f, 3) },
        { "GPS Latitude", ("GPS Latitude", "GPS Lat", "deg", 1.0f, 6) },
        { "GPS Longitude", ("GPS Longitude", "GPS Lon", "deg", 1.0f, 6) },
        { "GPS Time", ("GPS Time", "GPSTime", "s", 1.0f, 3) },
        { "Lap Dist", ("Lap Distance", "LapDist", "m", 1.0f, 1) },
        { "Total Dist", ("Total Dist", "TotDist", "m", 1.0f, 1) },
        { "Path Lateral", ("Path Lateral", "PathLat", "m", 1.0f, 2) },
        { "Track Edge", ("Track Edge", "TrkEdge", "m", 1.0f, 2) },

        // Temperatures & Pressures
        { "Engine Water Temp", ("Eng Water Temp", "Eng Wtr", "C", 1.0f, 1) },
        { "Engine Oil Temp", ("Eng Oil Temp", "Eng Oil", "C", 1.0f, 1) },
        { "Ambient Temperature", ("Ambient Temperature", "Ambient", "C", 1.0f, 1) },
        { "Track Temperature", ("Track Temperature", "Track", "C", 1.0f, 1) },
        { "Turbo Boost Pressure", ("Turbo Boost Pressure", "Boost", "bar", 0.00001f, 3) },
        { "Fuel Level", ("Fuel Level", "Fuel", "l", 1.0f, 2) },
        { "Virtual Energy", ("Virtual Energy", "VEnergy", "%", 1.0f, 1) },
        { "SoC", ("Battery Charge Level", "Bat Lev", "%", 1.0f, 1) },
        // Regen Rate: the game records the values in WATTS in the channel table but
        // declares the unit kW in channelsList (verified against a real session:
        // min -137125, max +193620 with unit 'kW'). Scale x0.001 so the file's values
        // match its declared unit — i2 must show -97.00/190.00, not -97000/190000.
        { "Regen Rate", ("Regen Rate", "Regen", "kW", 0.001f, 2) },
        { "Wind Speed", ("Wind Speed", "WndSpd", "m/s", 1.0f, 1) },
        { "Wind Heading", ("Wind Heading", "WndHdg", "deg", 1.0f, 1) },

        // Aero & Deflections
        { "FrontRideHeight", ("Front Ride Height", "FRideH", "mm", 1000.0f, 1) },
        { "RearRideHeight", ("Rear Ride Height", "RRideH", "mm", 1000.0f, 1) },
        { "Front3rdDeflection", ("Front 3rd Deflection", "F3rdDef", "mm", 1000.0f, 1) },
        { "Rear3rdDeflection", ("Rear 3rd Deflection", "R3rdDef", "mm", 1000.0f, 1) },

        // States & Flags
        { "In Pits", ("In Pits", "InPits", "", 1.0f, 0) },
        { "Speed Limiter", ("Speed Limiter", "Limiter", "", 1.0f, 0) },
        { "TC", ("Traction Control", "TC", "", 1.0f, 0) },
        { "TCCut", ("TC Cut", "TCCut", "", 1.0f, 0) },
        { "TCLevel", ("TC Level", "TCLev", "", 1.0f, 0) },
        { "TCSlipAngle", ("TC Slip Angle", "TCSlip", "", 1.0f, 0) },
        { "ABS", ("ABS", "ABS", "", 1.0f, 0) },
        { "ABSLevel", ("ABS Level", "ABSLev", "", 1.0f, 0) },
        { "Headlights State", ("Headlights", "Lights", "", 1.0f, 0) },
        { "Yellow Flag State", ("Yellow Flag", "YelFlag", "", 1.0f, 0) },
        { "Sector1 Flag", ("Sector Flag 1", "SecFlg1", "", 1.0f, 0) },
        { "Sector2 Flag", ("Sector Flag 2", "SecFlg2", "", 1.0f, 0) },
        { "Sector3 Flag", ("Sector Flag 3", "SecFlg3", "", 1.0f, 0) },
        { "OverheatingState", ("Overheating State", "Ovheat", "", 1.0f, 0) },
        { "AntiStall Activated", ("AntiStall", "AntiStl", "", 1.0f, 0) },
        { "LaunchControlActive", ("Launch Control", "LnchCtl", "", 1.0f, 0) },

        // Timing
        // "Current LapTime" is deliberately NOT mapped. the game's table is a 2-row event
        // table holding a running BEST-so-far, not the current lap: measured on the
        // paired session it steps 84.708 -> 84.786 -> 85.277 -> 85.684, i.e. ascending,
        // so it gets numerically WORSE as you drive faster laps. It also appears in
        // neither genuine i2 log and has no standard ID. i2 reads lap times
        // from "Last Laptime" (2209) and the session best from "Best Laptime" (2206),
        // both of which we emit, so this channel only misleads.
        // Emitted under i2's own name so it picks up standard ID 2206. Writing
        // "Best LapTime" instead left it on a synthetic ID, which is one reason i2
        // showed no lap times. the game's DuckDB "Best LapTime" table is a 2-row event
        // table, so the synthesised "Best Laptime" below is what actually gets written.
        { "Best LapTime", ("Best Laptime", "BestLap", "s", 1.0f, 3) },
        { "Lap Time", ("Lap Time", "LapTime", "s", 1.0f, 3) },
        // The four channels below are the ones a genuine i2 log actually uses to
        // define laps. Names and short names are taken verbatim from
        // log+example/2026-08-03 ... Laguna Seca - P1.ld, which renders laps and the
        // track report. "Lap Start Elapsed Time" is the load-bearing one: i2
        // differences it to get lap_time[n] = LapET[n] - LapET[n-1].
        { "Lap Time Running", ("Lap Time Running", "LapTmR", "s", 1.0f, 3) },
        { "Lap Start Elapsed Time", ("Lap Start Elapsed Time", "LapET", "s", 1.0f, 0) },
        { "Session Elapsed Time", ("Session Elapsed Time", "SessET", "s", 1.0f, 0) },
        { "Lap Number", ("Lap Number", "Lap N", "", 1.0f, 0) },
        { "Best Sector1", ("Best Sector 1", "BstSec1", "s", 1.0f, 3) },
        { "Best Sector2", ("Best Sector 2", "BstSec2", "s", 1.0f, 3) },
        { "Current Sector1", ("Cur Sector 1", "CurSec1", "s", 1.0f, 3) },
        { "Current Sector2", ("Cur Sector 2", "CurSec2", "s", 1.0f, 3) },
        { "Last Sector1", ("Last Sector 1", "LstSec1", "s", 1.0f, 3) },
        { "Last Sector2", ("Last Sector 2", "LstSec2", "s", 1.0f, 3) },
        // Measured against a genuine i2 log (log+example/Same data/Duckdb),
        // "Last Laptime" (2209) is the channel that STEPS at every lap boundary -
        // at 224, 310, 395, 480 and 565 s, exactly the crossings. "Best Laptime"
        // (2206) steps on each new best. Neither was emitted, so i2 had no
        // per-lap time to display. the game's DuckDB has no equivalent table, so it is
        // synthesised from the Lap events the way the other lap channels are.
        { "Last Laptime", ("Last Laptime", "LastLap", "s", 1.0f, 3) },
        { "Time Behind Next", ("Time Behind Next", "TimeBN", "s", 1.0f, 2) }
    };

    /// <summary>
    /// Formats multi-wheel channel names like "RideHeights", "Wheel Speed", "TyresTempCentre", etc.
    /// </summary>
    public static ChannelMeta GetWheelChannelMetadata(string baseTableName, string wheel,
                                                      string defaultUnit = "")
    {
        var wheelUpper = wheel.ToUpperInvariant();
        var n = baseTableName.Replace("_", " ").Trim();

        if (n.Equals("Wheel Speed", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Wheel Speed {wheelUpper}", $"WSpd {wheelUpper}", "km/h", 3.6f, 1);
        }
        if (n.Equals("Wheel Rot Speed", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Wheel Rot Speed {wheelUpper}", $"Rot {wheelUpper}", "rad/s", 1.0f, 1);
        }
        if (n.Equals("Susp Pos", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Susp Pos {wheelUpper}", $"Susp {wheelUpper}", "mm", 1000.0f, 1);
        }
        if (n.Equals("RideHeights", StringComparison.OrdinalIgnoreCase) || n.Equals("Ride Heights", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Ride Height {wheelUpper}", $"Ride {wheelUpper}", "mm", 1000.0f, 1);
        }
        if (n.Equals("Brakes Temp", StringComparison.OrdinalIgnoreCase) || n.Equals("Brake Temp", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Brake Temp {wheelUpper}", $"Brke {wheelUpper}", "C", 1.0f, 1);
        }
        if (n.Equals("Brakes Air Temp", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Brake Air Temp {wheelUpper}", $"BrkA{wheelUpper}", "C", 1.0f, 1);
        }
        if (n.Equals("Brakes Force", StringComparison.OrdinalIgnoreCase))
        {
            // The game records brake force as a 0..1 fraction while channelsList
            // declares '%' (Imola session: max 0.465). Scale x100 so the file's
            // values match the declared unit, like Regen Rate.
            return new ChannelMeta($"Brake Force {wheelUpper}", $"BrkF{wheelUpper}", "%", 100.0f, 1);
        }
        if (n.Equals("Brake Thickness", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Brake Thickness {wheelUpper}", $"BrkT{wheelUpper}", "%", 1.0f, 1);
        }
        if (n.Equals("Tyres Wear", StringComparison.OrdinalIgnoreCase) || n.Equals("Tyre Wear", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Tyre Wear {wheelUpper}", $"Wear {wheelUpper}", "%", 1.0f, 1);
        }
        if (n.Equals("TyresPressure", StringComparison.OrdinalIgnoreCase) || n.Equals("Tyre Pressure", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Tyre Pressure {wheelUpper}", $"TPrs{wheelUpper}", "kPa", 1.0f, 1);
        }
        if (n.Equals("TyresRimTemp", StringComparison.OrdinalIgnoreCase) || n.Equals("Tyre Rim Temp", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Tyre Rim Temp {wheelUpper}", $"RimT {wheelUpper}", "C", 1.0f, 1);
        }
        if (n.Equals("TyresRubberTemp", StringComparison.OrdinalIgnoreCase) || n.Equals("Tyre Rubber Temp", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Tyre Rubber Temp {wheelUpper}", $"RubT {wheelUpper}", "C", 1.0f, 1);
        }
        if (n.Equals("TyresCarcassTemp", StringComparison.OrdinalIgnoreCase) || n.Equals("Tyre Carcass Temp", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Tyre Carcass Temp {wheelUpper}", $"Carc {wheelUpper}", "C", 1.0f, 1);
        }
        if (n.Equals("TyresTempCentre", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Tyre Temp {wheelUpper} Centre", $"TT {wheelUpper} C", "C", 1.0f, 1);
        }
        if (n.Equals("TyresTempLeft", StringComparison.OrdinalIgnoreCase))
        {
            // For left side tyres (FL/RL), left is Outer. For right side (FR/RR), left is Inner.
            string layer = (wheelUpper == "FL" || wheelUpper == "RL") ? "Outer" : "Inner";
            string layerShort = (wheelUpper == "FL" || wheelUpper == "RL") ? "O" : "I";
            return new ChannelMeta($"Tyre Temp {wheelUpper} {layer}", $"TT {wheelUpper} {layerShort}", "C", 1.0f, 1);
        }
        if (n.Equals("TyresTempRight", StringComparison.OrdinalIgnoreCase))
        {
            // For left side tyres (FL/RL), right is Inner. For right side (FR/RR), right is Outer.
            string layer = (wheelUpper == "FL" || wheelUpper == "RL") ? "Inner" : "Outer";
            string layerShort = (wheelUpper == "FL" || wheelUpper == "RL") ? "I" : "O";
            return new ChannelMeta($"Tyre Temp {wheelUpper} {layer}", $"TT {wheelUpper} {layerShort}", "C", 1.0f, 1);
        }
        if (n.Equals("SurfaceTypes", StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelMeta($"Surface Type {wheelUpper}", $"Surf {wheelUpper}", "", 1.0f, 0);
        }

        // Generic fallback for wheel channel.
        //
        // The unit comes from the game's own channelsList declaration, which the
        // named rules above override where they know better. Without this a wheel
        // channel from a table we have no rule for - Susp Force, say - lost the
        // unit the game had told us ("N") and was written with none.
        var fullName = $"{n} {wheelUpper}";
        var sName = fullName.Length > 7 ? fullName[..7] : fullName;
        if (string.IsNullOrWhiteSpace(defaultUnit))
        {
            return new ChannelMeta(fullName, sName, "", 1.0f, 1);
        }
        var byUnit = GetMetadata(fullName, defaultUnit);
        return new ChannelMeta(fullName, sName, byUnit.Units, byUnit.Multiplier,
                               byUnit.Decimals);
    }

    /// <summary>
    /// Normalizes a single channel name from DuckDB and returns i2-friendly metadata.
    /// </summary>
    public static ChannelMeta GetMetadata(string originalName, string defaultUnit = "")
    {
        if (KnownBases.TryGetValue(originalName, out var known))
        {
            var unit = string.IsNullOrWhiteSpace(known.Units) ? defaultUnit : known.Units;
            return new ChannelMeta(known.BaseName, known.ShortName, unit, known.Multiplier, known.Decimals);
        }

        var cleaned = originalName.Replace("_", " ").Trim();
        var shortName = cleaned.Length > 7 ? cleaned[..7] : cleaned;
        var unitToUse = defaultUnit;
        float multiplier = 1.0f;
        int decimals = 2;

        if (unitToUse.Equals("m/s", StringComparison.OrdinalIgnoreCase))
        {
            unitToUse = "km/h";
            multiplier = 3.6f;
            decimals = 1;
        }
        else if (unitToUse.Equals("m", StringComparison.OrdinalIgnoreCase) && 
                (cleaned.Contains("height", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("deflection", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("susp", StringComparison.OrdinalIgnoreCase)))
        {
            unitToUse = "mm";
            multiplier = 1000.0f;
            decimals = 1;
        }
        else if (unitToUse.Equals("Pa", StringComparison.OrdinalIgnoreCase))
        {
            unitToUse = "bar";
            multiplier = 0.00001f;
            decimals = 3;
        }

        return new ChannelMeta(cleaned, shortName, unitToUse, multiplier, decimals);
    }
}
