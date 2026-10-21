using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Duckmetry.Core.DuckDb;
using Duckmetry.Core.Models;
using Duckmetry.Core.Ld;

namespace Duckmetry.Core.Converter;

public record ConversionProgress(
    int TotalFiles, 
    int CurrentFileIndex, 
    string CurrentFileName, 
    int FilePercent, 
    string Message
);

public class TelemetryConverter
{
    public class ConversionOptions
    {
        /// <summary>
        /// Write the car's setup as .svm. The JSON export is gone: it duplicated the same
        /// data in a format the game cannot load, and the .svm is what the game reads back.
        /// </summary>
        public bool ExtractCarSetup { get; set; } = true;

        /// <summary>The grid channels are interpolated onto before being decimated.</summary>
        public int ResampleFrequencyHz { get; set; } = RatePolicy.ResampleTargetHz;

        /// <summary>Which sample rate to write each channel at. Default: Optimised (default).</summary>
        public RateMode RateMode { get; set; } = RateMode.Optimised;

        /// <summary>How much to divide each channel's rate by in <see cref="RateMode.Reduced"/>.</summary>
        public int ReducedDivisor { get; set; } = 2;

        /// <summary>Per-channel rates for <see cref="RateMode.Pro"/>.</summary>
        public Dictionary<string, int> RateOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Write each channel's DISPLAY name as its DuckDB/the game spelling instead of i2's,
        /// keeping the Standard names ID, sample rate and quantisation behind it. i2 shows the
        /// file's name, so this makes i2's channel list read in the game's own vocabulary.
        /// The channels i2's built-in maths and lap/sector machinery resolve by name are left
        /// alone (<see cref="ChannelIds.KeepStandardNames"/>), or those would break.
        /// On by default; set false to emit i2's own spellings.
        /// </summary>
        public bool UseDuckDbChannelNames { get; set; } = true;

        /// <summary>
        /// Where converted sessions land: directly in the destination folder (Flat), or
        /// grouped <c>&lt;track&gt;/&lt;car class&gt;</c> under it (Sorted).
        /// </summary>
        public OutputLayout OutputLayout { get; set; } = OutputLayout.Flat;

        public bool OverwriteExisting { get; set; } = true;

        /// <summary>Builds the rate policy these options describe.</summary>
        public RatePolicy ToRatePolicy() => new()
        {
            Mode = RateMode,
            ReducedDivisor = ReducedDivisor,
            Overrides = RateOverrides,
        };
    }

