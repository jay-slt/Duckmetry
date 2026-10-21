namespace Duckmetry.Core.Models;

/// <summary>How converted sessions are laid out in the destination folder.</summary>
public enum OutputLayout
{
    /// <summary>
    /// Every session lands directly in the destination folder, named from its own
    /// metadata ("2026-10-08 19-20-42 - Autodromo Enzo e Dino Ferrari - Toyota Racing 2026 #7:LM").
    /// </summary>
    Flat,

    /// <summary>
    /// Sessions are grouped under the destination folder:
    /// <c>&lt;destination&gt;/&lt;track&gt;/&lt;car class&gt;/&lt;name&gt;</c> -
    /// one folder per circuit, one per class (hypercar, lmp2, lmp3, lmgt3, gte).
    /// </summary>
    Sorted,
}