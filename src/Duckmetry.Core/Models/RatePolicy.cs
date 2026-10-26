using System;
using System.Collections.Generic;
using Duckmetry.Core.Ld;

namespace Duckmetry.Core.Models;

/// <summary>
/// How to choose each channel's output sample rate.
/// </summary>
/// <remarks>
/// i2's expected rate is a per-channel <em>choice</em>, not a limit: the rate is a
/// plain uint16 at header offset 0x16 and n_data a uint32 at 0x0C, so any rate the format
/// can express is valid, and i2's own logs already use nine rates from 1 to 100 Hz.
/// What must hold is that n_data matches the declared rate and the session span, or
/// i2 reads the channel at the wrong instants - which is indistinguishable, in the
/// lap table, from a channel carrying no lap information.
/// </remarks>
public enum RateMode
{
    /// <summary>
    /// Every sample the game recorded: min(source, <see cref="RatePolicy.MaxSourceHz"/>).
    /// </summary>
    /// <remarks>
    /// Highest fidelity and the largest files - around 24 MB for a 557 s session in a
    /// 153-channel log, against 4.5 MB for <see cref="Optimised"/>. Note this can be
    /// <em>slower</em> than i2's rate for a channel the game recorded slowly, e.g.
    /// Engine Oil Temp at 7 Hz, so this is not uniformly "more".
    /// </remarks>
    Source,

    /// <summary>
    /// i2's own per-channel rate, transcribed from genuine i2-written logs.
    /// The default: efficient, and what i2 expects.
    /// </summary>
    Optimised,

    /// <summary>
    /// Every channel reduced by a single factor of its own rate - half, a third,
    /// and so on.
    /// </summary>
    /// <remarks>
    /// A factor rather than a ceiling, because a ceiling does not reduce
    /// uniformly: capping at 5 Hz discards 95% of a 100 Hz channel and nothing at
    /// all from a 2 Hz one, so the fast channels become useless while the slow
    /// ones are untouched. Dividing each channel's own rate keeps every channel's
    /// resolution in the same proportion to what i2 would have used.
    /// </remarks>
    Reduced,

    /// <summary>
    /// An explicit rate per channel, falling back to i2's rate where unspecified.
    /// </summary>
    Pro,
}

/// <summary>Resolves the output sample rate for each channel.</summary>
public sealed class RatePolicy
{
    /// <summary>Highest rate the game can configure, and so the ceiling for any mode.</summary>
    public const int MaxSourceHz = 100;

    /// <summary>The resample grid. Everything is resampled onto this first, then decimated.</summary>
    public const int ResampleTargetHz = 100;

    public RateMode Mode { get; init; } = RateMode.Optimised;

    /// <summary>
    /// How much to divide each channel's rate by in <see cref="RateMode.Reduced"/>.
    /// 2 halves every channel; 1 or less disables the reduction.
    /// </summary>
    public int ReducedDivisor { get; init; } = 2;

    /// <summary>Explicit rates for <see cref="RateMode.Pro"/>, keyed by i2 channel name.</summary>
    public IReadOnlyDictionary<string, int> Overrides { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// i2's rate for a standard channel ID, or null when i2 has none for it.
    /// </summary>
    public static int? DefaultHz(ushort id) =>
        ChannelIds.RateById.TryGetValue(id, out int hz) ? hz : null;

    /// <summary>The output rate for one channel.</summary>
    public int Resolve(string channelName, ushort? id, int sourceHz) =>
        ResolveWithReason(channelName, id, sourceHz).Hz;

    /// <summary>As <see cref="Resolve"/>, but also reports why - so the GUI can explain it.</summary>
    public (int Hz, string Reason) ResolveWithReason(string channelName, ushort? id, int sourceHz)
    {
        int source = sourceHz > 0 ? sourceHz : MaxSourceHz;
        int ceiling = Math.Min(source, MaxSourceHz);

        switch (Mode)
        {
            case RateMode.Source:
                return (ceiling,
                    $"source: the game recorded this channel at {source} Hz");

            case RateMode.Reduced:
            {
                // Reduced from the channel's own i2 rate, so the reduction is
                // proportional. Never below 1 Hz: a channel at 1 Hz reduced by a
                // factor of 8 would otherwise round to zero, which is not a rate.
                int divisor = ReducedDivisor < 1 ? 1 : ReducedDivisor;
                int basis = ResolveDefault(channelName, id, ceiling);
                int reduced = Math.Max(1, (int)Math.Round(basis / (double)divisor));
                return (Math.Min(reduced, ceiling),
                    $"reduced: i2's {basis} Hz / {divisor} = {Math.Min(reduced, ceiling)} Hz");
            }

            case RateMode.Pro:
                if (Overrides.TryGetValue(channelName, out int custom) && custom > 0)
                    return (Math.Min(custom, ceiling),
                        $"pro: set to {custom} Hz" +
                        (custom > ceiling ? $", limited by source {source} Hz" : ""));
                return (ResolveDefault(channelName, id, ceiling), "pro: i2's rate (no override)");

            default:
                return (ResolveDefault(channelName, id, ceiling), "Optimised (default)");
        }
    }

    private int ResolveDefault(string channelName, ushort? id, int ceiling)
    {
        // A channel i2 has no standard ID for keeps the resample target, matching
        // what the reference does for channels outside its table.
        if (id is not ushort sid || !ChannelIds.RateById.TryGetValue(sid, out int hz))
            return ceiling;

        // Never exceed what the game recorded: upsampling invents detail that was
        // never captured, and would make the file larger without adding information.
        return Math.Min(hz, ceiling);
    }
}