# Auto mode: how the app catches the game's live telemetry

Auto mode is the tray feature: the app sits in the Windows tray, and every time a
session is **finalised** ("back to the garage"), it converts that session's DuckDB on its
own. The one thing that makes it work without polling the disk every few seconds is the
game's own **shared memory** — the same mechanism the in-game overlays (TinyPedal, Crew
Chief's modern path) use. This document explains that mechanism: where the live telemetry
is, how the app attaches to it, and which signal means "the session is over".

This is written for two readers: players who want to know what their machine is doing
while Duckmetry sits in the tray, and developers of other tools who want to use the same
door the game leaves open. Everything here is **measured**, on the user's machine, against
the running game — the proof-of-concept lives in `tools/DuckmetryProbe`.

A note on the names up front: the interface this section describes is **the game's own**.
The block names (`LMU_Data`, the `$LMU…` family, the event names) are the literal names
the game publishes on the operating system, straight out of the interface headers that
ship inside the game itself (look in the game's `UserData/Support` folder on your
installation). They are kept exactly as-is because renaming them would mean pointing at
an object that does not exist; they belong to the game, not to this project.

---

## 1. The source: a named file mapping, no plugin involved

The game publishes its live data as a **named file mapping** — the interface its own
tooling (and the community overlays) uses. The headers that declare it — `SharedMemoryInterface.hpp`
and friends — ship in the game's `UserData/Support` folder; that is where this project's
knowledge of every field name and offset comes from. The block itself:

| thing | value |
|---|---|
| name | `LMU_Data` (Global\ and Local\ namespaces, plus variants) |
| size | 327,680 bytes |
| owner | the game process |
| consumers | any process, **without a plugin and without elevation** |
| contents | `SharedMemoryObjectOut { generic, paths, scoring, telemetry }` |

Alongside the block itself the game publishes:

- **events** — named events `LMU_Data_HoldEvent`, `LMU_Data_DataEvent`,
  `LMU_Data_Event`, `LMU_SharedMemoryLockEvent`. `LMU_Data_DataEvent` is **signalled while
  the game writes**, i.e. each "a new frame was written" pulse.
- **a lock section** — `LMU_SharedMemoryLockData`, a spinlock for torn-frame-safe reads.
- the **community family** `$LMU.3.8.SMMP_*$` — thirteen more blocks (`3.8` is the
  interface version). The `Extended` block (12,288 bytes) carries session flags and is a
  useful second watch source.

So "live telemetry from the game" is not something the app has to capture — the game
leaves a door open and the app simply opens it.

## 2. How the app attaches (the mechanism, exactly)

The probe's fast path is the whole story:

1. `OpenFileMappingW(FILE_MAP_READ, inheritHandle, "LMU_Data")` — this is the **`*W`**
   (wide-char) variant, and the parameter must be a real UTF-16 string.
2. `MapViewOfFile` a read view of the 327,680 bytes.
3. Read the event pulse with `OpenEventW(...)` on `LMU_Data_DataEvent`, then
   `WaitForSingleObject` / check its signal state.
4. Optionally spin on the lock section before reading, for torn-frame safety.

### The one trap that made it look impossible

The **default `DllImport` charset is `CharSet.Ansi`**. Declaring
`OpenFileMappingW`/`OpenEventW` without `CharSet = CharSet.Unicode` marshals the managed
`string` to **ANSI bytes**, which the `*W` functions then read as UTF-16 pairs — so the
name actually looked up was garbage (e.g. `MLU_Dat a`) and every by-name open honestly
failed with Win32 error 2 *for a name that genuinely does not exist*. That is the whole
reason the interface looked dead for days: the block was there, the name was right in the
headers, and the interop layer was lying about the result.

**Rule: every `*W` extern that takes a managed string needs
`[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]`.** With that
one line the block opened on the next run. (Python `mmap` works out of the box because it
uses the wide API with a correct UTF-16 string.)

## 3. What the app reads — and what actually moves

The scoring section inside `LMU_Data` (`ScoringInfoV01`, declared in the headers) is where
the session state lives. Finding it took an empirical detour that matters:

- The header-chain section sizes (generic + paths) **differ in this game build** — reads at
  the computed base returned garbage (`vehicles = 17104907`, i.e. ASCII `'J'`).
- The section base was pinned **empirically, by the player's own name**: the flip latch
  caught the ASCII string `Test Driver` at raw offset `0x6D4` = `mPlayerName` (REL 124 in
  `ScoringInfoV01`) → **scoring base = `0x658`**. The probe verifies this anchor at runtime
  (re-checks every 10 s until the player name appears).
- The byte `0x6D3` = `mInRealtime` (REL 123) **round-trips with the session, every run**:
  `False` in the menu and in the garage, `True` when the car is drivable, back to `False`
  at the garage return. The flip latch watched it flip `0 → 1` in-session across four
  separate runs.

