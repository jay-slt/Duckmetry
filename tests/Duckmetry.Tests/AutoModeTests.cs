using System;
using System.IO;
using System.Threading;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.DuckDb;
using Duckmetry.Core.Live;
using Duckmetry.Core.Models;
using Xunit;

namespace Duckmetry.Tests;

/// <summary>
/// The auto-mode engine: the latch state machine, the settle detector, and the watcher
/// end-to-end through the DuckDB-settle path (the shared-memory block cannot be opened
/// on Linux, so the settle fallback is exactly the path that is exercised).
/// </summary>
public class AutoModeTests
{
    // ---------------------------------------------------------------- SessionLatch

    [Fact]
    public void Latch_Never_Fires_When_No_Session()
    {
        var latch = new SessionLatch();
        for (int i = 0; i < 10; i++)
        {
            Assert.False(latch.Feed(false));
        }
        Assert.False(latch.InSession);
    }

    [Fact]
    public void Latch_Fires_Once_On_The_Falling_Edge()
    {
        var latch = new SessionLatch();
        Assert.False(latch.Feed(true));    // driving
        Assert.True(latch.InSession);
        Assert.False(latch.Feed(true));    // still driving
        Assert.True(latch.Feed(false));    // back to the garage -> fire
        Assert.False(latch.Feed(false));   // already reset
    }

    [Fact]
    public void Latch_Fires_Once_Per_Session()
    {
        var latch = new SessionLatch();
        Assert.False(latch.Feed(true));
        Assert.True(latch.Feed(false));    // session 1

        Assert.False(latch.Feed(true));    // session 2
        Assert.True(latch.Feed(false));
    }

    [Fact]
    public void Latch_GameGone_Fires_Only_When_In_Session()
    {
        var latch = new SessionLatch();
        Assert.False(latch.GameGone());    // nothing latched
        Assert.False(latch.Feed(true));
        Assert.True(latch.GameGone());     // game quit mid-session
        Assert.False(latch.GameGone());    // already reset
    }

    // ---------------------------------------------------------------- DuckDbSettleDetector

    [Fact]
    public void Settle_Requires_A_Quiet_Window_And_Stable_Size()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lmt_settle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "session.duckdb");
            File.WriteAllBytes(path, new byte[1024]);

            var det = new DuckDbSettleDetector(TimeSpan.FromMilliseconds(150));
            Assert.False(det.IsSettled(path));     // first observation records the size

            // A size change resets the quiet window.
            File.WriteAllBytes(path, new byte[2048]);
            Thread.Sleep(50);
            Assert.False(det.IsSettled(path));

            Thread.Sleep(250);
            Assert.True(det.IsSettled(path));      // stable long enough

            Assert.False(det.IsSettled(Path.Combine(dir, "missing.duckdb")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- AutoWatcher (settle path)

    [Fact]
    public void Watcher_Converts_A_Settled_DuckDb_Once()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"lmt_auto_{Guid.NewGuid():N}");
        try
        {
            var src = Path.Combine(testDir, "Src");
            var dst = Path.Combine(testDir, "Dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);

            var settings = new AppSettings
            {
                SourceFolder = src,
                DestinationFolder = dst,
                ExportCarSetup = false,
                OutputLayout = OutputLayout.Flat,
            };

            var converted = new ManualResetEventSlim(false);
            var watcher = new AutoWatcher(settings,
                tick: TimeSpan.FromMilliseconds(50),
                settleWindow: TimeSpan.FromMilliseconds(150));
            watcher.Converted += _ => converted.Set();
            watcher.Start();

            try
            {
                // The recording appears only AFTER auto mode starts — that is the
                // realistic flow (auto mode on, THEN the session ends and the file lands).
                CreateSampleDuckDbFile(Path.Combine(src, "auto_session.duckdb"));

                Assert.True(converted.Wait(TimeSpan.FromSeconds(20)),
                    "auto mode should convert the settled DuckDB");
                var lds = Directory.GetFiles(dst, "*.ld", SearchOption.AllDirectories);
                Assert.Single(lds);
            }
            finally
            {
                watcher.Dispose();
            }

            // Let it keep ticking for a moment: it must not convert a second time.
            Thread.Sleep(500);
            Assert.Single(Directory.GetFiles(dst, "*.ld", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public void Watcher_Ignores_Preexisting_Files_But_Converts_New_Ones()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"lmt_auto_baseline_{Guid.NewGuid():N}");
        try
        {
            var src = Path.Combine(testDir, "Src");
            var dst = Path.Combine(testDir, "Dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);

            // A recording already sitting in the folder when auto mode starts.
            CreateSampleDuckDbFile(Path.Combine(src, "old.duckdb"));

            var settings = new AppSettings
            {
                SourceFolder = src,
                DestinationFolder = dst,
                ExportCarSetup = false,
                OutputLayout = OutputLayout.Flat,
            };

            var converted = new ManualResetEventSlim(false);
            var watcher = new AutoWatcher(settings,
                tick: TimeSpan.FromMilliseconds(50),
                settleWindow: TimeSpan.FromMilliseconds(150));
            watcher.Converted += _ => converted.Set();
            watcher.Start();

            try
            {
                // The pre-existing file must NOT be converted.
                Thread.Sleep(800);
                Assert.Empty(Directory.GetFiles(dst, "*.ld", SearchOption.AllDirectories));

                // A file that appears after auto mode started must be converted.
                CreateSampleDuckDbFile(Path.Combine(src, "new.duckdb"));
                Assert.True(converted.Wait(TimeSpan.FromSeconds(20)),
                    "a recording that appears after auto mode starts should be converted");
                var lds = Directory.GetFiles(dst, "*.ld", SearchOption.AllDirectories);
                Assert.Single(lds);
            }
            finally
            {
                watcher.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
        }
    }

    // ---------------------------------------------------------------- rate planning

    [Fact]
    public void RatePlanner_Takes_The_Lowest_Rate_For_Shared_Channels()
    {
        var fileA = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ground Speed"] = 50,
            ["Engine RPM"] = 100,
        };
        var fileB = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ground Speed"] = 20,
            ["Throttle Pos"] = 50,
        };

        var ceilings = RatePlanner.MergeCeilings(new[] { fileA, fileB });

        // Shared channels are capped at the lowest of the two; exclusive ones keep theirs.
        Assert.Equal(20, ceilings["Ground Speed"]);
        Assert.Equal(100, ceilings["Engine RPM"]);
        Assert.Equal(50, ceilings["Throttle Pos"]);
        Assert.Equal(3, ceilings.Count);
    }

    [Fact]
    public void Reader_Reads_Channel_Frequencies_From_A_Real_Shape_Db()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lmt_freqs_{Guid.NewGuid():N}.duckdb");
        CreateSampleDuckDbFile(dbPath);
        try
        {
            using var reader = new DuckDbReader(dbPath);
            var frequencies = reader.ReadChannelFrequencies();
            Assert.Equal(50, frequencies["Ground Speed"]);
            Assert.Equal(50, frequencies["Engine RPM"]);
            Assert.Equal(4, frequencies.Count);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>A minimal DuckDB with the shape the game writes, enough for a conversion.</summary>
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