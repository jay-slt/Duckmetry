using System;
using System.IO;
using System.Linq;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Converter;
using Duckmetry.Core.DuckDb;
using Duckmetry.Core.Models;
using Duckmetry.Core.Ld;
using Duckmetry.Core.Scanner;
using Xunit;

namespace Duckmetry.Tests;

/// <summary>
/// Car class handling: the normalizer (recorded-class first, table fallback),
/// the sorted output path, the .ld banner, and the scanner-under-sorted layout.
/// </summary>
public class CarClassAndLayoutTests
{
    // ---------------------------------------------------------------- normalizer

    [Theory]
    [InlineData("Hyper", "Hypercar")]      // observed in a real session's CarClass
    [InlineData("hypercar", "Hypercar")]
    [InlineData("LMP2", "LMP2")]
    [InlineData("LMP3", "LMP3")]
    [InlineData("LMGT3", "LMGT3")]
    [InlineData("GTE", "GTE")]
    [InlineData("GT3", "LMGT3")]           // the game's own fork records GT3 for LMGT3
    [InlineData("Ford_Mustang_LMGT3 GT3 WEC2025", "LMGT3")]  // embedded keyword
    public void Normalize_Maps_Known_Vocabulary(string recorded, string expected)
    {
        Assert.Equal(expected, CarClassNormalizer.Normalize(recorded));
    }

    [Fact]
    public void Normalize_Keeps_Unknown_Vocabulary_So_New_Classes_Work()
    {
        // A class the devs add later must not be dumped into Other - it becomes the
        // folder name as-is (cleaned).
        Assert.Equal("GTP", CarClassNormalizer.Normalize("GTP"));
        Assert.Equal("Whatever 2027", CarClassNormalizer.Normalize("Whatever 2027"));
    }

    [Fact]
    public void Normalize_Blank_Gives_Other()
    {
        Assert.Equal("Other", CarClassNormalizer.Normalize(null));
        Assert.Equal("Other", CarClassNormalizer.Normalize("   "));
    }

    [Fact]
    public void FolderClass_Uses_Recorded_First_Table_Second_Other_Last()
    {
        Assert.Equal("Hypercar", CarClassNormalizer.FolderClass("Hyper", "Toyota Racing 2026 #7:LM"));
        // no recorded class -> the car-name fallback
        Assert.Equal("Hypercar", CarClassNormalizer.FolderClass(null, "Ferrari 499P"));
        Assert.Equal("LMGT3", CarClassNormalizer.FolderClass(null, "Ford Mustang GT3"));
        // neither -> Other
        Assert.Equal("Other", CarClassNormalizer.FolderClass(null, "Skoda Fabia"));
    }

    [Fact]
    public void BannerClass_Is_Null_When_Nothing_Can_Be_Resolved()
    {
        Assert.Null(CarClassNormalizer.BannerClass(null, "Skoda Fabia"));
        Assert.Equal("Hypercar", CarClassNormalizer.BannerClass("Hyper", "Toyota Racing 2026 #7:LM"));
    }

    // ---------------------------------------------------------------- banner

    [Fact]
    public void Banner_Shows_Bare_Name_With_The_Resolved_Class()
    {
        var meta = new SessionMetadata
        {
            Vehicle = "Toyota Racing 2026 #7:LM",
            CarClass = "Hyper",
        };
        Assert.Equal("Toyota Racing 2026 #7:LM (Hypercar)", LdWriter.BannerCarName(meta));
    }

    [Fact]
    public void Banner_Shows_Table_Class_When_Recording_Has_None()
    {
        var meta = new SessionMetadata { Vehicle = "Ferrari 499P" };
        Assert.Equal("Ferrari 499P (Hypercar)", LdWriter.BannerCarName(meta));
    }

    [Fact]
    public void Banner_Is_Bare_When_No_Class_Exists_Anywhere()
    {
        var meta = new SessionMetadata { Vehicle = "Skoda Fabia" };
        Assert.Equal("Skoda Fabia", LdWriter.BannerCarName(meta));
    }

    // ---------------------------------------------------------------- output path

