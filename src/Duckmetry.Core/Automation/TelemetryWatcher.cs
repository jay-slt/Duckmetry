using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Duckmetry.Core.Converter;
using Duckmetry.Core.Models;

namespace Duckmetry.Core.Automation;

/// <summary>
/// Converts telemetry sessions as they are finalised, without the user asking each time.
/// </summary>
/// <remarks>
/// the game flushes the DuckDB roughly every 10 seconds while telemetry is recording, and
/// stops when the session is finalised - by returning to the garage or by pressing the
/// stop-telemetry key. The moment the file stops growing IS the finalisation, but it can
/// only be observed after a quiet window longer than the flush interval; anything
/// shorter would convert a file that is still being written. So the quiet window is the
/// floor on how quickly the .ld can appear, and it defaults to comfortably above 10 s.
/// <para>
/// Two safeguards beyond the window. The conversion is written to a temporary name and
/// renamed into place only on success, so a failure never leaves a truncated .ld that a
/// later run would mistake for a converted session. And a session is skipped entirely if
/// an .ld of the same base name already exists, so re-runs and overlapping scans cannot
/// convert it twice.
/// </para>
/// </remarks>
public sealed class TelemetryWatcher : IDisposable
{
    /// <summary>How often the telemetry folder is inspected.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long the DuckDB must stop changing before it is treated as finalised.
    /// Must exceed the game's ~10 s flush interval or a session could be read mid-write.
    /// </summary>
    public TimeSpan QuietWindow { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Backoff between attempts, should a finalised file still be unreadable.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Where outputs are written before being moved into place.</summary>
    private const string StagingFolderName = ".duckmetry-staging";

    /// <summary>Give up on a session after this long without a successful conversion.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(2);

    public string SourceFolder { get; init; } = "";
    public string DestinationFolder { get; init; } = "";
    public TelemetryConverter.ConversionOptions Options { get; init; } = new();

    /// <summary>Raised with a human-readable line for each state change.</summary>
    public Action<string>? Log { get; init; }

    private readonly Dictionary<string, FileState> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class FileState
    {
        public long Length;
        public DateTime LastWrite;
        public DateTime FirstSeen;
        public DateTime UnchangedSince;
        public DateTime? NextAttempt;
        public int Attempts;
        public bool Converting;
    }

    /// <summary>One pass. Returns the sessions converted by this call.</summary>
    public IReadOnlyList<string> Scan()
    {
        var converted = new List<string>();
        if (string.IsNullOrWhiteSpace(SourceFolder) || !Directory.Exists(SourceFolder)) return converted;

        // Converting in place is the common case: the user watches the the game folder itself.
        string destination = string.IsNullOrWhiteSpace(DestinationFolder)
            ? SourceFolder : DestinationFolder;
        Directory.CreateDirectory(destination);

        var now = DateTime.UtcNow;
        foreach (var path in Directory.GetFiles(SourceFolder, "*.duckdb", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!_seen.TryGetValue(name, out var st))
            {
                st = new FileState { FirstSeen = now };
                _seen[name] = st;
                Log?.Invoke($"Found {name}, waiting for telemetry to finalise");
            }

            // Poll the file's metadata without holding it open.
            long length;
            DateTime write;
            try
            {
                var fi = new FileInfo(path);
                length = fi.Length;
                write = fi.LastWriteTimeUtc;
            }
            catch (IOException)
            {
                continue;   // being written or removed right now; try next pass
            }

            if (length != st.Length || write != st.LastWrite)
            {
                st.Length = length;
                st.LastWrite = write;
                st.UnchangedSince = now;
                st.NextAttempt = null;
                st.Attempts = 0;
                continue;   // still growing: telemetry is live
            }

            if (now - st.UnchangedSince < QuietWindow) continue;
            if (st.Converting) continue;
            if (st.NextAttempt is { } next && now < next) continue;
            if (now - st.FirstSeen > GiveUpAfter)
            {
                if (st.Attempts == 0)
                {
                    Log?.Invoke($"Giving up on {name} after {GiveUpAfter.TotalMinutes:F0} min");
                    st.NextAttempt = DateTime.MaxValue;
                }
                continue;
            }

            // An .ld of the same name means this session is already handled.
            string baseName = Path.GetFileNameWithoutExtension(path);
            if (Directory.GetFiles(destination, baseName + ".ld").Length > 0)
            {
                _seen.Remove(name);
                continue;
            }

            st.Converting = true;
            st.Attempts++;
            try
            {
                Convert(path, baseName, destination);
                _seen.Remove(name);
                converted.Add(baseName);
                Log?.Invoke($"Converted {baseName}.ld");
            }
            catch (Exception ex)
            {
                // Most often the game had not quite finished flushing. Back off and retry.
                st.Converting = false;
                st.NextAttempt = now + RetryDelay * st.Attempts;
                Log?.Invoke($"Could not convert {name} (attempt {st.Attempts}): {ex.Message}");
            }
        }

        return converted;
    }

    private void Convert(string duckDbPath, string baseName, string destination)
    {
        // Stage the outputs elsewhere and move them into place only on success, so a crash
        // or a retry never leaves a partial .ld that the "already converted?" check would
        // then mistake for a finished session.
        string staging = Path.Combine(destination, StagingFolderName);
        Directory.CreateDirectory(staging);
        foreach (var leftover in Directory.GetFiles(staging)) File.Delete(leftover);

        var opts = new TelemetryConverter.ConversionOptions
        {
            ExtractCarSetup = Options.ExtractCarSetup,
            ResampleFrequencyHz = Options.ResampleFrequencyHz,
            RateMode = Options.RateMode,
            ReducedDivisor = Options.ReducedDivisor,
            UseDuckDbChannelNames = Options.UseDuckDbChannelNames,
            OverwriteExisting = true,
        };
        foreach (var kv in Options.RateOverrides) opts.RateOverrides[kv.Key] = kv.Value;

        try
        {
            if (!TelemetryConverter.ConvertSingleFile(duckDbPath, staging, opts))
                throw new IOException("the converter reported failure");

            string produced = Directory.GetFiles(staging, "*.ld").SingleOrDefault()
                ?? throw new IOException("no .ld produced");
            File.Move(produced, Path.Combine(destination, baseName + ".ld"), overwrite: true);

            string? producedX = Directory.GetFiles(staging, "*.ldx").SingleOrDefault();
            if (producedX != null)
                File.Move(producedX, Path.Combine(destination, baseName + ".ldx"), overwrite: true);
        }
        finally
        {
            foreach (var leftover in Directory.GetFiles(staging)) File.Delete(leftover);
        }
    }

    /// <summary>
    /// Runs <see cref="Scan"/> on the poll interval until cancelled. For the watcher's
    /// own process; the GUI uses <see cref="Scan"/> directly on a timer.
    /// </summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        Log?.Invoke($"Watching {SourceFolder} every {PollInterval.TotalSeconds:F1} s, " +
                    $"converting after {QuietWindow.TotalSeconds:F0} s of quiet");
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Scan failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(PollInterval, cancel).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose() { }
}