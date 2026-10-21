using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duckmetry.Core.Models;

namespace Duckmetry.Core.Configuration;

/// <summary>
/// User settings and persisted paths for the application.
/// </summary>
public class AppSettings
{
    public string SourceFolder { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;

    /// <summary>
    /// Write the car setup as .svm, the format the game reads back. This replaces the JSON
    /// export: the .svm is what the game can load, and the JSON was only ever a debugging aid.
    /// </summary>
    public bool ExportCarSetup { get; set; } = true;

    /// <summary>Overwrite an existing .ld rather than skipping the session.</summary>
    public bool OverwriteExisting { get; set; } = false;

    // The sampling choice, persisted so it survives a restart. Kept as the enum's own name
    // rather than an index, so the JSON stays readable and reordering the UI cannot change
    // what an old settings file means.
    public RateMode RateMode { get; set; } = RateMode.Optimised;

    /// <summary>
    /// How much to divide each channel's rate by in <see cref="RateMode.Reduced"/>.
    /// 2 halves every channel. A factor rather than a ceiling: a ceiling takes far
    /// more from a fast channel than a slow one, so it leaves the fast channels
    /// useless rather than merely smaller.
    /// </summary>
    public int ReducedDivisor { get; set; } = 2;

    /// <summary>
    /// Per-channel rates for <see cref="RateMode.Pro"/>, keyed by i2 channel name.
    /// A dictionary is the right shape here: it is what ConversionOptions already carries,
    /// and it round-trips through System.Text.Json without a custom converter as long as the
    /// comparer is ordinal, which the converter sets up on deserialisation.
    /// </summary>
    public Dictionary<string, int> RateOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where converted sessions land in the destination folder: flat (everything in the
    /// folder) or sorted (<c>&lt;track&gt;/&lt;car class&gt;/&lt;name&gt;</c>).
    /// </summary>
    public OutputLayout OutputLayout { get; set; } = OutputLayout.Flat;

    /// <summary>
    /// When true, launching the app goes straight to auto mode (the tray) instead of
    /// showing the window.
    /// </summary>
    public bool AutoStartInAutoMode { get; set; }

    /// <summary>
    /// Write each channel's DISPLAY name as its DuckDB/the game spelling instead of i2's, with
    /// the Standard names ID, rate and quantisation kept. i2 shows the file's name, so this
    /// makes its channel list read in the game's own vocabulary. On by default, because the
    /// names a user reads should be the game's; turn it off to emit i2's own spellings.
    /// The channels i2's built-in maths and lap/sector machinery resolve by name are left
    /// standard either way, or those would break.
    /// </summary>
    public bool UseDuckDbChannelNames { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Without this the mode is written as a bare number - "RateMode": 1 - which
        // tells a user editing the file nothing, and contradicts the note above
        // about keeping the name. Numeric values from an older file are still read.
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Where the settings live: per user, not beside the executable.
    /// </summary>
    /// <remarks>
    /// Beside the executable was wrong for two reasons. The installation folder is
    /// frequently read-only - Program Files on Windows - so the file silently failed to
    /// save or, worse, never existed to be found. And the settings belong to the person
    /// who chose them, not to the installation: two users sharing a machine should not
    /// share a sampling mode.
    /// </remarks>
    // Test seam: lets a test redirect settings to an isolated file, so parallel test
    // classes never collide on the user's real settings file (a real flake source -
    // two round-trip tests deleting/writing/reading the same path concurrently).
    internal static Func<string>? SettingsFilePathOverride { get; set; }

    public static string GetSettingsFilePath()
    {
        if (SettingsFilePathOverride is { } over)
        {
            return over();
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Duckmetry");
        return Path.Combine(dir, "settings.json");
    }

    /// <summary>
    /// Where the settings were kept before the rename to Duckmetry. The folder and file
    /// names are the old project's, and must stay exactly these strings so that a user
    /// upgrading keeps their choices; the next save writes them to the new location.
    /// </summary>
    public static string GetPreviousSettingsFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LmuToMotec", "lmu_motec_settings.json");

    /// <summary>Where settings were kept before they moved: beside the executable.</summary>
    public static string GetLegacySettingsFilePath() => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "lmu_motec_settings.json");

    /// <summary>
    /// Loads settings from disk, or initializes defaults if not found.
    /// </summary>
    public static AppSettings Load()
    {
        var path = GetSettingsFilePath();
        try
        {
            // Settings from before the rename (or beside the executable, older still)
            // are read so upgrading does not quietly discard a user's choices. The next
            // save writes them to the new location.
            if (!File.Exists(path))
            {
                foreach (var old in new[] { GetPreviousSettingsFilePath(), GetLegacySettingsFilePath() })
                {
                    if (File.Exists(old))
                    {
                        path = old;
                        break;
                    }
                }
            }

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);

                // The sampling mode was renamed when the rate mode got its neutral name;
                // files written before that carry the old spelling. Migrate it before
                // parsing, or the whole file is rejected as corrupt and choices are lost.
                json = json.Replace("\"MoTeCOptimised\"", "\"Optimised\"");

                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // If corrupt or inaccessible, fallback to defaults
        }

        var defaultSettings = new AppSettings();
        defaultSettings.TryDetectDefaultPaths();
        return defaultSettings;
    }

    /// <summary>
    /// Saves the current settings to disk.
    /// </summary>
    public void Save()
    {
        var path = GetSettingsFilePath();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Failed to save settings to '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to auto-detect the game's telemetry folder and the i2 data folder.
    /// The path literals are the applications' own install/data folder names and are
    /// left exactly as they are — they are where the files actually live.
    /// </summary>
    public void TryDetectDefaultPaths()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder))
        {
            var defaultGamePaths = new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Le Mans Ultimate\UserData\Telemetry",
                @"D:\Steam\steamapps\common\Le Mans Ultimate\UserData\Telemetry",
                @"E:\Steam\steamapps\common\Le Mans Ultimate\UserData\Telemetry",
                @"D:\SteamLibrary\steamapps\common\Le Mans Ultimate\UserData\Telemetry",
                @"E:\SteamLibrary\steamapps\common\Le Mans Ultimate\UserData\Telemetry"
            };

            foreach (var path in defaultGamePaths)
            {
                if (Directory.Exists(path))
                {
                    SourceFolder = path;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            var userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(userDocs))
            {
                // "MoTeC" here is the i2 data folder's real name on disk — functional,
                // not a reference — and it is exactly where i2 looks for logged data.
                var i2DataDir = Path.Combine(userDocs, "MoTeC", "Logged Data");
                if (Directory.Exists(i2DataDir))
                {
                    DestinationFolder = i2DataDir;
                }
                else
                {
                    // Default fallback to Documents/MoTeC/Logged Data if created
                    DestinationFolder = i2DataDir;
                }
            }
        }
    }
}
