# The `.ld` file format, as reverse-engineered for Duckmetry

> **This document is empirical. It is not the truth of the `.ld` format, and should not be
> read as one.**
>
> Everything below was found by experiment: reading bytes, changing one variable at a time,
> and watching what the telemetry software (i2 — the analysis application that reads these
> files; "MoTeC i2" when the vendor matters, which is only in the trademark sense) did.
> The vendor has never published the format, so what is written here is *what works for
> this project*, not a specification. Large parts of the file are still unexplained (see
> §7), and several statements that once stood here as fact were later shown to be wrong —
> an unrecognised channel ID is *not* discarded (§3), and the Beacon payload long dismissed
> as an RF artefact turned out to be derivable (§5.5).
>
> **Our converter produces correct output. That is a weaker claim than understanding the
> format**, and the gap between the two has cost this project more than once. Treat every
> line as a working hypothesis with evidence attached, and prefer — in this order — a
> genuine i2-written log, then `gotzl/ldparser`, then this document.

What we know about the binary `.ld` file that the i2 telemetry software reads, how we know
it, and what remains a guess. If your goal is to *build* such a file — from a DuckDB
recording or from any other data you have — §9 is the recipe; the sections before it are
the evidence the recipe is built on.

Everything here was derived from four sources, in descending order of authority:

1. **`gotzl/ldparser`** — the reference Python parser. Its `ldChan` and `ldHead` struct
   definitions and its `ldData.write` method are the authoritative statement of the layout
   and of the quantisation arithmetic. Where this document and inference disagree, this
   source is right.
2. **Two genuine i2-written logs** — supplied by the user, one of them paired with the
   DuckDB it was recorded from. These are ground truth for what the software actually
   emits.
3. **`DAMPlugin.dll`** — the original in-game plugin. Its log format was the **inspiration
   and the starting point for this project, not the reference**: the way it laid out the
   header, the channel list and the data blocks was studied, then tested against DuckDB
   conversions, then adjusted and modified until i2 accepted the result. The plugin is a
   native C++ PE binary; it was read for strings and its channel table, and it is not
   decompilable to source (no managed metadata). Treat its exact byte choices as one
   historical implementer's choices, not as the format itself.
4. **The channel tables in the game's DuckDB files** — the names, units and sample rates
   the game itself records, which is where the vocabulary of every converted session
   comes from. See §9.2 for how the two sides (game vocabulary vs. the software's fixed
   vocabulary) are reconciled.

i2 itself is Windows-only and cannot be run here. Every claim below is a statement about
the bytes, not about how i2 renders them — with the exceptions explicitly marked.

---

## 1. File structure

### 1.1 The two regions

A `.ld` file is one header followed by two arrays, pointed at from the header:

```
0x0000  ldHead        1762 bytes  (0x6E2)
        channel headers, 124 bytes each, as a linked list
        channel data blocks, in list order
```

There is no index, no directory and no padding between the data blocks. The channel list
is a singly linked list, and **the order is the write order** — MoTeC reads channels in the
order they appear, so reordering changes what it sees.

### 1.2 `ldHead` — 1762 bytes

From ldparser's `ldHead.fmt = '<I4x II 20x I 24x HHH I 8s H H I 4x 16s 16x 16s 16x 64s 64s 64x
64s 64x 16s 64x 68x 40x 8s 36x I 112x I 4x I 116x I 24x 104x 4x I 1344x I 2x I 16x I 2x I 8x
6x I 12x I 12x 2x I 4x 488x 4x I 254x 10x I 130x I 8x I 26x 14x 2x 4x I 4x I 6x I 2x I 12x 12x
2x I 16x 4x I 4x I 2x I 26x 24x 14x 2x 4x I 22x 4x I 4x I 2x 2x 2x I 2x 30x 24x 2x 2x 2x I 40x
8x I 30x 26x 4x 2x 2x 4x 4x I 22x 4x 6x 2x 2x 2x I 40x 46x 2x 2x 2x I 40x'`:

Offsets below are **expanded from ldparser's `ldHead.fmt`**, not remembered. An earlier
hand-written note had three of them wrong; `headfields.py` in the verification directory
re-derives them from the format string.

| offset | size | field | notes |
|---|---|---|---|
| `0x00` | 4 | marker | `0x40` in both genuine logs |
| `0x04` | 4 | chann_meta_ptr | genuinely `0` in every file examined |
| `0x08` | 4 | chann_data_ptr | **pointer to the first channel header** |
| `0x0C` | 4 | — | first data block; `0x3448 + 245 × 124 = 0xAAF4` exactly |
| `0x24` | 4 | event_ptr | `1762`, the header size — a self-reference |
| `0x40` | 2 | — | `0` |
| `0x42` | 2 | — | `16960` |
| `0x44` | 2 | — | `15` |
| `0x46` | 4 | device serial | `12007` |
| `0x4A` | 8 | device type | `"ADL"` |
| `0x52` | 2 | — | `200` in both genuine logs |
| `0x54` | 2 | — | `128` in both genuine logs |
| `0x56` | 4 | num_channs | **`78` in both genuine logs despite 245 channels** — *not* the channel count |
| `0x5E` | 16 | date | the only fields that differ between the two logs |
| `0x7E` | 16 | time | |
| `0x9E` | 64 | driver | the only identity string MoTeC writes |
| `0xDE` | 64 | vehicleid | **empty in both genuine logs** |
| `0x15E` | 64 | venue | |
| `0x5DE` | 4 | pro-logging flag | `0x00D20822` in both genuine logs — but see below |
| `0x624` | 64 | short comment | empty in both |

