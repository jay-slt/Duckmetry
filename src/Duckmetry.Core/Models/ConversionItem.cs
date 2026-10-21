using System;
using System.IO;

namespace Duckmetry.Core.Models;

public enum ConversionStatus
{
    Pending,
    AlreadyConverted,
    Converting,
    Success,
    Failed
}

/// <summary>
/// Represents a session file in the UI list to be converted.
/// </summary>
public class ConversionItem
{
    public string DuckDbFilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(DuckDbFilePath);
    public long FileSizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    
    public bool IsSelected { get; set; } = true;
    public ConversionStatus Status { get; set; } = ConversionStatus.Pending;
    public string StatusMessage { get; set; } = string.Empty;

    // Metadata summary for user preview
    public string Track { get; set; } = "-";
    public string Car { get; set; } = "-";
    public string Driver { get; set; } = "-";
    public int LapCount { get; set; }
    public string SessionDateFormatted => LastModified.ToString("yyyy-MM-dd HH:mm");
    public string FileSizeFormatted => FormatFileSize(FileSizeBytes);

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#} {sizes[order]}";
    }
}
