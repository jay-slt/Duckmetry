namespace Duckmetry.Core.Ld;

/// <summary>
/// Standard names channel identifiers.
/// </summary>
/// <remarks>
/// The .ld channel header carries a 16-bit field at offset 0x10 that is NOT a
/// sequential counter: it is i2's own standard channel ID. i2 resolves each
/// channel against its built-in channel table by this value, and discards any
/// channel whose ID it does not recognise. That is why writing an arbitrary
/// sequence (0x2EE1 + i) produced a file with no channels at all.
///
/// The table below is transcribed from a genuine i2-written log that
/// renders laps and the track report:
/// log+example/2026-08-03 - 20-38-46 - WeatherTech Raceway Laguna Seca - P1.ld
/// </remarks>
internal static class ChannelIds
{
    /// <summary>Exact name -> standard ID, from the working reference log.</summary>
    private static readonly Dictionary<string, ushort> Standard = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ambient Temperature"] = 701,
        ["Anti Stall Activated"] = 1227,
        ["Beacon"] = 110,
        ["Best Laptime"] = 2206,
        ["Best Sector 1"] = 2204,
        ["Best Sector 2"] = 2205,
        ["Body Pitch"] = 1108,
        ["Body Roll"] = 1109,
        ["Brake Bias Rear"] = 1106,
        ["Brake Pos"] = 302,
        ["Brake Pos Filtered"] = 3101,
        ["Brake Pressure FL"] = 1200,
        ["Brake Pressure FR"] = 1201,
        ["Brake Pressure RL"] = 1202,
        ["Brake Pressure RR"] = 1203,
        ["Brake Temp FL"] = 1,
        ["Brake Temp FR"] = 2,
        ["Brake Temp RL"] = 3,
        ["Brake Temp RR"] = 4,
        ["Camber FL"] = 8200,
        ["Camber FR"] = 8201,
        ["Camber RL"] = 8202,
        ["Camber RR"] = 8203,
        ["Cloud Darkness"] = 7100,
        ["Clutch Pos"] = 303,
        ["Clutch Pos Filtered"] = 3102,
        ["Clutch RPM"] = 1104,
        ["Cur Sector 1"] = 2210,
        ["Cur Sector 2"] = 2211,
        ["Current Sector"] = 2103,
        ["Dent Severity 1"] = 1207,
        ["Dent Severity 2"] = 1208,
        ["Dent Severity 3"] = 1209,
        ["Dent Severity 4"] = 1210,
        ["Dent Severity 5"] = 1211,
        ["Dent Severity 6"] = 1212,
        ["Dent Severity 7"] = 1213,
        ["Dent Severity 8"] = 1214,
        ["Drag"] = 6103,
        ["Driver Type"] = 2214,
        ["Eng Oil Temp"] = 7,
        ["Eng Water Temp"] = 6,
        ["Engine Max RPM"] = 1204,
        ["Engine RPM"] = 5,
        ["Engine Torque"] = 1105,
        ["FFB Output"] = 3105,
        ["Finish Status"] = 2200,
        ["Flag"] = 2216,
        ["Front 3rd Pos"] = 4104,
        ["Front Downforce"] = 6104,
        ["Front Flap Activated"] = 1223,
        ["Front Ride Height"] = 6101,
        ["Front Tyre Compound"] = 1221,
        ["Front Wing Height"] = 6100,
        ["Fuel Level"] = 8,
        ["G Force Lat"] = 10,
        ["G Force Long"] = 11,
        ["G Force Vert"] = 12,
        ["GPS Latitude"] = 13,
        ["GPS Longitude"] = 14,
        ["Game Phase"] = 2217,
        ["Gear"] = 300,
        ["Grip Fract FL"] = 5100,
        ["Grip Fract FR"] = 5101,
        ["Grip Fract RL"] = 5102,
        ["Grip Fract RR"] = 5103,
        ["Ground Speed"] = 9,
        ["Headlights State"] = 1206,
        ["Ignition State"] = 1226,
        ["In Pits"] = 2104,
        ["Lap Distance"] = 2201,
        ["Lap Number"] = 2101,
        ["Lap Start Elapsed Time"] = 2102,
        ["Laps Behind Leader"] = 2109,
        ["Laps Behind Next"] = 2107,
        ["Last Impact Elapsed Time"] = 1215,
        ["Last Impact Magnitude"] = 1216,
        ["Last Impact X Pos"] = 1217,
        ["Last Impact Y Pos"] = 1218,
        ["Last Impact Z Pos"] = 1219,
        ["Last Laptime"] = 2209,
        ["Last Sector 1"] = 2207,
        ["Last Sector 2"] = 2208,
        ["Lat Force FL"] = 8100,
        ["Lat Force FR"] = 8101,
        ["Lat Force RL"] = 8102,
        ["Lat Force RR"] = 8103,
        ["Lat Ground Vel FL"] = 5208,
        ["Lat Ground Vel FR"] = 5209,
        ["Lat Ground Vel RL"] = 5210,
        ["Lat Ground Vel RR"] = 5211,
        ["Lat Patch Vel FL"] = 5200,
        ["Lat Patch Vel FR"] = 5201,
        ["Lat Patch Vel RL"] = 5202,
        ["Lat Patch Vel RR"] = 5203,
        ["Local Rot Accel X"] = 1231,
        ["Local Rot Accel Y"] = 1232,
        ["Local Rot Accel Z"] = 1233,
        ["Local Rotation X"] = 1228,
        ["Local Rotation Y"] = 1229,
        ["Local Rotation Z"] = 1230,
        ["Long Force FL"] = 8104,
        ["Long Force FR"] = 8105,
        ["Long Force RL"] = 8106,
        ["Long Force RR"] = 8107,
        ["Long Ground Vel FL"] = 5212,
        ["Long Ground Vel FR"] = 5213,
        ["Long Ground Vel RL"] = 5214,
        ["Long Ground Vel RR"] = 5215,
        ["Long Patch Vel FL"] = 5204,
        ["Long Patch Vel FR"] = 5205,
        ["Long Patch Vel RL"] = 5206,
        ["Long Patch Vel RR"] = 5207,
        ["Marker"] = 310,
        ["Max Straight Speed"] = 20,
        ["Min Corner Speed"] = 21,
        ["Min Path Wetness"] = 7104,
        ["Num Penalties"] = 2213,
        ["Num Pitstops"] = 2212,
        ["Num Red Lights"] = 2223,
        ["Off Path Wetness"] = 7105,
        ["Overheating State"] = 1205,
        ["Path Lateral"] = 2202,
        ["PitStatus"] = 2110,
        ["Place"] = 2105,
        ["Raining"] = 7101,
        ["Realtime Loss"] = 200,
        ["Rear 3rd Pos"] = 4105,
        ["Rear Downforce"] = 6105,
        ["Rear Flap Activated"] = 1224,
        ["Rear Flap Legal Status"] = 1225,
        ["Rear Ride Height"] = 6102,
        ["Rear Tyre Compound"] = 1222,
        ["Ride Height FL"] = 4100,
        ["Ride Height FR"] = 4101,
        ["Ride Height RL"] = 4102,
        ["Ride Height RR"] = 4103,
        ["Sector Flag 1"] = 2219,
        ["Sector Flag 2"] = 2220,
        ["Sector Flag 3"] = 2221,
        ["Server Scored"] = 2215,
        ["Session Elapsed Time"] = 2100,
        ["Speed Limiter On"] = 1220,
        ["Start Light"] = 2222,
        ["Steering"] = 304,
        ["Steering Filtered"] = 3103,
        ["Steering Shaft Torque"] = 3104,
        ["Steering Wheel Position"] = 305,
        ["Surface Type FL"] = 5220,
        ["Surface Type FR"] = 5221,
        ["Surface Type RL"] = 5222,
        ["Surface Type RR"] = 5223,
        ["Susp Force FL"] = 4200,
        ["Susp Force FR"] = 4201,
        ["Susp Force RL"] = 4202,
        ["Susp Force RR"] = 4203,
        ["Susp Pos FL"] = 400,
        ["Susp Pos FR"] = 401,
        ["Susp Pos RL"] = 402,
        ["Susp Pos RR"] = 403,
        ["Terrain Idx FL"] = 5216,
        ["Terrain Idx FR"] = 5217,
        ["Terrain Idx RL"] = 5218,
        ["Terrain Idx RR"] = 5219,
        ["Throttle Pos"] = 301,
        // i2 itself gives "Battery Charge Level" the same ID 301 as
        // "Throttle Pos"; a standard ID may legitimately be shared.
        ["Battery Charge Level"] = 301,
        ["Throttle Pos Filtered"] = 3100,
        ["Time Behind Leader"] = 2108,
        ["Time Behind Next"] = 2106,
        ["Toe FL"] = 8108,
        ["Toe FR"] = 8109,
        ["Toe RL"] = 8110,
        ["Toe RR"] = 8111,
        ["Track Edge"] = 2203,
        ["Track Temperature"] = 700,
        ["Turbo Boost Pressure"] = 1107,
        ["Tyre Carcass Temp FL"] = 5108,
        ["Tyre Carcass Temp FR"] = 5109,
        ["Tyre Carcass Temp RL"] = 5110,
        ["Tyre Carcass Temp RR"] = 5111,
        ["Tyre Flat FL"] = 5224,
        ["Tyre Flat FR"] = 5225,
        ["Tyre Flat RL"] = 5226,
        ["Tyre Flat RR"] = 5227,
        ["Tyre Load FL"] = 5104,
        ["Tyre Load FR"] = 5105,
        ["Tyre Load RL"] = 5106,
        ["Tyre Load RR"] = 5107,
        ["Tyre Pressure FL"] = 516,
        ["Tyre Pressure FR"] = 517,
        ["Tyre Pressure RL"] = 518,
        ["Tyre Pressure RR"] = 519,
        ["Tyre Rubber Temp FL C"] = 5113,
        ["Tyre Rubber Temp FL I"] = 5114,
        ["Tyre Rubber Temp FL O"] = 5112,
        ["Tyre Rubber Temp FR C"] = 5116,
        ["Tyre Rubber Temp FR I"] = 5117,
        ["Tyre Rubber Temp FR O"] = 5115,
        ["Tyre Rubber Temp RL C"] = 5119,
        ["Tyre Rubber Temp RL I"] = 5120,
        ["Tyre Rubber Temp RL O"] = 5118,
        ["Tyre Rubber Temp RR C"] = 5122,
        ["Tyre Rubber Temp RR I"] = 5123,
        ["Tyre Rubber Temp RR O"] = 5121,
        ["Tyre Temp FL Centre"] = 505,
        ["Tyre Temp FL Inner"] = 506,
        ["Tyre Temp FL Outer"] = 504,
        ["Tyre Temp FR Centre"] = 508,
        ["Tyre Temp FR Inner"] = 509,
        ["Tyre Temp FR Outer"] = 507,
        ["Tyre Temp RL Centre"] = 511,
        ["Tyre Temp RL Inner"] = 512,
        ["Tyre Temp RL Outer"] = 510,
        ["Tyre Temp RR Centre"] = 514,
        ["Tyre Temp RR Inner"] = 515,
        ["Tyre Temp RR Outer"] = 513,
        ["Tyre Wear FL"] = 500,
        ["Tyre Wear FR"] = 501,
        ["Tyre Wear RL"] = 502,
        ["Tyre Wear RR"] = 503,
        ["Vertical Tyre Deflection FL"] = 5228,
        ["Vertical Tyre Deflection FR"] = 5229,
        ["Vertical Tyre Deflection RL"] = 5230,
        ["Vertical Tyre Deflection RR"] = 5231,
        ["Wheel Detached FL"] = 8204,
        ["Wheel Detached FR"] = 8205,
        ["Wheel Detached RL"] = 8206,
        ["Wheel Detached RR"] = 8207,
        ["Wheel Rot Speed FL"] = 1100,
        ["Wheel Rot Speed FR"] = 1101,
        ["Wheel Rot Speed RL"] = 1102,
        ["Wheel Rot Speed RR"] = 1103,
        ["Wheel Y Location FL"] = 8208,
        ["Wheel Y Location FR"] = 8209,
        ["Wheel Y Location RL"] = 8210,
        ["Wheel Y Location RR"] = 8211,
        ["Wind Heading"] = 7103,
        ["Wind Speed"] = 7102,
        ["Yellow Flag State"] = 2218,
    };

    /// <summary>
    /// the game/DuckDB channel names -> i2's name for the same signal.
    /// </summary>
    /// <remarks>
    /// Without these, 34 of our channels fell through to synthetic IDs because the game
    /// spells several signals differently: "Lap Dist" is i2's "Lap Distance"
    /// (2201), "Sector 1 Flag" is "Sector Flag 1" (2219), "Current Sector 1" is
    /// "Cur Sector 1" (2210). Those are the channels i2 reads to build its lap and
    /// sector structure, so mis-typing them is what left the lap table empty. Found by
    /// diffing our emitted IDs against a genuine i2 log of the same session
    /// (log+example/Same data/Duckdb).
    /// </remarks>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Lap Dist"] = "Lap Distance",
        ["Sector 1 Flag"] = "Sector Flag 1",
        ["Sector 2 Flag"] = "Sector Flag 2",
        ["Sector 3 Flag"] = "Sector Flag 3",
        ["Current Sector 1"] = "Cur Sector 1",
        ["Current Sector 2"] = "Cur Sector 2",
        ["Wheel Speed FL"] = "Wheel Rot Speed FL",
        ["Wheel Speed FR"] = "Wheel Rot Speed FR",
        ["Wheel Speed RL"] = "Wheel Rot Speed RL",
        ["Wheel Speed RR"] = "Wheel Rot Speed RR",
        ["WheelsDetached FL"] = "Wheel Detached FL",
        ["WheelsDetached FR"] = "Wheel Detached FR",
        ["WheelsDetached RL"] = "Wheel Detached RL",
        ["WheelsDetached RR"] = "Wheel Detached RR",
        ["CloudDarkness"] = "Cloud Darkness",
        ["Minimum Path Wetness"] = "Min Path Wetness",
        ["OffpathWetness"] = "Off Path Wetness",
        ["Speed Limiter"] = "Speed Limiter On",
        // The game's plain channel is the PROCESSED one and "<X> Unfiltered" is the raw;
        // i2's logs carry the RAW one under the plain name. Verified against the genuine
        // log by how much of the session each sits pinned at its maximum: the genuine's
        // "Throttle Pos" is at max for 57.4% of samples, exactly the DuckDB's "Throttle Pos
        // Unfiltered", while the DuckDB's plain "Throttle Pos" manages 0.4%. Brake is 1.7%
        // against 0.0%, Clutch 100% against 4.3%.
        //
        // Feeding i2's plain name from the processed channel is what made our throttle
        // trace weave where the genuine log runs flat down a straight: the processed signal
        // never quite reaches 100 and dithers at the top.
        ["Brake Pos"] = "Brake Pos Filtered",
        ["Brake Pos Unfiltered"] = "Brake Pos",
        ["Throttle Pos"] = "Throttle Pos Filtered",
        ["Throttle Pos Unfiltered"] = "Throttle Pos",
        ["Steering"] = "Steering Filtered",
        ["Steering Unfiltered"] = "Steering",
        ["Clutch Pos"] = "Clutch Pos Filtered",
        ["Clutch Pos Unfiltered"] = "Clutch Pos",
        ["LastImpactMagnitude"] = "Last Impact Magnitude",
        ["FrontFlapActivated"] = "Front Flap Activated",
        ["RearFlapActivated"] = "Rear Flap Activated",
        ["RearFlapLegalStatus"] = "Rear Flap Legal Status",
        ["Tyre Rubber Temp FL"] = "Tyre Rubber Temp FL O",
        ["Tyre Rubber Temp FR"] = "Tyre Rubber Temp FR O",
        ["Tyre Rubber Temp RL"] = "Tyre Rubber Temp RL O",
        ["Tyre Rubber Temp RR"] = "Tyre Rubber Temp RR O",
    };

    // NOTE: "Brake Air Temp" deliberately has no alias. the game exposes it as a separate
    // sensor from "Brake Temp", and i2's IDs 1-4 belong to Brake Temp FL/FR/RL/RR.
    // Aliasing it made two distinct channels share one ID, which i2 cannot resolve.
    // It falls through to a synthetic ID above the standard range instead.

    /// <summary>Case- and separator-insensitive form, so "Lap Dist" == "lap dist".</summary>
    private static string Normalise(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Returns i2's standard ID for a channel name, or 0 when the channel is
    /// not one i2 knows. A name is only reused as-is, so an unrecognised channel
    /// keeps its own identity rather than being confused with a standard one.
    /// </summary>
    public static ushort? Lookup(string channelName)
    {
        if (Standard.TryGetValue(channelName, out ushort id)) return id;

        // the game's spelling -> i2's, then a punctuation- and case-insensitive retry
        // against both spellings.
        foreach (var candidate in Alias(channelName))
        {
            if (Standard.TryGetValue(candidate, out id)) return id;
            string key = Normalise(candidate);
            foreach (var (name, sid) in Standard)
                if (Normalise(name) == key) return sid;
        }

        return null;
    }

    /// <summary>
    /// i2's own short name per channel, from the 8-byte field at 0x40.
    /// </summary>
    /// <remarks>
    /// Transcribed from two genuine i2-written logs, which agree. Ours were close but
    /// not identical in places - "Sec1Flg" against i2's "SecFlg1" - and the field holds
    /// 7 characters plus a NUL, so an over-long name cannot fit at all.
    /// </remarks>
    private static readonly Dictionary<string, string> ShortNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ambient Temperature"] = "Ambient",
        ["Anti Stall Activated"] = "AStall",
        ["Battery Charge Level"] = "Bat Lev",
        ["Beacon"] = "Beacon",
        ["Best Laptime"] = "BestLap",
        ["Best Sector 1"] = "BstSec1",
        ["Best Sector 2"] = "BstSec2",
        ["Body Pitch"] = "Pitch",
        ["Body Roll"] = "Roll",
        ["Brake Bias Rear"] = "BBias R",
        ["Brake Pos"] = "Brake",
        ["Brake Pos Filtered"] = "BrakeF",
        ["Brake Pressure FL"] = "BrakeFL",
        ["Brake Pressure FR"] = "BrakeFR",
        ["Brake Pressure RL"] = "BrakeRL",
        ["Brake Pressure RR"] = "BrakeRR",
        ["Brake Temp FL"] = "Brke FL",
        ["Brake Temp FR"] = "Brke FR",
        ["Brake Temp RL"] = "Brke RL",
        ["Brake Temp RR"] = "Brke RR",
        ["Camber FL"] = "Cmbr FL",
        ["Camber FR"] = "Cmbr FR",
        ["Camber RL"] = "Cmbr RL",
        ["Camber RR"] = "Cmbr RR",
        ["Cloud Darkness"] = "Cloud",
        ["Clutch Pos"] = "Clutch",
        ["Clutch Pos Filtered"] = "ClutchF",
        ["Clutch RPM"] = "Cltch R",
        ["Cur Sector 1"] = "CurSec1",
        ["Cur Sector 2"] = "CurSec2",
        ["Current Sector"] = "CurSect",
        ["Delta Best"] = "Delta B",
        ["Dent Severity 1"] = "Dent 1",
        ["Dent Severity 2"] = "Dent 2",
        ["Dent Severity 3"] = "Dent 3",
        ["Dent Severity 4"] = "Dent 4",
        ["Dent Severity 5"] = "Dent 5",
        ["Dent Severity 6"] = "Dent 6",
        ["Dent Severity 7"] = "Dent 7",
        ["Dent Severity 8"] = "Dent 8",
        ["Drag"] = "Drag",
        ["Driver Type"] = "Driver",
        ["Eng Oil Temp"] = "Eng Oil",
        ["Eng Water Temp"] = "Eng Wtr",
        ["Engine Max RPM"] = "MaxRPM",
        ["Engine RPM"] = "Eng RPM",
        ["Engine Torque"] = "Eng Tq",
        ["FFB Output"] = "FFBOut",
        ["Finish Status"] = "Finish",
        ["Flag"] = "Flag",
        ["Front 3rd Pos"] = "F 3rd",
        ["Front Downforce"] = "FDownF",
        ["Front Flap Activated"] = "FFlap",
        ["Front Ride Height"] = "FRideH",
        ["Front Tyre Compound"] = "FTComp",
        ["Front Wing Height"] = "FWingH",
        ["Fuel Level"] = "Fuel",
        ["G Force Lat"] = "G Lat",
        ["G Force Long"] = "G Long",
        ["G Force Vert"] = "G Vert",
        ["GPS Latitude"] = "GPS Lat",
        ["GPS Longitude"] = "GPS Lon",
        ["Game Phase"] = "GPhase",
        ["Gear"] = "Gear",
        ["Grip Fract FL"] = "Grip FL",
        ["Grip Fract FR"] = "Grip FR",
        ["Grip Fract RL"] = "Grip RL",
        ["Grip Fract RR"] = "Grip RR",
        ["Ground Speed"] = "Grd Spd",
        ["Headlights State"] = "Hlights",
        ["Ignition State"] = "IgnSte",
        ["In Pits"] = "In Pits",
        ["Lap Distance"] = "LapDist",
        ["Lap Number"] = "Lap N",
        ["Lap Start Elapsed Time"] = "LapET",
        ["Laps Behind Leader"] = "LapsBL",
        ["Laps Behind Next"] = "LapsBN",
        ["Last Impact Elapsed Time"] = "Imp ET",
        ["Last Impact Magnitude"] = "Imp Mag",
        ["Last Impact X Pos"] = "Imp X",
        ["Last Impact Y Pos"] = "Imp Y",
        ["Last Impact Z Pos"] = "Imp Z",
        ["Last Laptime"] = "LastLap",
        ["Last Sector 1"] = "LstSec1",
        ["Last Sector 2"] = "LstSec2",
        ["Lat Force FL"] = "Lat FL",
        ["Lat Force FR"] = "Lat FR",
        ["Lat Force RL"] = "Lat RL",
        ["Lat Force RR"] = "Lat RR",
        ["Lat Ground Vel FL"] = "LtGV FL",
        ["Lat Ground Vel FR"] = "LtGV FR",
        ["Lat Ground Vel RL"] = "LtGV RL",
        ["Lat Ground Vel RR"] = "LtGV RR",
        ["Lat Patch Vel FL"] = "LtPV FL",
        ["Lat Patch Vel FR"] = "LtPV FR",
        ["Lat Patch Vel RL"] = "LtPV RL",
        ["Lat Patch Vel RR"] = "LtPV RR",
        ["Local Rot Accel X"] = "LRotA X",
        ["Local Rot Accel Y"] = "LRotA Y",
        ["Local Rot Accel Z"] = "LRotA Z",
        ["Local Rotation X"] = "LRot X",
        ["Local Rotation Y"] = "LRot Y",
        ["Local Rotation Z"] = "LRot Z",
        ["Long Force FL"] = "Long FL",
        ["Long Force FR"] = "Long FR",
        ["Long Force RL"] = "Long RL",
        ["Long Force RR"] = "Long RR",
        ["Long Ground Vel FL"] = "LgGV FL",
        ["Long Ground Vel FR"] = "LgGV FR",
        ["Long Ground Vel RL"] = "LgGV RL",
        ["Long Ground Vel RR"] = "LgGV RR",
        ["Long Patch Vel FL"] = "LgPV FL",
        ["Long Patch Vel FR"] = "LgPV FR",
        ["Long Patch Vel RL"] = "LgPV RL",
        ["Long Patch Vel RR"] = "LgPV RR",
        ["Marker"] = "Marker",
        ["Max Straight Speed"] = "Max Str",
        ["Min Corner Speed"] = "Min Cor",
        ["Min Path Wetness"] = "MinPWet",
        ["Motor RPM"] = "Mot RPM",
        ["Motor State"] = "MotorSt",
        ["Motor Temp"] = "Mot Tmp",
        ["Motor Torque"] = "Mot Tq",
        ["Motor Water Temp"] = "Mot Wtr",
        ["Num Penalties"] = "NumPens",
        ["Num Pitstops"] = "NumPits",
        ["Num Red Lights"] = "NRdLgts",
        ["Off Path Wetness"] = "OffPWet",
        ["Overheating State"] = "Ovheat",
        ["Path Lateral"] = "PathLat",
        ["PitStatus"] = "PitStat",
        ["Place"] = "Place",
        ["Raining"] = "Rain",
        ["Realtime Loss"] = "RTLoss",
        ["Rear 3rd Pos"] = "R 3rd",
        ["Rear Downforce"] = "RDownF",
        ["Rear Flap Activated"] = "RFlap",
        ["Rear Flap Legal Status"] = "RFlapLg",
        ["Rear Ride Height"] = "RRideH",
        ["Rear Tyre Compound"] = "RTComp",
        ["Ride Height FL"] = "Ride FL",
        ["Ride Height FR"] = "Ride FR",
        ["Ride Height RL"] = "Ride RL",
        ["Ride Height RR"] = "Ride RR",
        ["Sector Flag 1"] = "SecFlg1",
        ["Sector Flag 2"] = "SecFlg2",
        ["Sector Flag 3"] = "SecFlg3",
        ["Server Scored"] = "Scored",
        ["Session Elapsed Time"] = "SessET",
        ["Speed Limiter On"] = "Limiter",
        ["Start Light"] = "StrtLgt",
        ["Steering"] = "Steer",
        ["Steering Filtered"] = "SteerF",
        ["Steering Shaft Torque"] = "Str Tq",
        ["Steering Wheel Position"] = "SW Pos",
        ["Surface Type FL"] = "SrfceFL",
        ["Surface Type FR"] = "SrfceFR",
        ["Surface Type RL"] = "SrfceRL",
        ["Surface Type RR"] = "SrfceRR",
        ["Susp Force FL"] = "Ride FL",
        ["Susp Force FR"] = "Ride FR",
        ["Susp Force RL"] = "Ride RL",
        ["Susp Force RR"] = "Ride RR",
        ["Susp Pos FL"] = "Susp FL",
        ["Susp Pos FR"] = "Susp FR",
        ["Susp Pos RL"] = "Susp RL",
        ["Susp Pos RR"] = "Susp RR",
        ["Terrain Idx FL"] = "TnIdxFL",
        ["Terrain Idx FR"] = "TnIdxFR",
        ["Terrain Idx RL"] = "TnIdxRL",
        ["Terrain Idx RR"] = "TnIdxRR",
        ["Throttle Pos"] = "Thrttle",
        ["Throttle Pos Filtered"] = "ThrttlF",
        ["Time Behind Leader"] = "TimeBL",
        ["Time Behind Next"] = "TimeBN",
        ["Toe FL"] = "Long FL",
        ["Toe FR"] = "Long FR",
        ["Toe RL"] = "Long RL",
        ["Toe RR"] = "Long RR",
        ["Track Edge"] = "TrkEdge",
        ["Track Temperature"] = "Track",
        ["Turbo Boost Pressure"] = "Turbo",
        ["Tyre Carcass Temp FL"] = "TCT FL",
        ["Tyre Carcass Temp FR"] = "TCT FR",
        ["Tyre Carcass Temp RL"] = "TCT RL",
        ["Tyre Carcass Temp RR"] = "TCT RR",
        ["Tyre Flat FL"] = "Flat FL",
        ["Tyre Flat FR"] = "Flat FR",
        ["Tyre Flat RL"] = "Flat RL",
        ["Tyre Flat RR"] = "Flat RR",
        ["Tyre Load FL"] = "Load FL",
        ["Tyre Load FR"] = "Load FR",
        ["Tyre Load RL"] = "Load RL",
        ["Tyre Load RR"] = "Load RR",
        ["Tyre Pressure FL"] = "TPrsFL",
        ["Tyre Pressure FR"] = "TPrsFR",
        ["Tyre Pressure RL"] = "TPrsRL",
        ["Tyre Pressure RR"] = "TPrsRR",
        ["Tyre Rubber Temp FL C"] = "TRTFL C",
        ["Tyre Rubber Temp FL I"] = "TRTFL I",
        ["Tyre Rubber Temp FL O"] = "TRTFL O",
        ["Tyre Rubber Temp FR C"] = "TRTFR C",
        ["Tyre Rubber Temp FR I"] = "TRTFR I",
        ["Tyre Rubber Temp FR O"] = "TRTFR O",
        ["Tyre Rubber Temp RL C"] = "TRTRL C",
        ["Tyre Rubber Temp RL I"] = "TRTRL I",
        ["Tyre Rubber Temp RL O"] = "TRTRL O",
        ["Tyre Rubber Temp RR C"] = "TRTRR C",
        ["Tyre Rubber Temp RR I"] = "TRTRR I",
        ["Tyre Rubber Temp RR O"] = "TRTRR O",
        ["Tyre Temp FL Centre"] = "TT FL C",
        ["Tyre Temp FL Inner"] = "TT FL I",
        ["Tyre Temp FL Outer"] = "TT FL O",
        ["Tyre Temp FR Centre"] = "TT FR C",
        ["Tyre Temp FR Inner"] = "TT FR I",
        ["Tyre Temp FR Outer"] = "TT FR O",
        ["Tyre Temp RL Centre"] = "TT RL C",
        ["Tyre Temp RL Inner"] = "TT RL I",
        ["Tyre Temp RL Outer"] = "TT RL O",
        ["Tyre Temp RR Centre"] = "TT RR C",
        ["Tyre Temp RR Inner"] = "TT RR I",
        ["Tyre Temp RR Outer"] = "TT RR O",
        ["Tyre Wear FL"] = "Wear FL",
        ["Tyre Wear FR"] = "Wear FR",
        ["Tyre Wear RL"] = "Wear RL",
        ["Tyre Wear RR"] = "Wear RR",
        ["Vertical Tyre Deflection FL"] = "Flat FL",
        ["Vertical Tyre Deflection FR"] = "Flat FR",
        ["Vertical Tyre Deflection RL"] = "Flat RL",
        ["Vertical Tyre Deflection RR"] = "Flat RR",
        ["Wheel Detached FL"] = "WDet FL",
        ["Wheel Detached FR"] = "WDet FR",
        ["Wheel Detached RL"] = "WDet RL",
        ["Wheel Detached RR"] = "WDet RR",
        ["Wheel Rot Speed FL"] = "Rot FL",
        ["Wheel Rot Speed FR"] = "Rot FR",
        ["Wheel Rot Speed RL"] = "Rot RL",
        ["Wheel Rot Speed RR"] = "Rot RR",
        ["Wheel Y Location FL"] = "WYLocFL",
        ["Wheel Y Location FR"] = "WYLocFR",
        ["Wheel Y Location RL"] = "WYLocRL",
        ["Wheel Y Location RR"] = "WYLocRR",
        ["Wind Heading"] = "WindHdg",
        ["Wind Speed"] = "WindSpd",
        ["Yellow Flag State"] = "Yellow",
    };

    /// <summary>
    /// i2's spelling of a channel name, or the name unchanged when it is already one.
    /// </summary>
    /// <remarks>
    /// i2 resolves a channel by BOTH its standard ID (the field at 0x10) and its name,
    /// so an the game-spelled name carrying a i2 ID is only half resolved. "Lap Dist" has
    /// ID 2201 but i2 calls the channel "Lap Distance"; "FrontFlapActivated" has ID 1223
    /// but i2 calls it "Front Flap Activated". Emitting the the game spelling leaves i2
    /// unable to match the two.
    /// </remarks>
    public static string StandardName(string name)
    {
        if (Aliases.TryGetValue(name, out string? alias) && alias != name) return alias;
        foreach (var candidate in Alias(name))
            if (Standard.ContainsKey(candidate)) return candidate;
        return name;
    }

    /// <summary>i2's own short name for a channel, or null if it has none.</summary>
    public static string? StandardShortName(string name) =>
        ShortNames.TryGetValue(name, out string? s) ? s : null;

    /// <summary>
    /// i2's own sample rate for a standard channel ID, in Hz.
    /// </summary>
    /// <remarks>
    /// Transcribed from genuine i2-written logs. The rate is a property of the channel
    /// ID, not of its name, because that is how i2 resolves a channel - the field at
    /// 0x10.
    ///
    /// This matters: we write every channel at a single 50 Hz resample target, and i2
    /// writes its lap and telemetry channels at 1, 2, 4, 5, 10, 20, 25, 50 or 100 Hz. A
    /// channel declared at 50 Hz where i2 expects 10 Hz is read at a fifth of the
    /// spacing it expects, so the values land at the wrong instants. 97 of our channels
    /// were affected.
    ///
    /// Three IDs were held at a LOWER rate than the genuine logs use, on the reasoning
    /// that two logs disagreed and the lower rate was the safe choice. Re-checked against
    /// all three genuine logs in the tree, they AGREE:
    ///
    ///     id 5     Delta Best / Engine RPM / Motor RPM   10 Hz   (we wrote  5)
    ///     id 301   Battery Charge Level / Throttle Pos   10 Hz   (we wrote  2)
    ///     id 2110  Motor State / PitStatus               25 Hz   (we wrote  5)
    ///
    /// The first two show up in output. Throttle Pos at 2 Hz threw away 96% of a 50 Hz
    /// recording and read as a smooth weave beside the genuine log's straight; Engine RPM
    /// at 5 Hz halved it.
    ///
    /// Where an ID is shared, the FASTEST user must set the rate: a denser grid does
    /// not harm the slower channel, but a sparser one destroys the faster.
    /// </remarks>
    internal static readonly Dictionary<ushort, int> RateById = new()
    {
        [1] = 10,
        [2] = 10,
        [3] = 10,
        [4] = 10,
        [5] = 10,   // Engine RPM; shared with Delta Best and Motor RPM, which agree
        [6] = 5,
        [7] = 5,
        [8] = 5,
        [9] = 10,
        [10] = 25,
        [11] = 25,
        [12] = 25,
        [13] = 5,
        [14] = 5,
        [20] = 1,
        [21] = 1,
        [110] = 5,
        [200] = 1,
        [300] = 10,
        [301] = 10, // Throttle Pos; shared with Battery Charge Level, which agrees
        [302] = 10,
        [303] = 10,
        [304] = 10,
        [305] = 10,
        [310] = 1,
        [400] = 100,
        [401] = 100,
        [402] = 100,
        [403] = 100,
        [500] = 2,
        [501] = 2,
        [502] = 2,
        [503] = 2,
        [504] = 4,
        [505] = 4,
        [506] = 4,
        [507] = 4,
        [508] = 4,
        [509] = 4,
        [510] = 4,
        [511] = 4,
        [512] = 4,
        [513] = 4,
        [514] = 4,
        [515] = 4,
        [516] = 2,
        [517] = 2,
        [518] = 2,
        [519] = 2,
        [700] = 10,
        [701] = 10,
        [1100] = 20,
        [1101] = 20,
        [1102] = 20,
        [1103] = 20,
        [1104] = 10,
        [1105] = 10,
        [1106] = 20,
        [1107] = 10,
        [1108] = 25,
        [1109] = 25,
        [1200] = 10,
        [1201] = 10,
        [1202] = 10,
        [1203] = 10,
        [1204] = 10,
        [1205] = 1,
        [1206] = 1,
        [1207] = 1,
        [1208] = 1,
        [1209] = 1,
        [1210] = 1,
        [1211] = 1,
        [1212] = 1,
        [1213] = 1,
        [1214] = 1,
        [1215] = 1,
        [1216] = 1,
        [1217] = 1,
        [1218] = 1,
        [1219] = 1,
        [1220] = 10,
        [1221] = 1,
        [1222] = 1,
        [1223] = 10,
        [1224] = 10,
        [1225] = 10,
        [1226] = 10,
        [1227] = 10,
        [1228] = 25,
        [1229] = 25,
        [1230] = 25,
        [1231] = 25,
        [1232] = 25,
        [1233] = 25,
        [2100] = 50,
        [2101] = 50,
        [2102] = 50,
        [2103] = 50,
        [2104] = 5,
        [2105] = 1,
        [2106] = 5,
        [2107] = 5,
        [2108] = 5,
        [2109] = 5,
        [2110] = 25, // Motor State / PitStatus; the genuine logs agree on 25, not 5
        [2200] = 1,
        [2201] = 5,
        [2202] = 5,
        [2203] = 5,
        [2204] = 5,
        [2205] = 5,
        [2206] = 5,
        [2207] = 5,
        [2208] = 5,
        [2209] = 5,
        [2210] = 5,
        [2211] = 5,
        [2212] = 1,
        [2213] = 1,
        [2214] = 1,
        [2215] = 1,
        [2216] = 5,
        [2217] = 5,
        [2218] = 5,
        [2219] = 5,
        [2220] = 5,
        [2221] = 5,
        [2222] = 5,
        [2223] = 5,
        [3100] = 10,
        [3101] = 10,
        [3102] = 10,
        [3103] = 10,
        [3104] = 10,
        [3105] = 50,
        [4100] = 10,
        [4101] = 10,
        [4102] = 10,
        [4103] = 10,
        [4104] = 100,
        [4105] = 100,
        [4200] = 20,
        [4201] = 20,
        [4202] = 20,
        [4203] = 20,
        [5100] = 10,
        [5101] = 10,
        [5102] = 10,
        [5103] = 10,
        [5104] = 10,
        [5105] = 10,
        [5106] = 10,
        [5107] = 10,
        [5108] = 4,
        [5109] = 4,
        [5110] = 4,
        [5111] = 4,
        [5112] = 4,
        [5113] = 4,
        [5114] = 4,
        [5115] = 4,
        [5116] = 4,
        [5117] = 4,
        [5118] = 4,
        [5119] = 4,
        [5120] = 4,
        [5121] = 4,
        [5122] = 4,
        [5123] = 4,
        [5200] = 20,
        [5201] = 20,
        [5202] = 20,
        [5203] = 20,
        [5204] = 20,
        [5205] = 20,
        [5206] = 20,
        [5207] = 20,
        [5208] = 20,
        [5209] = 20,
        [5210] = 20,
        [5211] = 20,
        [5212] = 20,
        [5213] = 20,
        [5214] = 20,
        [5215] = 20,
        [5216] = 4,
        [5217] = 4,
        [5218] = 4,
        [5219] = 4,
        [5220] = 4,
        [5221] = 4,
        [5222] = 4,
        [5223] = 4,
        [5224] = 2,
        [5225] = 2,
        [5226] = 2,
        [5227] = 2,
        [5228] = 100,
        [5229] = 100,
        [5230] = 100,
        [5231] = 100,
        [6100] = 20,
        [6101] = 10,
        [6102] = 10,
        [6103] = 20,
        [6104] = 20,
        [6105] = 20,
        [7100] = 1,
        [7101] = 1,
        [7102] = 2,
        [7103] = 2,
        [7104] = 1,
        [7105] = 1,
        [8100] = 100,
        [8101] = 100,
        [8102] = 100,
        [8103] = 100,
        [8104] = 100,
        [8105] = 100,
        [8106] = 100,
        [8107] = 100,
        [8108] = 100,
        [8109] = 100,
        [8110] = 100,
        [8111] = 100,
        [8200] = 100,
        [8201] = 100,
        [8202] = 100,
        [8203] = 100,
        [8204] = 100,
        [8205] = 100,
        [8206] = 100,
        [8207] = 100,
        [8208] = 100,
        [8209] = 100,
        [8210] = 100,
        [8211] = 100,
    };

    /// <summary>
    /// Channel names that keep their i2 spelling even in a DuckDB-name build - the price
    /// of renaming. A name is here for one of two reasons:
    /// <list type="bullet">
    ///   <item><description><b>i2's built-in maths look their inputs up by name.</b>
    ///   Confirmed in i2 (Oct 2026): renaming <c>Ground Speed</c> (id 9) or <c>Lap Distance</c>
    ///   (id 2201) - IDs untouched - made them read <c>(Unavailable)</c> in the CorrSpeed /
    ///   CorrDist / GPS / Damper-Velocity dialogs. Hence the Ground Speed, GPS, G-Force,
    ///   Lap Distance and Susp Pos entries.</description></item>
    ///   <item><description><b>i2's lap/sector machinery.</b> <c>Beacon</c> (i2 forces its
    ///   name regardless) and the three <c>Sector Flag</c> channels - the only lap-system
    ///   channels a rename would otherwise touch. The Sector Flags are conservative: no i2
    ///   measurement shows they matter, but renaming them buys a user nothing and removes a
    ///   confound when a sector-placement report is being investigated.</description></item>
    /// </list>
    /// A DuckDB-name build leaves these alone and renames everything else.
    /// See references/channel-names-and-i2-display.md.
    /// </summary>
    public static readonly IReadOnlySet<string> KeepStandardNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Ground Speed",
        "GPS Speed", "GPS Latitude", "GPS Longitude", "GPS Time",
        "G Force Long",
        "Lap Distance",
        "Susp Pos FL", "Susp Pos FR", "Susp Pos RL", "Susp Pos RR",
        "Beacon",
        "Sector Flag 1", "Sector Flag 2", "Sector Flag 3",
    };

    /// <summary>The name as written, then the name as i2 spells it, if aliased.</summary>
    private static IEnumerable<string> Alias(string name)
    {
        yield return name;
        if (Aliases.TryGetValue(name, out string? alias) && alias != name)
            yield return alias;
    }

    /// <summary>
    /// Assigns an ID to every channel, giving unrecognised ones a synthetic value just
    /// above the highest standard ID, so they cannot collide with one. A standard ID may
    /// legitimately be shared: i2's own table gives both "Throttle Pos" and "Battery
    /// Charge Level" the ID 301, and "Engine RPM", "Motor RPM" and "Delta Best" the ID 5.
    /// </summary>
    public static ushort[] Assign(IReadOnlyList<string> names)
    {
        var ids = new ushort[names.Count];
        var used = new HashSet<ushort>(Standard.Values);
        // Above every ID in the table rather than a hand-picked base: i2's real range
        // reaches 8211, so a constant like 8000 would sit inside it.
        ushort next = (ushort)(Standard.Values.Max() + 1);

        for (int i = 0; i < names.Count; i++)
        {
            if (Lookup(names[i]) is ushort id)
            {
                ids[i] = id;
                continue;
            }

            while (!used.Add(next)) next++;
            ids[i] = next;
            next++;
        }
        return ids;
    }
}