**`0x08` is the single most damaging field to get wrong.** When it was zeroed, MoTeC opened
the file, showed the track report, and listed **no channels at all** — no error, just an
empty data menu. It took many releases to find.

**The header carries only the driver and venue.** In both genuine logs the vehicle,
comment, event and session slots (`0xDE`, `0x624`, `0x6E2`, `0x722`) are **empty**. An early
version wrote the vehicle into `0xDE`, and MoTeC rendered the bytes as visible junk
characters immediately after the driver name in the data menu. Leave them empty.

The `0x52`/`0x54`/`0x56` triple is constant across all three genuine logs and is *not* a
channel count. We used to write our own channel count at `0x56`; that was one of the wrong
constants in the set below.

**Lap rendering lives in these header fields.** MoTeC decides how many laps a log has from
the header, and a header carrying our old constants produced **13 laps for a 4-lap session** —
every Beacon crossing taken as a lap. All of these are now written with values that three
genuine logs agree on byte for byte:

| Field | Genuine | Our former value |
|---|---|---|
| `0x24` event_ptr / header size | 1762 | 1890 |
| `0x40` | 0 | 1 |
| `0x46` device serial | 12007 | 8004 |
| `0x52` | 200 | 420 |
| `0x54` | 128 | 44464 |
| `0x56` | 78 | (our channel count) |
| `0x5A` | 100 | 0 |
| `0x5C` | 1 | 0 |
| `0x66C` | 99 | 0 |
| `0x5DE` pro-logging flag | `0x00D20822` | `0x000C81A4` |

Found by transplanting a genuine header into our file in place, which fixed the lap count,
then bisecting. **They must move together**: i2 refuses to open a file whose pro-logging flag
is genuine while the rest of the header is not — transplanting only `0x00D20822` gave *"this
version of i2 only supports pro logging enabled files"*. Note that `0x24` is written as 1762
even though our own header region is 1890 bytes: MoTeC keys on the value, not on our layout.

### 1.3 `ldChan` — 124 bytes

From ldparser's `ldChan.fmt = '<IIII H HHH hhhh 32s 8s 12s 40x'`:

| offset | size | field | notes |
|---|---|---|---|
| `0x00` | 4 | prev | previous channel header, `0` for the first |
| `0x04` | 4 | next | next channel header, `0` for the last |
| `0x08` | 4 | data_ptr | offset of this channel's samples |
| `0x0C` | 4 | n_data | **sample count** |
| `0x10` | 2 | channel ID | **standard channel ID — this is what identifies a channel** |
| `0x12` | 2 | dtype_a | `0x03`/`0x05` signed int, `0x07` float |
| `0x14` | 2 | width | bytes per sample: 1, 2 or 4 |
| `0x16` | 2 | freq | **sample rate in Hz** |
| `0x18` | 2 | shift | signed |
| `0x1A` | 2 | multiplier | signed |
| `0x1C` | 2 | scale | **not always 1** |
| `0x1E` | 2 | decimals | |
| `0x20` | 32 | name | |
| `0x40` | 8 | short name | max 7 characters plus NUL |
| `0x48` | 12 | unit | `km/h`, `C`, `rpm`, `%` … |

**`0x40` is an 8-byte short name and `0x48` is the unit.** This was misread for several
releases as the reverse. The disambiguation is trivial once seen: a genuine log has
`km/h` at `0x48` and `Grd Spd` at `0x40`; a unit and a short name cannot be confused once
you check which field is empty.

### 1.4 Data blocks

Blocks are packed **immediately after the channel headers, in list order, with no gaps or
alignment.** The genuine files satisfy `first_data + Σ(n_data × width) == file_size`
exactly; so must ours. A useful invariant, because when it breaks the file is malformed in a
way MoTeC may not report.

---

## 2. Quantisation — the arithmetic that took longest to get right

From ldparser's `ldData.write`:

```python
conv_data = ((data / mul) - shift) * scale / pow(10., -dec)
```

and its inverse:

```
value = raw / (scale × 10^decimals) × multiplier + shift
```

**The shift is added AFTER the raw is divided back out.** An earlier version applied it
before scaling, which read genuine Ground Speed samples as −24,400 km/h instead of 0.

### 2.1 The four fields that bite

| field | trap |
|---|---|
| `scale` at `0x1C` | Not always 1. Twelve channels use 2 or 50: `Ambient Temperature` and `Track Temperature` use **50**, the load and downforce channels use 2. It is a stable property of the channel ID — identical in both genuine logs. |
| `shift` | A **value**, not a scaled quantity. Computing it as the midpoint of `min × 10^decimals` and announcing it in the header makes a channel whose values are 3.6 decode as 3599. |
| `width` | 1, 2 or 4 bytes. **Every width must be handled in the data loop**; see below. |
| `n_data` vs `freq` | Must agree, or MoTeC reads the channel at the wrong instants — indistinguishable, in the lap table, from a channel carrying no lap information. |

### 2.2 Element width is the bug that hid everything

MoTeC declares 1-byte elements for flag channels (`Current Sector`, `Sector Flag 1/2/3`,
`Surface Type`, `Wheel Detached`, the flap channels) — 19 of ours. A writer with branches
for 2- and 4-byte elements and none for 1-byte emits every such sample as an `int32`:

```
3 channels × 182,897 samples = 548,691 bytes of overrun
```

Every block after the first 1-byte channel is displaced, so later channels read whatever
data had shifted into them. `Ground Speed` showed 15,000 km/h.

**Why this survived so long:** the quantiser round-trips perfectly in an isolated
single-channel test, because a one-channel file contains no 1-byte channel. Only converting
the whole set exposes it.

### 2.3 Quantisation is per channel ID

Both genuine logs agree on the (dtype, width, shift, multiplier, scale, decimals) triple
for all 238 IDs they contain. That table is transcribed in `LdWriter.QuantById`
and keyed by the standard ID — **not by name**, since aliases mean the same channel can
arrive under two spellings.

---

## 3. Standard channel IDs

`0x10` is what makes a channel a *known* channel to MoTeC. A channel whose ID is unknown
to MoTeC may still appear in the data menu, but it will not drive lap logic or appear in
standard reports.

### 3.1 Confirmed in i2 (Oct 2026): the ID is a semantic key, not a filter

The sentence above was a guess for a long time. It has now been measured, with
single-variable test files (each byte-length identical to its base, differing only in the
`0x20` name or the `0x10` id). What i2 actually does:

| test | result |
|---|---|
| rename a channel, keep its standard ID | i2 shows the **file's** name; the database name is not used |
| set an id to **8300**, then to **12001** (`0x2EE1`), name untouched | the channel is **still listed and named** — no id range is rejected |
| rename the lap channels (`Session Elapsed Time`, `Lap Distance`, `Lap Number`) | lap count and lap times **unchanged** — the lap system is keyed by **ID** |
| rename `Ground Speed` or `Lap Distance` | i2's **built-in maths** read them as `(Unavailable)` — the maths are keyed by **name** |
| rename `Beacon` | **no effect** — i2 owns that channel's name |

So: **names are free, IDs are not** — but only for the channels something else consumes.
The channels i2's maths and lap/sector machinery resolve by name are the keep-set in
`ChannelIds.KeepStandardNames`; everything else may carry the game's own name.

This also settles the old report that writing `0x2EE1 + i` produced "no channels at all".
An id of `0x2EE1` is listed fine — the cause was the zeroed `0x08` pointer (§1.2), a
separate bug from the same era, and the claim that i2 discards unrecognised ids is wrong.

Two published `.ld` writers confirm the same thing from the outside:
`stevendaniluk/MotecLogGenerator` and `riccardotornesello/motecldparser` both write every
channel under `0x2EE1+n` with arbitrary names, and both are documented as i2-openable.

**238 IDs** were transcribed from the two genuine logs, with their short names and rates.
They live in `ChannelIds.cs` (`Standard`, `ShortNames`, `RateById`).

Real MoTeC files reuse IDs — `Throttle Pos` and `Battery Charge Level` both take 301;
`Engine RPM`, `Motor RPM` and `Delta Best` all take 5. That is MoTeC's own behaviour, not a
bug, but it means an ID is not always unique.

Synthetic IDs (for channels MoTeC has no standard for) start **above the highest standard
ID**, currently 8211. A hand-picked base like 8000 would collide with real MoTeC channels.

---

## 4. Sample rates

`freq` at `0x16` is a plain `uint16` and `n_data` a plain `uint32` — **nothing binds them to
a fixed table.** Any rate the format can express is structurally valid. This was asserted
without evidence for several releases and was wrong.

Evidence it is a choice, not a limit:

- The genuine logs use **nine rates**: 1, 2, 4, 5, 10, 20, 25, 50, 100 Hz.
- **34 channels run at 100 Hz**, including `Susp Pos FL/FR/RL/RR`, `Camber FL..RR` and
  `Vertical Tyre Deflection FL..RR`.
- The genuine log writes `Throttle Pos` at 10 Hz where the transcribed table says 2 Hz —
  so even "MoTeC's rate" is not a ceiling.

DamPlugin exposes this as a user setting: `Data rate = 1..5`, where *5* is documented as
*"100Hz all channels"* and *"the various channels are logged at different rates, except for
= 5"*.

MoTeC's own rate for a channel is therefore a **default**, and the converter offers four
policies (`RatePolicy`): `Source` (everything the game recorded), `Optimised` (the
default), `Reduced` (one user ceiling), and `Pro` (per-channel). No mode may exceed the
rate the game recorded — upsampling invents detail that was never captured.

---

## 5. Laps — the hard part

### 5.1 What MoTeC reads

MoTeC derives laps from **`Beacon`, standard ID 110**. Not from `Lap Number`, not from
`Lap Start Elapsed Time`, and not from anything in the `.ldx`. Zero the Beacon and i2 finds
no laps at all — a single session-long segment labelled *"No Lap"* — which is how that was
proved, in both a genuine log and ours.

`Lap Number` (2101), `Lap Start Elapsed Time` (2102), `Session Elapsed Time` (2100) and
`Current Sector` (2103) are all necessary — they carry the lap *times* and the sector state
— but none of them alone makes MoTeC render a lap.

**The Beacon supplies the crossings; the ldHead decides how many of them are laps.** Those
are two separate things, and conflating them cost the longest hunt in the project. A header
carrying our old constants (§1.2) turned a 4-lap session into **13 laps — one per crossing**
— while the Beacon in that same file was byte-identical to the genuine log's: same pulse
times, same counters, same `100`/`56` settles, same 14 pulses. Every Beacon and lap-channel
check passed the whole time. Transplanting a genuine ldHead into our file *in place* fixed
the count; bisecting the fields afterwards produced the set in §1.2.

