using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using DuckDB.NET.Data;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Converter;
using Duckmetry.Core.DuckDb;
using Duckmetry.Core.Ld;
using Duckmetry.Core.Models;
using Duckmetry.Core.Scanner;
using Xunit;

namespace Duckmetry.Tests;

public class ConverterTests : IDisposable
{
    private readonly string _testDir;

    public ConverterTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DuckmetryTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private string CreateSampleDuckDbFile(string fileName = "2026-09-22_Spa_Ferrari499P.duckdb")
    {
        var dbPath = Path.Combine(_testDir, fileName);
        using var conn = new DuckDBConnection($"Data Source={dbPath}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            // 1. Create metadata table with setup JSON
            var dummySetup = JsonSerializer.Serialize(new
            {
                car = "Ferrari 499P",
                aero = new { rear_wing = 3, front_splitter = 2 },
                tyres = new { compound = "Soft", pressure_fl = 1.65, pressure_fr = 1.65 }
            });

            cmd.CommandText = @"
                CREATE TABLE metadata (key VARCHAR, value VARCHAR);
                INSERT INTO metadata VALUES 
                    ('driver', 'Sim Driver'),
                    ('car', 'Ferrari 499P'),
                    ('track', 'Circuit de Spa-Francorchamps'),
                    ('session', 'Practice 1'),
                    ('recordingtime', '2026-09-22T20_04_44Z'),
                    ('setup_json', '" + dummySetup.Replace("'", "''") + @"');
            ";
            cmd.ExecuteNonQuery();

            // 2. Create events table with lap beacons (real the game Lap table format)
            cmd.CommandText = @"
                CREATE TABLE eventsList (lap INTEGER, start_time DOUBLE, lap_time DOUBLE);
                INSERT INTO eventsList VALUES 
                    (1, 0.0, 138.45),
                    (2, 138.45, 137.92),
                    (3, 276.37, 137.50);
            ";
            cmd.ExecuteNonQuery();

            // 3. Create channelsList (real the game metadata table for stream channels)
            cmd.CommandText = @"
                CREATE TABLE channelsList (channelName VARCHAR, frequency INTEGER, unit VARCHAR);
                INSERT INTO channelsList VALUES
                    ('Ground Speed', 50, 'km/h'),
                    ('Engine RPM', 50, 'rpm'),
                    ('Throttle Pos', 50, '%'),
                    ('Brake Pos', 50, '%'),
                    ('Virtual Energy Tank', 50, '%');
            ";
            cmd.ExecuteNonQuery();

            // 4. Create telemetry channel tables in real the game format (stream: only 'value' column, no timestamp)
            cmd.CommandText = @"
                CREATE TABLE ""Ground Speed"" (value FLOAT);
                CREATE TABLE ""Engine RPM"" (value FLOAT);
                CREATE TABLE ""Throttle Pos"" (value FLOAT);
                CREATE TABLE ""Brake Pos"" (value FLOAT);
                CREATE TABLE ""Virtual Energy Tank"" (value FLOAT);

                -- Populate sample time series (10 seconds at 50Hz = 500 samples)
                INSERT INTO ""Ground Speed"" SELECT (100 + i * 0.1)::FLOAT AS value FROM range(0, 500) tbl(i);
                INSERT INTO ""Engine RPM"" SELECT (4000 + i * 2)::FLOAT AS value FROM range(0, 500) tbl(i);
                INSERT INTO ""Throttle Pos"" SELECT (0.8)::FLOAT AS value FROM range(0, 500) tbl(i);
                INSERT INTO ""Brake Pos"" SELECT (0.0)::FLOAT AS value FROM range(0, 500) tbl(i);
                INSERT INTO ""Virtual Energy Tank"" SELECT (89.5 - i * 0.005)::FLOAT AS value FROM range(0, 500) tbl(i);
            ";
            cmd.ExecuteNonQuery();
        }

        return dbPath;
    }

    [Fact]
    public void Test_DuckDbReader_Reads_Metadata_And_Laps()
    {
        var dbPath = CreateSampleDuckDbFile();
        using var reader = new DuckDbReader(dbPath);
        reader.Open();

        var meta = reader.ReadMetadata();
        Assert.Equal("Sim Driver", meta.Driver);
        Assert.Equal("Ferrari 499P", meta.Vehicle);
        Assert.Equal("Circuit de Spa-Francorchamps", meta.Venue);
        Assert.NotNull(meta.RawSetupJson);
        Assert.Contains("rear_wing", meta.RawSetupJson);

        var laps = reader.ReadLapBeacons();
        Assert.Equal(3, laps.Count);
        Assert.Equal(1, laps[0].LapNumber);
        Assert.Equal(0.0, laps[0].StartTimeSeconds);
        Assert.Equal(138.45, laps[1].StartTimeSeconds);

        var channels = reader.ReadAndResampleChannels(50);
        Assert.True(channels.Count >= 5);
        
        var speedCh = channels.Find(c => c.StandardName == "Ground Speed");
        Assert.NotNull(speedCh);
        Assert.Equal("km/h", speedCh.Units);
        Assert.NotEmpty(speedCh.Samples);
    }

    [Fact]
    public void Test_TelemetryConverter_EndToEnd()
    {
        var dbPath = CreateSampleDuckDbFile("test_session.duckdb");
        var outputDir = Path.Combine(_testDir, "Output");

        var options = new TelemetryConverter.ConversionOptions
        {
            ExtractCarSetup = true,
            ResampleFrequencyHz = 50,
            OverwriteExisting = true
        };

        bool success = TelemetryConverter.ConvertSingleFile(dbPath, outputDir, options);
        Assert.True(success);

        // Verify Output Files
        //
        // Found by what is there rather than by name: the output is named from the
        // session's own metadata (date, time, track, car), not from the DuckDB's
        // filename, so its name is not the input's.
        var ldFile = Directory.GetFiles(outputDir, "*.ld").Single();
        var ldxFile = Path.ChangeExtension(ldFile, ".ldx");
        var svmFile = Path.ChangeExtension(ldFile, ".svm");

        Assert.True(File.Exists(ldFile), "i2 .ld binary file must exist");
        Assert.True(File.Exists(ldxFile), "i2 .ldx XML file must exist");
        Assert.True(File.Exists(svmFile), "Car setup .svm file must exist");
        Assert.False(File.Exists(Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(ldFile) + "_setup.json")),
            "the JSON setup export must no longer be written");

        // Verify .ld size (at least header + channel headers)
        var fileInfo = new FileInfo(ldFile);
        Assert.True(fileInfo.Length > 5000, "File size must be larger than headers");

        // Verify .ldx XML contents
        var ldxContent = File.ReadAllText(ldxFile);
        // Laps are the BCN <Marker> elements inside <Layer><MarkerBlock>; i2 builds
       // lap ranges between consecutive markers. <Details> is a sibling of <Layer>.
        Assert.Contains("Version=\"1.6\"", ldxContent);
            Assert.DoesNotContain("<MarkerGroup", ldxContent);
        Assert.DoesNotContain("<Marker ", ldxContent);
        Assert.DoesNotContain("<RangeBlock", ldxContent);
        Assert.Contains("<Details>", ldxContent);
        Assert.Contains("<String Id=\"Total Laps\" Value=\"3\"", ldxContent);
        Assert.Contains("Circuit de Spa-Francorchamps", ldxContent);
        // Verify the setup content, now in the .svm the game can read back.
        var svmContent = File.ReadAllText(svmFile);
        // What this fixture actually produces: its CarSetup is
        //   { car, aero: { rear_wing, front_splitter }, tyres: {...} }
        // which has no VM_/WM_ keys, so SvmWriter maps no settings into sections and
        // the file is header-only. The assertions below say so outright rather than
        // pretending the settings were translated - this fixture predates the real
        // CarSetup shape, and the translation itself is covered by
        // Test_Svm_Writer_Matches_Game_Format.
        Assert.Contains("UpgradeSetting=(0,0,0,0)", svmContent);
        Assert.Contains("//Generated by Duckmetry", svmContent);
        Assert.DoesNotContain("Ferrari 499P", svmContent);   // no parenthesised class
    }

    [Fact]
    public void Test_Scanner_Detects_Converted_And_Unconverted_Files()
    {
        var srcDir = Path.Combine(_testDir, "TelemetrySource");
        var dstDir = Path.Combine(_testDir, "i2Dest");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        var file1 = Path.Combine(srcDir, "Session_1.duckdb");
        var file2 = Path.Combine(srcDir, "Session_2.duckdb");
        File.WriteAllText(file1, "dummy");
        File.WriteAllText(file2, "dummy");

        // Simulate Session_1 already converted
        File.WriteAllText(Path.Combine(dstDir, "Session_1.ld"), "dummy_ld");

        var items = TelemetryScanner.ScanDirectories(srcDir, dstDir, readMetadataPreview: false);
        Assert.Equal(2, items.Count);

        var item1 = items.Find(i => i.FileName == "Session_1.duckdb");
        var item2 = items.Find(i => i.FileName == "Session_2.duckdb");

        Assert.NotNull(item1);
        Assert.Equal(ConversionStatus.AlreadyConverted, item1.Status);
        Assert.False(item1.IsSelected);

        Assert.NotNull(item2);
        Assert.Equal(ConversionStatus.Pending, item2.Status);
        Assert.True(item2.IsSelected);
    }

    [Fact]
    public void Test_Real_DuckDb_File_Conversion_EndToEnd()
    {
        // Find workspace root
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.NotNull(dir);
        var realPath = Path.Combine(dir, "log+example/Paul.Ricard.Circuit_P_2026-06-02T20_04_44Z/Paul Ricard Circuit_P_2026-06-02T20_04_44Z.duckdb");
        // The reference fixtures are not committed to this repository - they are the
        // user's own recorded telemetry, which must not be redistributed. Skip
        // rather than fail when the checkout has no sample data.
        if (!File.Exists(realPath)) return;

        var outputDir = Path.Combine(_testDir, "RealOutput");
        var options = new TelemetryConverter.ConversionOptions
        {
            ExtractCarSetup = true,
            ResampleFrequencyHz = 50,
            OverwriteExisting = true
        };

        var logMessages = new List<string>();
        bool success = TelemetryConverter.ConvertSingleFile(realPath, outputDir, options, logger: msg => logMessages.Add(msg));
        Assert.True(success, $"Conversion failed: {string.Join("\n", logMessages)}");

        // The output is named after the session's own metadata, so it is found by what
        // is there - and should not carry the DuckDB's filename.
        var baseName = Path.GetFileNameWithoutExtension(realPath);
        var ldFile = Directory.GetFiles(outputDir, "*.ld").Single();
        var ldxFile = Path.ChangeExtension(ldFile, ".ldx");
        var svmFile = Path.ChangeExtension(ldFile, ".svm");

        Assert.True(File.Exists(ldFile), "Generated .ld must exist");
        Assert.True(File.Exists(ldxFile), "Generated .ldx must exist");
        Assert.True(File.Exists(svmFile), "Generated .svm must exist");
        Assert.False(File.Exists(Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(ldFile) + "_setup.json")),
            "the JSON setup export must no longer be written");

