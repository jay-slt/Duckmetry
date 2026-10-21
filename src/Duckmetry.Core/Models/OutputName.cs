using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Duckmetry.Core.Models;

/// <summary>
/// Builds the name a converted session is written under, and (with
/// <see cref="OutputPath"/>) where in the destination it goes.
/// </summary>
/// <remarks>
/// The output used to take the DuckDB's filename, which is the game's own and varies
/// between game versions - "WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z" in
/// one, "2026-10-01 - 15-22-51 - WeatherTech Raceway Laguna Seca - P1" in another. That
/// made a folder of conversions hard to scan and impossible to sort usefully.
///
/// Naming from the session's own metadata gives the same shape every time, and puts the
/// timestamp first so the folder sorts chronologically.
/// </remarks>
public static class OutputName
{
    /// <summary>
    /// Placeholders <see cref="SessionMetadata"/> carries before anything has been read.
    /// They must not end up in a filename, so they are treated as absent.
    /// </summary>
    private static readonly string[] Placeholders =
        { "Telemetry Car", "Telemetry Circuit", "Telemetry Driver", "Telemetry Session" };

    /// <summary>Characters Windows will not accept in a filename.</summary>
    private static readonly char[] Forbidden =
        { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>
    /// Builds the base name, without an extension:
    /// "2026-10-01 15-22-50 - WeatherTech Raceway Laguna Seca - Proton Competition 2025 #88-LM"
    /// </summary>
    /// <param name="meta">What the session says about itself.</param>
    /// <param name="fallback">Used when the metadata carries nothing usable.</param>
    /// <summary>
    /// A destination-folder segment safe on Windows: same cleaning as a filename part.
    /// </summary>
    internal static string CleanFolderPart(string name) => Clean(name);

    public static string Build(SessionMetadata meta, string fallback)
    {
        var parts = new List<string>();

        if (meta.SessionDateTime != default)
        {
            // Local time, so it agrees with the date and time the .ld banner shows for
            // the same session. The offset used is this machine's current one, which is
            // an approximation for a session logged elsewhere in the year.
            parts.Add(meta.SessionDateTime.ToLocalTime()
                .ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture));
        }

        AddIfMeaningful(parts, meta.Venue);
        AddIfMeaningful(parts, meta.BareCarName);

        return parts.Count == 0 ? Clean(fallback) : Clean(string.Join(" - ", parts));
    }

    private static void AddIfMeaningful(List<string> parts, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        foreach (var placeholder in Placeholders)
        {
            if (trimmed.Equals(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        parts.Add(trimmed);
    }

    /// <summary>
    /// Replaces the characters a filename cannot hold, and tidies the result:
    /// "Proton Competition 2025 #88:LM" becomes "Proton Competition 2025 #88-LM".
    /// </summary>
    internal static string Clean(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(Forbidden, c) >= 0 || char.IsControl(c) ? '-' : c);
        }

        // Collapse runs of whitespace only: a hyphen and a space are the separators
        // between the parts, and collapsing those would join the parts together.
        var cleaned = Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim().Trim('.');
        return cleaned.Length == 0 ? "session" : cleaned;
    }
}
