using System;
using System.IO;

namespace Duckmetry.Core.Live;

/// <summary>
/// Decides when a DuckDB has "settled" — the game writes it incrementally (roughly a
/// rebuild every 10 s) and only finalises it at the garage, so converting while it is
/// still being written yields a truncated log. A file is settled once it exists and its
/// size has been unchanged for a quiet window.
/// </summary>
public sealed class DuckDbSettleDetector
{
    private readonly TimeSpan _quietWindow;
    private long _lastSize = -1;
    private DateTime _lastChange = DateTime.MinValue;

    public DuckDbSettleDetector(TimeSpan? quietWindow = null)
    {
        _quietWindow = quietWindow ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Observes one file. Returns true when the file exists and has held a constant size
    /// for the quiet window. A file that disappears resets the state.
    /// </summary>
    public bool IsSettled(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                _lastSize = -1;
                return false;
            }

            long size = fi.Length;
            DateTime now = DateTime.UtcNow;
            if (size != _lastSize)
            {
                _lastSize = size;
                _lastChange = now;
                return false;
            }

            return now - _lastChange >= _quietWindow;
        }
        catch
        {
            _lastSize = -1;
            return false;
        }
    }

    /// <summary>Resets the observed state (e.g. for a new file).</summary>
    public void Reset()
    {
        _lastSize = -1;
        _lastChange = DateTime.MinValue;
    }
}