using Duckmetry.Core.Configuration;

namespace Duckmetry.Tests;

/// <summary>
/// Points AppSettings at an isolated temp file for the duration of a test, so parallel
/// test classes never collide on the user's real settings file. That collision made the
/// settings round-trip tests flaky: two classes deleted/wrote/read the same
/// %LOCALAPPDATA%\Duckmetry\settings.json concurrently and intermittently lost the race.
/// </summary>
internal static class TestSettings
{
    public static IDisposable UseTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"duckmetry-settings-{Guid.NewGuid():N}.json");
        var old = AppSettings.SettingsFilePathOverride;
        AppSettings.SettingsFilePathOverride = () => path;
        return new RestoreOnDispose(() => AppSettings.SettingsFilePathOverride = old);
    }

    private sealed class RestoreOnDispose(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            onDispose();
        }
    }
}