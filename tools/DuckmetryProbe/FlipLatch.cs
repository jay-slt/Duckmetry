// Whole-block persistent-flip detector.
//
// The official prefix fields (mOptionsLocation, events) and the community block's session
// flags did not move on the user's machine even with the car on track, so instead of
// transcribing deep telemetry offsets blind (a ~3,150-byte struct full of bitfields is
// exactly where transcription errors hide), this watches EVERY byte of LMU_Data and
// reports only the bytes that FLIP AND HOLD: leave the baseline for 5 straight seconds,
// or return to the baseline from a held state. Telemetry churns every tick (transient,
// never reported); a state flag flips at the garage transition and holds (reported). The
// reported offsets are then named from the header - header-independent discovery, no
// offset arithmetic to get wrong.

using System.Runtime.InteropServices;
using System.Diagnostics;

namespace DuckmetryProbe;

internal sealed class FlipLatch
{
    private readonly byte[] _base;     // baseline snapshot (first tick)
    private readonly byte[] _prev;     // last tick's bytes
    private readonly long[] _stableAt; // ms when _prev[i] first became equal to the latest value
    private readonly bool[] _leftBase; // has been observed holding a non-baseline value
    private readonly bool[] _reportedLeft;
    private readonly bool[] _reportedReturn;
    private readonly int _holdMs;

    /// <summary>Number of flip reports emitted so far.</summary>
    public int Flips { get; private set; }

    public FlipLatch(int size, int holdMs = 5000)
    {
        _base = new byte[size];
        _prev = new byte[size];
        _stableAt = new long[size];
        _leftBase = new bool[size];
        _reportedLeft = new bool[size];
        _reportedReturn = new bool[size];
        _holdMs = holdMs;
    }

    /// <summary>Take the t=0 snapshot: the baseline every flip is measured against.</summary>
    public void Seed(IntPtr view)
    {
        Marshal.Copy(view, _base, 0, _base.Length);
        _base.CopyTo(_prev, 0);
    }

    /// <summary>
    /// One watch tick. <c>onFlip(offset, from, to)</c> fires once per direction per byte:
    /// leaving the baseline, and (if it happens) returning to it.
    /// </summary>
    public void Tick(double tSeconds, IntPtr view, Action<int, byte, byte> onFlip, Action<int> onBusy)
    {
        long now = (long)(tSeconds * 1000.0);
        var buf = new byte[_base.Length];
        Marshal.Copy(view, buf, 0, buf.Length);

        int changing = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            byte b = buf[i];
            if (b == _prev[i])
            {
                if (_stableAt[i] == 0) continue;
                if (now - _stableAt[i] < _holdMs) continue;

                if (!_leftBase[i])
                {
                    if (b == _base[i]) continue;          // still baseline, just quiet
                    _leftBase[i] = true;
                    _reportedLeft[i] = true;
                    Flips++;
                    onFlip(i, _base[i], b);               // LEFT the baseline and held
                }
                else if (!_reportedReturn[i] && b == _base[i])
                {
                    _reportedReturn[i] = true;
                    Flips++;
                    onFlip(i, _prev[i], b);               // RETURNED to baseline and held
                }
            }
            else
            {
                changing++;
                _stableAt[i] = now;
                _prev[i] = b;
            }
        }
        if (changing > 0) onBusy(changing);
    }
}