using System.Reflection;

namespace Duckmetry.Gui;

/// <summary>
/// Loads the app's artwork from the embedded resources (the .ico files generated from
/// the user's assets/*.jpg). One source of truth for the window icon and the tray icon.
/// </summary>
internal static class Icons
{
    private static readonly Assembly Asm = typeof(Icons).Assembly;

    /// <summary>The application icon: window title bar, taskbar, alt-tab.</summary>
    public static Icon App { get; } = Load("duckmetry.ico");

    /// <summary>The tray icon (16/32 px), shown in the notification area.</summary>
    public static Icon Tray { get; } = Load("duckmetry_tray.ico");

    private static Icon Load(string resourceName)
    {
        using var stream = Asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded icon '{resourceName}' not found");
        return new Icon(stream);
    }
}