    [Fact]
    public void OutputPath_Flat_Is_The_Bare_Name()
    {
        var meta = new SessionMetadata { Venue = "Autodromo Enzo e Dino Ferrari", Vehicle = "Toyota Racing 2026 #7:LM" };
        string baseName = "2026-10-08 19-20-42 - Autodromo Enzo e Dino Ferrari - Toyota Racing 2026 #7:LM";
        Assert.Equal(baseName, OutputPath.RelativeBaseFor(meta, baseName, OutputLayout.Flat));
    }

    [Fact]
    public void OutputPath_Sorted_Is_Track_Then_Class_Then_Name()
    {
        var meta = new SessionMetadata
        {
            Venue = "Autodromo Enzo e Dino Ferrari",
            Vehicle = "Toyota Racing 2026 #7:LM",
            CarClass = "Hyper",
        };
        string baseName = "2026-10-08 19-20-42 - Autodromo Enzo e Dino Ferrari - Toyota Racing 2026 #7:LM";
        string expected = Path.Combine("Autodromo Enzo e Dino Ferrari", "Hypercar", baseName);
        Assert.Equal(expected, OutputPath.RelativeBaseFor(meta, baseName, OutputLayout.Sorted));
    }

    [Fact]
    public void OutputPath_Sorted_Cleans_Venue_And_Falls_Back_To_Other()
    {
        var meta = new SessionMetadata { Venue = "Spa:Francorchamps", Vehicle = "Skoda Fabia" };
        string expected = Path.Combine("Spa-Francorchamps", "Other", "name");
        Assert.Equal(expected, OutputPath.RelativeBaseFor(meta, "name", OutputLayout.Sorted));
    }

    // ---------------------------------------------------------------- settings round-trip

    [Fact]
    public void Regen_Rate_Meta_Scales_Watts_To_The_Declared_kW()
    {
        // The game records Regen Rate in watts but declares the unit kW in channelsList
        // (verified on a real session: min -137125 / max +193620 with unit 'kW'). The
        // dictionary must scale x0.001 so the file's values match its declared unit.
        var meta = ChannelDictionary.GetMetadata("Regen Rate");
        Assert.Equal("kW", meta.Units);
        Assert.Equal(0.001f, meta.Multiplier);
        Assert.Equal(2, meta.Decimals);
    }

    [Fact]
    public void Test_OutputLayout_Round_Trips_Through_Settings()
    {
        // Isolated temp path: the parallel suite must never touch the user's real
        // settings file (two round-trip tests racing on it was a real flake source).
        using var _ = TestSettings.UseTempPath();

        var written = new AppSettings
        {
            OutputLayout = OutputLayout.Sorted,
            SourceFolder = @"C:\src",
            DestinationFolder = @"C:\dst",
        };
        written.Save();
        var loaded = AppSettings.Load();
        Assert.Equal(OutputLayout.Sorted, loaded.OutputLayout);
        Assert.Equal(@"C:\src", loaded.SourceFolder);
    }

    [Fact]
    public void Test_Settings_From_Before_The_Rename_Still_Load()
    {
        // The sampling mode's old spelling must migrate, or the whole file is rejected
        // as corrupt and the user's choices are lost.
        using var _ = TestSettings.UseTempPath();
        File.WriteAllText(AppSettings.GetSettingsFilePath(),
            "{\"SourceFolder\":\"C:\\\\old-src\",\"RateMode\":\"MoTeCOptimised\"}");

        var loaded = AppSettings.Load();
        Assert.Equal(RateMode.Optimised, loaded.RateMode);
        Assert.Equal(@"C:\old-src", loaded.SourceFolder);
    }

    // ------------------------------------------------- scanner under sorted layout

