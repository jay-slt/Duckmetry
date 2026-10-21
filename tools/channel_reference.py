#!/usr/bin/env python3
"""Generate the channel reference from evidence, and audit the mapping.

Read-only. Two sources of truth are joined:
  * the genuine DAMPlugin logs - what i2 channels exist, and each one's identity
    (name, standard ID, short name, unit, rate, width)
  * the sample DuckDB - what data the game actually records

A third input, a real converted .ld, supplies what we currently emit. It is
optional, but without it the "we emit" column is inference rather than fact, and
the document says so.

    python3 tools/channel_reference.py --converted <converted.ld>

Linking a DuckDB table to its i2 channel is done by normalised keyword match,
not by reimplementing the naming rules - those live in ChannelDictionary.cs and
ChannelIds.cs, and a second copy of them here would drift out of step. Where
a link is inferred rather than read, it is marked.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys

import duckdb

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Channels that exist in i2 logs but that the game deactivates for legal reasons, so they
# are never written to the DuckDB. Called out in the generated doc so their absence
# reads as deliberate rather than as a gap waiting to be filled.
LEGALLY_DEACTIVATED = {
    "Drag",
    "Front Downforce",
    "Rear Downforce",
    "Front Wing Height",
}

# The only genuine DAMPlugin logs in the tree, confirmed by channel count
# (~245 against our 154). Our diagnostics are edits of these same sessions and
# must never be counted as evidence.
GENUINE = [
    "log+example/2026-08-03 - 20-38-46 - WeatherTech Raceway Laguna Seca - P1.ld",
    "log+example/Same data/Duckdb/"
    "2026-10-01 - 15-22-51 - WeatherTech Raceway Laguna Seca - P1.ld",
]
DB = ("log+example/Same data/Duckdb/"
      "WeatherTech Raceway Laguna Seca_P_2026-10-01T13_22_50Z.duckdb")

# Fields that describe a channel's IDENTITY. "samples" is excluded: it differs by
# session length on every channel and is not part of how i2 sees a channel.
IDENTITY = ("id", "short", "unit", "rate", "width")

# A table whose name differs from the channel it produces. Mirrors the wheel
# rules in ChannelDictionary.GetWheelChannelMetadata; kept explicit so a drift is
# visible rather than silent.
KEYWORD = {
    "brakesairtemp": "brakeairtemp", "brakestemp": "braketemp",
    "brakesforce": "brakeforce", "tyrespressure": "tyrepressure",
    "tyresrimtemp": "tyrerimtemp", "tyresrubbertemp": "tyrerubbertemp",
    "tyrescarcasstemp": "tyrecarcasstemp", "tyreswear": "tyrewear",
    "tyrestempcentre": "tyretemp", "tyrestempleft": "tyretemp",
    "tyrestempright": "tyretemp", "rideheights": "rideheight",
    "wheelsdetached": "wheeldetached", "surfacetypes": "surfacetype",
    "lapdist": "lapdistance", "soc": "batterychargelevel",
    "antistallactivated": "antistall", "tc": "tractioncontrol",
    "currentlaptime": "laptime", "lap time": "laptime",
    "bestlaptime": "bestlaptime", "lastlaptime": "lastlaptime",
    "brakeposunfiltered": "brakepos", "throttleposunfiltered": "throttlepos",
    "clutchposunfiltered": "clutchpos", "steeringposunfiltered": "steering",
    "steeringpos": "steering",
}


def norm(s):
    return re.sub(r"[^a-z0-9]", "", s.lower())


WHEELS = ("FL", "FR", "RL", "RR")


def read_ld(path):
    """Channel table from a i2 .ld."""
    d = open(path, "rb").read()
    first = struct.unpack_from("<I", d, 0x08)[0]
    chans, seen, ptr = {}, {None}, first
    while ptr and ptr not in seen and ptr + 124 <= len(d):
        seen.add(ptr)
        _, nxt = struct.unpack_from("<II", d, ptr)
        name = d[ptr + 0x20:ptr + 0x20 + 32].split(b"\x00")[0].decode("ascii", "replace")
        chans[name] = dict(
            id=struct.unpack_from("<H", d, ptr + 0x10)[0],
            width=struct.unpack_from("<H", d, ptr + 0x14)[0],
            rate=struct.unpack_from("<H", d, ptr + 0x16)[0],
            samples=struct.unpack_from("<I", d, ptr + 0x0C)[0],
            # 0x40 short name (8 bytes), 0x48 unit (8 bytes): a header whose unit
            # is "km/h" reads "Grd Spd\0km/h" across those two fields.
            short=d[ptr + 0x40:ptr + 0x48].split(b"\x00")[0].decode("ascii", "replace"),
            unit=d[ptr + 0x48:ptr + 0x50].split(b"\x00")[0].decode("ascii", "replace"),
        )
        ptr = nxt
    return chans


def genuine_dictionary():
    union, conflicts, used = {}, {}, []
    for rel in GENUINE:
        p = os.path.join(ROOT, rel)
        if not os.path.exists(p):
            # Tolerated, not fatal: the reference logs are the user's own data and may be
            # moved or trashed between runs. Skipping keeps the document regenerable; it
            # reappears automatically if the file is restored. The Sources line records
            # how many were actually read, and the cross-session check needs two.
            print(f"  [warn] genuine log not present, skipping: {rel}")
            continue
        chans = read_ld(p)
        used.append((rel, len(chans)))
        for name, meta in chans.items():
            if name not in union:
                union[name] = dict(meta)
                continue
            diff = {k: (union[name][k], meta[k]) for k in IDENTITY
                    if union[name][k] != meta[k]}
            if diff:
                conflicts.setdefault(name, {}).update(diff)
    if not union:
        sys.exit("no genuine DAMPlugin log found in the tree - cannot build the reference")
    if len(used) < 2:
        print(f"  [warn] only {len(used)} genuine log(s) present; "
              "the cross-session rate check has nothing to compare")
    return union, conflicts, used


def duckdb_tables():
    con = duckdb.connect(os.path.join(ROOT, DB), read_only=True)
    names = sorted(r[0] for r in con.execute("SHOW TABLES").fetchall())
    out = {}
    for t in names:
        cols = [c[0] for c in con.execute(f'DESCRIBE "{t}"').fetchall()]
        rows = con.execute(f'SELECT count(*) FROM "{t}"').fetchone()[0]
        span = None
        if any(c.lower() == "ts" for c in cols):
            lo, hi = con.execute(f'SELECT min(ts), max(ts) FROM "{t}"').fetchone()
            if lo is not None and hi is not None and hi > lo:
                span = hi - lo
        out[t] = dict(columns=cols, rows=rows, span=span,
                      wheel=len([c for c in cols if c.lower().startswith("value")]) >= 4)
    con.close()
    span = max((v["span"] or 0) for v in out.values())
    for v in out.values():
        v["rate"] = round(v["rows"] / span) if span else None
    return out, span


def parse_known_bases(path=os.path.join(
        ROOT, "src/Duckmetry.Core/Models/ChannelDictionary.cs")):
    """The DuckDB-name -> i2-name table, read from the shipping code."""
    text = open(path).read()
    bases = {}
    for m in re.finditer(
            r'\{\s*"([^"]+)"\s*,\s*\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]*)"'
            r'\s*,\s*([\d.]+)f\s*,\s*(\d+)\s*\)\s*\}', text):
        bases[m.group(1)] = dict(std=m.group(2), short=m.group(3),
                                 unit=m.group(4))
    if not bases:
        sys.exit(f"could not parse KnownBases in {path}")
    return bases


def parse_renames(path=os.path.join(
        ROOT, "src/Duckmetry.Core/Ld/ChannelIds.cs")):
    """DuckDB/the game spelling -> i2 name, read from the **Aliases block only**.

    The file also holds a ShortNames block whose entries look identical
    (`["Name"] = "value",`), and merging the two made "Brake Pos" resolve to its short
    name "Brake". Slicing the block out by name keeps the map to real renames.
    """
    text = open(path).read()
    start = text.index("Aliases = new")
    block = text[start:text.index("\n    };", start)]
    aliases = {m[0]: m[1]
               for m in re.findall(r'\["([^"]+)"\]\s*=\s*"([^"]+)"\s*,', block)}
    if not aliases:
        sys.exit(f"could not parse the Aliases block in {path}")
    return aliases


def link(table, info, emitted, genuine, bases, renames):
    """Which channels a DuckDB table corresponds to.

    Non-wheel tables go through the shipping name tables, but ChannelIds.cs
    holds several string->string maps (the DuckDB renames, the short names), so a
    rename is only accepted when its result is actually present in what we emit -
    otherwise "Brake Pos" would resolve to its short name "Brake".

    Wheel tables match by containment: their names carry a per-wheel suffix which
    may itself have a face, as in "Tyre Temp FL Outer".
    """
    if info["wheel"]:
        # Two strategies, unioned. Containment catches the table whose emitted
        # name is a singular form of it ("Brakes Air Temp" -> "Brake Air Temp FL").
        # The exact path catches the table whose per-wheel channels are renamed
        # ("Wheel Speed FL" -> "Wheel Rot Speed FL"), which containment cannot see.
        key = KEYWORD.get(norm(table), norm(table))
        em = {n for n in emitted if key in norm(n)}
        gm = {n for n in genuine if key in norm(n)}
        for w in WHEELS:
            for cand in (f"{table} {w}", renames.get(f"{table} {w}")):
                if not cand:
                    continue
                em |= {n for n in emitted if norm(n) == norm(cand)}
                gm |= {n for n in genuine if norm(n) == norm(cand)}
        return sorted(em), sorted(gm)

    base = bases.get(table)
    name = base["std"] if base else table.replace("_", " ").strip()
    # The alias comes FIRST: the shipping code maps a DuckDB name to its i2 name and
    # then applies the alias (ChannelIds.StandardName), so "Brake Pos" - the game's
    # PROCESSED pedal - ends up as "Brake Pos Filtered", and its "Unfiltered" table takes
    # the plain "Brake Pos". Trying the plain name first matched the wrong channel.
    candidates = [renames[name]] if name in renames else []
    candidates.append(name)
    for cand in candidates:
        em = [n for n in emitted if norm(n) == norm(cand)]
        gen = [n for n in genuine if norm(n) == norm(cand)]
        if em or gen:
            return em, gen
    return [], []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="docs/CHANNELS.md")
    ap.add_argument("--json", default="docs/channel-dictionary.json")
    ap.add_argument("--converted", default=None,
                    help="a converted .ld - ground truth for what we emit")
    args = ap.parse_args()

    genuine, conflicts, used = genuine_dictionary()
    tables, span = duckdb_tables()
    emitted_meta = read_ld(os.path.join(ROOT, args.converted)) if args.converted else {}
    emitted = set(emitted_meta)

    linked = {}
    bases, renames = parse_known_bases(), parse_renames()
    for t, info in tables.items():
        linked[t] = link(t, info, emitted, genuine, bases, renames)

    gen_used = {n for _, names in linked.values() for n in names}
    drop = [t for t, (e, _) in linked.items() if not e]
    unresolved = [t for t, (e, g) in linked.items() if e and not g]

    write_doc(os.path.join(ROOT, args.out), genuine, conflicts, used, tables,
              span, emitted_meta, linked, args.converted)
    write_json(os.path.join(ROOT, args.json), genuine, conflicts, tables, linked)

    print(f"genuine logs        : {len(used)} ({', '.join(str(n) for _, n in used)} ch)")
    print(f"genuine channels    : {len(genuine)}  (sessions differ on {len(conflicts)})")
    print(f"DuckDB tables       : {len(tables)}  (span {span:.1f}s)")
    if emitted:
        print(f"our emitted channels: {len(emitted)}")
        print(f"  of which real i2 channels (present in a genuine log): "
              f"{len(emitted & set(genuine))}")
        print(f"  not present in any genuine log: {len(emitted - set(genuine))}")
    print(f"tables with no emitted channel : {len(drop)}")
    print(f"tables emitted but with no genuine channel: {len(unresolved)}")
    print(f"written: {args.out}, {args.json}")


def write_doc(path, genuine, conflicts, used, tables, span, emitted_meta, linked, converted):
    emitted = set(emitted_meta)
    a = []
    w = a.append
    w("# Channel reference")
    w("")
    w("**Generated — do not edit by hand.** Regenerate with")
    w("`uv run --with duckdb python tools/channel_reference.py --converted <converted.ld>`.")
    w("")
    w("**Two sources, two different roles — do not confuse them.**")
    w("")
    w("- **The DAMPlugin logs are the REFERENCE, not a data source.** They are genuine")
    w("  i2 output for the same sessions, so they define which i2 channels exist and")
    w("  what identity each carries (id, short name, unit, rate, width). **No channel this")
    w("  converter emits comes from a DAMPlugin file.**")
    w("- **The DuckDB is the DATA SOURCE.** Every channel we emit is either read from a game")
    w("  DuckDB table or synthesised from the DuckDB's lap and sector events. Section 4")
    w("  lists which, channel by channel.")
    w("")
    w("Section 1 is the DAMPlugin channel set. Section 2 is the DuckDB tables and what each")
    w("becomes. Section 3 is the difference between them. Section 4 is every channel we")
    w("emit, with the DuckDB table it comes from.")
    w("")
    w("## Sources")
    w("")
    w(f"- DAMPlugin: {len(genuine)} distinct channels over {len(used)} genuine logs "
      f"({', '.join(str(n) for _, n in used)} channels each).")
    w(f"- DuckDB: {len(tables)} tables over a {span:.1f} s session.")
    if converted:
        w(f"- Our output: {len(emitted)} channels read from `{converted}`.")
    else:
        w("- Our output: **not supplied** — section 1's last column is blank and "
          "tables cannot be checked against what we emit.")
    w("")
    w("## 1. Every DAMPlugin channel")
    w("")
    w("| channel | id | short | unit | rate | width | we emit it |")
    w("|---|---|---|---|---|---|---|")
    for name in sorted(genuine):
        m = genuine[name]
        rate = f"{m['rate']}*" if name in conflicts else m["rate"]
        em = "yes" if name in emitted else ("**no**" if converted else "?")
        w(f"| {name} | {m['id']} | {m['short']} | {m['unit']} | {rate} "
          f"| {m['width']} | {em} |")
    w("")
    if conflicts:
        w("`*` the genuine sessions disagree on the rate, because the game recorded the")
        w("channel at different rates in each. i2's rate is a preference clamped by")
        w("what was recorded, not a constant:")
        w("")
        for name, diff in sorted(conflicts.items()):
            w(f"- `{name}`: " + "; ".join(f"{k} {v[0]} vs {v[1]}" for k, v in diff.items()))
        w("")
    w("## 2. Every DuckDB table")
    w("")
    w("| DuckDB table | columns | rows | ~rate | becomes | of which i2 channels |")
    w("|---|---|---|---|---|---|")
    for t, info in tables.items():
        em, gen = linked[t]
        cols = ", ".join(info["columns"])
        rate = f"{info['rate']} Hz" if info["rate"] else "—"
        shown = ", ".join(em) if em else "*nothing*"
        known = f"{len(gen)}/{len(em)}" if em else "—"
        w(f"| `{t}` | {cols} | {info['rows']} | {rate} | {shown} | {known} |")
    w("")
    w("`becomes` is matched on channel names, so it is inference, not proof of data")
    w("flow; `of which i2 channels` counts how many of those names appear in a")
    w("genuine log, i.e. have a real i2 identity rather than a synthetic one.")
    w("")
    w("## 3. Gaps")
    w("")
    drop = [t for t, (e, _) in linked.items() if not e]
    w(f"### 3.1 DuckDB tables producing no channel ({len(drop)})")
    w("")
    w("Data the game records that does not reach i2 at all. Some are consumed")
    w("internally instead (lap and sector tables drive the lap channels); the rest are")
    w("simply dropped.")
    w("")
    for t in drop:
        w(f"- `{t}` ({', '.join(tables[t]['columns'])}, {tables[t]['rows']} rows)")
    w("")
    gen_only = sorted(n for n in genuine if converted and n not in emitted)
    w(f"### 3.2 DAMPlugin channels we never produce ({len(gen_only)})")
    w("")
    w("Existing in genuine logs; nothing to map until the game records them.")
    w("")
    legal = [n for n in gen_only if n in LEGALLY_DEACTIVATED]
    if legal:
        w("**Some of these are deactivated for legal reasons and will never be recorded.**")
        w("the game does not put them in the DuckDB at all, so there is nothing to convert, and")
        w("their absence is deliberate rather than a mapping gap:")
        w("")
        for n in legal:
            w(f"- {n}")
        w("")
        w("A user who wants them calculates them in i2 with a maths channel. If the game")
        w("ever starts recording them they will map with no code change, because the names")
        w("above are i2's own.")
        w("")
    for n in gen_only:
        m = genuine[n]
        w(f"- {n} (id {m['id']}, {m['rate']} Hz, unit `{m['unit']}`, `{m['short']}`)")
    w("")
    w("## 4. Every channel we emit, and where its data comes from")
    w("")
    w(f"All {len(emitted_meta)} channels the converter writes, in emit order, with the DuckDB")
    w("table each is read from. A channel with no table is **synthesised** — built from the")
    w("game's lap and sector events, because the game records the events and not the channel.")
    w("")
    w("`i2-known` means the name carries a real Standard names ID (it appears in a genuine")
    w("log). Where it does not, i2 has no entry for the channel; it still displays, on a")
    w("synthetic ID above every real one.")
    w("")
    w("The `channel` column gives i2's name. In the **default build** the display name is")
    w("instead the DuckDB spelling of the source table (`Brakes Temp FL`), except for the")
    w("keep-set — `Beacon`, the GPS channels, `G Force Long`, `Ground Speed`, `Lap Distance`,")
    w("`Susp Pos *` and `Sector Flag *` — which keep i2's name because i2's built-in maths")
    w("and lap/sector machinery look them up by name.")
    w("")
    w("| # | channel | id | i2-known | data source (DuckDB table) |")
    w("|---:|---|---:|---|---|")
    by_source = {}
    for t, (em, _gen) in linked.items():
        for n in em:
            by_source.setdefault(n, []).append(t)
    for i, (name, m) in enumerate(emitted_meta.items(), 1):
        srcs = sorted(by_source.get(name, []))
        src = ", ".join(f"`{t}`" for t in srcs) if srcs else "*synthesised (lap/sector events)*"
        known = "yes" if name in genuine else "no"
        w(f"| {i} | {name} | {m['id']} | {known} | {src} |")
    w("")
    open(path, "w").write("\n".join(a) + "\n")


def write_json(path, genuine, conflicts, tables, linked):
    json.dump(dict(
        genuine=genuine, conflicts=conflicts,
        duckdb_tables={t: dict(**{k: v for k, v in i.items() if k != "span"},
                               emitted=linked[t][0], std=linked[t][1])
                       for t, i in tables.items()},
    ), open(path, "w"), indent=1, sort_keys=True)


if __name__ == "__main__":
    main()
