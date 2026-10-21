using System;
using System.Collections.Generic;
using System.Linq;

namespace Duckmetry.Core.Models;

/// <summary>
/// Builds the per-channel rate plan for <see cref="RateMode.Pro"/> from the sessions the
/// user selected. Each session declares its own channels and rates (the game records
/// different channels at different rates, and two sessions may differ); a rate is only
/// valid for a session that contains the channel at least that fast, so the ceiling for
/// a channel shared by several files is the LOWEST of their rates — "for 2 files with
/// different rates for the same channel, the highest possible rate is the lowest of the
/// 2". Channels present in only one file keep that file's rate.
/// </summary>
public static class RatePlanner
{
    /// <summary>
    /// Merges one channel→rate map per selected file into the union of channels, each
    /// capped at the lowest rate among the files that contain it. Case-insensitive.
    /// </summary>
    public static Dictionary<string, int> MergeCeilings(
        IEnumerable<Dictionary<string, int>> perFileRates)
    {
        var ceilings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (perFileRates is null) return ceilings;

        foreach (var map in perFileRates)
        {
            if (map is null) continue;
            foreach (var (name, rate) in map)
            {
                if (!ceilings.TryGetValue(name, out int current) || rate < current)
                {
                    ceilings[name] = rate;
                }
            }
        }
        return ceilings;
    }
}