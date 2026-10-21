namespace Duckmetry.Core.Live;

/// <summary>
/// The auto-mode state machine: fires exactly once when a session ends. It watches
/// the game's <c>mInRealtime</c> flag — True while the car is drivable, False in the
/// menu/garage — and reports the **falling edge** (True → False) after a session was
/// seen live, which is the measured "back to the garage" signal (docs/AUTO-MODE.md).
/// A second "game gone" input handles the game exiting instead of being driven back.
/// </summary>
public sealed class SessionLatch
{
    private bool _inSession;

    /// <summary>
    /// Feeds one sample of the flag. Returns true exactly once per session: when the
    /// flag falls True→False while a session was latched, or on the first
    /// <see cref="GameGone"/> after a session was latched. Resets internally.
    /// </summary>
    public bool Feed(bool inRealtime)
    {
        if (inRealtime)
        {
            _inSession = true;
            return false;
        }

        if (_inSession)
        {
            // Falling edge — the session ended (back to the garage).
            _inSession = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The game process/block went away while a session was latched — treat it as the
    /// session ending (the driver quit rather than returning to the garage).
    /// </summary>
    public bool GameGone()
    {
        if (_inSession)
        {
            _inSession = false;
            return true;
        }
        return false;
    }

    /// <summary>True while a session is latched (a car was seen drivable).</summary>
    public bool InSession => _inSession;
}