A full measured session timeline (the user's run with a practice session and a garage
return at the 57 s mark):

```
15.4 s  events[0] = LOAD             (session loading)
18.5 s  events[0] = START_SESSION    (session started)
23.1 s  mInRealtime 0 -> 1           (car drivable)
24.6 s  mGamePhase -> 5 (green)
50.8 s  FFB torque -> 0              (car stopped)
51.3 s  mInRealtime 1 -> 0           <-- the return
57 s    back to the garage (user's mark)
        mGamePhase stays 5, mNumVehicles stays 1 (the block is not cleared in the garage)
```

### What does NOT move (measured, do not re-investigate)

- `ApplicationStateV01.mOptionsLocation` — `0` (main UI) in menus, the pit lane, **and on
  track**. Inert on this game build.
- the event queue's session events — only `LOAD`/`START_SESSION`/`FFB` were ever observed;
  `END_SESSION`/`EXIT_REALTIME` never arrive.
- the `Extended` block's `mInRealtimeFC` / `mSessionStarted` — `False` on track (at FFB
  `−0.216`, i.e. genuinely driving).
- scoring `mNumVehicles`, `mGamePhase` — inert through the whole verdict run.
- **the DuckDB recording is invisible to the shared memory.** Starting an in-game DuckDB
  recording while driving produced no flag, no event, nothing. The only trace of a
  recording is the DuckDB file itself.

### Liveness, proven

The reads are genuinely live and are the game: FFB torque churns every ~0.5 s (peaks
~0.095, `−0.216` on track), and `LMU_Data_DataEvent` is signalled while the game writes.

## 4. The auto-mode trigger chain

The trigger is the **falling edge of `mInRealtime`** — the game's own "back to the
garage" moment — latched with a "was driving" state so it fires **once per session**:

```
open LMU_Data  ->  every ~0.5 s: read mInRealtime
  mInRealtime True:  latch "in session"
  mInRealtime False AND latch "in session":  -> the session ended -> convert
```

That is the primary signal. Two free belt-and-braces triggers cover the cases where the
block is unreachable (game closed rather than driven back, a crash):

- **the game process exits** while the latch says "in session", and
- **a DuckDB appears and settles** (its size stops changing; ~1 s quiet window) in the
  telemetry folder.

All three funnel into the same action: **convert that session's DuckDB with the user's
settings** (`OutputLayout` included), one conversion at a time, and **never** on the game's
~10 s incremental DuckDB rebuild tick — converting a half-written DuckDB yields a truncated
log, which is the one failure this whole design exists to avoid.

## 5. The proof-of-concept vs the product

- `tools/DuckmetryProbe` is the **proof-of-concept**: it opens the block, decodes
  the official prefix, watches the events, arms the flip latch, and reports the session
  state — the mechanism this document describes, proven against the live game. It is an
  internal diagnostic and is **not shipped**.
- The **product** is the tray watcher in the GUI: a small WinForms app that consumes the
  same mechanism (open → read `mInRealtime` → latch → convert), lives in the tray, and
  reports conversions. It uses the same two ingredients as the probe —
  `OpenFileMappingW` + `CharSet.Unicode`, and the pinned base `0x658`.

**If you want to build your own tool on this door**, the whole recipe is: open the named
block read-only with the wide-char API (the `CharSet.Unicode` rule below is not a style
choice — with the default ANSI marshalling the name lookup fails with error 2 and the
block "does not exist"), read `mInRealtime` at the pinned offset on a ~0.5 s loop, latch
it with a "was driving" state, and fire your action on the falling edge. That is the
entire signal chain; the offsets and the measured timeline above are the places
everyone gets stuck.

## 6. Traps that cost real time — do not repeat

1. **`CharSet.Ansi` on `*W` imports** — the whole "listed but unopenable" saga (§2). Any
   extern taking a managed name string needs `CharSet.Unicode`.
2. **The header-chain offset math is wrong for this build.** Compute nothing — anchor on
   the player name at `0x658 + 124`, verify it at runtime, and read `mInRealtime` at
   `+123`.
3. **Documented session fields being inert is normal here.** `mOptionsLocation`, the
   event queue, `mGamePhase`, `mNumVehicles`, the `Extended` flags — all measured
   motionless while the car was on track. Only `mInRealtime` round-trips. Build the trigger
   on what moves, not on what the header *says* moves.
4. **`GetFileNameWithoutExtension` drops directories** (used in the scanner work): use
   `Path.ChangeExtension` when a relative path's directory part must survive.
5. **Verify field names against the supplied headers before wiring a trigger.** A proposed
   `mInGarage` does not exist in the S397 headers or pyLMU — only `mInGarageStall`
   (per-vehicle, a different meaning).
