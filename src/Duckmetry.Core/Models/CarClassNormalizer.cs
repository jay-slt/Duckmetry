using System;
using System.Text;

namespace Duckmetry.Core.Models;

/// <summary>
/// Turns the class a session records into the canonical labels used by the sorted output
/// (and the .ld banner). The game records its own vocabulary - a real session wrote
/// <c>CarClass = "Hyper"</c> for a Hypercar; the known set is hypercar / lmp2 / lmp3 /
/// lmgt3 / gte. New cars and new classes must work without a code change: the recorded
/// class is normalised by keyword when it matches known vocabulary, otherwise it is used
/// as-is; the car-name table is only a fallback for sessions that recorded no class at
/// all, and <c>Other</c> only for sessions with neither.
/// </summary>
public static class CarClassNormalizer
{
    public const string Hypercar = "Hypercar";
    public const string Lmp2 = "LMP2";
    public const string Lmp3 = "LMP3";
    public const string Lmgt3 = "LMGT3";
    public const string Gte = "GTE";
    public const string Other = "Other";

    /// <summary>
    /// The recorded vocabulary, most specific first (lmgt3 before gt3, lmp2/lmp3 before
    /// lmp... none collide). Keywords are matched by letter/digit token, so
    /// "Ford_Mustang_LMGT3 GT3 WEC2025" matches lmgt3 and "Hyper" matches hyper.
    /// </summary>
    private static readonly (string Keyword, string Canonical)[] KnownVocabulary =
    {
        ("hyper", Hypercar),
        ("hypercar", Hypercar),
        ("lmp2", Lmp2),
        ("lmp3", Lmp3),
        ("lmgt3", Lmgt3),
        ("gte", Gte),
        ("gt3", Lmgt3),   // the game's own fork records "GT3" for its LMGT3 cars
    };

    /// <summary>
    /// Fallback for sessions that recorded no class: known the game cars by name. This list is
    /// deliberately secondary - DLC and future cars are covered by the recorded class, and
    /// a car missing from this list only ever lands in <c>Other</c> when nothing was
    /// recorded either.
    /// </summary>
    private static readonly (string Car, string Canonical)[] KnownCars =
    {
        // Hypercars
        ("toyota gr010", Hypercar), ("toyota", Hypercar),
        ("ferrari 499p", Hypercar), ("ferrari 499", Hypercar),
        ("porsche 963", Hypercar),
        ("peugeot 9x8", Hypercar),
        ("bmw m hybrid v8", Hypercar), ("bmw m hybrid", Hypercar),
        ("cadillac v-series.r", Hypercar), ("cadillac", Hypercar),
        ("lamborghini sc63", Hypercar),
        ("alpine a424", Hypercar),
        ("acura arx-06", Hypercar),
        ("vanwall", Hypercar),
        ("isotta fraschini", Hypercar),
        ("aston martin valkyrie", Hypercar),
        // LMP2
        ("oreca 07", Lmp2), ("oreca", Lmp2),
        // LMP3
        ("ligier js p320", Lmp3), ("ligier", Lmp3),
        ("duqueine d-08", Lmp3), ("duqueine", Lmp3),
        ("giraudi", Lmp3),
        // LMGT3
        ("296 gt3", Lmgt3), ("ferrari 296", Lmgt3),
        ("911 gt3 r", Lmgt3), ("911 gt3", Lmgt3), ("porsche 911 gt3", Lmgt3),
        ("m4 gt3", Lmgt3), ("bmw m4", Lmgt3),
        ("huracan gt3", Lmgt3), ("huracan", Lmgt3),
        ("mercedes-amg gt3", Lmgt3), ("mercedes amg gt3", Lmgt3),
        ("vantage gt3", Lmgt3),
        ("720s gt3", Lmgt3), ("mclaren 720s", Lmgt3),
        ("corvette z06 gt3.r", Lmgt3), ("corvette z06", Lmgt3),
        ("mustang gt3", Lmgt3),
        ("lexus rc f gt3", Lmgt3),
        // GTE
        ("911 rsr", Gte), ("911 rsr-19", Gte),
        ("488 gte", Gte),
        ("vantage gte", Gte),
        ("corvette c8.r", Gte),
    };

    private static readonly char[] Forbidden =
        { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>
    /// The class label a session belongs to, as a destination-folder segment:
    /// recorded class normalised, else the car-name fallback, else <c>Other</c>.
    /// Never returns null.
    /// </summary>
    public static string FolderClass(string? recordedClass, string? carName) =>
        ResolveClass(recordedClass, carName) ?? Other;

    /// <summary>
    /// The class label shown in the .ld banner next to the driver's name: the same
    /// resolution as <see cref="FolderClass"/>, but null when nothing at all could be
    /// resolved, so a banner never reads "(Other)".
    /// </summary>
    public static string? BannerClass(string? recordedClass, string? carName) =>
        ResolveClass(recordedClass, carName);

    private static string? ResolveClass(string? recordedClass, string? carName)
    {
        if (!string.IsNullOrWhiteSpace(recordedClass))
        {
            string label = Normalize(recordedClass);
            if (label != Other) return label;
        }

        if (!string.IsNullOrWhiteSpace(carName))
        {
            string key = Tokenize(carName);
            foreach (var (car, canonical) in KnownCars)
            {
                // Both sides tokenised: the table keys keep their spaces for reading,
                // the comparison ignores them ("Ferrari 499P" matches "ferrari 499p").
                if (key.Contains(Tokenize(car), StringComparison.Ordinal))
                {
                    return canonical;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The canonical label for a recorded class. Known vocabulary is mapped to the five
    /// canonical labels; anything else (a class the devs add later) is kept as-is,
    /// cleaned of characters a folder name cannot hold. Only a blank input gives Other.
    /// </summary>
    public static string Normalize(string? recordedClass)
    {
        if (string.IsNullOrWhiteSpace(recordedClass)) return Other;
        string key = Tokenize(recordedClass);
        foreach (var (keyword, canonical) in KnownVocabulary)
        {
            if (key.Contains(keyword, StringComparison.Ordinal))
            {
                return canonical;
            }
        }
        return Clean(recordedClass);
    }

    /// <summary>Lower-cased letters and digits only - "Ford_Mustang_LMGT3 GT3" -> "fordmustanglmt3gt3".</summary>
    private static string Tokenize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string Clean(string s)
    {
        string t = s.Trim();
        foreach (char c in Forbidden)
        {
            t = t.Replace(c, '-');
        }
        return string.IsNullOrWhiteSpace(t) ? Other : t;
    }
}