    [Fact]
    public void Test_Scanner_Recognises_A_Converted_Session_In_Sorted_Layout()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"lmt_sorted_{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(testDir, "Src");
            var dstDir = Path.Combine(testDir, "Dst");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);

            var created = CreateSampleDuckDbFile(Path.Combine(testDir, "sample.duckdb"));
            var dbPath = Path.Combine(srcDir, "scan_session.duckdb");
            File.Move(created, dbPath);

            // Convert into the SORTED layout.
            Assert.True(TelemetryConverter.ConvertSingleFile(dbPath, dstDir,
                new TelemetryConverter.ConversionOptions
                {
                    ExtractCarSetup = false,
                    OutputLayout = OutputLayout.Sorted,
                }));

            // The output is nested: track/class/name - not in the destination root.
            var allLds = Directory.GetFiles(dstDir, "*.ld", SearchOption.AllDirectories);
            Assert.Single(allLds);
            string rel = Path.GetRelativePath(dstDir, allLds[0]);
            Assert.StartsWith(Path.Combine("Circuit de Spa-Francorchamps", "Hypercar") + Path.DirectorySeparatorChar, rel);

            // A SORTED scan recognises it as converted; a FLAT scan does not (it looks
            // only at the destination root, where nothing lives).
            var sorted = TelemetryScanner.ScanDirectories(srcDir, dstDir, OutputLayout.Sorted);
            Assert.Single(sorted);
            Assert.Equal(ConversionStatus.AlreadyConverted, sorted[0].Status);
            Assert.False(sorted[0].IsSelected);

            var flat = TelemetryScanner.ScanDirectories(srcDir, dstDir, OutputLayout.Flat);
            Assert.Equal(ConversionStatus.Pending, flat[0].Status);
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public void Test_Ldx_Session_Line_Carries_Model_And_Class()
    {
        // i2's Data tab renders the Session entry (and ignores the Vehicle entry), so the
        // car model + class ride along: "Practice 1 - Ferrari 499P (Hypercar)".
        var testDir = Path.Combine(Path.GetTempPath(), $"lmt_ldx_{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(testDir, "Src");
            var dstDir = Path.Combine(testDir, "Dst");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            var dbPath = Path.Combine(srcDir, "session.duckdb");
            CreateSampleDuckDbFile(dbPath);

            Assert.True(TelemetryConverter.ConvertSingleFile(dbPath, dstDir,
                new TelemetryConverter.ConversionOptions { ExtractCarSetup = false }));

            var ldx = Directory.GetFiles(dstDir, "*.ldx", SearchOption.AllDirectories).Single();
            string text = File.ReadAllText(ldx);
            Assert.Contains("Id=\"Session\" Value=\"Practice 1 - Ferrari 499P (Hypercar)\"", text);
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
        }
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A minimal DuckDB with the same shape the game writes (metadata, eventsList,
    /// channelsList, per-channel tables) - enough for a full conversion.
    /// </summary>
    private static string CreateSampleDuckDbFile(string dbPath)
    {
        using var conn = new DuckDB.NET.Data.DuckDBConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE metadata (key VARCHAR, value VARCHAR);
            INSERT INTO metadata VALUES
                ('driver', 'Sim Driver'),
                ('car', 'Ferrari 499P'),
                ('track', 'Circuit de Spa-Francorchamps'),
                ('session', 'Practice 1'),
                ('recordingtime', '2026-09-22T20_04_44Z');
            CREATE TABLE eventsList (lap INTEGER, start_time DOUBLE, lap_time DOUBLE);
            INSERT INTO eventsList VALUES (1, 0.0, 138.45), (2, 138.45, 137.92), (3, 276.37, 137.50);
            CREATE TABLE channelsList (channelName VARCHAR, frequency INTEGER, unit VARCHAR);
            INSERT INTO channelsList VALUES
                ('Ground Speed', 50, 'km/h'),
                ('Engine RPM', 50, 'rpm'),
                ('Throttle Pos', 50, '%'),
                ('Brake Pos', 50, '%');
            CREATE TABLE ""Ground Speed"" (value FLOAT);
            INSERT INTO ""Ground Speed"" SELECT (100 + i * 0.1)::FLOAT AS value FROM range(0, 500) tbl(i);
            CREATE TABLE ""Engine RPM"" (value FLOAT);
            INSERT INTO ""Engine RPM"" SELECT (4000 + i * 2)::FLOAT AS value FROM range(0, 500) tbl(i);
            CREATE TABLE ""Throttle Pos"" (value FLOAT);
            INSERT INTO ""Throttle Pos"" VALUES (0.8);
            CREATE TABLE ""Brake Pos"" (value FLOAT);
            INSERT INTO ""Brake Pos"" VALUES (0.0);
        ";
        cmd.ExecuteNonQuery();
        return dbPath;
    }
}