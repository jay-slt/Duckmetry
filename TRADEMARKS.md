# Trademarks and affiliation

This project is an independent, community-made tool. It is not affiliated with,
endorsed by, sponsored by, or connected in any way to MoTeC Ltd, Studio 397,
Le Mans Virtual, the ACO, the FIA, or any other party involved in producing,
licensing, publishing or operating Le Mans Ultimate or MoTeC i2.

## Third-party marks

These names and logos are the property of their respective owners. They are used
here only to identify the software and data this tool reads and the tools it
interoperates with:

| Mark | Owner | Used to refer to |
|---|---|---|
| **Duckmetry** | this project | The converter itself — the project's own name, claimed here only to record that it is not derived from any third-party mark |
| **MoTeC**, **i2**, **i2 Pro** | MoTeC Ltd | The telemetry analysis software whose `.ld` / `.ldx` files this tool reads the format of, and whose setup `.svm` files it writes |
| **Le Mans Ultimate** | Studio 397 / its licensors | The racing simulation whose recorded telemetry this tool converts |
| **FIA WEC** | FIA / ACO | The championship the simulation is licensed against — referenced only to identify the game |
| **Le Mans** (as a place) | ACO | The circuit names that appear in recorded session filenames and metadata |
| **DuckDB** | DuckDB Foundation | The database engine the game's telemetry is stored in, used as a dependency |
| **SimHub**, **Crew Chief** | their respective authors | Named only when describing which existing tools read the same data |

No endorsement is claimed or implied in either direction.

## What this project does not contain

- No MoTeC source code, binaries, headers, or documentation
- No game assets, telemetry, or data from Le Mans Ultimate
- No logos, artwork, or branding
- No game or telemetry files are bundled in any release asset

## What it reads, and from where

To use it you point it at telemetry files **that you recorded yourself**, on your
own machine, from a game you own. The tool performs no network access, phones
home, or transmits anything. It reads local files and writes local files.

## Reverse engineering

The file formats are documented from observed behaviour and from publicly
available specifications and community implementations, and are implemented
independently. Nothing here is derived from decompiled or reverse-engineered
proprietary source code. Where the project's notes describe a format's internal
layout, that reflects analysis of file structure and of files the author
generated — the intent is interoperability, which is the ordinary purpose of
documenting a data format so that other tools can read it.

## Format ownership

The `.ld`, `.ldx` and `.svm` file formats are associated with MoTeC. This project
writes files that those applications can read; it does not claim ownership of
those formats, and it makes no representation about them.

## Correcting this file

If you hold rights in a mark listed here and believe its use in this project is
inaccurate or excessive, please open an issue. Such requests are taken seriously
and the wording will be adjusted.