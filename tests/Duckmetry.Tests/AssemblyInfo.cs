using Xunit;

// The suite touches shared state (the settings path, temp recordings, the watcher's
// working directories, the channel-audit duckdb). xUnit runs test classes in parallel
// by default, which made the settings round-trip tests flaky: two classes deleted /
// wrote / read the same %LOCALAPPDATA%\Duckmetry\settings.json concurrently, and even
// the per-test temp-path seam leaked because it is a shared static. Serializing the
// whole assembly costs ~13 s and removes the entire class of cross-test race. The
// settings tests still use TestSettings.UseTempPath() so they never touch the user's
// real settings file either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]