So when laps are wrong, the header is as much a suspect as the Beacon, and it is the
cheaper one to test: it is 22 differing bytes in the first 1762.

### 5.2 The Beacon channel

Transcribed from 31 pulses across the two genuine logs. At **5 Hz**:

```
three samples per crossing, then the channel settles:

  sample 0   -8192              a constant marker, identical in both references
  sample 1   counter            starts at -32767, +1 per pulse, +2 after a sector rise
  sample 2   payload            16421..17382, NOT derivable — see 5.5
  then       100 after a lap line, 56 after a sector split
```

Before the first pulse the channel holds **0**.

**The settle value is the discriminator.** 100 means a lap boundary, 56 a sector split.
Unambiguous across all 31 pulses.

The counter's step sequence — `+1, +2, +1, +1, +2, +1, +1, +2, +1, +1, +2, +1, +1` — steps
by two immediately after a sector *rise*.

### 5.3 Where the pulses go

Verified against the DuckDB's own events rather than assumed. For the paired session, with
its 92.71 s offset, **all 13 of our pulses land on a genuine pulse to within 0.20 s and
every one settles on the right kind**:

```
genuine 131.40 100   164.00  56   198.20  56   217.00 100   249.60  56   283.20  56
ours    131.60 100   164.20  56   198.20  56   217.20 100   249.60  56   283.40  56
        302.40 100   334.80  56   368.40  56   387.20 100   419.20  56   452.80  56
        302.40 100   334.80  56   368.60  56   387.20 100   419.40  56   453.00  56
                                                                  471.80 100 / 472.00 100
```

The genuine log has a 14th pulse at 504.20 s — lap five's second sector split. Our DuckDB
does not record it because the session ends first. That difference is in the source.

**The out-lap contributes nothing** — neither its line at 92.71 s nor its sector splits.
That matches the `.ldx`, which does not count the out-lap as completed.

### 5.4 Two traps in building it

**The pulse must be built on the channel's own grid.** It is three samples of the 5 Hz
grid — 0.6 s of wall time. Built on the 100 Hz resample grid and decimated, it vanishes:
decimation keeps every twentieth sample, so a pulse 0.03 s wide is never sampled at all.
Such channels are exempt from the decimation step.

**A sample before the next pulse must hold the *previous* settle value, not the next
pulse's.** Computing a slot index before the pulse gives a negative number, which falls
through to the settle arm and makes the channel read as though the first pulse had already
happened. That inverts every lap/sector flag after it — the genuine `100,56,56` pattern
comes out as `56,56,100`.

### 5.5 The payload is the crossing's sub-second time

**Solved at v2.20.0.** The pulse's third sample is not an RF artefact. It is the crossing's
sub-second time in milliseconds, offset by `0x4000`:

    payload = 0x4000 + round(1000 * frac(t))

where `t` is the crossing's time on the file's own time axis — the Beacon channel's first
sample is 0, so `t` is the session time minus the session's start offset.

Verified exactly against the genuine log's five lap-line crossings, whose times are known to
the millisecond from its own displayed lap times:

| Crossing time t | `0x4000 + round(1000·frac t)` | Genuine payload |
|---|---|---|
| 131.302 | 16686 | 16686 |
| 216.986 | 17370 | 17370 |
| 302.263 | 16647 | 16647 |
| 387.049 | 16433 | 16433 |
| 471.757 | 17141 | 17141 |

All fourteen crossings agree to within the 0.02 s at which crossing times can be read from
the log's `Current Sector` channel.

Our own output reproduces the genuine log's four timed laps at the payload's own resolution:
85.684 / 85.277 / 84.786 / **84.707** against 85.684 / 85.277 / 84.786 / **84.708**. The last
lap is 1 ms out, which is the granularity the field stores — integer milliseconds. A lap time
is a difference of two of them, so it inherits that bound; nothing finer is expressible.

**Why it was missed for so long.** The value correlates with crossing time only once that
time is taken modulo one second, and the earlier searches — including the entire "ruled out"
list that stood here before — correlated against crossing times quantised to the 0.2 s
Beacon sample grid. That quantisation discards precisely the sub-second information the
field carries, so every candidate, `frac(t)` included, came back uncorrelated. The genuine
sub-second sits *between* samples; the payload is how the logger records where.

**What it costs to get wrong.** A constant payload deprives MoTeC of its sub-sample
resolution, so lap times collapse to whole seconds. It was held at `16900`, the midpoint of
the observed range, from v2.18.0 through v2.19.0 — which is why those builds showed
plausible lap *counts* with useless lap *times*.

The generated file is missing the 14th pulse (the last sector split of lap 5) because the
session ends before that crossing in the source data.

### 5.6 Timing is absolute session time

Lap channels carry **absolute session seconds**, not time since the first sample. The
paired session's telemetry starts at 92.71 s; a genuine log's `Session Elapsed Time`
starts at 93.0 and its `Lap Start Elapsed Time` steps through 0, 224, 310, 395, 480, 565.
A version that started at 0 put every lap boundary 92.71 s out and the trailing in-lap
absorbed the whole session.

`Lap Start Elapsed Time` holds **0 until the first crossing** — that leading 0 is "no lap
yet", not a 92.71 s segment.

