using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Duckmetry.Core.DuckDb;
using Duckmetry.Core.Models;

namespace Duckmetry.Core.Scanner;

/// <summary>
/// Scans source and destination directories to determine converted vs unconverted telemetry sessions.
/// </summary>
public static class TelemetryScanner
{
    public static List<ConversionItem> ScanDirectories(
        string sourceFolder, string destinationFolder, OutputLayout layout = OutputLayout.Flat,
        bool readMetadataPreview = true)
    {
        var items = new List<ConversionItem>();

        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            return items;
        }

        // Get all duckdb files
        var duckDbFiles = Directory.GetFiles(sourceFolder, "*.duckdb", SearchOption.TopDirectoryOnly)
                                   .OrderByDescending(File.GetLastWriteTime)
                                   .ToList();

        // Get existing .ld files in destination (if destination exists), RECURSIVELY -
        // sorted output lives in track/class subfolders. What is remembered is the
        // destination-RELATIVE path (no extension), which is exactly how the converter
        // computes where it writes; plus the base names for the legacy DuckDB-named
        // output from earlier versions.
        var existingLdRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingLdBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(destinationFolder) && Directory.Exists(destinationFolder))
        {
            var ldFiles = Directory.GetFiles(destinationFolder, "*.ld", SearchOption.AllDirectories);
            foreach (var f in ldFiles)
            {
                existingLdRelative.Add(RelativeWithoutExtension(destinationFolder, f));
                existingLdBaseNames.Add(Path.GetFileNameWithoutExtension(f));
            }
        }

        foreach (var filePath in duckDbFiles)
        {
            var fi = new FileInfo(filePath);
            var baseName = Path.GetFileNameWithoutExtension(filePath);

            // The path the converter will write, built the same way the converter builds
            // it (OutputName + OutputPath), so "already converted" cannot disagree.
            var expectedRelative = baseName;

            var item = new ConversionItem
            {
                DuckDbFilePath = filePath,
                FileSizeBytes = fi.Length,
                LastModified = fi.LastWriteTime,
            };

            // Attempt quick metadata preview
            if (readMetadataPreview)
            {
                try
                {
                    using var reader = new DuckDbReader(filePath);
                    reader.Open();
                    var meta = reader.ReadMetadata();
                    item.Track = string.IsNullOrWhiteSpace(meta.Venue) ? "-" : meta.Venue;
                    item.Car = string.IsNullOrWhiteSpace(meta.Vehicle) ? "-" : meta.Vehicle;
                    item.Driver = string.IsNullOrWhiteSpace(meta.Driver) ? "-" : meta.Driver;
                    var outputBase = OutputName.Build(meta, baseName);
                    expectedRelative = OutputPath.RelativeBaseFor(meta, outputBase, layout);
                }
                catch
                {
                    // If file is locked (e.g. game currently writing), parse name from filename
                    ParseFromFileName(baseName, item);
                }
            }
            else
            {
                ParseFromFileName(baseName, item);
            }

            // A session is done once an .ld exists at the path the converter would write
            // (flat: the base name; sorted: track/class/base). The DuckDB's own filename
            // is checked as well, so output from earlier versions - which was named after
            // the DuckDB - still counts.
            bool alreadyConverted = existingLdRelative.Contains(expectedRelative)
                                    || existingLdBaseNames.Contains(baseName);

            item.Status = alreadyConverted
                ? ConversionStatus.AlreadyConverted
                : ConversionStatus.Pending;
            item.IsSelected = !alreadyConverted;

            items.Add(item);
        }

        return items;
    }

    /// <summary>The destination-relative path of an .ld file, without its extension.</summary>
    private static string RelativeWithoutExtension(string destinationFolder, string ldPath)
    {
        // ChangeExtension keeps the relative directories; GetFileNameWithoutExtension
        // would drop them, breaking the sorted-layout match.
        string relative = Path.GetRelativePath(destinationFolder, ldPath);
        return Path.ChangeExtension(relative, null) ?? relative;
    }

    private static void ParseFromFileName(string baseName, ConversionItem item)
    {
        var parts = baseName.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) item.Track = parts[1];
        if (parts.Length >= 3) item.Car = parts[2];
    }
}
