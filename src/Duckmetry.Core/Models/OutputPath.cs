using System.IO;

namespace Duckmetry.Core.Models;

/// <summary>
/// Where a converted session lands inside the destination folder. The one place that
/// decides the relative path, so the converter (which writes) and the scanner (which
/// decides "already converted") cannot disagree about it.
/// </summary>
public static class OutputPath
{
    /// <summary>
    /// The destination-relative base path (no extension) for a session:
    /// flat = just the built name; sorted = <c>&lt;track&gt;/&lt;class&gt;/&lt;name&gt;</c>.
    /// </summary>
    public static string RelativeBaseFor(SessionMetadata meta, string baseName, OutputLayout layout)
    {
        if (layout != OutputLayout.Sorted)
        {
            return baseName;
        }

        string venue = OutputName.CleanFolderPart(meta.Venue);
        string carClass = CarClassNormalizer.FolderClass(meta.RecordedClass, meta.Vehicle);
        return Path.Combine(venue, carClass, baseName);
    }

    /// <summary>
    /// The class label for this session (folder semantics - Other when unresolvable),
    /// exposed for the settings preview.
    /// </summary>
    public static string ClassFor(SessionMetadata meta) =>
        CarClassNormalizer.FolderClass(meta.RecordedClass, meta.Vehicle);
}