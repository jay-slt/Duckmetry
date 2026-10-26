using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Converter;
using Duckmetry.Core.Models;
using Duckmetry.Core.Scanner;

namespace Duckmetry.Core.Live;

/// <summary>
/// The auto-mode engine (docs/TRAY-MODE-PLAN.md, docs/AUTO-MODE.md): watches for a
/// session being finalised and converts it with the current settings, one at a time,
/// never on the game's ~10 s DuckDB rebuild tick.
///
/// Triggers, in order of preference:
/// 1. the falling edge of <c>mInRealtime</c> (the measured "back to the garage" signal);
/// 2. the game process/block disappearing while a session was live;
/// 3. a DuckDB appearing and settling, when the block is unreachable (or always, as the
///    safety net) — the game can be closed rather than driven back.
///
/// The shared-memory block is optional: with the game not running the watcher still
/// converts settled files, which is also how the engine is exercised by the test suite.
/// </summary>
public sealed class AutoWatcher : IDisposable
{
    private readonly AppSettings _settings;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _settleWindow;
    private readonly TimeSpan _postSignalDelay;

    private readonly SessionLatch _latch = new();
    private readonly DuckDbSettleDetector _settle = new();
    private readonly HashSet<string> _convertedThisRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The recordings present when auto mode started. Only files that appear AFTER this
    /// moment are candidates (the user's rule: pressing auto mode means "from now on",
    /// not "convert everything lying around").
    /// </summary>
    private readonly HashSet<string> _baseline = new(StringComparer.OrdinalIgnoreCase);

    private LiveReader? _live;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _started;
    private volatile bool _converting;

    public AutoWatcher(AppSettings settings, TimeSpan? tick = null, TimeSpan? settleWindow = null,
        TimeSpan? postSignalDelay = null)
    {
        _settings = settings;
        _tick = tick ?? TimeSpan.FromMilliseconds(500);
        _settleWindow = settleWindow ?? TimeSpan.FromSeconds(5);

        // Once the garage signal arrives, the game still needs a moment to finalise the
        // DuckDB (flush the WAL, close the file). The settle check below is the real
        // safety net; this pause is the user-requested belt: never start reading the
        // file before the game has had time to finish writing it.
        _postSignalDelay = postSignalDelay ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Human-readable progress/status, fired on the watcher's background thread.</summary>
    public event Action<string>? Log;

    /// <summary>Fired once per converted file, with its path, on the background thread.</summary>
    public event Action<string>? Converted;

    public bool Started => _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        SnapshotBaseline();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>
    /// Records which recordings exist right now; they are the ones already handled and
    /// must not be picked up. A fresh start (re-entering auto mode) takes a fresh
    /// snapshot, so files converted in an earlier session are not offered again.
    /// </summary>
    private void SnapshotBaseline()
    {
        _baseline.Clear();
        if (string.IsNullOrWhiteSpace(_settings.SourceFolder) || !Directory.Exists(_settings.SourceFolder))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(_settings.SourceFolder, "*.duckdb", SearchOption.TopDirectoryOnly))
        {
            _baseline.Add(f);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _live?.Dispose();
        _live = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        _live = LiveReader.TryOpen();
        if (_live is not null)
        {
            Log?.Invoke(
                $"Connected to LMU_Data (player \"{_live.PlayerName()}\", " +
                $"anchor {( _live.PlayerNameAnchorOk() ? "OK" : "MISMATCH — layout drifted?")}).");
        }
        else
        {
            Log?.Invoke("LMU_Data not available — watching the telemetry folder for finished sessions.");
        }

        if (_baseline.Count > 0)
        {
            Log?.Invoke($"{_baseline.Count} existing recording(s) found — only new files will be converted from now on.");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Tick(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[Error] {ex.Message}");
            }
            await Task.Delay(_tick, ct).ConfigureAwait(false);
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        if (_live is not null)
        {
            bool inRealtime;
            try
            {
                inRealtime = _live.ReadInRealtime();
            }
            catch
            {
                inRealtime = false;
            }

            if (_latch.Feed(inRealtime))
            {
                Log?.Invoke("Session ended (back to the garage) — waiting for the game to finalise the file.");
                await Task.Delay(_postSignalDelay, ct).ConfigureAwait(false);
                TriggerConversion(ct);
                return;
            }

            if (!StillThere())
            {
                if (_latch.GameGone())
                {
                    Log?.Invoke("Game exited while a session was live — waiting for the file to finalise.");
                    await Task.Delay(_postSignalDelay, ct).ConfigureAwait(false);
                    TriggerConversion(ct);
                }
                _live.Dispose();
                _live = null;
            }
            return;
        }

        // Fallback / safety net: re-open the block if the game has started, and watch
        // for a finished DuckDB.
        _live = LiveReader.TryOpen();
        if (_live is not null) return;   // next tick will run the live path

        var candidate = NewestUnconverted();
        if (candidate is not null && _settle.IsSettled(candidate))
        {
            Log?.Invoke($"Telemetry file settled: {Path.GetFileName(candidate)}");
            TriggerConversion(ct);
        }
    }

    private bool StillThere() => LiveReader.TryOpen() is not null;

    private string? NewestUnconverted()
    {
        if (string.IsNullOrWhiteSpace(_settings.SourceFolder) || !Directory.Exists(_settings.SourceFolder))
        {
            return null;
        }

        var items = TelemetryScanner.ScanDirectories(
            _settings.SourceFolder, _settings.DestinationFolder, _settings.OutputLayout);
        return items.FirstOrDefault(i =>
                   i.Status == ConversionStatus.Pending &&
                   !_baseline.Contains(i.DuckDbFilePath) &&
                   !_convertedThisRun.Contains(Path.GetFileNameWithoutExtension(i.DuckDbFilePath)))
                   ?.DuckDbFilePath;
    }

    private void TriggerConversion(CancellationToken ct)
    {
        if (_converting) return;
        _converting = true;
        try
        {
            string? db = NewestUnconverted();
            if (db is null)
            {
                Log?.Invoke("Triggered, but no unconverted DuckDB is waiting.");
                return;
            }

            // Belt and braces: the garage finalises the file, but never convert a file
            // that is still being written. Bounded wait; the file should already be
            // stable at the garage.
            var settle = new DuckDbSettleDetector(_settleWindow);
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!settle.IsSettled(db) && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                Thread.Sleep(_tick);
            }

            if (ct.IsCancellationRequested) return;
            if (!settle.IsSettled(db))
            {
                Log?.Invoke($"[Warning] {Path.GetFileName(db)} is still being written — not converting.");
                return;
            }

            var options = BuildOptions();
            Log?.Invoke($"Converting {Path.GetFileName(db)}...");
            bool ok = TelemetryConverter.ConvertSingleFile(
                db, _settings.DestinationFolder, options,
                logger: m => Log?.Invoke(m),
                cancel: ct);

            if (ok)
            {
                _convertedThisRun.Add(Path.GetFileNameWithoutExtension(db));
                Converted?.Invoke(db);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Error] {ex.Message}");
        }
        finally
        {
            _converting = false;
        }
    }

    private TelemetryConverter.ConversionOptions BuildOptions() => new()
    {
        ExtractCarSetup = _settings.ExportCarSetup,
        RateMode = _settings.RateMode,
        ReducedDivisor = _settings.ReducedDivisor,
        RateOverrides = _settings.RateOverrides,
        UseDuckDbChannelNames = _settings.UseDuckDbChannelNames,
        OutputLayout = _settings.OutputLayout,
        OverwriteExisting = _settings.OverwriteExisting,
    };

    public void Dispose()
    {
        Stop();
    }
}