#### The Out Lap is measured from the log's own first sample

Our Out Lap reads ~0.13 s longer than the genuine log's (`2:11.430` against `2:11.302`).
That is a difference in the log *window*, not in the timing:

| | span | last sample |
|---|---|---|
| genuine DAMPlugin log | 557.46 s | session 650.0 |
| our conversion | 557.60 s | session 650.0 |

Both logs end at the same point; the DuckDB's telemetry begins marginally earlier, so our
file contains slightly more of the session, and MoTeC — which measures the Out Lap from its
own log's first sample — reports a correspondingly longer one. The four timed laps are
differences between crossings and are unaffected: they match to the millisecond (§5.5).

**Related, and better explained by §5.7.** `Session Elapsed Time` opens at `92` in our file
and `93` in the genuine log. An earlier note here guessed a one-LSB quantiser difference; the
more likely reading is the ~0.2 s phase difference that runs through every channel, reaching
a rounding boundary — ours sits at 92.7 and the genuine log's near 93. Note that the pair
cannot be reconciled by a single rounding rule, which is itself evidence that the two logs'
session clocks differ slightly rather than that our quantiser is wrong.

### 5.7 Why the Section Times report can still differ

v2.20.0 gets the lap count and the lap times right, but MoTeC's **Section Times** report — the
per-section breakdown of a lap into `Str 0-1`, `Turn 1`, … — still differs slightly from a
DAMPlugin log of the same session: **0.02–0.07 s per section**, while the lap **totals** match
to 1 ms.

The totals are Beacon crossings and they are correct. The section rows are whatever MoTeC's
track model defines, located positionally. Comparing all three distance curves against the
DuckDB, which holds the game's own value:

| curve | best fit to the DuckDB | rms |
|---|---|---|
| our `Lap Distance` | `ours[i] ≈ DuckDB[2i]` — no offset | 5.4 m |
| the genuine log's `Lap Distance` | `genuine[i] ≈ DuckDB[2i+3]` — ~0.3 s | 28.3 m |

**Ours is faithful to the game; DamPlugin's is a different curve.** Its `Lap Distance` is a
different distance model — ~0.3 s out of phase with the game's own and ~28 m rms away from it.
Every channel in our output tracks the DuckDB the same way, and every channel sits ~0.2 s
from the genuine log's uniformly, which is DamPlugin's phase, not ours.

So matching the Section Times report exactly would mean reproducing DamPlugin's distance model
rather than the game's, and that is not derivable from the DuckDB. The part genuinely worth
fixing is the 5.4 m: our resampler smears its source when going from the DuckDB's 10 Hz to
5 Hz, where sampling at the target instants would not.

Diagnostics (`diag2_lapdist_ref.ld` and `diag2_gps_ref.ld`) are the v2.20 output with the
genuine log's `Lap Distance`, and its `GPS Latitude`/`Longitude`, transplanted in — values
verified present on all 2787 samples, size and structure unchanged.
If the section times match after one of those, that channel is the one MoTeC locates sections
by.

Note from building them: **the genuine log's GPS channels are dead.** `GPS Latitude` holds a
sentinel of raw `-99963931` — "no data" — for the whole session, and its Section Times are
correct anyway. So MoTeC does not need GPS to place sections, which makes the GPS file a
sharper test than intended: if our sections come right once our valid (and possibly wrong)
GPS is replaced by that sentinel, MoTeC was reading our GPS.

A first pair of these diagnostics was built with source and destination offsets swapped, so
nothing was transplanted and the files were our output with unrelated bytes overwritten — and
i2 opened them and drew the sections anyway, which is why the mistake was invisible there.
**Verify a transplant by decoding the target channel back out, not by checking the file size.**

### 5.8 Open doubt: does i2 reflect .ld channel edits in this report at all?

Both rounds of .ld-channel transplants — the genuine `Lap Distance`, and the genuine GPS —
produced Section Times indistinguishable from our unmodified output. Either those channels are
not what MoTeC locates sections by, or **i2 did not recompute its section analysis for the
edited files**. Until that is settled, further .ld-level transplants prove nothing. The
cheapest way to settle it is a deliberately absurd value in the suspect channel: if the report
does not change even then, the report is not being recomputed from the file.

### 5.9 What the .ldx does and does not carry

Ours is 612 bytes of details. The genuine log's is 9888 bytes, and its extra content is ~140
`_Setup_*` car-setup values — **not** a section or track model. So the section model does not
live in the .ldx, and a .ldx swap cannot explain the report difference.

Two things in ours are wrong regardless:

- **`Total Laps` reads 6** where the genuine reads 4. i2 renders 4 correctly from the .ld, so
  this is metadata rather than the rendered count — but it is inconsistent with our own output
  and should be fixed. Note the counting comment in `LdxWriter`: the filter excludes the
  out-lap and the in-lap, so 6 is not what that filter was written to produce.
- **`GPS Latitude`/`Longitude` hold implausible values** — about 60.00 and −0.00, where Laguna
  Seca is 36.58, −121.75. The genuine log's hold a `-99963931` sentinel: it does not log GPS at
  all, and its sections are correct. Ours are valid-looking and wrong, which is the worst of
  the three possibilities if MoTeC uses them.

### 5.10 The header metadata fields (what i2 shows in its banner)

i2's session banner comes from the `.ld` header, and two of its fields were wrong.

