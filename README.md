# eldenring-enable-graces

A small **Windows Forms** app that reads Elden Ring's `regulation.bin`, lists
every **`BonfireWarpParam`** row (every "Site of Grace" warp entry), and lets you
**force-enable graces** by checking a box per row (sets `eventflagId → 76800`),
then write the changes back — restoring any row's original flag by unchecking.

It's built on the same format libraries Smithbox uses (the locally-cloned
`Smithbox/` repo is the "knowledge base").

## What it does

- **Browse regulation.bin** — pick the file directly and load it.
- Grid of all graces with an **Enabled** checkbox, **ID**, **Name**,
  **Event Flag ID**, and **Original** (the value captured on first open).
- **Check a row** → `eventflagId = 76800`. **Uncheck** → restored to its original.
- **Enable all / Disable all** buttons.
- **Save** writes changes back into `regulation.bin` (re-encrypted the way the
  game expects). A one-time **`regulation.bin.bak`** backup is created on first
  save, and a **`regulation.bin.originals.json`** sidecar records every row's
  original flag on first open — so unchecking stays correct across save/close/
  reopen cycles.

## Requirements

- **.NET 10 SDK** (`net10.0-windows`). That's it — the format libraries are
  vendored under [`lib/`](lib) (from Smithbox, commit `9f3ad7d`), so this repo
  builds standalone with no other checkout required.

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
# saves, re-reads, and verifies enabled rows == 76800 while every other row is
# unchanged. Prints RESULT: PASS/FAIL.
dotnet run -c Release -- --roundtrip "C:\path\to\regulation.bin"
```

## Project layout

```
eldenring-enable-graces/
  eldenring-enable-graces.csproj   # WinForms net10.0
  Program.cs                       # entry + --selftest/--read/--roundtrip console modes
  MainForm.cs                      # browse + checkbox grid + enable/disable all + save
  RegulationReader.cs              # decrypt/encrypt regulation, read/write BonfireWarpParam
  GraceRow.cs                      # one row (Current + Original event flag)
  Assets/
    BonfireWarpParam.xml           # PARAMDEF (from Smithbox's ER Defs)
    BonfireWarpParam.json          # English row names (from Smithbox's ER metadata)
  lib/                             # vendored from Smithbox (commit 9f3ad7d); GPL/MIT
    SoulsFormats/SoulsFormats/     # FromSoft format I/O (regulation.bin decrypt, PARAM/PARAMDEF)
    Andre.Formats/                 # version-aware Param read/write
    Andre.Core/                    # shared core (Game enum, logging, containers)
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
  → flagCol.SetValue(row, 76800) for each enabled row
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
matches the data exactly. See Smithbox's
[libraries-and-dependencies doc](https://github.com/vawser/Smithbox/blob/main/docs/libraries-and-dependencies.md).

## Updating the vendored libraries (`lib/`)

`lib/` is a frozen snapshot of three projects from
[Smithbox](https://github.com/vawser/Smithbox) (currently commit `9f3ad7d`):
SoulsFormats, Andre.Formats, and Andre.Core. You'd only update it if SoulsFormats
fixes a format bug or adds decryption for a newer game version.

To refresh from a newer Smithbox:

1. Clone [Smithbox](https://github.com/vawser/Smithbox) and note its commit.
2. Copy its `src/Andre/SoulsFormats/SoulsFormats`, `src/Andre/Andre.Formats`, and
   `src/Andre/Andre.Core` over the matching folders in `lib/`, keeping the
   `lib/SoulsFormats/SoulsFormats` layout so the relative references still resolve.
3. Strip the unused archive-decryption bits from `lib/Andre.Formats/`: delete
   `bhd5_decrypt_rust.dll` and the `native/` folder, and remove the
   `bhd5` / `CopyRustDllIfPresent` lines from `lib/Andre.Formats/Andre.Formats.csproj`.
4. Remove any build artifacts:
   `find lib -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +`
5. `dotnet build -c Release`, then run `--selftest` and `--roundtrip` to confirm.
6. Update the commit hash here and in **Project layout** above.

## Notes

- "Original" = the value present the first time you open a given `regulation.bin`
  (recorded in the `.originals.json` sidecar). To compare against true vanilla,
  you'd load a Smithbox vanilla regulation instead — not needed for the
  check/uncheck toggle.
- The force-enable flag is **configurable** — set it in the UI ("Enable flag"
  box; default `76800`, persisted to `settings.json` under
  `%LocalAppData%\eldenring-enable-graces\`). Verify the in-game effect with a
  test regulation before relying on whatever value you choose.
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
