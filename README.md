# eldenring-enable-graces

A small **Windows Forms** app that reads Elden Ring's `regulation.bin`, lists
every **`BonfireWarpParam`** row (every "Site of Grace" warp entry), and lets you
**force-enable graces** by checking a box per row (sets `eventflagId → 76101`),
then write the changes back — restoring any row's original flag by unchecking.

It's built on the same format libraries Smithbox uses (the locally-cloned
`Smithbox/` repo is the "knowledge base").

## What it does

- **Browse regulation.bin** — pick the file directly and load it.
- Grid of all graces with an **Enabled** checkbox, **ID**, **Name**,
  **Event Flag ID**, and **Original** (the value captured on first open).
- **Check a row** → `eventflagId = 76101`. **Uncheck** → restored to its original.
- **Enable all / Disable all** buttons.
- **Save** writes changes back into `regulation.bin` (re-encrypted the way the
  game expects). A one-time **`regulation.bin.bak`** backup is created on first
  save, and a **`regulation.bin.originals.json`** sidecar records every row's
  original flag on first open — so unchecking stays correct across save/close/
  reopen cycles.

## Requirements

- **.NET 10 SDK** (`net10.0-windows`). The app references Smithbox's in-tree
  libraries (`../Smithbox/src/Andre/...`), which target `net10.0`.
- This repo expects the [Smithbox](https://github.com/vawser/Smithbox) clone to
  live as a sibling directory (`../Smithbox`).

## Build & run

```sh
dotnet build -c Release
dotnet run -c Release --project eldenring-enable-graces.csproj
```

### Headless checks (no GUI)

```sh
# Embedded PARAMDEF + row names load, and eventflagId exists.
dotnet run -c Release -- --selftest

# Decrypt a regulation and print the first graces + total count.
dotnet run -c Release -- --read "C:\path\to\regulation.bin"

# Full round-trip on a COPY (never touches the original): enables a few rows,
# saves, re-reads, and verifies enabled rows == 76101 while every other row is
# unchanged. Prints RESULT: PASS/FAIL.
dotnet run -c Release -- --roundtrip "C:\path\to\regulation.bin"
```

## Project layout

```
eldenring-enable-graces/
  eldenring-enable-graces.csproj   # WinForms net10.0; refs Smithbox SoulsFormats + Andre.Formats
  Program.cs                       # entry + --selftest/--read/--roundtrip console modes
  MainForm.cs                      # browse + checkbox grid + enable/disable all + save
  RegulationReader.cs              # decrypt/encrypt regulation, read/write BonfireWarpParam
  GraceRow.cs                      # one row (Current + Original event flag)
  Assets/
    BonfireWarpParam.xml           # PARAMDEF (from Smithbox's ER Defs)
    BonfireWarpParam.json          # English row names (from Smithbox's ER metadata)
```

## How it works (the interesting bits)

**Read:**
```
regulation.bin
  → SFUtil.DecryptERRegulation(bytes)        // DCX/DCP decompress + AES decrypt → BND4
  → bnd.Files[* "BonfireWarpParam.param"]
  → Andre.Formats.Param.Read(bytes)
  → param.ApplyParamdef(PARAMDEF, version)   // version from bnd.Version; version-aware
  → flagCol = param["eventflagId"]           // Column API for get/set
  → merge row.ID with English names JSON
```

**Write** (mirrors Smithbox's `SaveParameters_ER`):
```
  → one-time File.Copy → regulation.bin.bak
  → DecryptERRegulation → Andre.Formats.Param.Read → ApplyParamdef(version)
  → flagCol.SetValue(row, 76101) for each enabled row
  → paramFile.Bytes = param.Write()
  → SFUtil.EncryptERRegulation(bnd, ZeroIv)  // zero IV, no DCX — form the game accepts
  → write .temp, then File.Move over the original
```

### Why `Andre.Formats.Param` and not `SoulsFormats.PARAM`

Both can *read* the param, but `SoulsFormats.PARAM.Write()` throws on this
paramdef (a `NullReferenceException` in `Row.WriteCells` from a version-gated
bitfield layout). Smithbox edits params through `Andre.Formats.Param`
(`src/Andre/Andre.Formats/Param.cs`) — a faster, version-aware reimplementation
whose write path works. The version-aware `ApplyParamdef(def, regulationVersion)`
is essential: it filters fields by the regulation's own version so the row layout
matches the data exactly. See
[`../Smithbox/docs/libraries-and-dependencies.md`](../Smithbox/docs/libraries-and-dependencies.md).

## Notes

- "Original" = the value present the first time you open a given `regulation.bin`
  (recorded in the `.originals.json` sidecar). To compare against true vanilla,
  you'd load a Smithbox vanilla regulation instead — not needed for the
  check/uncheck toggle.
- 76101 is the chosen "force-enable" flag. Verify the in-game effect with a test
  regulation before relying on it.
- Only Elden Ring is wired up (matches the project name). The same pattern works
  for Nightreign (`DecryptNRRegulation`/`EncryptNightreignRegulation`) or DS3.

## License & Credits

Copyright (C) 2026 Phath. Licensed under the **GNU General Public License v3.0** —
see [LICENSE](LICENSE). GPL-3.0 is required because this project links
**SoulsFormats** (GPL-3.0); derivatives must also be GPL-3.0 with source available.

Built on top of the **[Smithbox](https://github.com/vawser/Smithbox)** codebase,
which is the source of — and is credited for:

- **SoulsFormats** ([JKAnderson/SoulsFormats](https://github.com/JKAnderson/SoulsFormats), GPL-3.0) —
  reading/writing Elden Ring's `regulation.bin` and the `BonfireWarpParam` param.
- **Andre.Formats** (Smithbox, MIT) — the version-aware `Param` read/write used here.
- The `BonfireWarpParam.xml` PARAMDEF and English row-name `.json` in `Assets/`,
  sourced from Smithbox's Elden Ring param metadata.

Elden Ring is a trademark of FromSoftware, Inc. This tool is not affiliated with
or endorsed by FromSoftware; use it only with legally-obtained game files.