**The date must use a literal slash.** `0x5E` holds `DD/MM/YYYY` and `0x7E` holds
`HH:MM:SS`, 16 bytes each, exactly as the genuine log has them. Writing them with a .NET
custom format string and no culture is a trap: in `"dd/MM/yyyy"` the `/` means *the culture's
date separator*, so a German-locale Windows wrote `01.10.2026` where the genuine has
`01/10/2026` — and i2 renders the whole thing as **"Invalid DateTime"**. `CultureInfo.InvariantCulture`
is what keeps the slash a slash. There is a regression test pinned under `de-DE`.

**The time must be local.** The DuckDB records the session start in UTC (its filename ends in
`Z`), and the genuine log shows local — `13:22:50` against `15:22:51` for the same session.
Convert with `ToLocalTime()`. Note the honest limit: that uses the *converting* machine's
current offset, so a session logged on the other side of a daylight-saving change is off by the
hour, since the DuckDB does not record the offset in force at the time.

**The car name must not carry the game's raw class suffix.** The `ldVehicle` block (260
bytes) holds the car name, and **MoTeC looks that name up in its own database to label the
session**: the genuine log carries `Proton Competition 2025 #88:LM` and i2 shows "GT3"
beside the driver. This project folds the class into `Vehicle` as `"<name> (<class>)"` for
its own `.svm` and `.ldx` use, and writing that folded *raw* string here stopped the lookup
matching — our banner showed no model at all.

**Phase 1 (2026-10-14) changed the banner deliberately.** `BannerCarName` now writes the
**canonical** class label — `Toyota Racing 2026 #7:LM (Hypercar)` — into `ldVehicle`,
per the maintainer's request to show the class next to the driver's name. The game's raw
suffix (`…:LM (GT3)`) is still stripped; the `.svm` keeps the game's own class id. The old
rule said *any* suffix defeated i2's car-database lookup — **whether the canonical label
survives it is unverified in i2 and needs one look** (does the model still show beside the
driver?). If i2's lookup fails again, the canonical label moves to the `.ldx` only and the
`.ld` `ldVehicle` goes back to the bare name.

Do **not** put the vehicle in the `0xDE` metadata slot: an earlier attempt did, and MoTeC
rendered "unknown characters" immediately after the driver name in its data menu.

### 5.11 Channel sample rates are per ID, and an ID can be shared

Every channel declares its own rate (header `0x16`) and MoTeC reads it at that rate. The rate
is looked up by the channel's standard ID (§3), so **an ID shared by two channels can only
carry one rate, and the fast channel must win.**

Three IDs were held at a *slower* rate than the genuine logs use, on an earlier reading that
two logs disagreed and the lower rate was the safe choice. Re-checked against all three
genuine logs in the tree, they agree:

| id | channels | genuine | we wrote |
|---|---|---|---|
| 5 | Delta Best / Engine RPM / Motor RPM | 10 Hz | 5 |
| 301 | Battery Charge Level / Throttle Pos | 10 Hz | 2 |
| 2110 | Motor State / PitStatus | 25 Hz | 5 |

Throttle Pos is the one that showed: the game records it at 50 Hz and MoTeC logs it at 10,
and at 2 Hz we threw away 96% of the signal — it drew as a smooth weave beside the genuine
log's straight line down the straights. Engine RPM was halved the same way. A denser grid does
not harm the slower sharer, because the rate is a per-channel choice and not a limit.

**A self-consistent rate can still be wrong — this is the trap the structural checks miss.**
They assert `n_data == rate × span`, and a wrong rate satisfies that perfectly. Only a
comparison against a genuine log catches it (§8). Nine channels still differ from the genuine
log for a legitimate reason: the DuckDB records them slower than DamPlugin's plugin does
(Ambient and Track Temperature 1 Hz against 10, Wind Heading/Speed 1 against 2, Time Behind
Next 2 against 5, the three G-forces 10 against 25). The source is the ceiling — upsampling
would invent detail the game never captured.

### 5.12 The game's "Unfiltered" channel is the one MoTeC logs

The DuckDB offers each pedal and steering input twice, as `<X>` and `<X> Unfiltered`, and the
rename table sent `"<X> Unfiltered" → "<X> Filtered"`. **That is backwards.** The game's plain
channel is the *processed* one; its frequency content has been through a filter, and `Unfiltered`
is the raw axis. MoTeC's logs carry the raw one under the plain name.

Confirmed by how much of the session each channel sits pinned at its own maximum, against the
genuine log:

| channel | genuine log | DuckDB plain | DuckDB Unfiltered |
|---|---|---|---|
| `Throttle Pos` | 57.4% | 0.4% | **57.4%** |
| `Brake Pos` | 1.7% | 0.0% | **1.7%** |
| `Clutch Pos` | 100.0% | 4.3% | **100.0%** |

`Steering` is not recorded by the game at all, so that pair is moot, but the mapping is now
consistent for all four.

**This is what made the throttle weave.** The processed signal never quite reaches 100% and
dithers across the top, so feeding MoTeC's `Throttle Pos` from it drew a smooth weave where the
genuine log runs flat down the straights. After swapping, our `Throttle Pos` tracks the genuine's
with an rms of 0.59 percentage points at a −0.2 s offset, where the mismatch had been ~62.

Note the earlier fix in §5.11 was **not** the cause of this symptom. Throttle Pos at 2 Hz was a
real resolution loss (96% of a 50 Hz recording discarded) and worth fixing on its own merits, but
the weave came from the source mapping, not the rate.

