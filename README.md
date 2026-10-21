# Duckmetry

**Duckmetry converts racing-sim telemetry (DuckDB files) into `.ld` / `.ldx` files that
the i2 telemetry analysis software reads, plus `.svm` car setups the game loads back.**

> [!NOTE]
> **How this project was made — read this first.** Duckmetry is *vibe-coded*: it was
> built with heavy AI assistance from the first line of code, in close conversation with
> a human who tested every build on real sessions. Nothing in the code or the docs is
> asserted on authority — every claim about the file formats was verified empirically,
> byte by byte, against real files, and the test suite pins that down. That is the honest
> description of how it was made: assisted writing, human verification. Fork it, distrust
> it, or build on it — all valid.

> [!IMPORTANT]
> **Independent project.** This is an unofficial community tool. It is not affiliated
> with, endorsed by, or sponsored by MoTeC Ltd, Studio 397, the ACO or the FIA. It reads
> telemetry files **you recorded yourself**, on your own machine, and does nothing else:
> no network access, no telemetry transmitted, no game assets included. See
> [TRADEMARKS.md](TRADEMARKS.md).

---

## For players: getting started

1. Download the latest `Duckmetry.*.exe` from the **Releases** page (Windows 10/11,
   64-bit). No install, no dependencies — one file.
2. Double-click it. The first time, the **Settings** window opens: point **Source
   telemetry folder** at your game's telemetry folder (typically
   `Steam\steamapps\common\Le Mans Ultimate\UserData\Telemetry`) and **Destination
   folder** at where you want the converted files (the analysis software's `Logged Data`
   folder, often `Documents\MoTeC\Logged Data`). Save.
3. Press **Rescan** — your recordings appear as a list of sessions. Check the ones you
   want, press **Convert selected**, and the converted `.ld`/`.ldx` files land in the
   destination, ready for the analysis software.

That is the whole happy path. Everything else is tuning, explained below.

**Where the app keeps its state:** settings live in
`%LOCALAPPDATA%\Duckmetry\settings.json` (source/destination folders, rate mode,
auto-start, per-channel overrides). If the app ever crashes, it writes a log to
`%LOCALAPPDATA%\Duckmetry\crash-*.log` — include the latest one if you report an issue.

## The window, option by option

**Source / Destination folders + Browse…** — where the recordings are and where the
converted files go. The app remembers them.

**Rescan** — refreshes the session list. Sessions already converted are marked as such
and are not offered for conversion again unless overwrite is enabled.

**The session list** — one row per recording: name, date, status. Tick the sessions to
convert. Tick several, convert several.

**Sample rate** — how many samples per second each channel is written at. Four modes:

- **Optimised (default)** — the analysis software's own standard rate per channel; the
  smallest files while keeping every channel usable.
- **Source** — every sample the game recorded; the full fidelity, roughly 3× the size.
- **Reduced** — every channel ÷2; lean files when you only need trends.
- **Pro (per-channel rates)** — your rate per channel. See *Set rates…* below.

The one rule in every mode: **never faster than the recording** — the game records at
the rates configured in its own telemetry `settings.json` (your `UserData/Telemetry`
folder), and those rates are the ceiling. Full details in [docs/SAMPLING.md](docs/SAMPLING.md).

**Set rates…** — appears when *Pro* is selected and at least one session is checked.
It reads the selected sessions' channels and their recorded rates, and lets you set each
channel's rate up to the lowest rate found across the selected files (the highest rate
valid for all of them). Your choices are saved and reused; check the sessions again and
press the button to edit them.

**Game channel names (DuckDB spelling)** — on by default: channels appear under the game's own
spelling. Uncheck to use the analysis software's standard spellings. Either way the
channel *IDs* are the standard ones, so maths and lap machinery keep working.

**Car setup (.svm)** — also export the car setup that was active during the session, in
the format the game reads back.

**Sorted output (track/class folders)** — write into `<track>\<class>\<session>.ld`
instead of all files in the destination folder. Classes come from the session's own
recorded car class (`Hypercar`, `GT3`, …); unknown ones go in an `Other` folder.

**Convert selected** — converts every checked session, with a progress bar and a log of
what happened to each file.

**Auto mode → tray** — hides the window and watches for sessions ending. See below.

**Settings…** — the same window as first run, plus two toggles: *Overwrite existing .ld
files*, and *Start in auto mode (tray) when the app launches*.

## Auto mode

With auto mode on, Duckmetry lives in the Windows tray. When a session is **finalised**
(you're back in the garage), it converts that session on its own and shows a balloon
notification. The trigger isn't polling the folder: the app reads the game's own live
shared-memory interface (the same one in-game overlays use) and fires on the exact
"back to the garage" signal, then waits for the recording to settle before converting.
If the game exits instead of returning to the garage, it still catches the file.

The tray icon's right-click menu: **Show window**, **Start in auto mode at launch**,
**Settings…**, **Quit**.

How it works, in detail (for the curious and for other developers):
[docs/AUTO-MODE.md](docs/AUTO-MODE.md).

Headless use: the same exe also works from a command line — `Duckmetry.exe <source
folder> <destination folder>` converts everything without opening the window.

## Notes from real use

**Drag and downforce cannot be converted.** `Drag`, `Front Downforce`, `Rear Downforce`
(and `Front Wing Height`) exist in reference logs, but the game deactivates them for
licensing reasons and its own recorder does not write them to the DuckDB. There is
nothing in your recording to convert. Add a maths channel in i2 if you need them.

---

## For developers: building from the repo

Requires the **.NET 9 SDK**. The solution builds on Windows and Linux; the GUI itself
only *runs* on Windows.

```bash
dotnet build -c Release        # the whole solution
dotnet test  -c Release        # the test suite (conversion, format, auto-mode)
```

Publish the GUI as a single file:

```bash
dotnet publish src/Duckmetry.Gui -c Release -r win-x64 \
  --self-contained true /p:PublishSingleFile=true \
  -o dist/windows-x64
```

**Debugging and contributing:** this is a plain MIT-licensed .NET project — fork, build,
and debug freely. The released exe is a *self-contained single-file Release* build, which
runs standalone but is not the best attach point for a debugger; for development build a
Debug configuration and run it from an IDE:

```bash
dotnet build -c Debug              # debug symbols on
dotnet run --project src/Duckmetry.Gui -c Debug   # the window, debuggable
```

The Release build embeds its PDB (`DebugType=embedded`), so even the published exe
carries line numbers in stack traces and in the crash log under
`%LOCALAPPDATA%\Duckmetry\crash-*.log`.

**Repository layout:**

- `src/Duckmetry.Core` — the engine: DuckDB reader, converter, `.ld`/`.ldx`/`.svm`
  writers, channel dictionary, rate policy, auto-mode watcher.
- `src/Duckmetry.Gui` — the Windows GUI + tray + headless CLI in one exe.
- `src/Duckmetry.Cli` — the older console tool, still useful headless on Linux.
- `tests/Duckmetry.Tests` — xUnit suite, including end-to-end conversions.
- `tools/` — the shared-memory diagnostic probe; internal only, not shipped.

**Where the knowledge lives:** [docs/LD-FORMAT.md](docs/LD-FORMAT.md) is the empirical
account of the `.ld` format (and a recipe for building one from any data);
[docs/SAMPLING.md](docs/SAMPLING.md) covers the rate modes;
[docs/AUTO-MODE.md](docs/AUTO-MODE.md) explains the live-telemetry trigger.

## License

MIT — see [LICENSE](LICENSE). Trademark posture and the non-affiliation statement:
[TRADEMARKS.md](TRADEMARKS.md).