        // Named date, time, track and car, so a folder sorts chronologically. The time
        // is local, matching the .ld banner for the same session: this log starts at
        // 20:04:44 UTC, which is 22:04:44 on this machine. This particular session has
        // no car recorded, so its name stops after the track.
        var outputName = Path.GetFileNameWithoutExtension(ldFile);
        Assert.NotEqual(baseName, outputName);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2} - ", outputName);
        Assert.Contains("Paul Ricard", outputName);

        // Verify LD Binary format
        using (var fs = File.OpenRead(ldFile))
        using (var br = new BinaryReader(fs))
        {
            uint marker = br.ReadUInt32();
            Assert.Equal((uint)0x40, marker);

            // 0x08 is the first channel header pointer and MUST be populated. This test
            // previously read 0x0C and asserted 5000 there, encoding a misread of the
            // reference that left 0x08 zeroed - the cause of "no channels at all".
            // Both known-good files agree: genuine reference 0x08 = 0x3448, v2.2.0 = 0x1388.
            fs.Seek(0x08, SeekOrigin.Begin);
            uint firstChanPtr = br.ReadUInt32();
            Assert.Equal(5000u, firstChanPtr);

            // 0x0C is the first channel's data block, and must agree with its header.
            fs.Seek(0x0C, SeekOrigin.Begin);
            uint firstDataPtr = br.ReadUInt32();
            fs.Seek(firstChanPtr + 0x08, SeekOrigin.Begin);
            Assert.Equal(firstDataPtr, br.ReadUInt32());

            // 0x56 is NOT the channel count: all three genuine i2 logs hold 78 there
            // while carrying 245 channels. Walking the linked list is the only correct
            // way to count them.
            int numChannels = 0;
            for (uint p = firstChanPtr; p != 0 && numChannels < 10000; numChannels++)
            {
                fs.Seek(p + 0x04, SeekOrigin.Begin);
                p = br.ReadUInt32();
            }
            Assert.True(numChannels >= 50, $"Expected >= 50 channels, found {numChannels}");

            // Verify the first channel header
            fs.Seek(firstChanPtr, SeekOrigin.Begin);
            uint ch0Prev = br.ReadUInt32();
            uint ch0Next = br.ReadUInt32();
            uint ch0Data = br.ReadUInt32();
            uint ch0Len = br.ReadUInt32();
            Assert.Equal((uint)0, ch0Prev);
            Assert.Equal(firstChanPtr + 124, ch0Next);
            Assert.True(ch0Data > firstChanPtr, "channel data must follow the headers");
            Assert.True(ch0Len > 0, "Channel 0 must have samples");

            // The whole file must be covered by the data blocks, with no overrun.
            long lastDataEnd = 0;
            fs.Seek(firstChanPtr, SeekOrigin.Begin);
            for (int i = 0; i < numChannels; i++)
            {
                fs.Seek(firstChanPtr + (long)i * 124 + 0x08, SeekOrigin.Begin);
                uint dp = br.ReadUInt32();
                uint n = br.ReadUInt32();
                long end = dp + (long)n * 2;   // every channel is int16 in the reference
                if (end > lastDataEnd) lastDataEnd = end;
            }
            Assert.Equal(fs.Length, lastDataEnd);
        }

// Verify LDX XML
var ldxContent = File.ReadAllText(ldxFile);
// The Details block carries the session summary and setup; the lap ranges
// themselves come from the BCN <Marker> elements in the marker layer above it.
Assert.Contains("<String Id=\"Venue\" Value=\"Paul Ricard Circuit", ldxContent);
Assert.Contains("<String Id=\"Driver\" Value=\"Test Driver\"", ldxContent);
// This fixture's crossings are 186.585(Out) 309.54(1) 421.38(2) 540.08(3).
// The out-lap is not counted and the final crossing only starts the in-lap, so
// 2 laps are complete - 111.840 s and 118.700 s. This matches the convention
// read off the genuine log in log+example/Same data/Duckdb, whose .ldx
// reports 4 laps from LapET 0,224,310,395,480,565 (out + 4 + in-lap start).
Assert.Contains("<String Id=\"Total Laps\" Value=\"2\"", ldxContent);
Assert.Contains("<String Id=\"Fastest Time\" Value=\"1:51.840\"", ldxContent);
// A genuine i2-written .ldx has no markers at all: its 'Marker' channel
// (ID 310) is all zeros and the lap structure comes from the .ld's channels.
        int markers = ldxContent.Split("<Marker ").Length - 1;
        Assert.Equal(0, markers);
    }

[Fact]
public void Test_Real_DuckDb_LapTime_Channel_Wraps_For_i2()
{
        // Regression guard: the game's "Lap Time" table holds only two rows, so the old
        // hold-last resampling produced a flat 0 -> max curve. i2 counts laps by
        // detecting wrap-around, so a non-wrapping channel reports 0 laps.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);

        var realPath = Path.Combine(dir, "log+example/Paul.Ricard.Circuit_P_2026-06-02T20_04_44Z/Paul Ricard Circuit_P_2026-06-02T20_04_44Z.duckdb");
        // The reference fixtures are not committed to this repository - they are the
        // user's own recorded telemetry, which must not be redistributed. Skip
        // rather than fail when the checkout has no sample data.
        if (!File.Exists(realPath)) return;

        using var reader = new DuckDbReader(realPath);
        reader.Open();

        var channels = reader.ReadAndResampleChannels(50);
        // "Lap Time Running" is no longer emitted: it exists in neither genuine i2
        // log, has no standard ID, and is a sawtooth that never resets. i2 reads lap
        // times from "Last Laptime" (2209), which holds the last completed lap's time.
        Assert.DoesNotContain(channels, c => c.StandardName == "Lap Time Running");

        // The degenerate 2-row the game "Lap Time" must not survive as a flat channel.
        Assert.DoesNotContain(channels, c => c.StandardName == "Lap Time");

        var last = channels.Find(c => c.StandardName == "Last Laptime");
        Assert.NotNull(last);
        Assert.NotEmpty(last.Samples);
        // -1 is the "no completed timed lap yet" sentinel; nothing may sit below it.
        Assert.True(last.Samples.All(f => !float.IsNaN(f) && f >= -1f),
            "Last Laptime must be finite and >= the -1 sentinel");

        // This fixture's crossings are 186.585(Out) 309.54(1) 421.38(2) 540.08(3).
        // Matching the genuine log (ID 2209, whose runs are 0 -> -1 -> 85.7 -> ...):
        // 0 while the out-lap runs, -1 from the first crossing until the first timed
        // lap completes, then each completed lap's duration. The out-lap's own
        // 122.955 s is deliberately NOT written. Measured: 4 distinct values, 3 steps.
        var held = last.Samples.Distinct().OrderBy(f => f).ToArray();
        Assert.Equal(4, held.Length);              // 0, -1, and the two completed laps
        Assert.Equal(-1f, held[0]);
        Assert.Equal(0f, held[1]);
        var timed = held.Where(f => f > 20f).ToArray();
        Assert.Equal(2, timed.Length);             // Lap 1 and Lap 2 only
        Assert.All(timed, f => Assert.InRange(f, 80f, 200f));
        // It steps at each crossing rather than ramping: 0 -> -1 -> lap -> lap.
        int steps = 0;
        for (int i = 1; i < last.Samples.Length; i++)
            if (Math.Abs(last.Samples[i] - last.Samples[i - 1]) > 0.01f) steps++;
        Assert.Equal(3, steps);

        // Best Laptime tracks only the fastest completed lap, so the out-lap is absent.
        var best = channels.Find(c => c.StandardName == "Best Laptime");
 Assert.NotNull(best);
   var bestHeld = best!.Samples.Distinct().OrderBy(f => f).ToArray();
      Assert.Equal(2, bestHeld.Length);          // -1 then the fastest lap
   Assert.InRange(bestHeld[1], 80f, 200f);
   }

    [Fact]
    public void Test_Ldx_Matches_Genuine_i2_Byte_Layout()
    {
        // A genuine i2 .ldx (log+example/WeatherTech.Raceway.Laguna.Seca) has no
        // BOM, CRLF endings, one-space-per-level indentation, a bare xml declaration
        // with no encoding attribute, and tight '/>'. XmlWriter cannot produce the
        // first four, which is why LdxWriter emits the text directly.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);
        var ldxPath = Path.Combine(_testDir, "layout.ldx");
        var meta = new SessionMetadata
        {
            Driver = "Test Driver",
            Vehicle = "Duqueine Team #30:ELMS25 (LMP2_ELMS)",
            Venue = "Paul Ricard Circuit"
        };
        LdxWriter.WriteFile(ldxPath, meta, new System.Collections.Generic.List<LapBeacon>());
        var bytes = File.ReadAllBytes(ldxPath);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            ".ldx must not start with a UTF-8 BOM");
        Assert.StartsWith("<?xml version=\"1.0\"?>", text);
        Assert.DoesNotContain("encoding=", text);
        // Every LF must be part of a CRLF pair, so no bare-LF endings survive.
        Assert.Equal(text.Count(c => c == '\n'), text.Split("\r\n").Length - 1);
        Assert.Equal(0, text.Count(c => c == '\r') - text.Count(c => c == '\n'));
        Assert.DoesNotContain(" />", text);
        Assert.Contains("Version=\"1.6\"", text);
        // No layer at all, matching a genuine i2-written .ldx: <Layers> holds
        // only <Details>. CRLF line endings and single-space indentation.
        Assert.Contains("\r\n <Layers>\r\n <Details>\r\n", text);
        Assert.DoesNotContain("<Layer>", text);
        Assert.DoesNotContain("<MarkerBlock>", text);
        Assert.DoesNotContain("<RangeBlock/>", text);
    }

    [Fact]
    public void Test_Svm_Export_Uses_Game_Format()
    {
        // The .svm must mirror the game's own format: header lines, [SECTION] blocks,
        // and "<PascalName>Setting=<int>//<display>" lines.
        var svmPath = Path.Combine(_testDir, "setup.svm");
        var meta = new SessionMetadata
        {
            Vehicle = "Duqueine Team #30:ELMS25 (LMP2_ELMS)",
            RawSetupJson = """
            {
              "VM_REAR_WING":    { "value": 4, "stringValue": "P5" },
              "VM_ENGINE_MIXTURE": { "value": 1, "stringValue": "Race" },
              "VM_FUEL_LEVEL":   { "value": 7, "stringValue": "12.0L (4.5 laps)" },
              "WM_CAMBER-W_FL":  { "value": 11, "stringValue": "-1.5 deg" },
              "WM_FASTBUMP-W_RL":{ "value": 3, "stringValue": "4" }
            }
            """
        };
        Assert.True(SvmWriter.TryWriteFile(svmPath, meta));
        var text = File.ReadAllText(svmPath);
        Assert.Contains("VehicleClassSetting=\"LMP2_ELMS\"", text);
        Assert.Contains("UpgradeSetting=(", text);
        // VM_REAR_WING is abbreviated to RW, not "RearWing".
        Assert.Contains("[REARWING]\r\nRWSetting=4//P5", text);
        Assert.Contains("EngineMixtureSetting=1//Race", text);
        // Per-corner names drop the "-W" wheel marker; the section implies the corner.
        Assert.Contains("[FRONTLEFT]", text);
        Assert.Contains("CamberSetting=11//-1.5 deg", text);
        Assert.Contains("[REARLEFT]", text);
        Assert.Contains("FastBumpSetting=3//4", text);
        // No key may leak the internal wheel marker.
        Assert.DoesNotContain("-WSetting", text);
        // [BASIC] holds derived sliders that have no counterpart in the setup JSON.
        Assert.DoesNotContain("[BASIC]", text);
    }

    [Fact]
    public void Test_InLap_Is_Not_Counted_As_A_Fastest_Lap()
    {
        // Regression: the time after the FINAL line crossing is a partial in-lap, not a
        // completed lap. The reader used to clamp it to session end and hand it to the
        // writer as a lap time, which made the in-lap (67.585s) look like the fastest lap
        // of the session. It must be excluded from Total Laps, Fastest Time and markers.
var ldxPath = Path.Combine(_testDir, "inlap.ldx");
var meta = new SessionMetadata { Driver = "Test", Vehicle = "Car (CLASS)", Venue = "V" };

// Structure taken from the genuine i2 log of a 4-lap session
// (log+example/Same data/Duckdb): LapET 0 -> 224 -> 310 -> 395 -> 480 -> 565,
// i.e. the leading segment is the OUT lap and is NOT counted, and the final
// crossing has no successor so it starts the in-lap rather than a lap.
// A genuine .ldx reports Total Laps=4, Fastest=1:24.708 from such a sequence.
var beacons = new List<LapBeacon>
{
    new(0,     "Out",   0.0,   224.140),
    new(1,     "Lap 1", 224.140, 85.680),
    new(2,     "Lap 2", 309.820, 85.280),
    new(3,     "Lap 3", 395.100, 84.780),
    new(4,     "Lap 4", 479.880, 84.700),
    new(LapBeacon.InLapLapNumber, "In", 564.580, 40.0),
};

LdxWriter.WriteFile(ldxPath, meta, beacons);
var text = File.ReadAllText(ldxPath);

// The in-lap must not become the fastest lap, and the out-lap is not counted.
Assert.Contains("<String Id=\"Total Laps\" Value=\"4\"/>", text);
// Lap 4 at 84.700s is the fastest completed lap (the genuine log reports 1:24.708).
Assert.Contains("<String Id=\"Fastest Time\" Value=\"1:24.700\"/>", text);
Assert.Contains("<String Id=\"Fastest Lap\" Value=\"4\"/>", text);

// 4 completed laps => 5 markers (N+1); the in-lap must not add a sixth.
        Assert.Equal(0, text.Split("<Marker ").Length - 1);
}