### 5.13 The track report, and the start/finish line

MoTeC's track report draws the circuit **from the log's own data** — lap distance, lateral
path — but it does **not** take the start/finish line or the section boundaries from the log.
Those come from i2's own track model. For a track i2 has no model of, the circuit is drawn
correctly while the line sits somewhere it should not, which throws every section off with it.

**The fix is in i2, not in the `.ld`: Track Editor → Generate Track.** Once per track, a
couple of seconds. Confirmed by the user on Imola. Lap count and lap times are correct either
way — they do not depend on the report.

Three candidates were ruled out with evidence before concluding this:

- **The GPS channels are not used.** Both hold nonsense in our output (latitude a constant
  60.00, longitude near 0) — but they hold nonsense in MoTeC's own DAMPlugin log too (latitude
  ~0.0036, longitude a constant −135.0). i2 cannot be placing the line from position data, or
  MoTeC's own report would be wrong as well.
- **`Lap Distance` is not offset.** Ours reads 29 m at the first lap crossing and MoTeC's 34 m,
  within 5 m. They differ at the start of the log (ours opens at 0 m, MoTeC's at −234 m, the
  out-lap) but converge by the line, so there is no gross shift to correct.
- **The `ldVenue` block carries no geometry.** MoTeC's and ours are both the track name
  followed by zeros, so there is nothing we are failing to write. `TrackLengthMeters` exists in
  the metadata model and is never populated — a dead field rather than the cause, and the
  DuckDB has no track length to populate it with (only `TrackName` and `TrackLayout`).

---

## 6. The `.ldx`

MoTeC **rewrites the `.ldx` after opening a file**. The generated `.ldx` is therefore an
input, not a result: nothing in it controls what MoTeC renders.

The genuine `.ldx` is a details-only XML file: `<LDXFile>` → `<Layers>` → `<Details>` →
a flat list of `<String>` and `<Numeric>` entries. **It contains no marker layer and no
`RangeBlock`.** Several releases emitted an invented marker block and it had no effect
whatsoever.

The only detail entries a non-MoTeC writer can meaningfully produce are the three strings:

```
Total Laps      4
Fastest Time    1:24.708
Fastest Lap     4
```

Everything else in the genuine file is `_Setup_*` numerics reflecting the car's setup —
which the `.svm` export covers instead.

`Fastest Lap` is a **lap number**, not a timestamp. For the paired session: `Total Laps = 4`,
`Fastest Lap = 4`, `Fastest Time = 1:24.708`.

Layout: no BOM, CRLF line endings.

---

## 7. What is still a guess

- **The `0x52`/`0x54`/`0x56` constants and `0x42`/`0x44`/`0x46`.** Constant across all three
  genuine logs and now written as such, but *why* MoTeC wants those particular values is not
  understood. The Beacon payload (§5.5) and the header constants (§1.2) were guesses and are
  not any more.
- **Why MoTeC accepts an arbitrary rate.** Structurally valid and demonstrated at 100 Hz,
  but MoTeC's rendering of a rate it would not itself choose is untested.
- **Whether MoTeC reads `PitStatus`** (ID 2110). Present in both genuine logs and absent
  from ours; it steps 5 → 0 → 2 → 4 in step with `In Pits`. Probably cosmetic.
- **Most claims about rendering.** i2 runs only on Windows, so most statements here are about
  bytes rather than about what i2 draws. Two things are now confirmed *in i2* by the user:
  the lap-count fix (§1.2) renders 4 laps, and the computed payload (§5.5) gives
  millisecond lap times. The four lap times also match the genuine log to the millisecond
  when read back out of the generated file (§8), which needs no MoTeC at all.

---

## 8. Reproducing the checks

`dotnet test` covers what can be asserted without sample data: the channel list and its data
offsets, `first_data + Σ(n_data × width) == file_size`, the per-channel quantisation fields,
the Beacon pulse structure, and — since v2.20.0 — the ten ldHead constants MoTeC takes the
lap count from.

The rest need a real session recording. Assert them from raw
bytes, taking the expected layout from ldparser rather than from our own writer:

- every channel's `n_data` agrees with its `freq` and the session span
- `first_data + Σ(n_data × width) == file_size`, exactly
- data blocks do not overlap
- `scale`, ID, short name and unit match the reference on all shared channels
- the Beacon pulse count, rate, settle pattern, counter steps and zero before the first pulse
- the ten ldHead constants match all three genuine logs

### Three methods that settled the lap questions

Each of these turned a stalled hunt around, and each generalises.

**Transplant one thing, in place, into a file known to work.** Replacing our ldHead with the
genuine log's — byte length unchanged, only the two structural pointers restored — proved the
header was the cause in a single test, after far longer spent verifying everything that
*turned out not to be* the cause. The inverse earns its keep too: put our value into the
genuine log and see whether it breaks. Keep the edit byte-length-identical so only the named
variable changes.

**Read the value back out of the generated file.** Because the Beacon payload is the
crossing's sub-second time, crossing times can be recovered from our own `.ld` and compared
with the genuine log's directly — no MoTeC required. That is how the lap times were confirmed:
85.684 / 85.277 / 84.786 / 84.707 against 85.684 / 85.277 / 84.786 / 84.708.

