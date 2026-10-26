#!/usr/bin/env python3
"""
channel_audit.py — check that every channel's values match its declared unit.

Usage:
    python3 channel_audit.py <session.duckdb> [--path-to-dict src/Duckmetry.Core/Models/ChannelDictionary.cs]

For every channel in a DuckDB recording this tool prints the declared unit, the
effective conversion the converter will apply (from the channel dictionary), the raw
value range, and the range as it will appear in the .ld. Channels whose projected
values are implausible for their declared unit are flagged as SUSPECT — e.g. the
Regen Rate case (values in watts, unit declared kW) would be caught here before a
report. Run it on a fresh recording after a game update: any new channel with a
unit/value mismatch shows up in seconds.

The dictionary is parsed straight out of ChannelDictionary.cs so the tool can never
drift from the converter. "Preparing for new channels" is exactly the point: an
unknown channel with an implausible range is listed, not silently passed.
"""
import re
import sys

# ------------------------------------------------------------------ error handling
def die(msg):
    print(f"error: {msg}")
    sys.exit(1)

# ------------------------------------------------------------------ plau вability bands (projected .ld values)
# Generous bands; a unit outside them in either direction is flagged.
BANDS = {
    "km/h": (0.0, 500.0), "mph": (0.0, 300.0), "m/s": (0.0, 150.0),
    "rpm": (-100.0, 30000.0), "RPM": (-100.0, 30000.0),
    "%": (-110.0, 110.0),
    "C": (-80.0, 1400.0), "K": (0.0, 1500.0),
    "bar": (0.0, 40.0), "Pa": (0.0, 3e6), "kPa": (0.0, 4000.0), "hPa": (800.0, 1200.0),
    "N": (-1e5, 1e5), "kN": (-1000.0, 1000.0),
    "N.m": (-2e4, 2e4), "Nm": (-2e4, 2e4),
    "kW": (-3000.0, 3000.0), "W": (-5e5, 5e5), "MW": (-5.0, 5.0),
    "A": (-5000.0, 5000.0), "V": (0.0, 2000.0),
    "L": (0.0, 500.0), "l": (0.0, 500.0), "kg": (0.0, 5000.0), "g": (0.0, 10000.0), "kg/h": (0.0, 1e5),
    "G": (-12.0, 12.0),
    "deg": (-360.0, 720.0), "rad": (-15.0, 15.0), "rad/s": (-300.0, 300.0),
    "m": (-100.0, 100000.0), "mm": (-100.0, 5000.0), "cm": (-10.0, 500.0), "km": (0.0, 1000.0),
    "s": (0.0, 1e7), "min": (0.0, 1e5), "h": (0.0, 1e5),
    "Hz": (0.0, 1e7), "kHz": (0.0, 1e4), "MHz": (0.0, 1e4),
}

def band_for(unit):
    key = unit.strip()
    if key in BANDS:
        return BANDS[key]
    low = key.lower()
    for cand, band in BANDS.items():
        if cand.lower() == low:
            return band
    return None

# ------------------------------------------------------------------ dictionary mirror
def parse_dictionary(path):
    """Parse ChannelDictionary.cs: KnownBases + the wheel-channel table + the
    implicit unit conversions of GetMetadata. Returns (known, wheels)."""
    try:
        text = open(path, encoding="utf-8").read()
    except OSError:
        die(f"cannot read dictionary at {path}")

    known = {}
    for m in re.finditer(r'\{\s*"([^"]+)",\s*\("([^"]+)",\s*"([^"]*)",\s*"([^"]*)",\s*([0-9.]+)f?,\s*([0-9]+)\)\s*\}', text):
        name, _base, _short, unit, mult, dec = m.groups()
        known[name.strip()] = (unit, float(mult), int(dec))

    wheels = {}
    # First alternative of each n.Equals() condition (so "Brakes Temp" wins over
    # the "Brake Temp" alias), tolerating // comment lines between the brace and
    # the return (a real comment was added above Brakes Force in the dictionary).
    wheel_pat = re.compile(
        r'n\.Equals\("([^"]+)"[^)]*\)(?:\s*\|\|\s*n\.Equals\("[^"]+"[^)]*\))*\s*\)'
        r'\s*\{\s*(?:[^\r\n]*\n\s*)*?'
        r'return new ChannelMeta\(\$?"([^"]*)",\s*\$?"([^"]*)",\s*"([^"]*)",\s*([0-9.]+)f?,\s*([0-9]+)\);')
    for m in wheel_pat.finditer(text):
        base, _std, _short, unit, mult, dec = m.groups()
        wheels[base.strip()] = (unit, float(mult), int(dec))
    return known, wheels

WHEEL_SUFFIXES = (" FL", " FR", " RL", " RR")

