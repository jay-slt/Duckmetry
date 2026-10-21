# LMU shared-memory probe

A read-only diagnostic. It answers one question:

> **Can a program other than a plugin see LMU's session state while the game is running?**

If yes, the converter's auto mode gets told when a session ended, instead of guessing from
whether a file has stopped changing.

## There are two interfaces, and only one of them needs a plugin

| | mapping | what it carries | needs anything installed? |
|---|---|---|---|
| **Official (Studio 397)** | `LMU_Data` | the game's **event queue**, application state, scoring, and per-frame **telemetry for up to 104 vehicles** | **no** — the game publishes it itself |
| Community plugin | `$LMU_SMMP_Extended$` / `Extended` | session state only — 32 fields, **no telemetry** | yes, a plugin `dll` |

**The official one is the target.** Studio 397's own headers declare `LMU_Data` as a plain named file mapping, and
their own usage example opens it from a *separate child process*
(`OpenFileMapping(..., L"LMU_Data")`). So an ordinary Win32 application can attach, with
nothing installed and no plugin loaded.

This corrects an earlier conclusion in this folder. The community block genuinely carried no
telemetry, and that was written up as a property of LMU shared memory. It is a property of
*that plugin*: the official block carries `TelemInfoV01 telemInfo[104]`, which is the full
per-frame channel set. Whether it is worth reading is a separate question — the DuckDB
already records the channels the converter needs, at higher rates — but "the shared memory
cannot give us channels" was wrong.

## What this probe still does not do

It does **not** decode telemetry, and does not need to. The part auto mode needs is the
session state, which lives in the first ~100 bytes of the block. Reading only that prefix is
deliberate: it means the reader never has to reproduce the full 104-vehicle layout, which is
where transcription errors would hide.

## The "back to garage" signal

The official block carries the game's own event queue. `SharedMemoryEvent` has 16 members;
these are the ones that matter:

| value | event | meaning |
|---|---|---|
| 7 | `SME_END_SESSION` | the session ended |
| 8 | `SME_ENTER_REALTIME` | the car became drivable |
| 9 | `SME_EXIT_REALTIME` | the car stopped being drivable |

and `ApplicationStateV01.mOptionsLocation` — 0 main UI, 1 track loading, 2 monitor, 3 on
track. Leaving 3 for 0 or 2 is "back to the garage".

What the header does **not** say is how the array is maintained. `CopySharedMemoryObj` scans
its *values*, which suggests a queue; but `SME_ENTER` is 0, so an empty slot cannot be told
from an `ENTER` event by value alone. Whether the slots hold their own index, a rolling queue,
or a settled set is exactly what the probe's `events[]` dump is for — one real run settles it.

## Safety

- Read-only. Opens the mapping with `FILE_MAP_READ` only.
- Writes nothing anywhere.
- Holds the view only while reading, and unmaps it on the way out.
- For the community block, the version block is checked before and after each read, so a
  frame caught mid-write is reported as torn rather than decoded into wrong numbers.

## Running it

### The exe

`LmuSharedMemoryProbe.exe` is self-contained: no .NET install needed on the Windows machine.

1. Copy `LmuSharedMemoryProbe.exe` to your LMU machine.
2. **Start LMU.** For the official block that is all that is required — no plugin, no
   configuration.
3. Run the exe from an **elevated** command prompt. Elevation matters: Windows hides another
   process's shared memory from an unelevated reader, and that looks identical to "the block
   is not published".

```
LmuSharedMemoryProbe.exe
```

It prints, in order:

1. the **documented names opened directly** — `LMU_Data` first, then the community names.
   These opens are instant; if one succeeds, you will see it within a second.
2. the decoded state from whichever block is large enough, and a verdict.

The system-wide handle scan (every named file-mapping on the machine) only runs when **no**
documented name opens, because it is slow — one kernel call per section handle, minutes on a
busy elevated machine. When it does run it is time-budgeted to 25 s, prints progress so it
never looks stuck, and marks the listing as `TRUNCATED` if the budget runs out.

`LMU_Data` is decoded with the official reader; the community names with the `LMU_Extended`
reader, so a machine with both is fine — the probe says which layout it used.

### Watching it change

```
LmuSharedMemoryProbe.exe --watch --seconds 120
```

Re-reads twice a second and prints only what changed. Start it while you are in the garage,
drive, then **come back to the garage and let it run through that moment**. The rows that
move are the signal.

**Send me the whole output** — especially the `events[]` rows and `app.mOptionsLocation`
before and after returning to the garage. That is what decides the auto-mode trigger.

#### Saving the output

The probe writes nothing, so redirect it to capture everything:

```
LmuSharedMemoryProbe.exe > probe.txt 2>&1
```

or, to keep it on screen and in a file at once, from PowerShell:

```
.\LmuSharedMemoryProbe.exe 2>&1 | Tee-Object -FilePath probe.txt
```

Either way you end up with `probe.txt` next to the exe. Paste that file's contents back, or
attach it to the release. It contains no telemetry and no personal data — only the mapping
names on the machine and a decoded struct — so it is safe to send.

### Self-check

```
LmuSharedMemoryProbe.exe --selfcheck
```

Runs on any platform, including Linux. It checks two things:

- the community `LMU_Extended` offsets against hand-computed values, plus a round trip;
- the official layout **against the Studio 397 headers themselves**. The event list, the
  struct order and every offset are re-derived from `SharedMemoryInterface.hpp` and
  `InternalsPlugin.hpp` and compared with the constants in `LmuOfficialLayout.cs`.

The second is the stronger check: if Studio 397 changes the struct, it fails loudly here
instead of the probe printing plausible but wrong numbers.

Current status: **85 checks, all passing.**

## Exit codes

| code | meaning |
|---|---|
| 0 | a block opened and its layout matched |
| 1 | no block opened — the game is not running, or this is not elevated |
| 2 | the probe could not run (wrong platform, or enumeration failed) |
| 3 | something opened, but nothing was large enough to decode |

If you get **3**, that is still a useful result, not a failure: the sizes printed are the
sizes of the blocks on *your* LMU build, which tells us what the layout really is. Send it
as-is. It also means the build differs from the headers, and the offsets need revisiting.

## Where the layouts came from

- **Official**: Studio 397's own SDK headers (`LMU_Data.hpp` and friends),
  `Copyright (c) 2025 Studio 397 BV and Motorsport Games Inc.` The prefix offsets are
  re-derived from them at run time by `--selfcheck`.
- **Community**: `tembob64/LMU_SharedMemoryMapPlugin`, `Include/LMU_State.h`, which declares
  `#pragma pack(push, 4)`. Offsets are computed from the field list at run time rather than
  hand-written, so the self-check compares two independent derivations.
