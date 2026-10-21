using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Converter;
using Duckmetry.Core.Models;
using Duckmetry.Core.Scanner;

namespace Duckmetry.App;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Non-interactive mode: if args are provided, use them directly
        if (args.Length >= 2)
        {
            string sourceFolder = args[0];
            string destFolder = args[1];

            if (!Directory.Exists(sourceFolder))
            {
                Console.Error.WriteLine($"Source folder not found: {sourceFolder}");
                Environment.Exit(1);
            }

            Directory.CreateDirectory(destFolder);

            // Honour the saved settings here as well as in the menu: a user who has
            // chosen a sampling mode should get it whether they run the interactive
            // menu or the command line.
            var fileSettings = AppSettings.Load();

            var opts = new TelemetryConverter.ConversionOptions
            {
                ExtractCarSetup = false,
                RateMode = fileSettings.RateMode,
                ReducedDivisor = fileSettings.ReducedDivisor,
                RateOverrides = fileSettings.RateOverrides,
                UseDuckDbChannelNames = fileSettings.UseDuckDbChannelNames,
                OutputLayout = fileSettings.OutputLayout,
                OverwriteExisting = true
            };

            var items = TelemetryScanner.ScanDirectories(sourceFolder, destFolder, fileSettings.OutputLayout);

            if (items.Count == 0)
            {
                Console.Error.WriteLine("No DuckDB files found in source folder.");
                Environment.Exit(1);
            }

            Console.WriteLine($"Found {items.Count} session(s). Converting all...");
            foreach (var item in items) item.IsSelected = true;

            var progress = new Progress<ConversionProgress>(p =>
            {
                Console.Write($"\r--> [{p.CurrentFileIndex}/{p.TotalFiles}] {p.Message}".PadRight(79));
            });

            TelemetryConverter.ConvertBatch(items, destFolder, opts, progress,
                log => Console.WriteLine($"\n    {log}"));

            Console.WriteLine("\n\n[OK] Conversion completed.");
            Environment.Exit(0);
        }

        PrintHeader();

        // 1. Load persisted settings
        var settings = AppSettings.Load();

        // 2. Resolve Source Folder
        if (string.IsNullOrWhiteSpace(settings.SourceFolder) || !Directory.Exists(settings.SourceFolder))
        {
            Console.WriteLine("📁 [Step 1/2] Select the game Telemetry Source Folder:");
            Console.WriteLine("   (e.g., C:\\Program Files (x86)\\Steam\\steamapps\\common\\Le Mans Ultimate\\UserData\\Telemetry)");
            Console.Write("Enter Path: ");
            var input = Console.ReadLine()?.Trim().Trim('"', '\'');
            while (string.IsNullOrWhiteSpace(input) || !Directory.Exists(input))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Directory does not exist. Please enter a valid path:");
                Console.ResetColor();
                Console.Write("Enter Path: ");
                input = Console.ReadLine()?.Trim().Trim('"', '\'');
            }
            settings.SourceFolder = input;
            settings.Save();
        }

        // 3. Resolve Destination Folder
        if (string.IsNullOrWhiteSpace(settings.DestinationFolder) || !Directory.Exists(settings.DestinationFolder))
        {
            Console.WriteLine("\n💾 [Step 2/2] Select i2 Logged Data Destination Folder:");
            Console.WriteLine("   (e.g., C:\\Users\\<User>\\Documents\\i2\\Logged Data)");
            Console.Write("Enter Path: ");
            var input = Console.ReadLine()?.Trim().Trim('"', '\'');
            while (string.IsNullOrWhiteSpace(input))
            {
                Console.Write("Enter Path: ");
                input = Console.ReadLine()?.Trim().Trim('"', '\'');
            }

            if (!Directory.Exists(input))
            {
                try
                {
                    Directory.CreateDirectory(input);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating directory: {ex.Message}");
                }
            }
            settings.DestinationFolder = input;
            settings.Save();
        }

        // Main interactive loop
        bool running = true;
        while (running)
        {
            Console.Clear();
            PrintHeader();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[Source]      : {settings.SourceFolder}");
            Console.WriteLine($"[Destination] : {settings.DestinationFolder}");
            Console.WriteLine($"[Export Setup]: {(settings.ExportCarSetup ? "YES (.svm)" : "NO")}");
            Console.WriteLine($"[Sample Rate] : {settings.RateMode}");
            Console.WriteLine($"[Chan Names]  : {(settings.UseDuckDbChannelNames ? "DuckDB spelling" : "Standard names")}");
            Console.WriteLine($"[Layout]      : {LayoutSummary(settings)}");
            Console.ResetColor();
            Console.WriteLine(new string('-', 80));

            Console.WriteLine("Scanning directories...\n");
            var items = TelemetryScanner.ScanDirectories(
                settings.SourceFolder, settings.DestinationFolder, settings.OutputLayout);

            if (items.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No *.duckdb files found in the source directory.");
                Console.ResetColor();
            }
            else
            {
                PrintItemsTable(items);
            }

            Console.WriteLine(new string('-', 80));
            Console.WriteLine("[G] Go / Convert Selected Files");
            Console.WriteLine("[A] Toggle Select All / Unselect All");
            Console.WriteLine("[T] Toggle Car Setup Export (Currently: " + (settings.ExportCarSetup ? "ON" : "OFF") + ")");
            Console.WriteLine("[N] Toggle Channel Names (Currently: " + (settings.UseDuckDbChannelNames ? "DuckDB spelling" : "Standard names") + ")");
            Console.WriteLine("[M] Cycle Sample Rate (Currently: " + RateSummary(settings) + ")");
            Console.WriteLine("[O] Toggle Output Layout (Currently: " + LayoutSummary(settings) + ")");
            Console.WriteLine("[S] Change Source Folder");
            Console.WriteLine("[D] Change Destination Folder");
            Console.WriteLine("[R] Refresh / Rescan");
            Console.WriteLine("[Q] Quit");
            Console.Write("\nSelect option (or enter item number 1-" + items.Count + " to toggle): ");

            var keyInput = Console.ReadLine()?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(keyInput)) continue;

            if (int.TryParse(keyInput, out int itemIndex) && itemIndex >= 1 && itemIndex <= items.Count)
            {
                items[itemIndex - 1].IsSelected = !items[itemIndex - 1].IsSelected;
                continue;
            }

            switch (keyInput)
            {
                case "G":
                    ExecuteConversion(items, settings);
                    Console.WriteLine("\nPress any key to return to menu...");
                    Console.ReadKey();
                    break;

                case "A":
                    bool allSelected = items.All(i => i.IsSelected);
                    foreach (var i in items) i.IsSelected = !allSelected;
                    break;

                case "T":
                    settings.ExportCarSetup = !settings.ExportCarSetup;
                    settings.Save();
                    break;

                case "N":
                    settings.UseDuckDbChannelNames = !settings.UseDuckDbChannelNames;
                    settings.Save();
                    break;

                case "M":
                    // Cycles the four sampling modes. Reduced uses the divisor from the
                    // settings file (2 by default); Pro needs per-channel rates, which are
                    // JSON-only, so it sits here meaning "i2's rates unless overridden".
                    settings.RateMode = settings.RateMode switch
                    {
                        RateMode.Optimised => RateMode.Source,
                        RateMode.Source => RateMode.Reduced,
                        RateMode.Reduced => RateMode.Pro,
                        _ => RateMode.Optimised,
                    };
                    settings.Save();
                    break;

                case "O":
                    settings.OutputLayout = settings.OutputLayout == OutputLayout.Flat
                        ? OutputLayout.Sorted
                        : OutputLayout.Flat;
                    settings.Save();
                    break;

                case "S":
                    Console.Write("\nEnter new Source Folder: ");
                    var newSrc = Console.ReadLine()?.Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(newSrc) && Directory.Exists(newSrc))
                    {
                        settings.SourceFolder = newSrc;
                        settings.Save();
                    }
                    break;

                case "D":
                    Console.Write("\nEnter new Destination Folder: ");
                    var newDst = Console.ReadLine()?.Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(newDst))
                    {
                        Directory.CreateDirectory(newDst);
                        settings.DestinationFolder = newDst;
                        settings.Save();
                    }
                    break;

                case "R":
                    break;

                case "Q":
                    running = false;
                    break;
            }
        }
    }

    /// <summary>One-line description of the sampling mode, for the menu.</summary>
    private static string RateSummary(AppSettings s) => s.RateMode switch
    {
        RateMode.Source => "Source (every sample the game recorded)",
        RateMode.Reduced => $"Reduced (each channel / {s.ReducedDivisor})",
        RateMode.Pro => "Pro (per-channel; edit the settings file)",
        _ => "Optimised (default)",
    };

    /// <summary>One-line description of the output layout, for the menu.</summary>
    private static string LayoutSummary(AppSettings s) => s.OutputLayout switch
    {
        OutputLayout.Sorted => "Sorted (track/class subfolders)",
        _ => "Flat (everything in the destination folder)",
    };

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
========================================================================
                   Duckmetry — DuckDB to i2 converter
                 Unofficial community tool (standalone)
