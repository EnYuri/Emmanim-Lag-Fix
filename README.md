# Emmanim Lag Fix

A Cosmoteer performance mod for very large, heavily modded ships and fleets.

The project combines ordinary `.rules` tuning with a narrowly-scoped .NET 10
code layer. The code loader is deliberately restricted to the exact mod ID
`nayuri.emmanim_lag_fix`; it ignores DLLs from every other mod and accepts only
the bundled Harmony library and `EmmanimLagFix.Code.dll`.

> [!WARNING]
> Every multiplayer participant must install the same mod version because the
> `.rules` portion changes deterministic simulation settings.

## Current optimizations

- Reduces crew assignment, resource-search and expensive-check rates.
- Consolidates loose vanilla resource nuggets into larger stacks.
- Removes exterior-crew thruster effects.
- Widens the deterministic lockstep input-delay allowance.
- Caches the upper-right selected-ship resource aggregation for one second.
- Limits ship-transfer and station-trade full resource snapshots to 2 Hz.
- Spreads initial transfer/trade row insertion across frames instead of adding
  the full modded resource catalog to the main-thread layout in one burst.
- Paces background transfer-row construction so it does not monopolize a worker
  core while the simulation and networking threads are active.
- Lazily creates crew role-priority controls per expanded part and refreshes
  their visual state at 10 Hz.
- Runs host/client multiplayer simulation creation below normal thread priority,
  preserving CPU scheduling time for Steam networking during the first sync.
- Logs host creation and client decode/creation durations separately.
- Replaces the resource manager's exclusive per-ship count lock with immutable
  copy-on-write snapshots, removing lock contention from parallel readers.
- Limits display-only smoothed part values to 20 Hz while preserving their full
  accumulated game-time delta and every deterministic fixed update.
- Extends the application-level multiplayer session timeout from 10 to 30
  seconds without changing packets, resend cadence, input ordering, or
  simulation state.
- Preallocates the client initial-sync stream to its known payload size and
  releases it immediately after deserialization, before game construction.

The code patches UI aggregation/construction, resource bookkeeping, visual
updates, and local multiplayer timeout/initialization behavior. It does not
modify resource quantities, trade execution, crew jobs, packet formats, or
deterministic simulation state.

## Active memory investigation

A 2026-08-27 same-process trace comparison confirmed long-lived Gen 2 and GC-handle
growth, plus a separate vanilla `BlueprintPartStatProvider` delegate-allocation
storm on large ships. The exact measurements, trace paths, analysis helper and
recommended Harmony patch are preserved in [MEMORY_DIAGNOSTICS.md](MEMORY_DIAGNOSTICS.md).
Read that file before changing caches or adding memory-related patches.

## Resource logistics and path-search investigation

Large multi-tile storage parts multiply otherwise identical source, sink and
path-contiguity work. The controlled ship-removal tests, diagnostic traces,
rejected 2.0.11 shared cache, released lock-free `PerShipCount` implementation and
safety constraints for any future path optimization are preserved in
[RESOURCE_LOGISTICS_DIAGNOSTICS.md](RESOURCE_LOGISTICS_DIAGNOSTICS.md). Read it
before caching resource locations, routes, candidates or sink-job results.

## Multiplayer synchronization investigation

The complete `GameInit` transfer, client-side duplicate buffering, game-creation
memory peak, frame-coupled ACK path, implemented timeout/buffer mitigations and
the constraints for a future dedicated ACK pump are documented in
[MULTIPLAYER_SYNC_DIAGNOSTICS.md](MULTIPLAYER_SYNC_DIAGNOSTICS.md).

## Repository layout

```text
Mod/                            Distributable mod folder
EmmanimLagFix.Code/             Harmony performance patches
EmmanimLagFix.Code.SmokeTest/   Patch-resolution smoke test
ModLoader/                      Dedicated managed loader fork
ModPreLoader/                   Alternate preloader
CosmoDoorstop/                  Native Windows entry point
Pack.ps1                        Builds the GitHub release archive
```

## Installation

Releases are distributed as a single archive from the
[Releases](https://github.com/EnYuri/Emmanim-Lag-Fix/releases) page. Extract it
anywhere and run `Install.bat`; there is no Steam Workshop item to subscribe to.

The installer places the mod in the Cosmoteer user `Mods` folder and the code
loader in `Cosmoteer\Bin`, resolving both the same way the game does. It
declines to run while the game is open, to overwrite a `winmm.dll` or
`ModLoader.dll` it did not place, or to replace a mod folder that is not this
mod. `Uninstall.bat` removes files only when their hashes still match its
install manifest.

`Install.bat -NoLoader` installs the `.rules` optimizations alone, with no
native DLL. See `Mod/README.md` for the full switch list.

To work from a source tree instead, copy `Mod` into the user `Mods` folder and
run `Mod/Install.bat -LoaderOnly`.

## Packaging a release

```powershell
.\Pack.ps1 -RefreshBinaries
```

This copies the freshly built loader, proxy and code module into `Mod/`,
regenerates `Mod/Source` from the repository (the LGPL source bundle that ships
beside the binary), and writes `build/Emmanim-Lag-Fix-<version>.zip`. The
version is read from `Mod/mod.rules`, so the archive name cannot disagree with
what the game reports. The script prints the tag and `gh release create`
commands to run next.

Pushing a matching `v*` tag runs `.github/workflows/release.yml`. The Windows
runner validates the tag against `Mod/mod.rules`, checks the committed installer
and binary payload, runs `Pack.ps1`, verifies the archive contents, and publishes
the ZIP as a GitHub Release asset. It deliberately packages the committed DLLs
instead of rebuilding the code module because Cosmoteer's proprietary reference
assemblies are not available on GitHub-hosted runners. Build and smoke-test the
DLL locally before committing and tagging.

## Building

The managed projects require the .NET 10 SDK and references to the game's
`Cosmoteer.dll` and `HalflingCore.dll`. Override the `CosmoteerBin` MSBuild
property when the game is installed elsewhere.

```powershell
dotnet build ModLoader.sln -c Release
dotnet run --project EmmanimLagFix.Code.SmokeTest -c Release
```

The Windows x64 proxy DLL requires Visual C++ and xmake:

```powershell
.\CosmoDoorstop\build.ps1 -Arch x64
```

## Upstream and licensing

The loader is derived from
[`radistmorse/CosmoteerModLoader`](https://github.com/radistmorse/CosmoteerModLoader)
at commit `2aee1c7d0175c7c3508435f3eccb5411b103581e` and remains under
LGPL-2.1. Harmony is distributed under the MIT license. Original Emmanim code
is MIT-licensed. See `LICENSE`, `LICENSE.txt`, the notices in `Mod/Code` and
`Mod/Loader`, and [EMMANIM_FORK.md](EMMANIM_FORK.md).