def effective_meta(name, declared_unit, known, wheels):
    """Mirror ChannelDictionary.GetMetadata + GetWheelChannelMetadata closely enough
    to know the unit and multiplier the converter will apply."""
    key = name.strip()
    if key in known:
        unit, mult, dec = known[key]
        return ("dict", unit, mult, dec)

    un_suffixed = key
    for sfx in WHEEL_SUFFIXES:
        if key.upper().endswith(sfx):
            un_suffixed = key[:-3]
            break
    if key in wheels:
        unit, mult, dec = wheels[key]
        return ("wheel", unit, mult, dec)
    if un_suffixed != key and un_suffixed in wheels:
        unit, mult, dec = wheels[un_suffixed]
        return ("wheel", unit, mult, dec)

    cleaned = key.replace("_", " ")
    unit = (declared_unit or "").strip()
    mult, dec = 1.0, 2
    if unit.lower() == "m/s":
        unit, mult, dec = "km/h", 3.6, 1
    elif unit.lower() == "m" and any(w in cleaned.lower() for w in ("height", "deflection", "susp")):
        unit, mult, dec = "mm", 1000.0, 1
    elif unit.lower() == "pa":
        unit, mult, dec = "bar", 0.00001, 3
    return ("fallback" if unit else "unknown", unit, mult, dec)

# ------------------------------------------------------------------ the audit itself
def main():
    if len(sys.argv) < 2:
        die("usage: channel_audit.py <session.duckdb> [--dict path/to/ChannelDictionary.cs]")
    db = sys.argv[1]
    dict_path = None
    if "--dict" in sys.argv:
        dict_path = sys.argv[sys.argv.index("--dict") + 1]

    try:
        import duckdb
    except ImportError:
        die("need the duckdb module:  uv run --with duckdb python3 channel_audit.py ...")
    if dict_path is None:
        import os
        cand = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Duckmetry.Core", "Models", "ChannelDictionary.cs")
        if os.path.exists(cand):
            dict_path = cand
    if dict_path is None:
        die("dictionary path not found; pass --dict <ChannelDictionary.cs>")
    known, wheels = parse_dictionary(dict_path)

    con = duckdb.connect(db, read_only=True)
    channels = con.execute("SELECT channelName, frequency, unit FROM channelsList ORDER BY channelName").fetchall()
    tables = {}
    for (t,) in con.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='main'").fetchall():
        vcs = [c[0] for c in con.execute(
            f"SELECT column_name FROM information_schema.columns WHERE table_name='{t}' AND column_name LIKE 'value%'").fetchall()]
        if vcs:
            tables[t] = vcs

    suspect = []
    print(f"{'channel':44s} {'src':9s} {'decl unit':10s} {'-> unit':8s} {'x':>7s} {'projected min':>13s} {'projected max':>13s}  verdict")
    for name, freq, unit in channels:
        vcs = tables.get(name)
        if not vcs:
            print(f"{name:44s} {'(no data table)':24s}  --")
            continue
        mn, mx = None, None
        is_bool = False
        for vc in vcs:
            typ = con.execute(f"SELECT data_type FROM information_schema.columns WHERE table_name='{name}' AND column_name='{vc}'").fetchone()[0]
            if "BOOL" in typ.upper():
                is_bool = True
                continue
            st = con.execute(f'SELECT min("{vc}"), max("{vc}") FROM "{name}"').fetchone()
            if mn is None or st[0] < mn: mn = st[0]
            if mx is None or st[1] > mx: mx = st[1]
        if mn is None:
            print(f"{name:44s} {'(boolean flag)':24s}  ok (flag)")
            continue

        src, eff_unit, mult, dec = effective_meta(name, unit, known, wheels)
        proj = (mn * mult, mx * mult)
        band = band_for(eff_unit)

        flags = []
        if band is not None and not (band[0] - 5 <= proj[0] and proj[1] <= band[1] + 5):
            flags.append(f"outside {band}")
        if eff_unit == "%" and abs(proj[1]) < 1.0 and proj[1] != 0.0:
            flags.append("looks like a fraction, not a percent (x100?)")
        if src == "unknown":
            flags.append("new/unknown channel")
        verdict = "SUSPECT: " + "; ".join(flags) if flags else "ok"
        if flags:
            suspect.append(name)
        print(f"{name:44s} {src:9s} {str(unit or ''):10s} {eff_unit:8s} {mult:7.4g} {proj[0]:13.3f} {proj[1]:13.3f}  {verdict}")

    con.close()
    print()
    print(f"{len(channels)} channels checked")
    if suspect:
        print(f"SUSPECT ({len(suspect)}):")
        for s in suspect:
            print("   -", s)
        sys.exit(1)
    print("all channels look consistent with their declared units")

if __name__ == "__main__":
    main()