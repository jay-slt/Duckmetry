using System;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Duckmetry.Core.Live;

/// <summary>
/// Reader for the game's official shared-memory block <c>LMU_Data</c> — the same
/// mechanism the auto mode uses to detect "back to the garage". Mechanism and
/// empirical findings are documented in <c>docs/AUTO-MODE.md</c>.
///
/// The scoring base is pinned empirically at <c>0x658</c> (the player-name anchor:
/// <c>mPlayerName</c> "Test Driver" was measured at raw offset <c>0x6D4</c> =
/// base + 124). The header-chain section sizes differ in this game build, so nothing
/// here is computed — it is measured. <c>mInRealtime</c> (REL 123) round-trips with
/// the session: False in the menu/garage, True when drivable, back to False at the
/// garage return.
///
/// Uses the managed <see cref="MemoryMappedFile.OpenExisting(string, MemoryMappedFileRights)"/>
/// wrapper rather than raw P/Invoke — it calls the same wide-char API correctly (the
/// CharSet.Ansi trap that stalled the probe does not exist here).
/// </summary>
public sealed class LiveReader : IDisposable
{
    // The scoring section base, pinned empirically (see docs/AUTO-MODE.md §3).
    internal const long ScoringBase = 0x658;

    // ScoringInfoV01 field offsets relative to the scoring base (header-declared,
    // pack-independent).
    private const int PlayerNameOffset = 124;   // mPlayerName[32]
    private const int InRealtimeOffset = 123;   // mInRealtime (byte, after mNumRedLights)

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private readonly long _length;
    private bool _disposed;

    private LiveReader(MemoryMappedFile file, MemoryMappedViewAccessor view, long length)
    {
        _file = file;
        _view = view;
        _length = length;
    }

    /// <summary>
    /// Tries to open <c>LMU_Data</c> read-only. Returns null when the game is not
    /// running or the block cannot be opened — the caller falls back to watching the
    /// DuckDB alone. Never throws.
    /// </summary>
    public static LiveReader? TryOpen()
    {
        foreach (string name in new[]
                 {
                     "LMU_Data",
                     @"Global\LMU_Data",
                     @"Local\LMU_Data",
                 })
        {
            try
            {
                MemoryMappedFile file = MemoryMappedFile.OpenExisting(
                    name, MemoryMappedFileRights.Read);
                MemoryMappedViewAccessor view = file.CreateViewAccessor(
                    0, 0, MemoryMappedFileAccess.Read);

                long length = view.Capacity;
                if (length < ScoringBase + PlayerNameOffset + 32)
                {
                    view.Dispose();
                    file.Dispose();
                    continue;
                }

                return new LiveReader(file, view, length);
            }
            catch (Exception)
            {
                // Not running / no permission / not Windows — try the next name.
            }
        }
        return null;
    }

    /// <summary>The game's mInRealtime flag (True while the car is drivable).</summary>
    public bool ReadInRealtime()
    {
        if (_view is null) return false;
        try
        {
            return _view.ReadByte(ScoringBase + InRealtimeOffset) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the player-name anchor is present at the pinned base — the runtime
    /// check that the block is decoded at the right place.
    /// </summary>
    public bool PlayerNameAnchorOk()
    {
        if (_view is null) return false;
        try
        {
            var bytes = new byte[32];
            _view.ReadArray(ScoringBase + PlayerNameOffset, bytes, 0, 32);
            int printable = 0;
            foreach (byte b in bytes)
            {
                if (b >= 0x20 && b < 0x7F) printable++;
                else if (b != 0) return false;
            }
            return printable >= 4;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The player's name as recorded in the block, or "(none)".</summary>
    public string PlayerName()
    {
        if (_view is null) return "(none)";
        try
        {
            var bytes = new byte[32];
            _view.ReadArray(ScoringBase + PlayerNameOffset, bytes, 0, 32);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            string s = Encoding.ASCII.GetString(bytes, 0, end).Trim();
            return s.Length == 0 ? "(none)" : s;
        }
        catch
        {
            return "(none)";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
    }
}