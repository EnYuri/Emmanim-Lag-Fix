# Emmanim Lag Fix

A Cosmoteer performance mod for very large, heavily modded ships and fleets.

The project combines ordinary `.rules` tuning with a narrowly-scoped .NET 10
code layer. The code loader is deliberately restricted to the exact mod ID
`nayuri.emmanim_lag_fix`; it ignores DLLs from every other mod and accepts only
the bundled Harmony library and `EmmanimLagFix.Code.dll`.

> [!WARNING]
> The `.rules` portion is established, but the dedicated loader is currently in
> pre-release testing. Its assemblies compile cleanly and all Harmony targets
> resolve against Cosmoteer 0.30.4c, but an in-game startup test is still pending.

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

The code patches UI aggregation/construction and local initialization scheduling.
They do not modify resource quantities, trade execution, crew jobs or simulation
state.

## Repository layout

```text
Mod/                            Workshop-ready mod folder
EmmanimLagFix.Code/             Harmony performance patches
EmmanimLagFix.Code.SmokeTest/   Patch-resolution smoke test
ModLoader/                      Dedicated managed loader fork
ModPreLoader/                   Alternate preloader
CosmoDoorstop/                  Native Windows entry point
```

## Installation

1. Copy `Mod` into Cosmoteer's user `Mods` folder and enable it.
2. The `.rules` optimizations work without DLL injection.
3. To test the code layer, close Cosmoteer and run `Mod/Install-Loader.ps1`.
4. Use `Mod/Uninstall-Loader.ps1` to remove it.

The installer refuses to overwrite a different `winmm.dll` or `ModLoader.dll`.
The uninstaller removes files only when their hashes still match its install
manifest.

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