**Correlate against a reference you trust, and mind what it is quantised to.** The payload
eluded every search until the reference times were taken from the genuine log's *displayed
lap times*. Earlier attempts used crossing times snapped to the 0.2 s Beacon sample grid —
which discards precisely the sub-second quantity being measured, guaranteeing the null result
they found. A "not derivable" conclusion is only as good as the resolution of its reference.

Related, and cheap to get wrong: the DuckDB's `Lap` table sits on the game's 10 ms tick,
while its `Lap Time` table holds the same laps at full float precision. Lap *precision* comes
from the latter.

Run them before trusting any change to the writer.

---

## 9. Building an `.ld` from your own data

This section is the recipe. The rest of the document is the evidence behind it. It covers
the pipeline in general terms, then how Duckmetry fills each step from DuckDB recordings,
then what changes if your data comes from somewhere else.

### 9.1 The pipeline, in five steps

Every `.ld` is the same shape (see §1): one header, a linked list of channel headers, and
one data block per channel, in list order. Building one is:

1. **Collect the session metadata** — venue (track), driver, date, session type, time.
   i2 shows these in its banner (§5.10); get them right once and the session presents
   itself properly.
2. **Collect the channels** — for each channel: a name, an ID, a sample rate, units, a
   quantisation, and the samples. The header has room for all of it (the channel header
   is 124 bytes; §1.3).
3. **Resolve each channel's ID** — the ID is the semantic key i2 resolves the channel by
   (§3.1): the same ID means the same thing across sessions, and i2's built-in maths and
   lap machinery key off specific IDs. If your channel matches a known channel (the
   standard ID table in §3), use its ID. If it does not, allocate a **synthetic ID**
   (Duckmetry reserves the 8212–8250 range) — the channel still displays and plots; it
   just has no built-in special treatment. Never reuse a standard ID for something
   different, and never put a synthetic ID on a channel the lap machinery resolves by
   name (the Sector Flag channels, the Beacon, Lap Distance — these carry fixed standard
   IDs for a reason).
4. **Write the data blocks** — in the same order as the channel list, each packed per the
   channel's quantisation (element width, scale, offset, decimals — §2). The single
   invariant that catches most mistakes:
   `first_data + Σ(n_data × width) == file_size` (§8). If it does not hold, i2 will not
   read the file.
5. **Decide the sample rates** — per channel, any rate from 1 to 100 Hz, declared in the
   channel header. The one hard rule is **never write a channel faster than your source
   recorded it** — upsampling invents detail that was never captured (§4, and
   `docs/SAMPLING.md`).

Lap data is the part that costs the most to get right and is entirely optional for a file
to be *readable*: without a Beacon channel there are no laps. If you want laps, §5 is the
full account — the short version is: emit a 100 Hz Beacon channel, let it free-run,
write a pulse (the byte that flips) on each start/finish crossing, and make each pulse's
payload the crossing's sub-second time (§5.5). The `.ldx` sidecar (§6) then carries the
lap markers i2 displays.

### 9.2 From a DuckDB recording (what Duckmetry does)

A DuckDB telemetry file (the game's recorder output) is, essentially, the same data in a
different envelope — which is the whole reason the converter exists. The mapping:

| `.ld` needs | DuckDB provides |
|---|---|
| session metadata | the `metadata` table: venue, car, driver, session type, date/time, weather, car class |
| channel names + rates | the `channelsList` table: one row per channel, `channelName`, `frequency`, `unit` |
| samples | one table per channel, named after the channel, one row per sample |
| car setup | the `settings` table → written as the `.svm` the game reads back |

The discovery is **dynamic**: the converter asks the file itself
(`SELECT channelName, frequency FROM channelsList`) and then reads whatever tables exist.
A channel the game adds in a future update needs no converter change — it appears in
`channelsList`, gets read, and lands in the `.ld`. The only two points where the game's
vocabulary meets the software's fixed vocabulary are the channel **ID** (step 3 above:
known names resolve to standard IDs via the mapping table; unknown ones get a synthetic
ID) and the **display name** (the file can carry either the game's own spelling or the
standard spelling — Duckmetry defaults to the game's, see `UseDuckDbChannelNames` in
`docs/SAMPLING.md`).

The sample-rate ceiling deserves its own note: **what the game recorded is what the
player asked for.** The game records channels at the rates named in its own telemetry
`settings.json` (in the game's `UserData/Telemetry` folder, right next to the recordings)
— that file lists the channels, their names and their rates, and the player edits it
directly. The DuckDB then contains exactly those rates, and those rates are the ceiling
no mode may exceed (§4).

### 9.3 From any other data

The same five steps apply; only step 2's source changes. Whatever your data is — CSV,
a database, another simulator's telemetry — you need per channel: name, sample rate,
units, quantisation (or a sensible default: i2 accepts many combinations; §2 explains
which four fields matter and what breaks if they are inconsistent), and the samples.
Everything else is copy the recipe:

- Build the header with the constants the genuine logs agree on (§1.2), and the two
  structural pointers (first channel list entry, first data block).
- Write the channel list, then the data blocks in the same order.
- Check `first_data + Σ(n_data × width) == file_size` before anything else.
- Name the file the way you want it to sort: `<date>-<time> - <venue> - <session>` is a
  convention that sorts chronologically.
- **Verify against a real file before shipping anything.** The single most effective
  method in this project's history: take a known-good file, transplant one byte region at
  a time from your new output, and see what breaks (§8). A converter that produces
  *correct* output is not the same as one that understands the format — the tests in §8
  are how you tell the difference.