========================================================================");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Independent project. Not affiliated with, endorsed by or sponsored by");
        Console.WriteLine("MoTeC Ltd or Studio 397. Reads your own locally recorded sessions only.");
        Console.ResetColor();
    }

    private static void PrintItemsTable(List<ConversionItem> items)
    {
        Console.WriteLine(string.Format("{0,-4} {1,-4} {2,-28} {3,-15} {4,-15} {5,-12}", 
            "#", "[ ]", "File Name", "Track", "Car", "Status"));
        Console.WriteLine(new string('-', 80));

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var check = it.IsSelected ? "[X]" : "[ ]";
            var statusStr = it.Status == ConversionStatus.AlreadyConverted ? "Converted" : "Pending";

            if (it.Status == ConversionStatus.AlreadyConverted)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.White;
            }

            var fn = it.FileName.Length > 27 ? it.FileName[..24] + "..." : it.FileName;
            var tr = it.Track.Length > 14 ? it.Track[..11] + "..." : it.Track;
            var car = it.Car.Length > 14 ? it.Car[..11] + "..." : it.Car;

            Console.WriteLine(string.Format("{0,-4} {1,-4} {2,-28} {3,-15} {4,-15} {5,-12}", 
                i + 1, check, fn, tr, car, statusStr));
        }
        Console.ResetColor();
    }

    private static void ExecuteConversion(List<ConversionItem> items, AppSettings settings)
    {
        var toConvert = items.Where(i => i.IsSelected).ToList();
        if (toConvert.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\nNo files selected for conversion. Use item numbers or [A] to select files.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\nStarting conversion of {toConvert.Count} files...\n");
        Console.ResetColor();

        var options = new TelemetryConverter.ConversionOptions
        {
            ExtractCarSetup = settings.ExportCarSetup,
            RateMode = settings.RateMode,
            ReducedDivisor = settings.ReducedDivisor,
            RateOverrides = settings.RateOverrides,
            UseDuckDbChannelNames = settings.UseDuckDbChannelNames,
            OutputLayout = settings.OutputLayout,
            OverwriteExisting = settings.OverwriteExisting
        };

        var progress = new Progress<ConversionProgress>(p =>
        {
            Console.Write($"\r--> [{p.CurrentFileIndex}/{p.TotalFiles}] {p.Message}".PadRight(79));
        });

        TelemetryConverter.ConvertBatch(toConvert, settings.DestinationFolder, options, progress, log =>
        {
            Console.WriteLine($"\n    {log}");
        });

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n\n✓ Conversion completed successfully!");
        Console.ResetColor();
    }
}