[Fact]
public void Test_Lap_Channels_Use_Absolute_Session_Time()
{
    // Regression: lap channels were emitted relative to the first sample, putting
    // every boundary 92.71 s early on the pairing in log+example/Same data. i2
    // stores ABSOLUTE session time, so lap boundaries landed at the wrong instant
    // and the trailing in-lap absorbed the whole session.
    //
    // Verified against a genuine i2 log of that same DuckDB session:
    //   DuckDB Lap ts   : 92.71, 224.14, 309.82, 395.10, 479.88, 564.58
    //   genuine LapET   : 0,      224,    310,    395,    480,    565
    // The genuine file truncates to whole seconds; we keep full precision.
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
    dir = Directory.GetParent(dir)?.FullName;
    Assert.NotNull(dir);
    var db = Path.Combine(dir,
    "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
    if (!File.Exists(db)) return;   // fixture not present in this checkout

    using var reader = new DuckDbReader(db);
    var channels = reader.ReadAndResampleChannels(50);

    var lapet = channels.Find(c => c.StandardName == "Lap Start Elapsed Time");
    var sess = channels.Find(c => c.StandardName == "Session Elapsed Time");
    Assert.NotNull(lapet);
    Assert.NotNull(sess);

    // Every line crossing must appear unshifted. The first DuckDB row is the
    // out-lap start, which i2 stores as 0 ("no lap yet") - it is deliberately
    // NOT a boundary, otherwise it becomes a phantom 92.71 s lap.
    var expected = new[] { 224.14, 309.82, 395.10, 479.88, 564.58 };
    Assert.All(expected, e => Assert.Contains(lapet.Samples,
        s => Math.Abs(s - e) < 0.05f));   // crossings present, unshifted
    Assert.Equal(0f, lapet.Samples[0]);

    // Session Elapsed Time starts near the first crossing (92.7 -> 93), not at 0.
    Assert.InRange(sess.Samples[0], 90f, 96f);

    // Differences between consecutive LapET values are the lap times, and the
    // .ldx of the genuine log reports a fastest lap of 84.708 s.
    var steps = new List<float>();
    for (int i = 1; i < lapet.Samples.Length; i++)
if (lapet.Samples[i] != lapet.Samples[i - 1]) steps.Add(lapet.Samples[i]);
var laps = new List<float>();
float prev = 0f;
foreach (var s in steps) { laps.Add(s - prev); prev = s; }

// laps[0] is the out-lap; the rest are real laps and must be ~85 s.
Assert.Equal(5, laps.Count);
Assert.All(laps.Skip(1), l => Assert.InRange(l, 80f, 90f));
float fastestReal = laps.Skip(1).Min();
Assert.InRange(fastestReal, 84f, 86f);
}

[Fact]
public void Test_Last_And_Best_Laptime_Are_Emitted_With_Standard_Ids()
{
    // Regression: neither channel was emitted at all. Measured against the genuine
    // i2 log in log+example/Same data/Duckdb, "Last Laptime" (2209) is the
    // channel that steps at every lap boundary - at 224, 310, 395, 480 and 565 s -
    // and "Best Laptime" (2206) steps once per completed lap. Without them i2 has
    // no per-lap time to display. Both also have to carry i2's standard ID: a
    // synthetic ID means i2 cannot resolve the channel at all.
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
    dir = Directory.GetParent(dir)?.FullName;
    Assert.NotNull(dir);
    var db = Path.Combine(dir,
    "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
    if (!File.Exists(db)) return;   // fixture not present in this checkout

    using var reader = new DuckDbReader(db);
    var channels = reader.ReadAndResampleChannels(50);

    var last = channels.Find(c => c.StandardName == "Last Laptime");
    var best = channels.Find(c => c.StandardName == "Best Laptime");
    Assert.NotNull(last);
    Assert.NotNull(best);

    // Genuine log: Last Laptime steps 5 times (0 -> -1 -> four lap durations), Best
    // Laptime 4 (it has no 0 phase, and the out-lap is never a "best").
    int StepCount(float[] samples)
    {
        int n = 0;
        for (int i = 1; i < samples.Length; i++)
        if (Math.Abs(samples[i] - samples[i - 1]) > 1e-4f) n++;
        return n;
    }
    Assert.Equal(5, StepCount(last.Samples));
    Assert.Equal(4, StepCount(best.Samples));

    // Both carry the four completed-lap durations.
    float[] CompletedLaps(float[] samples) => samples.Distinct()
    .Where(v => v > 60f && v < 130f)
    .OrderBy(v => v)
    .ToArray();
    foreach (var lap in CompletedLaps(last.Samples))
    Assert.InRange(lap, 84f, 86f);
    Assert.Equal(4, CompletedLaps(last.Samples).Length);
    Assert.Equal(4, CompletedLaps(best.Samples).Length);
    // The out-lap's duration (131.43 s) is never written to either channel: the
    // reference holds the whole out-lap as the -1 region, whose runs are
    // 0 -> -1 -> 85.7 -> 85.3 -> 84.8 -> 84.7.
    Assert.DoesNotContain(last.Samples, v => v > 130f);
    Assert.DoesNotContain(best.Samples, v => v > 130f);          // never a "best"
}

[Fact]
public void Test_Lap_Channels_Use_i2_Sample_Rates()
{
    // Regression: every channel was written at the single resample target (50 Hz).
    // i2 does not record lap-management channels uniformly - "Last Laptime"
    // (2209), "Best Laptime" (2206), "Lap Distance" (2201), "Cur Sector 1/2",
    // "Best/Last Sector 1/2" and "Sector Flag 1/2/3" are all 5 Hz in a genuine log,
    // while "Lap Number" (2101), "Lap Start Elapsed Time" (2102) and
    // "Session Elapsed Time" (2100) are 50 Hz. A rate that disagrees with the sample
    // count makes i2 read the channel at the wrong instants.
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
    dir = Directory.GetParent(dir)?.FullName;
    Assert.NotNull(dir);
    var db = Path.Combine(dir,
    "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
    if (!File.Exists(db)) return;   // fixture not present in this checkout

    using var reader = new DuckDbReader(db);
    var channels = reader.ReadAndResampleChannels(50);

    var fiveHz = new[] { "Last Laptime", "Best Laptime", "Best Sector 1",
        "Best Sector 2", "Last Sector 1", "Last Sector 2" };
    var fiftyHz = new[] { "Lap Number", "Lap Start Elapsed Time", "Session Elapsed Time" };

    foreach (var nm in fiveHz.Concat(fiftyHz))
    {
        var c = channels.Find(x => x.StandardName == nm);
        Assert.True(c is not null, $"{nm} should be emitted");
        int expect = fiveHz.Contains(nm) ? 5 : 50;
        Assert.Equal(expect, c!.FrequencyHz);

        // The sample count must match the declared rate over the session's span.
        // 557 s of data: 5 Hz -> ~2787 samples, 50 Hz -> ~27874.
        if (expect == 5)
        Assert.InRange(c.Samples.Length, 2700, 2900);
        else
        Assert.InRange(c.Samples.Length, 27000, 29000);
    }
}

[Fact]
public void Test_Sector_Time_Channels_Pulse_Like_The_Reference()
{
    // The reference emits "Cur Sector 1/2" and "Last Sector 1/2" as PULSES that drop to a
    // sentinel between laps, not as hold-last staircases, and "Best Sector 1/2" updates at
    // the sector split rather than at the lap line. Measured runs, from the genuine log in
    // log+example/Same data/Duckdb:
    //   Cur Sector 1   -1, 32.7, -1, 32.5, -1, 32.4, -1, 32.1, -1, 32.3
    //   Last Sector 1   0, -1, 32.7, 32.5, 32.4, 32.1
    //   Best Sector 1  -1, 32.7, 32.5, 32.4, 32.1
    // Sector 2 is the same pulse shape, with the values from the recorded table.
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
    dir = Directory.GetParent(dir)?.FullName;
    Assert.NotNull(dir);
    var db = Path.Combine(dir,
    "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
    if (!File.Exists(db)) return;

    using var reader = new DuckDbReader(db);
    var channels = reader.ReadAndResampleChannels(50);

    float[] Runs(string name)
    {
        var c = channels.Find(x => x.StandardName == name);
        Assert.NotNull(c);
        var runs = new List<float>();
        foreach (var v in c!.Samples)
        {
            // Quantise to the channel's 1-decimal encoding first: two laps whose sector
            // times differ below that resolution are one run to i2.
            float q = MathF.Round(v, 1);
            if (runs.Count == 0 || Math.Abs(runs[^1] - q) > 1e-3f) runs.Add(q);
        }
        return runs.ToArray();
    }

    void AssertRuns(float[] expected, float[] actual, string what)
    {
        Assert.True(expected.Length == actual.Length &&
        expected.Zip(actual).All(p => Math.Abs(p.First - p.Second) < 1e-2f),
        $"{what}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }

    AssertRuns(new[] { -1f, 32.7f, -1f, 32.5f, -1f, 32.4f, -1f, 32.1f, -1f, 32.3f },
    Runs("Cur Sector 1"), "Cur Sector 1");
    AssertRuns(new[] { 0f, -1f, 32.7f, 32.5f, 32.4f, 32.1f },
    Runs("Last Sector 1"), "Last Sector 1");
    AssertRuns(new[] { -1f, 32.7f, 32.5f, 32.4f, 32.1f },
    Runs("Best Sector 1"), "Best Sector 1");

    // Sector 2 is the same shape, with the values taken from the recorded "Current Sector 2"
    // table - 65.7 for the last lap, not the 65.76 the crossing difference would give.
    AssertRuns(new[] { -1f, 66.7f, -1f, 66.1f, -1f, 66.1f, -1f, 65.7f, -1f },
    Runs("Cur Sector 2"), "Cur Sector 2");
    AssertRuns(new[] { 0f, -1f, 66.7f, 66.1f, 65.7f },
    Runs("Last Sector 2"), "Last Sector 2");
    AssertRuns(new[] { -1f, 66.7f, 66.1f, 65.7f },
    Runs("Best Sector 2"), "Best Sector 2");
}

[Fact]
public void Test_Ldx_Has_No_Setup_Block()
{
    // The car setup goes to its own .svm file. i2 rewrites
    // the .ldx on load anyway, so _Setup_* entries there were dead weight.
    var ldxPath = Path.Combine(_testDir, "nosetup.ldx");
    var meta = new SessionMetadata
    {
        Vehicle = "Car (CLASS)",
        RawSetupJson = """{ "VM_REAR_WING": { "value": 7, "stringValue": "P11" } }"""
    };

    LdxWriter.WriteFile(ldxPath, meta, new List<LapBeacon>());
    var text = File.ReadAllText(ldxPath);

    Assert.DoesNotContain("_Setup_", text);
        Assert.DoesNotContain("<Numeric", text);
        Assert.Contains("<String Id=\"Vehicle\" Value=\"Car (CLASS)\"/>", text);
    }

    [Fact]
    public void Test_Real_DuckDb_Emits_Lap_Number_Channel()
    {
        // A genuine i2 log defines lap boundaries with an integer "Lap Number"
        // channel that steps at each crossing. i2 reads the .ld and then REWRITES
        // the .ldx, so the .ld channels are the input - both are now synthesised.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);

        var realPath = Path.Combine(dir, "log+example/Paul.Ricard.Circuit_P_2026-06-02T20_04_44Z/Paul Ricard Circuit_P_2026-06-02T20_04_44Z.duckdb");
        // The reference fixtures are not committed to this repository - they are the
        // user's own recorded telemetry, which must not be redistributed. Skip
        // rather than fail when the checkout has no sample data.
        if (!File.Exists(realPath)) return;

        using var reader = new DuckDbReader(realPath);
        reader.Open();
        var channels = reader.ReadAndResampleChannels(50);

        var lapNumber = channels.Find(c => c.StandardName == "Lap Number");
        Assert.NotNull(lapNumber);
        Assert.NotEmpty(lapNumber.Samples);

        // 4 real crossings => the counter steps 0,1,2,3 and never reaches 4.
        var steps = lapNumber.Samples.Distinct().OrderBy(v => v).ToList();
        Assert.Equal(new float[] { 0, 1, 2, 3 }, steps);

        // It must be integral, and it must step UP at each crossing (3 steps).
        Assert.All(lapNumber.Samples, v => Assert.Equal(v, MathF.Floor(v)));
        int increases = 0;
        for (int i = 1; i < lapNumber.Samples.Length; i++)
        {
            if (lapNumber.Samples[i] > lapNumber.Samples[i - 1] + 0.5f) increases++;
        }
        Assert.Equal(3, increases);

        // No unit - i2 leaves that field empty for this channel.
        Assert.True(string.IsNullOrWhiteSpace(lapNumber.Units));
    }

    [Theory]
    // The four sampling modes a user can choose. "Source" is everything the game
    // recorded (largest), "Optimised (default)" is i2's own per-channel rate (default),
    // "Reduced" divides each channel's own rate, and "Pro" sets channels individually.
    [InlineData(RateMode.Source, 0)]
    [InlineData(RateMode.Optimised, 0)]
    [InlineData(RateMode.Reduced, 2)]
    [InlineData(RateMode.Pro, 0)]
    public void Test_Rate_Modes_Produce_A_Consistent_File(RateMode mode, int arg)
    {
        // log+example is git-ignored, so this fixture may legitimately be absent.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);
        var dbPath = Path.Combine(dir,
            "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
        if (!File.Exists(dbPath)) return;

        string modeDir = Path.Combine(_testDir, $"rate-{mode}");
        Directory.CreateDirectory(modeDir);
        var opts = new TelemetryConverter.ConversionOptions
        {
            ExtractCarSetup = false,
            RateMode = mode,
            ReducedDivisor = arg == 0 ? 2 : arg,
        };
        if (mode == RateMode.Pro)
        {
            opts.RateOverrides["Ground Speed"] = 100;
            opts.RateOverrides["Engine RPM"] = 100;
        }

        Assert.True(TelemetryConverter.ConvertSingleFile(dbPath, modeDir, opts),
            "conversion should succeed");

        // The output is named after the input DuckDB.
        string ldPath = Directory.GetFiles(modeDir, "*.ld").Single();
        var raw = File.ReadAllBytes(ldPath);
        const int HeaderSize = 124;
        int firstData = (int)BitConverter.ToUInt32(raw, 0x0C);

        // Walk the headers, and check the invariant that matters: every channel implies the
        // same session span, and the data region ends exactly at the last byte.
        var spans = new List<double>();
        long declared = 0;
        int channels = 0;
        int ptr = (int)BitConverter.ToUInt32(raw, 0x08);
        var seen = new HashSet<int>();
        while (ptr > 0 && ptr + HeaderSize <= raw.Length && seen.Add(ptr))
        {
            uint n = BitConverter.ToUInt32(raw, ptr + 0x0C);
            ushort width = BitConverter.ToUInt16(raw, ptr + 0x14);   // bytes per sample
            ushort rate = BitConverter.ToUInt16(raw, ptr + 0x16);    // Hz
            declared += (long)n * width;
            Assert.True(rate > 0, "every channel must declare a rate");
            spans.Add(n / (double)rate);
            channels++;
            int next = (int)BitConverter.ToUInt32(raw, ptr + 0x04);
            if (next == 0) break;
            ptr = next;
        }

        Assert.True(channels > 100, $"expected the full channel set, got {channels}");
        double spread = spans.Max() - spans.Min();
        Assert.True(spread / spans.Min() < 0.01,
            $"channel spans disagree: {spans.Min():F2} .. {spans.Max():F2} s");
        Assert.Equal(raw.Length, firstData + declared);
    }

    [Fact]
    public void Test_Reduced_Divides_Each_Channels_Own_Rate()
    {
        // Reduced is a factor, not a ceiling: a ceiling would take 95% of a 100 Hz
        // channel while leaving a 2 Hz one untouched. Each channel is divided from
        // its OWN i2 rate, so a fast channel stays fast relative to a slow one.
        // ID 301 (Throttle Pos) is 10 Hz and ID 9 (Ground Speed) is 10 Hz in i2's
        // table; a 100 Hz source lets both reach their full rate.
        int Reduced(int divisor, string name, ushort id)
        {
            var policy = new RatePolicy { Mode = RateMode.Reduced, ReducedDivisor = divisor };
            return policy.Resolve(name, id, 100);
        }

        Assert.Equal(5, Reduced(2, "Throttle Pos", 301));    // 10 / 2
        Assert.Equal(2, Reduced(4, "Throttle Pos", 301));    // 10 / 4 = 2.5, rounds to 2
        Assert.Equal(10, Reduced(1, "Throttle Pos", 301));   // 1 disables the reduction
        Assert.Equal(10, Reduced(0, "Throttle Pos", 301));   // as does 0

        // Never zero, however large the factor: a rate of 0 is not a file i2 can
        // read, and a 1 Hz channel divided by 8 would otherwise round to it.
        Assert.Equal(1, Reduced(8, "Ambient Temperature", 701));

        // The reduction never exceeds what the source recorded: a channel the game
        // logged at 2 Hz cannot be written at 5.
        var capped = new RatePolicy { Mode = RateMode.Reduced, ReducedDivisor = 1 };
        Assert.Equal(2, capped.Resolve("Throttle Pos", 301, 2));

        // And the whole point: a fast channel keeps its lead over a slow one.
        int fast = Reduced(2, "Susp Pos FL", 400);    // 100 Hz in i2's table
        int slow = Reduced(2, "Ambient Temperature", 701);
        Assert.True(fast > slow * 4,
            $"a 100 Hz channel should stay well ahead of a 10 Hz one, got {fast} vs {slow}");
    }

    [Fact]
    // "Beacon" (ID 110) is the channel i2 reads laps from. Its structure, verified
    // against the genuine log of this same DuckDB session: a three-sample pulse at each lap
    // line and each sector split, then settling at 100 for a lap and 56 for a sector,
    // holding 0 before the first pulse. The counter in the pulse's second sample starts at
    // -32767 and rises by one per pulse, by two after a sector rise.
    public void Test_Beacon_Pulses_Match_The_Genuine_Log_Structure()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);
        var dbPath = Path.Combine(dir,
            "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
        if (!File.Exists(dbPath)) return;

        Assert.True(TelemetryConverter.ConvertSingleFile(dbPath, _testDir,
            new TelemetryConverter.ConversionOptions { ExtractCarSetup = false }));

        var raw = File.ReadAllBytes(Directory.GetFiles(_testDir, "*.ld").Single());
        const int HeaderSize = 124;

        // Find Beacon and decode it with i2's own formula.
        double[]? beacon = null;
        int beaconRate = 0, ptr = (int)BitConverter.ToUInt32(raw, 0x08);
        var seen = new HashSet<int>();
        while (ptr > 0 && ptr + HeaderSize <= raw.Length && seen.Add(ptr))
        {
            uint n = BitConverter.ToUInt32(raw, ptr + 0x0C);
            ushort id = BitConverter.ToUInt16(raw, ptr + 0x10);
            ushort width = BitConverter.ToUInt16(raw, ptr + 0x14);
            uint dataPtr = BitConverter.ToUInt32(raw, ptr + 0x08);
            short shift = BitConverter.ToInt16(raw, ptr + 0x18);
            short mul = BitConverter.ToInt16(raw, ptr + 0x1A);
            short scale = BitConverter.ToInt16(raw, ptr + 0x1C);
            short dec = BitConverter.ToInt16(raw, ptr + 0x1E);

            if (id == 110)
            {
                beaconRate = BitConverter.ToUInt16(raw, ptr + 0x16);
                beacon = new double[n];
                for (uint k = 0; k < n; k++)
                {
                    int o = (int)(dataPtr + k * width);
                    int v = width switch
                    {
                        1 => raw[o] > 127 ? raw[o] - 256 : raw[o],
                        2 => BitConverter.ToInt16(raw, o),
                        _ => BitConverter.ToInt32(raw, o),
                    };
                    beacon[k] = v / (scale * Math.Pow(10, dec)) * mul + shift;
                }
            }
            int next = (int)BitConverter.ToUInt32(raw, ptr + 0x04);
            if (next == 0) break;
            ptr = next;
        }

        Assert.NotNull(beacon);
        Assert.Equal(5, beaconRate);

        // The pulse frame: marker, counter, payload - then a settle that names the kind.
        var pulses = beacon!.Select((v, i) => (v, i)).Where(x => x.v == -8192).ToList();
        Assert.Equal(14, pulses.Count);

        var settles = new List<double>();
        var counters = new List<double>();
        foreach (var (_, i) in pulses)
        {
            Assert.True(i + 3 < beacon.Length, "each pulse must have three samples and a settle");
            Assert.Equal(-8192, beacon[i]);
            counters.Add(beacon[i + 1]);
            Assert.InRange(beacon[i + 2], 16000, 18000);
            settles.Add(beacon[i + 3]);
        }

        // Five lap lines and nine sector splits, in the pattern lap, sector, sector,
        // repeated - the last lap line's FIRST sector split does pulse, and its second does
        // not: the genuine log's final transition carries no pulse.
        double[] expectedSettles = { 100, 56, 56, 100, 56, 56, 100, 56, 56, 100, 56, 56, 100, 56 };
        Assert.Equal(expectedSettles, settles);

        // The counter starts at -32767 and steps +1, +2, +1, +1, +2 ... as the genuine
        // log's fourteen pulses do.
        Assert.Equal(-32767, counters[0]);
        int[] expectedSteps = { 1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 1 };
        for (int i = 0; i < expectedSteps.Length; i++)
        {
            Assert.Equal(expectedSteps[i], counters[i + 1] - counters[i]);
        }

        // Nothing before the first pulse: the genuine log holds 0 until the first crossing.
        Assert.Equal(0, beacon[pulses[0].i - 1]);
    }

    [Fact]
    public void Test_One_Byte_Channels_Do_Not_Overrun_The_Data_Region()
    {
        // Regression: i2 declares a 1-byte element for flags such as "Current Sector"
        // and "Sector Flag N". The data loop wrote every sample as an int32, so the stream
        // ran 3 bytes ahead per sample of every 1-byte channel and displaced all the
        // blocks that followed - 548,691 bytes on the paired session. Channels whose
        // pointers were shifted then decoded as garbage.
        //
        // This puts several 1-byte channels ahead of a 4-byte one, then asserts the
        // emitted file is exactly as long as the headers say it should be.
        var ldPath = Path.Combine(_testDir, "onebyte.ld");
        // These names resolve to standard IDs 2219..2221 and 2103, which is what makes
        // i2 declare a 1-byte element for them; a name with no ID takes the computed
        // fallback width instead, so the test would not exercise the bug.
        var channels = new List<TelemetryChannel>
        {
            new() { StandardName = "Sector Flag 1", ShortName = "SecFlg1",
                    FrequencyHz = 5, Samples = new float[] { 0, 1, 0, 0, 1 } },
            new() { StandardName = "Sector Flag 2", ShortName = "SecFlg2",
                    FrequencyHz = 5, Samples = new float[] { 0, 0, 1, 0, 0 } },
            new() { StandardName = "Sector Flag 3", ShortName = "SecFlg3",
                    FrequencyHz = 5, Samples = new float[] { 1, 0, 0, 1, 0 } },
            new() { StandardName = "Current Sector", ShortName = "CurSect",
                    FrequencyHz = 50, Samples = new float[] { 0, 1, 2, 1, 0 } },
            new() { StandardName = "Front Flap Activated", ShortName = "FFlap",
                    FrequencyHz = 10, Samples = new float[] { 0, 0, 1, 0, 0 } },
        };
        channels.Add(new TelemetryChannel
        {
            StandardName = "Ground Speed",
            ShortName = "Grd Spd",
            Units = "km/h",
            FrequencyHz = 10,
            Samples = new float[] { 0f, 100f, 231.36f },
        });
        LdWriter.WriteFile(ldPath, new SessionMetadata(), channels);

        var raw = File.ReadAllBytes(ldPath);
        const int HeaderSize = 124;
        int firstData = (int)BitConverter.ToUInt32(raw, 0x0C);
        long declared = 0;
        int oneByteChannels = 0;
        int ptr = (int)BitConverter.ToUInt32(raw, 0x08);
        var seen = new HashSet<int>();
        while (ptr > 0 && ptr + HeaderSize <= raw.Length && seen.Add(ptr))
        {
            uint n = BitConverter.ToUInt32(raw, ptr + 0x0C);
            ushort width = BitConverter.ToUInt16(raw, ptr + 0x14);
            if (width == 1) oneByteChannels++;
            declared += (long)n * width;
            int next = (int)BitConverter.ToUInt32(raw, ptr + 0x04);
            if (next == 0) break;
            ptr = next;
        }

        Assert.Equal(5, oneByteChannels);
        // Each of those channels would have added 3 bytes per sample if mis-written.
        Assert.Equal(raw.Length, firstData + declared);
    }

    [Fact]
    public void Test_Counter_Channels_Use_i2_Own_Shift_And_Decode_Back_Exactly()
    {
        // Regression, and a correction of an earlier wrong belief: a blanket shift was
        // once applied to every channel, and the fix was to force shift 0 on counters.
        // Both halves of that were wrong. i2's own log uses shift 32760 on "Lap Number"
        // and on the timing channels, and shift 0 on the 1-byte flags - so the shift is a
        // per-channel property, not a blanket rule. What matters is that the stored value
        // DECODES back to the sample, which is:
        //
        //     value = raw / (scale * 10^decimals) * multiplier + shift
        //
        // i.e. the shift is added AFTER the scaling. Decoding it as
        // (raw + shift) * ... puts the reference's "Lap Number" at -32760 instead of 0.
        var ldPath = Path.Combine(_testDir, "shift.ld");
        var channels = new List<TelemetryChannel>
        {
            new() { StandardName = "Lap Number", ShortName = "Lap N",
                    FrequencyHz = 50, Samples = new float[] { 0, 0, 1, 1, 2, 3 } },
            new() { StandardName = "Current Sector", ShortName = "CurSect",
                    FrequencyHz = 50, Samples = new float[] { 0, 1, 2, 1, 0, 0 } },
            new() { StandardName = "Ground Speed", ShortName = "Grd Spd", Units = "km/h",
                    FrequencyHz = 10, Samples = new float[] { 0f, 12.5f, 231.36f, 100f } },
        };
        LdWriter.WriteFile(ldPath, new SessionMetadata(), channels);

        var raw = File.ReadAllBytes(ldPath);
        Assert.Equal(0x40u, BitConverter.ToUInt32(raw, 0x00));

        // Walk the channel headers the way a reader does, and decode each block back.
        var expected = channels.ToDictionary(
            c => c.StandardName,
            c => c.Samples.Select(v => MathF.Round(v, 3)).ToArray());

        const int HeaderSize = 124;
        int ptr = (int)BitConverter.ToUInt32(raw, 0x08);
        var seen = new HashSet<int>();
        int decodedChannels = 0;
        while (ptr > 0 && ptr + HeaderSize <= raw.Length && seen.Add(ptr))
        {
            string name = System.Text.Encoding.ASCII
                .GetString(raw, ptr + 0x20, 32).TrimEnd('\0');
            uint dataPtr = BitConverter.ToUInt32(raw, ptr + 0x08);
            uint n = BitConverter.ToUInt32(raw, ptr + 0x0C);
            ushort width = BitConverter.ToUInt16(raw, ptr + 0x14);
            short shift = BitConverter.ToInt16(raw, ptr + 0x18);
            short mul = BitConverter.ToInt16(raw, ptr + 0x1A);
            short scale = BitConverter.ToInt16(raw, ptr + 0x1C);
            short dec = BitConverter.ToInt16(raw, ptr + 0x1E);

            var want = expected[name];
            Assert.Equal(want.Length, (int)n);
            for (uint i = 0; i < n; i++)
            {
                int stored = width switch
                {
                    1 => unchecked((sbyte)raw[dataPtr + i]),
                    2 => BitConverter.ToInt16(raw, (int)dataPtr + (int)i * 2),
                    _ => BitConverter.ToInt32(raw, (int)dataPtr + (int)i * 4),
                };
                float value = (float)(stored / (scale * Math.Pow(10, dec)) * mul + shift);
                // Compare against the resolution the header declares rather than
                // exactly: a channel at 10^-5 with a multiplier of 4 resolves to
                // 2.5e-5, and the reference's own logs carry the same quantisation.
                double quantum = (double)scale * Math.Pow(10, dec) / mul;
                Assert.True(Math.Abs(value - want[i]) <= quantum,
                    $"{name}[{i}]: decoded {value} but sample was {want[i]} " +
                    $"(quantum {quantum:G4})");
            }
            decodedChannels++;

            int next = (int)BitConverter.ToUInt32(raw, ptr + 0x04);
            if (next == 0) break;
            ptr = next;
        }
        Assert.Equal(channels.Count, decodedChannels);

        // The data must exactly fill the space the headers reserve, with no overrun.
        uint firstData = BitConverter.ToUInt32(raw, 0x0C);
        long declared = 0;
        ptr = (int)firstData - HeaderSize * channels.Count;
        seen.Clear();
        while (ptr > 0 && ptr + HeaderSize <= raw.Length && seen.Add(ptr))
        {
            declared += (long)BitConverter.ToUInt32(raw, ptr + 0x0C) *
                        BitConverter.ToUInt16(raw, ptr + 0x14);
            int next = (int)BitConverter.ToUInt32(raw, ptr + 0x04);
            if (next == 0) break;
            ptr = next;
        }
        Assert.Equal(raw.Length, firstData + declared);
    }

    [Fact]
    public void Test_Header_Writes_The_Genuine_LdHead_Constants()
    {
        // 0x5DE is the "Pro logging enabled" flag: i2 refuses to open a log
        // without it. But the whole constant set matters, because i2 takes the lap
        // count from the header. Ours were lifted from a different source file, and i2
        // then rendered every Beacon crossing as a lap - 13 laps for a 4-lap session.
        // All three genuine logs agree on these byte for byte, and they have to move
        // together: i2 rejects a file whose pro-logging flag is genuine while the rest
        // of the header still carries the old values.
        var ldPath = Path.Combine(_testDir, "promagic.ld");
        LdWriter.WriteFile(ldPath, new SessionMetadata
        {
            Driver = "Test Driver",
            Vehicle = "Car",
            Venue = "Circuit"
        }, new List<TelemetryChannel>
        {
            new() { StandardName = "Speed", ShortName = "Spd", FrequencyHz = 10,
                   Samples = new float[] { 1, 2, 3, 4 } }
        });

        var head = File.ReadAllBytes(ldPath).Take(1890).ToArray();

        Assert.Equal(1762u, BitConverter.ToUInt32(head, 0x24));    // header size field
        Assert.Equal((ushort)0, BitConverter.ToUInt16(head, 0x40));
        Assert.Equal(12007u, BitConverter.ToUInt32(head, 0x46));   // device serial
        Assert.Equal((ushort)200, BitConverter.ToUInt16(head, 0x52));
        Assert.Equal((ushort)128, BitConverter.ToUInt16(head, 0x54));
        Assert.Equal(78u, BitConverter.ToUInt32(head, 0x56));      // NOT the channel count
        Assert.Equal(100, head[0x5A]);
        Assert.Equal(1, head[0x5C]);
        Assert.Equal(99, head[0x66C]);

        var magic = BitConverter.ToUInt32(head, 0x5DE);
        Assert.Equal(0x00D20822u, magic);
        Assert.Equal(LdWriter.ProLoggingMagic, magic);

        // The driver string must still be where the reference puts it (0x9E).
        var text = System.Text.Encoding.ASCII.GetString(head);
        Assert.Equal("Test Driver", text.Substring(0x9E, 11).Split('\0')[0]);
    }

    [Fact]
    public void Test_Header_Date_Uses_A_Literal_Slash_Under_Any_Culture()
    {
        // Regression, reported from a German-locale Windows: the header date came out
        // "01.10.2026" and i2 showed "Invalid DateTime". In a .NET custom format string
        // "/" is the CULTURE's date separator, so the genuine log's "01/10/2026" only
        // survives with an invariant culture. Pinned under de-DE, which is where it broke.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var ldPath = Path.Combine(_testDir, "dateculture.ld");
            LdWriter.WriteFile(ldPath, new SessionMetadata
            {
                Driver = "Test Driver",
                Vehicle = "Car",
                Venue = "Circuit",
                SessionDateTime = new DateTime(2026, 10, 1, 13, 22, 50, DateTimeKind.Utc)
            }, new List<TelemetryChannel>
            {
                new() { StandardName = "Speed", ShortName = "Spd", FrequencyHz = 10,
                       Samples = new float[] { 1, 2, 3, 4 } }
            });

            var head = File.ReadAllBytes(ldPath);
            var date = System.Text.Encoding.ASCII.GetString(head, 0x5E, 16).Split('\0')[0];
            Assert.Equal("01/10/2026", date);
            Assert.DoesNotContain(".", date);

            // The time must be the local rendering of the UTC session start, not UTC.
            var time = System.Text.Encoding.ASCII.GetString(head, 0x7E, 16).Split('\0')[0];
            var expected = new DateTime(2026, 10, 1, 13, 22, 50, DateTimeKind.Utc)
                .ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Assert.Equal(expected, time);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Test_Vehicle_In_Ld_Carries_The_Name_Without_The_Class_Suffix()
    {
        // i2 looks the car up in its own database to label the session. The genuine log
        // carries "Proton Competition 2025 #88:LM" and i2 shows "GT3" beside the driver;
        // ours carried that plus " (GT3)" and i2 showed no model at all.
        var ldPath = Path.Combine(_testDir, "vehicle.ld");
        LdWriter.WriteFile(ldPath, new SessionMetadata
        {
            Driver = "Test Driver",
            Vehicle = "Proton Competition 2025 #88:LM (GT3)",
            Venue = "Circuit"
        }, new List<TelemetryChannel>
        {
            new() { StandardName = "Speed", ShortName = "Spd", FrequencyHz = 10,
                   Samples = new float[] { 1, 2, 3, 4 } }
        });

        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(ldPath));
        Assert.Contains("Proton Competition 2025 #88:LM", text);
        Assert.DoesNotContain("(GT3)", text);
    }

    [Fact]
    public void Test_Shared_Id_Channels_Use_The_Faster_Rate()
    {
        // ID 301 is shared by Throttle Pos and Battery Charge Level. The table used to
        // hold the slower channel's 2 Hz, and Throttle Pos - recorded at 50 Hz and logged
        // by i2 at 10 - read as a weave where the genuine log is a straight: 96% of
        // the signal was thrown away. All three genuine logs in the tree give every user
        // of IDs 5, 301 and 2110 the rate the table now holds, so the faster user wins.
        var policy = new RatePolicy();
        // 301 is Throttle Pos' standard ID; 5 is Engine RPM's. Both are shared, and the
        // genuine logs give every user of them the same rate.
        Assert.Equal(10, policy.Resolve("Throttle Pos", 301, 100));
        Assert.Equal(10, policy.Resolve("Engine RPM", 5, 100));
        Assert.Equal(25, policy.Resolve("Motor State", 2110, 100));

        // A denser grid does not damage the slower sharer - i2's rate is a per-channel
        // choice, not a limit - so Battery Charge Level riding along at 10 Hz is fine.

        // And the source is still the ceiling: a channel the game recorded at 2 Hz stays
        // at 2 even though i2 wants 10.
        Assert.Equal(2, policy.Resolve("Throttle Pos", 301, 2));
    }

    [Fact]
    public void Test_DuckDbChannelNames_RenamesDisplayButKeepsStandardId()
    {
        // A DuckDB-name build swaps the display name at 0x20 for the DuckDB spelling while the
        // standard ID behind it stays: i2 shows the file's name (confirmed in i2, Oct 2026),
        // but the ID is what the lap logic, reports and built-in maths bind to. The channels
        // i2's maths and lap/sector machinery resolve by name (ChannelIds.KeepStandardNames)
        // are left standard,
        // or those maths would read them as missing.
        var dbPath = CreateSampleDuckDbFile("names.duckdb");
        var ldPath = Path.Combine(_testDir, "names.ld");

        using (var reader = new DuckDbReader(dbPath))
        {
            reader.Open();
            var channels = reader.ReadAndResampleChannels(
                50, null, null, default, useDuckDbChannelNames: true);

            // The DuckDB "Throttle Pos" is the processed pedal; i2 names it
            // "Throttle Pos Filtered" (ID 3100). Display renamed, identity unchanged.
            var throttle = channels.Find(c => c.Identity == "Throttle Pos Filtered");
            Assert.NotNull(throttle);
            Assert.Equal("Throttle Pos", throttle.StandardName);

            // "Ground Speed" is in the maths keep-set: its name must NOT move.
            var speed = channels.Find(c => c.Identity == "Ground Speed");
            Assert.NotNull(speed);
            Assert.Equal("Ground Speed", speed.StandardName);

            LdWriter.WriteFile(ldPath, new SessionMetadata(), channels);
        }

        // Read the emitted names/IDs back out of the file.
        using var fs = File.OpenRead(ldPath);
        using var br = new BinaryReader(fs);
        fs.Seek(0x08, SeekOrigin.Begin);
        uint head = br.ReadUInt32();
        var byName = new Dictionary<string, ushort>();
        for (uint p = head; p != 0 && byName.Count < 10000; )
        {
            fs.Seek(p + 0x10, SeekOrigin.Begin);
            ushort id = br.ReadUInt16();
            fs.Seek(p + 0x20, SeekOrigin.Begin);
            var nb = new byte[32];
            fs.ReadExactly(nb, 0, 32);
            byName[System.Text.Encoding.ASCII.GetString(nb).Split('\0')[0]] = id;
            fs.Seek(p + 0x04, SeekOrigin.Begin);
            p = br.ReadUInt32();
        }

        Assert.True(byName.ContainsKey("Throttle Pos"), "renamed channel not written under its DuckDB name");
        Assert.Equal((ushort)3100, byName["Throttle Pos"]);             // standard ID preserved
        Assert.False(byName.ContainsKey("Throttle Pos Filtered"),
            "the standard name should have been replaced, not duplicated");
        Assert.Equal((ushort)9, byName["Ground Speed"]);                // keep-set untouched
    }

    [Fact]
    public void Test_DuckDbChannelNames_Is_On_By_Default()
    {
        // Flipped to on in 2.24.0: a user should read the game's own channel names without
        // having to ask. The i2 spellings remain one toggle away.
        Assert.True(new TelemetryConverter.ConversionOptions().UseDuckDbChannelNames);
        Assert.True(new AppSettings().UseDuckDbChannelNames);
    }

    [Fact]
    public void Test_Channel_Id_At_0x10_Uses_Standard_Ids()
    {
        // Regression: 0x10 is i2's standard channel ID, not a sequence number.
        // We wrote 0x2EE1 + i (12001..) and i2 showed NO CHANNELS AT ALL, because
        // it could not resolve any of those IDs. Values must come from i2's own
        // table, transcribed from the working reference log.
        var ldPath = Path.Combine(_testDir, "ids.ld");
        LdWriter.WriteFile(ldPath, new SessionMetadata(), new List<TelemetryChannel>
        {
            new() { StandardName = "Ground Speed", ShortName = "Grd Spd", FrequencyHz = 10,
                    Samples = new float[] { 1, 2, 3, 4 } },
            new() { StandardName = "Throttle Pos", ShortName = "Thrttle", FrequencyHz = 10,
                    Samples = new float[] { 0, 1, 0, 1 } },
            new() { StandardName = "Lap Number", ShortName = "Lap N", FrequencyHz = 50,
                    Samples = new float[] { 0, 0, 1, 1 } },
            // A channel i2 has no standard ID for must not collide with one.
            new() { StandardName = "Some Custom Channel", ShortName = "CustCh", FrequencyHz = 50,
                    Samples = new float[] { 5, 6, 7, 8 } },
        });

        var expected = new Dictionary<string, ushort>
        {
            ["Ground Speed"] = 9,
            ["Throttle Pos"] = 301,
            ["Lap Number"] = 2101,
        };

        using var fs = File.OpenRead(ldPath);
        using var br = new BinaryReader(fs);
        Assert.Equal(0x40u, br.ReadUInt32());
        // 0x56 holds a genuine constant (78), not the channel count; walk the list.
        fs.Seek(0x08, SeekOrigin.Begin);
        uint listHead = br.ReadUInt32();
        int count = 0;
        for (uint p = listHead; p != 0 && count < 10000; count++)
        {
            fs.Seek(p + 0x04, SeekOrigin.Begin);
            p = br.ReadUInt32();
        }

        var seen = new List<ushort>();
        for (int i = 0; i < count; i++)
        {
            fs.Seek(5000L + (long)i * 124 + 0x10, SeekOrigin.Begin);
            ushort id = br.ReadUInt16();
            seen.Add(id);

            fs.Seek(5000L + (long)i * 124 + 0x20, SeekOrigin.Begin);
            var nameBytes = new byte[32];
            fs.ReadExactly(nameBytes, 0, 32);
            string name = System.Text.Encoding.ASCII.GetString(nameBytes).Split('\0')[0];

            if (expected.TryGetValue(name, out ushort want))
            {
                Assert.Equal(want, id);   // real Standard names ID
            }
            else
            {
                Assert.DoesNotContain(id, expected.Values);   // no collision
            }
        }

        // Every ID must be in i2's space, never the old invented 12001+ sequence.
        Assert.All(seen, id => Assert.True(id < 12000, $"ID {id} is outside i2's range"));

        // Unmapped channels get a synthetic ID above every standard one. That used to
        // be a hardcoded 8000, which sits INSIDE i2's real range - 8100..8211 are
        // Camber / Lat Force / Toe / Wheel Y Location - so it could alias a real
        // channel. It is now derived from the highest standard ID.
        var synthetic = seen.Where(id => id >= 8000).ToList();
        Assert.NotEmpty(synthetic);
        Assert.All(synthetic, id => Assert.True(id > 8211,
            $"ID {id} is inside i2's standard range and could alias a channel"));

        // Synthetic IDs must not repeat either.
        Assert.Equal(synthetic.Count, synthetic.Distinct().Count());
    }

    [Fact]
    public void Test_Header_First_Channel_Pointer_Is_Populated()
    {
        // THE regression behind "no channels at all" (v2.4.0 - v2.6.0). ldHead
        // offset 0x08 is the first channel header pointer and must not be zero:
        //   genuine reference  0x08 = 0x3448  -> channels load, values visible
        //   v2.2.0             0x08 = 0x1388  -> channels load, values visible
        //   v2.4.0..v2.6.0     0x08 = 0x0000  -> NO channels at all
        // v2.4.0 zeroed it after I misread which offset held the pointer. The track
        // report kept working because it renders from ldHead, not the channel list.
        var ldPath = Path.Combine(_testDir, "hdrptr.ld");
        LdWriter.WriteFile(ldPath, new SessionMetadata(), new List<TelemetryChannel>
        {
            new() { StandardName = "Throttle Pos", ShortName = "Thrttle", FrequencyHz = 10,
                    Samples = new float[] { 0, 1, 0, 1 } },
            new() { StandardName = "Brake Pos", ShortName = "Brake", FrequencyHz = 10,
                    Samples = new float[] { 1, 0, 1, 0 } },
        });

        using var fs = File.OpenRead(ldPath);
        using var br = new BinaryReader(fs);
        Assert.Equal(0x40u, br.ReadUInt32());

        fs.Seek(0x08, SeekOrigin.Begin);
        uint firstHeader = br.ReadUInt32();
        uint firstData = br.ReadUInt32();          // 0x0C

        Assert.Equal(5000u, firstHeader);
        Assert.Equal(5000u + 2 * 124, firstData);  // right after the two 124-byte headers

        // The pointer must land on a real channel header, not stray bytes.
        fs.Seek(firstHeader, SeekOrigin.Begin);
        Assert.Equal(0u, br.ReadUInt32());          // prev of the first channel
        Assert.Equal(firstHeader + 124, br.ReadUInt32());

        // And the header's own data pointer must agree with 0x0C.
        fs.Seek(firstHeader + 0x08, SeekOrigin.Begin);
        Assert.Equal(firstData, br.ReadUInt32());
    }
    [Fact]
    public void Test_Channel_Header_Writes_Short_Name_And_Unit_In_Their_Own_Fields()
    {
        // Regression: the writer packed a 32-byte short name at 0x40, overrunning the
        // 8-byte field and destroying the 12-byte unit field at 0x48. Every channel then
        // showed a blank unit, so i2 had no scale to interpret the samples with - the
        // cause of the nonsense values (speed reading ~15000 instead of ~285 km/h).
        //
        // Field layout per the reference parser (github.com/gotzl/ldparser):
        //     ldChan.fmt = '<IIII H HHH hhhh 32s 8s 12s 40x'   (124 bytes)
        //     0x20 name (32B)   0x40 SHORT NAME (8B)   0x48 UNIT (12B)
        // Verified against two genuine i2-written logs, where 0x48 holds 'km/h',
        // '%', 'C', 'mm', 'N', 'm/s', 's', 'rad', 'rad/s', 'm', 'kPa' and 'rpm'.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.NotNull(dir);
        var db = Path.Combine(dir,
            "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
        if (!File.Exists(db)) return;   // fixture not present in this checkout

        var outDir = Path.Combine(Path.GetTempPath(), "duckmetry-unit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            Assert.True(TelemetryConverter.ConvertSingleFile(
                db, outDir,
                new TelemetryConverter.ConversionOptions
                {
                    ExtractCarSetup = false,
                    ResampleFrequencyHz = 50,
                    OverwriteExisting = true
                },
                logger: _ => { }), "conversion should succeed");

            var bytes = File.ReadAllBytes(Directory.GetFiles(outDir, "*.ld").Single());
            const int ChannelHeaderSize = 124;
            uint ptr = BitConverter.ToUInt32(bytes, 0x08);
            Assert.NotEqual(0u, ptr);

            var seen = new HashSet<uint>();
            int withUnit = 0, withShort = 0, total = 0;
            while (ptr != 0 && seen.Add(ptr) && ptr + ChannelHeaderSize <= bytes.Length)
            {
                string name = Read(bytes, (int)ptr + 0x20, 32);
                string shortName = Read(bytes, (int)ptr + 0x40, 8);
                string unit = Read(bytes, (int)ptr + 0x48, 12);
                total++;

                // The 8-byte field holds 7 characters plus a NUL.
                Assert.True(shortName.Length <= 7,
                    $"{name}: short name '{shortName}' is {shortName.Length} chars, field holds 7");
                if (shortName.Length > 0) withShort++;

                if (name == "Ground Speed") Assert.Equal("km/h", unit);
                if (name == "Brake Temp FL") Assert.Equal("C", unit);
                if (name == "Throttle Pos") Assert.Equal("%", unit);
                if (name == "Last Laptime") Assert.Equal("s", unit);
                if (name == "Lap Start Elapsed Time") Assert.Equal("s", unit);
                if (unit.Length > 0) withUnit++;

                uint next = BitConverter.ToUInt32(bytes, (int)ptr + 0x04);
                if (next == 0) break;
                ptr = next;
            }

            Assert.True(total > 50, $"expected many channels, walked {total}");
            Assert.Equal(total, withShort);
            Assert.True(withUnit >= total / 4,
                $"only {withUnit}/{total} channels carry a unit at 0x48");
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* best effort */ }
        }

        static string Read(byte[] b, int offset, int len)
        {
            int n = 0;
            while (n < len && b[offset + n] != 0) n++;
            return System.Text.Encoding.ASCII.GetString(b, offset, n);
        }
    }
    [Fact]
    public void Test_A_Newly_Activated_Channel_Arrives_With_Its_i2_Identity()
    {
        // The scenario this guards: the game starts recording a channel it did not
        // before, and the converter has to translate it correctly without anyone
        // having hand-written an entry for it.
        //
        // "Susp Force" is a real example - four channels in a genuine i2 log
        // (ids 4200-4203, 20 Hz, newtons, short name "Ride FL") that the DuckDB does
        // not record today. This injects it, converts, and checks what came out.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Duckmetry.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);
        var sampleDb = Path.Combine(dir,
            "log+example/Same data/Duckdb/WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb");
        if (!File.Exists(sampleDb)) return;

        // A copy, because this test writes to it.
        var dbPath = Path.Combine(_testDir, "injected.duckdb");
        File.Copy(sampleDb, dbPath, overwrite: true);

        using (var con = new DuckDB.NET.Data.DuckDBConnection($"Data Source={dbPath}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE ""Susp Force"" AS
                  SELECT (random()*4000+2000)::FLOAT AS value1,
                         (random()*4000+2000)::FLOAT AS value2,
                         (random()*4000+2000)::FLOAT AS value3,
                         (random()*4000+2000)::FLOAT AS value4
                  FROM range(11146);
                INSERT INTO channelsList VALUES ('Susp Force', 20, 'N');";
            cmd.ExecuteNonQuery();
        }

        var outDir = Path.Combine(_testDir, "injected-out");
        Directory.CreateDirectory(outDir);
        Assert.True(TelemetryConverter.ConvertSingleFile(dbPath, outDir,
            new TelemetryConverter.ConversionOptions { ExtractCarSetup = false }),
            "conversion should succeed");

        var ld = Directory.GetFiles(outDir, "*.ld").Single();
        var raw = File.ReadAllBytes(ld);
        const int HeaderSize = 124;

        // Walk the channel list and find what the injected table produced.
        var found = new Dictionary<string, (ushort Id, ushort Rate, string Unit, string Short)>();
        int ptr = (int)BitConverter.ToUInt32(raw, 0x08);
        var seen = new HashSet<int>();
        while (ptr > 0 && ptr + HeaderSize <= raw.Length && seen.Add(ptr))
        {
            string name = System.Text.Encoding.ASCII
                .GetString(raw, ptr + 0x20, 32).TrimEnd('\0');
            if (name.StartsWith("Susp Force", StringComparison.Ordinal))
            {
                found[name] = (
                    BitConverter.ToUInt16(raw, ptr + 0x10),              // standard ID
                    BitConverter.ToUInt16(raw, ptr + 0x16),              // Hz
                    System.Text.Encoding.ASCII.GetString(raw, ptr + 0x48, 8).TrimEnd('\0'),
                    System.Text.Encoding.ASCII.GetString(raw, ptr + 0x40, 8).TrimEnd('\0'));
            }
            int next = (int)BitConverter.ToUInt32(raw, ptr + 0x04);
            if (next == 0) break;
            ptr = next;
        }

        Assert.Equal(4, found.Count);

        // The names must be i2's own, so that the ID table can resolve them.
        Assert.True(found.ContainsKey("Susp Force FL"));
        Assert.True(found.ContainsKey("Susp Force RR"));

        // Identity, against what the genuine log carries for these channels.
        Assert.Equal(4200, found["Susp Force FL"].Id);
        Assert.Equal(4203, found["Susp Force RR"].Id);
        Assert.Equal(20, found["Susp Force FL"].Rate);
        Assert.Equal("Ride FL", found["Susp Force FL"].Short);

        // The unit is the one the game declared in channelsList. Before this was
        // wired through, a wheel channel from a table with no hand-written rule
        // lost it and was written with none.
        Assert.Equal("N", found["Susp Force FL"].Unit);
        Assert.Equal("N", found["Susp Force RR"].Unit);
    }

    [Fact]
    public void Test_Output_Name_Is_Date_Time_Track_And_Car()
    {
        var meta = new SessionMetadata
        {
            SessionDateTime = new DateTime(2026, 10, 1, 13, 22, 50, DateTimeKind.Utc),
            Venue = "WeatherTech Raceway Laguna Seca",
            Vehicle = "Proton Competition 2025 #88:LM",
        };

        var name = OutputName.Build(meta, "fallback");

        // The timestamp comes first, and has no colon: a folder of conversions then
        // sorts chronologically, and the name is legal on Windows.
        var local = meta.SessionDateTime.ToLocalTime();
        Assert.StartsWith(local.ToString("yyyy-MM-dd"), name);

        // A colon would be illegal in a Windows filename: "#88:LM" must lose it.
        Assert.Contains("Proton Competition 2025 #88-LM", name);
        Assert.DoesNotContain(":", name);

        // Three parts, in the order intended: date-time, track, car.
        var parts = name.Split(" - ");
        Assert.Equal(3, parts.Length);
        Assert.Contains("WeatherTech", parts[1]);
        Assert.Contains("Proton", parts[2]);
    }

    [Fact]
    public void Test_Output_Name_Falls_Back_When_Metadata_Is_Unusable()
    {
        // A DuckDB with no readable metadata still has to produce a name.
        var empty = new SessionMetadata { SessionDateTime = default };
        Assert.Equal("2026-08-03 - 20-38-46 - P1",
            OutputName.Build(empty, "2026-08-03 - 20-38-46 - P1"));

        // SessionMetadata carries placeholders before anything is read. They must not
        // end up in a filename, which is what would happen if "Telemetry Car" were trusted.
        var placeholders = new SessionMetadata
        {
            SessionDateTime = new DateTime(2026, 10, 1, 13, 22, 50, DateTimeKind.Utc),
            Venue = "Telemetry Circuit",
            Vehicle = "Telemetry Car",
        };
        var name = OutputName.Build(placeholders, "fallback");
        Assert.DoesNotContain("the game", name);
        Assert.StartsWith(placeholders.SessionDateTime.ToLocalTime()
            .ToString("yyyy-MM-dd"), name);
    }

    [Fact]
    public void Test_Settings_Live_In_The_Users_Own_Folder()
    {
        // Not beside the executable: that folder is often read-only, and settings
        // belong to the person who chose them rather than to the installation.
        var path = AppSettings.GetSettingsFilePath();
        Assert.Contains("Duckmetry", path);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), path);

        // The old location is still known, so settings from an earlier version can be
        // read rather than silently discarded on upgrade.
        Assert.NotEqual(AppSettings.GetLegacySettingsFilePath(), path);
    }

    [Fact]
    public void Test_Scanner_Recognises_A_Converted_Session_By_Its_New_Name()
    {
        // The converter names its output from the session's metadata, so the scanner has
        // to look for that name rather than the DuckDB's filename. Otherwise every
        // session would be offered for conversion again after it had already been done -
        // the name the two halves disagree about is the whole coupling.
        var srcDir = Path.Combine(_testDir, "ScanSource");
        var dstDir = Path.Combine(_testDir, "ScanDest");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        var created = CreateSampleDuckDbFile("scan_session.duckdb");
        var dbPath = Path.Combine(srcDir, Path.GetFileName(created));
        File.Move(created, dbPath);

        // Nothing has been converted yet, so the session is offered.
        var before = TelemetryScanner.ScanDirectories(srcDir, dstDir);
        Assert.Single(before);
        Assert.Equal(ConversionStatus.Pending, before[0].Status);

        Assert.True(TelemetryConverter.ConvertSingleFile(dbPath, dstDir,
            new TelemetryConverter.ConversionOptions { ExtractCarSetup = false }));

        // What was written is not named after the DuckDB, which is the point of the
        // change and the reason the scan below is worth asserting.
        var converted = Directory.GetFiles(dstDir, "*.ld").Single();
        Assert.NotEqual("scan_session", Path.GetFileNameWithoutExtension(converted));

        var after = TelemetryScanner.ScanDirectories(srcDir, dstDir);
        Assert.Single(after);
        Assert.Equal(ConversionStatus.AlreadyConverted, after[0].Status);
        Assert.False(after[0].IsSelected);
    }

    [Fact]
    public void Test_Settings_Save_And_Load_At_The_Users_Location()
    {
        // Asserting the path is worth little on its own: the point of moving the file is
        // that it can be written where it now lives. This round-trips it, which also
        // proves the mode survives as a name rather than a bare number.
        //
        // Isolated temp path: the parallel suite must never touch the user's real
        // settings file (two round-trip tests racing on it was a real flake source).
        using var _ = TestSettings.UseTempPath();
        var path = AppSettings.GetSettingsFilePath();

        var written = new AppSettings
        {
            RateMode = RateMode.Reduced,
            ReducedDivisor = 3,
            RateOverrides = { ["Throttle Pos"] = 50 },
        };
        written.Save();

        Assert.True(File.Exists(path), $"Save must write to {path}");

        var loaded = AppSettings.Load();
        Assert.Equal(RateMode.Reduced, loaded.RateMode);
        Assert.Equal(3, loaded.ReducedDivisor);
        Assert.Equal(50, loaded.RateOverrides["Throttle Pos"]);

        // Readable to a person editing it by hand, which is the only UI there is.
        Assert.Contains("\"RateMode\": \"Reduced\"", File.ReadAllText(path));
    }

}
