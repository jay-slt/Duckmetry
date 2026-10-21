# Choosing the sample rate

Every channel in a converted `.ld` is written at its own rate, and the analysis software
reads it at exactly the rate the file declares. That rate is a choice, not a limit: the
format allows anything from 1 to 100 Hz, and the reference logs already use nine
different rates.

The choice is yours. It lives in a settings file in your own user folder:

    Windows   %LOCALAPPDATA%\Duckmetry\settings.json
    Linux     ~/.local/share/Duckmetry/settings.json

The converter reads it from both the interactive menu and the command line. The file
used to live in other places over the project's history — beside the executable, then
under the old project name — and a file left in any of the old places is still read, so
upgrading does not lose your choices; the next save writes them to the new location.

## Where the rates themselves come from

The game records each channel at the rate **you** configured. In the game's telemetry
folder (`UserData/Telemetry`, right next to the recordings) sits a `settings.json` of
its own: the list of channels, their names and their rates. Edit that file and the game
records at your new rates — so when a conversion says "the game recorded this at 50 Hz",
that number is the player's own choice, not a hidden constant. **That file is the
ceiling.** No conversion mode may write a channel faster than the recording contains;
upsampling would invent detail that was never captured. (It would also be pointless:
nothing is gained beyond the recorded resolution.)

## The four modes

```json
{
  "RateMode": "Optimised",
  "ReducedDivisor": 2,
  "RateOverrides": {}
}
```

| mode | what it writes | reference session |
|---|---|---|
| `Source` | every sample the game recorded | 15.9 MB |
| `Optimised` | the standard rate per channel — **the default** | 5.8 MB |
| `Reduced` | each channel divided by `ReducedDivisor` | 2.9 MB at ÷2 |
| `Pro` | your rate per channel, the standard for the rest | 6.2 MB in the example below |

Those sizes are for one 557-second session with 154 channels. `Source` is roughly three
times `Optimised`, which is the honest cost of keeping everything.

### The source rate is always a ceiling

In every mode, a channel is never written faster than the game recorded it (which, per
the section above, is what the player chose in the game's telemetry `settings.json`).
`Ambient Temperature` is recorded at 1 Hz, so it stays at 1 Hz in every mode — `Source`
included. `Source` is not uniformly "more": for a channel the game logged slowly it is
*slower* than what `Optimised` would choose.

### Why `Reduced` divides rather than caps

An earlier version capped every channel at one ceiling. That does not reduce uniformly:
a 5 Hz cap discards 95% of a 100 Hz channel and nothing at all from a 2 Hz one, so the
fast channels become useless while the slow ones are untouched — which is the opposite
of useful.

Dividing each channel by the same factor keeps every channel's resolution in proportion
to the standard choice. Measured on the reference session:

    channel              standard   /2   /4
    Susp Pos FL            100      50   25
    Ground Speed            10       5    2
    Throttle Pos            10       5    2
    Beacon                   5       2    1
    Ambient Temperature      1       1    1

A 100 Hz suspension channel still has 50 Hz of resolution at ÷2 — five times the
resolution of a 10 Hz channel — instead of being dragged down to the level of the
slowest channel in the log. Nothing is ever reduced below 1 Hz, because a rate of zero
is not a file i2 can read.

`ReducedDivisor` of 1 or 0 disables the reduction.

### `Pro`: setting channels individually

`RateOverrides` maps a channel name to a rate. Channels you do not name keep the
standard rate. The names are your sessions' channel names — in the GUI, select the
sessions, pick `Pro` and press **Set rates…** to read the list straight from the files
and edit it; the same editor restores your previous choices and saves back to this
settings file.

```json
{
  "RateMode": "Pro",
  "RateOverrides": {
    "Ground Speed": 100,
    "Engine RPM": 100,
    "Throttle Pos": 50,
    "Brake Pos": 50
  }
}
```

Two files selected with different recorded rates for the same channel: the highest rate
you can choose is the lowest of the two recordings — the rate that is valid for *both*.

Overrides are still capped by what the recording contains: asking for 100 Hz on a
channel logged at 50 Hz gives you 50.

## A note on file size

The rate is the only lever on size, and it applies per channel. Halving everything
roughly halves the file; raising a handful of channels to their source rate costs
proportionally little, because most channels are slow. In the example above, taking
`Ground Speed` and `Engine RPM` to 100 Hz added 0.4 MB to 5.8 MB.