    public static bool ConvertSingleFile(
        string duckDbFilePath, 
        string destinationFolder, 
        ConversionOptions options, 
        IProgress<int>? fileProgress = null,
        Action<string>? logger = null,
        CancellationToken cancel = default)
    {
        if (!File.Exists(duckDbFilePath))
        {
            logger?.Invoke($"[Error] File not found: {duckDbFilePath}");
            return false;
        }

        Directory.CreateDirectory(destinationFolder);
        var baseName = Path.GetFileNameWithoutExtension(duckDbFilePath);

        try
        {
            logger?.Invoke($"Opening DuckDB: {baseName}");
            using var reader = new DuckDbReader(duckDbFilePath);
            reader.Open();
            fileProgress?.Report(10);

            // 1. Read Metadata.
            //
            // This happens before the output paths are built, not after, because the
            // session is written under a name derived from its own metadata - date,
            // time, track and car - so the name is not known until here. The cost is
            // that the DuckDB has to be opened before an already-converted session can
            // be skipped; the scanner filters those out first, so in practice it is
            // only paid when a single file is converted on its own.
            var metadata = reader.ReadMetadata();
            fileProgress?.Report(20);
            cancel.ThrowIfCancellationRequested();

            var outputBase = OutputName.Build(metadata, baseName);
            var relativeBase = OutputPath.RelativeBaseFor(metadata, outputBase, options.OutputLayout);
            var targetLdPath = Path.Combine(destinationFolder, $"{relativeBase}.ld");
            var targetLdxPath = Path.Combine(destinationFolder, $"{relativeBase}.ldx");
            var targetSvmPath = Path.Combine(destinationFolder, $"{relativeBase}.svm");

            if (!options.OverwriteExisting && File.Exists(targetLdPath))
            {
                logger?.Invoke($"[Skip] Already exists: {targetLdPath}");
                return true;
            }

            // Sorted output lives in track/class subfolders the destination may not have yet.
            string? targetDir = Path.GetDirectoryName(targetLdPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 2. Export the car setup as .svm
            //
            // The JSON export is gone. It duplicated the same data in a format the game cannot
            // load, so it was only ever useful for inspecting what the converter read - and
            // it doubled the setup files in the output folder for no benefit. The .svm is
            // the format the game reads back, and it is what a setup should be saved as.
            if (options.ExtractCarSetup && !string.IsNullOrWhiteSpace(metadata.RawSetupJson))
            {
                try
                {
                    if (SvmWriter.TryWriteFile(targetSvmPath, metadata))
                    {
                        logger?.Invoke($"Saved car setup: {Path.GetFileName(targetSvmPath)}");
                    }
                    else
                    {
                        logger?.Invoke("[Warning] No usable setup data for .svm export.");
                    }
                }
                catch (Exception ex)
                {
                    // A setup that cannot be written must not fail the conversion - the .ld
                    // is the product, the setup is a bonus beside it.
                    logger?.Invoke($"[Warning] Failed saving car setup: {ex.Message}");
                }
            }
            fileProgress?.Report(30);

            // 3. Read Lap Beacons
            var lapBeacons = reader.ReadLapBeacons();
            fileProgress?.Report(40);

            // 4. Read & Resample Telemetry Channels
            logger?.Invoke($"Resampling channels at {options.ResampleFrequencyHz} Hz...");
            var channels = reader.ReadAndResampleChannels(
            options.ResampleFrequencyHz,
            fileProgress,
            options.ToRatePolicy(),
            cancel,
            options.UseDuckDbChannelNames);
            fileProgress?.Report(75);
            cancel.ThrowIfCancellationRequested();

            if (channels.Count == 0)
            {
                logger?.Invoke($"[Warning] No channel data found in {baseName}. Writing metadata-only i2 file.");
            }

            // 5. Write i2 .ld binary
            logger?.Invoke($"Writing i2 binary: {Path.GetFileName(targetLdPath)}");
            LdWriter.WriteFile(targetLdPath, metadata, channels);
            fileProgress?.Report(90);

            // 6. Write i2 .ldx XML
            logger?.Invoke($"Writing i2 markers: {Path.GetFileName(targetLdxPath)}");
            LdxWriter.WriteFile(targetLdxPath, metadata, lapBeacons);
            fileProgress?.Report(100);

            logger?.Invoke($"✓ Successfully converted {baseName}");
            return true;
        }
        catch (Exception ex)
        {
            // Include where it threw. The message alone ("Object reference not set to
            // an instance of an object") names no line, which makes a null deref
            // unactionable from a bug report - a user can only send the text.
            logger?.Invoke($"[Error] Conversion failed for {baseName}: {ex}");
            return false;
        }
    }

    public static void ConvertBatch(
        IReadOnlyList<ConversionItem> items, 
        string destinationFolder, 
        ConversionOptions options, 
        IProgress<ConversionProgress>? progress = null,
        Action<string>? logger = null,
        CancellationToken cancel = default)
    {
        var selectedItems = new List<ConversionItem>();
        foreach (var item in items)
        {
            if (item.IsSelected) selectedItems.Add(item);
        }

        int total = selectedItems.Count;
        for (int i = 0; i < total; i++)
        {
            var item = selectedItems[i];
            item.Status = ConversionStatus.Converting;

            progress?.Report(new ConversionProgress(
                total, 
                i + 1, 
                item.FileName, 
                0, 
                $"Converting ({i + 1}/{total}): {item.FileName}"
            ));

            var fileProgress = new Progress<int>(percent =>
            {
                progress?.Report(new ConversionProgress(
                    total, 
                    i + 1, 
                    item.FileName, 
                    percent, 
                    $"Converting ({i + 1}/{total}): {item.FileName} [{percent}%]"
                ));
            });

            if (cancel.IsCancellationRequested) break;

            bool success = ConvertSingleFile(item.DuckDbFilePath, destinationFolder,
                options, fileProgress, logger, cancel);

            if (success)
            {
                item.Status = ConversionStatus.Success;
                item.StatusMessage = "Converted";
            }
            else
            {
                item.Status = ConversionStatus.Failed;
                item.StatusMessage = "Failed";
            }
        }
    }
}
