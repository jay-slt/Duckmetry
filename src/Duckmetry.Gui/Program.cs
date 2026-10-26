using System;
using System.Runtime.InteropServices;
using System.Threading;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Converter;
using Duckmetry.Core.Models;
using Duckmetry.Core.Scanner;

namespace Duckmetry.Gui;

/// <summary>
/// The merged entry point: no arguments -> the window (with tray + auto mode);
/// two arguments -> the same headless conversion the console CLI always did.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A WinExe has no console: any early exception would kill the process
        // invisibly ("double click, loading, then nothing"). Catch everything, write
        // the full exception to a log the user can send, and say where it went.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportFatal(e.ExceptionObject as Exception ??
                        new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error"));
        if (args.Length >= 2)
        {
            try
            {
                return RunCli(args[0], args[1]);
            }
            catch (Exception ex)
            {
                ReportFatal(ex);
                return 1;
            }
        }

        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            if (AppSettings.Load().AutoStartInAutoMode)
            {
                form.EnterAutoMode();
            }
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
            return 1;
        }
    }

    /// <summary>
    /// Writes the full exception (with stack trace) to
    /// <c>%LOCALAPPDATA%\Duckmetry\crash-*.log</c> and tells the user where it went.
    /// </summary>
    private static void ReportFatal(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Duckmetry");
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(log,
                $"Time:   {DateTime.Now:O}{Environment.NewLine}" +
                $"Command: {Environment.CommandLine}{Environment.NewLine}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}");
            MessageBox.Show(
                "Duckmetry ran into a problem.\n\n" +
                $"Details: {ex.GetType().Name}\n{ex.Message}\n\n" +
                $"The full error was written to:\n{log}\n\n" +
                "Please send that file when reporting the problem.",
                "Duckmetry", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing we can do without a console; the app is already dying.
        }
    }

    /// <summary>
    /// Headless CLI, mirroring the console app's two-argument mode: convert every
    /// pending DuckDB in the source folder into the destination, honouring the saved
    /// settings, and print a summary to stdout.
    /// </summary>
    private static int RunCli(string sourceFolder, string destinationFolder)
    {
        AttachParentConsole();
        try
        {
            var settings = AppSettings.Load();
            var options = new TelemetryConverter.ConversionOptions
            {
                ExtractCarSetup = false,
                RateMode = settings.RateMode,
                ReducedDivisor = settings.ReducedDivisor,
                RateOverrides = settings.RateOverrides,
                UseDuckDbChannelNames = settings.UseDuckDbChannelNames,
                OutputLayout = settings.OutputLayout,
                OverwriteExisting = false,
            };

            if (!Directory.Exists(sourceFolder))
            {
                Console.Error.WriteLine($"Source folder does not exist: {sourceFolder}");
                return 1;
            }
            Directory.CreateDirectory(destinationFolder);

            var items = TelemetryScanner.ScanDirectories(sourceFolder, destinationFolder, settings.OutputLayout);
            if (items.Count == 0)
            {
                Console.Error.WriteLine("No DuckDB files found in source folder.");
                return 1;
            }

            var pending = items.Where(i => i.Status == ConversionStatus.Pending).ToList();
            if (pending.Count == 0)
            {
                Console.WriteLine("Everything is already converted.");
                return 0;
            }

            int ok = 0;
            foreach (var item in pending)
            {
                bool converted = TelemetryConverter.ConvertSingleFile(
                    item.DuckDbFilePath, destinationFolder, options,
                    cancel: CancellationToken.None);
                Console.WriteLine($"{(converted ? "OK  " : "FAIL")} {item.FileName}");
                if (converted) ok++;
            }

            Console.WriteLine($"\nConverted {ok}/{pending.Count}.");
            return ok == pending.Count ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// A WinExe has no console by default; when launched from a terminal the output
    /// must go to the parent console. Redirected stdout (pipes) already works and is
    /// left alone; an attached console covers the "run it in a window" case.
    /// NOTE: touching the Console API in a console-less GUI app can throw
    /// IOException("The handle is invalid") — guarded, never fatal.
    /// </summary>
    private static void AttachParentConsole()
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                AttachConsole(-1);
            }
        }
        catch
        {
            // No console attached — output goes nowhere, which is fine for a GUI app.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}