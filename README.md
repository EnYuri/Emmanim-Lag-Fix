# Emmanim Lag Fix

A Cosmoteer performance mod for very large, heavily modded ships and fleets —
the kind that bog down singleplayer framerate and, in multiplayer, eventually
drop a player outright. Cosmoteer's multiplayer is deterministic lockstep: every
client simulates the game locally and only inputs cross the wire, so the
slowest machine sets the pace for everyone, and one that falls too far behind
gets disconnected with a `WaitingForAck` timeout rather than just lagging. This
mod targets the CPU-saturation chain behind that timeout, plus a broader set of
hot paths that don't specifically cause disconnects but cost frame time on big
saves. See [Current optimizations](#current-optimizations) for the full list.

It combines ordinary `.rules` tuning, safe to read and reason about like any
other mod, with a narrowly-scoped .NET 10 code layer for work that `.rules`
alone can't express - UI caching, lock-free data structures, and the
`WaitingForAck` timeout extension itself. The installer sets up both together
by default; `Install.bat -NoLoader` skips the code layer and keeps just the
`.rules` tuning (see [Installation](#installation)).

The code loader is deliberately restricted to the exact mod ID
`nayuri.emmanim_lag_fix`; it ignores DLLs from every other mod and accepts only
the bundled Harmony library and the two code modules shipped in this package,
`EmmanimLagFix.Code.dll` and `ModsQol.Code.dll`. The allow-list is a fixed set
compiled into the loader, not a folder scan, so adding a DLL to `Code/` by hand
does not make it load.

`ModsQol.Code.dll` is an experimental test module, added in 2.1.0, for a
private companion mod that is not published anywhere. Nobody else has that
mod, so the module is always dormant and does nothing for other users - see
[Mods QoL support](#mods-qol-support).

> [!WARNING]
> Every multiplayer participant must install the same mod version because the
> `.rules` portion changes deterministic simulation settings.

## Current optimizations

- Avoids repeated planetary tag-enumerator allocations in resource hauling,
  preserving danger-zone decisions. Transfer expiry creates callback state
  only when scheduling a change or removal. See [2.2.4 validation](CHANGELOG.md).
- Uses vanilla crew assignment rates without Huge Crews. With Huge Crews and
  the code loader, uses one quarter of its assignment rates with vanilla floors
  (currently 250/s normal and 62.5/s low priority). Resource search stays 120/s.
- Keeps finer parallel batches for scene updates and vanilla sizing for other
  workloads; reduces crew oxygen-check and resource-search overhead. See
  [validation and limitations](CHANGELOG.md). Game FPS gains are not established.
- Reduces expensive manual-transfer and salvage check rates.
- Consolidates loose vanilla resource nuggets into larger stacks.
- Removes exterior-crew thruster effects.
- Caches the upper-right selected-ship resource aggregation for one second.
- Limits ship-transfer and station-trade full resource snapshots to 2 Hz.
- Spreads initial transfer/trade row insertion across frames instead of adding
  the full modded resource catalog to the main-thread layout in one burst.
- Paces background transfer-row construction so it does not monopolize a worker
  core while the simulation and networking threads are active.
- Lazily creates crew role-priority controls per expanded part and refreshes
  their visual state at 10 Hz.
- Keeps host/client multiplayer simulation creation at the runtime-selected
  thread priority; lowering it was measured to worsen first-sync completion.
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
- Preallocates both ends of the initial multiplayer data stream and avoids the
  client's second complete payload copy when guarded stream ownership transfer
  is available.
- Reduces normal whole-game integrity hashes and host state updates from 30 Hz
  to 6 Hz without lowering lockstep input or simulation cadence.
- Reuses the host's per-client `InputTick` forwarding filters instead of
  allocating a closure and delegate for every received tick.
- Shards the resource manager's sink-job collection lists per thread, removing
  the largest single lock convoy in the process from the parallel per-sink pass.
  Vanilla sorts both lists into a total order over distinct sink indexes before
  reading them, so the merged result is identical to vanilla's.
- Rescans minimap membership at 10 Hz and re-tests only the previously visible
  sources in between, instead of asking every object in the sector whether it is
  visible on every drawn frame. Disappearance and blip positions stay immediate.
- Parks idle `FastParallel` workers instead of letting them spin forever.
  Halfling's idle branch is `SpinWait.SpinOnce(-1)`, which never blocks; it was
  39.0 s of the 65.9 s of process CPU in a 20-second trace, with 17.8 s of GC
  suspension rendezvous underneath it. Workers spin for a bounded budget and
  then block on an event of their own until the next dispatch releases them.
  Machines below eight workers use a shorter 20-iteration budget instead of
  disabling parking and restoring the unbounded spin; wider machines use 60.
  The current auto-reset wake event avoids the managed monitor taken by the
  earlier reset/set path: 2.1.3 shared one monitor and simply relocated the cost
  into `Monitor.Wait` and `Enter_Slowpath`, part of it on the main thread.
  Raising `SpinOnce`'s
  `sleep1Threshold` instead was rejected after measuring `Thread.Sleep(1)` at
  10.6 ms in a process that never calls `timeBeginPeriod`.
- Reuses stable per-ship update and fixed-update callback snapshots, rebuilding
  one only when its callback list changes. Runtime callback registration keeps
  vanilla's next-invocation semantics, and weak ownership avoids extending a
  destroyed ship's lifetime.

The code patches UI aggregation/construction, resource bookkeeping, visual
updates, and local multiplayer timeout/initialization behavior. It does not
modify resource quantities, trade execution, crew jobs, packet formats, or
deterministic simulation state.

## Active memory investigation

A 2026-08-27 same-process trace comparison confirmed long-lived Gen 2 and GC-handle
growth, plus a separate vanilla `BlueprintPartStatProvider` delegate-allocation
storm on large ships. The exact measurements, trace paths, analysis helper and
recommended Harmony patch are preserved in MEMORY_DIAGNOSTICS.md, a local
working-reference file kept out of this repository. Read it before changing
caches or adding memory-related patches.

## Resource logistics and path-search investigation

Large multi-tile storage parts multiply otherwise identical source, sink and
path-contiguity work. The controlled ship-removal tests, diagnostic traces,
rejected 2.0.11 shared cache, released lock-free `PerShipCount` implementation and
safety constraints for any future path optimization are preserved in
RESOURCE_LOGISTICS_DIAGNOSTICS.md, a local working-reference file kept out of
this repository. Read it before caching resource locations, routes, candidates
or sink-job results.

## Multiplayer synchronization investigation

The complete `GameInit` transfer, client-side duplicate buffering, game-creation
memory peak, frame-coupled ACK path, implemented timeout/buffer mitigations and
the constraints for a future dedicated ACK pump are documented in
MULTIPLAYER_SYNC_DIAGNOSTICS.md, a local working-reference file kept out of
this repository.

## Diagnostics logging

The mod ships two switches enabled, `multiplayer-memory-diagnostics.flag` and
`singleplayer-memory-diagnostics.flag` in the mod folder. While a switch is
present, one line a minute is written to the Cosmoteer log
(`Saved Games\Cosmoteer\<id>\Logs\log <date>.txt`) recording memory, GC
counts, simulation size and, in multiplayer, each player's lockstep input queue
and whether that player is the one holding the readiness gate. The sampling is
read-only: it never touches a queue, a resource or the simulation.

Both peers need the multiplayer switch. The host's line names which player is
delaying the game, but only the same minute in that player's own log says
whether the cause is on their side. Delete a flag file to turn its line off.

## Repository layout

```text
Mod/                            Distributable mod folder
EmmanimLagFix.Code/             Harmony performance patches
EmmanimLagFix.Code.SmokeTest/   Patch-resolution smoke test
ModsQol.Code/                   Mods QoL support module (see below)
ModsQol.Code.SmokeTest/         Its own patch-resolution smoke test
ModLoader/                      Dedicated managed loader fork
ModPreLoader/                   Alternate preloader
CosmoDoorstop/                  Native Windows entry point
Pack.ps1                        Builds the GitHub release archive
```

## Installation

Releases are distributed as a single archive from the
[Releases](https://github.com/EnYuri/Emmanim-Lag-Fix/releases) page. Extract it
anywhere and run `Install.bat`.

There *is* a Steam Workshop listing, `Emmanim Lag Fix (GitHub download
required)`, but it's a stub: subscribing to it changes nothing in your game.
Workshop can't host or review the optional native code layer this mod ships,
so the listing exists only so the mod is discoverable from Workshop search; its
description points here. Get the real mod from Releases above.

The installer places the mod in the Cosmoteer user `Mods` folder and the code
loader in `Cosmoteer\Bin`, resolving both the same way the game does. Both code
modules travel inside the mod folder, so a single `Install.bat` run puts
`EmmanimLagFix.Code.dll` and `ModsQol.Code.dll` in place together; only
`winmm.dll` and `ModLoader.dll` go outside it, and only those two are tracked in
the uninstall manifest. It
declines to run while the game is open, to overwrite a `winmm.dll` or
`ModLoader.dll` it did not place, or to replace a mod folder that is not this
mod. `Uninstall.bat` removes files only when their hashes still match its
install manifest.

`Install.bat -NoLoader` installs the `.rules` optimizations alone, with no
native DLL. See `Mod/README.md` for the full switch list.

To work from a source tree instead, copy `Mod` into the user `Mods` folder and
run `Mod/Install.bat -LoaderOnly`.

## Mods QoL support

`ModsQol.Code.dll` (2.1.0) is a test module for a private companion mod called
**Mods QoL**, which is not published anywhere public. It has no effect unless
that exact mod is also installed, which for anyone outside this installation
it never is - so for practical purposes this module doesn't do anything.

> [!NOTE]
> Mods QoL is a private mod, not published on Steam Workshop or anywhere else.
> This section documents behavior for the one installation that runs both mods
> together; if you don't have Mods QoL, this module simply does nothing.

> [!WARNING]
> The sentinel changes how much power a wire delivers, which is simulation state.
> Players in the same multiplayer game running Mods QoL 1.65.0 or later must
> either all install this loader or all skip it.

## Packaging a release

```powershell
.\Pack.ps1 -RefreshBinaries
```

This copies the freshly built loader, proxy and code module into `Mod/`,
regenerates `Mod/Source` from the repository (the LGPL source bundle that ships
beside the binary), and writes `build/Emmanim-Lag-Fix-<version>.zip`. The
version is read from `Mod/mod.rules`, so the archive name cannot disagree with
what the game reports. The archive it writes is for local verification; the one
that ships is built by CI from the committed payload.

Publishing is therefore just the tag:

```powershell
git tag -a v<version> -m "Emmanim Lag Fix <version>"
git push origin v<version>
```

Do not also run `gh release create`. The workflow's last step does exactly that,
and a release made by hand fails the job with *a release with the same tag name
already exists*. Note too that this repository has an `upstream` remote, so a
bare `gh` command targets the fork source rather than this repository - always
pass `--repo EnYuri/Emmanim-Lag-Fix`.

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

The code loader bundled here is a modified fork of
[`radistmorse/CosmoteerModLoader`](https://github.com/radistmorse/CosmoteerModLoader)
(Steam Workshop: [Yet Another Mod Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3577650065)),
pinned to commit `2aee1c7d0175c7c3508435f3eccb5411b103581e` and kept under its
original LGPL-2.1 license. The fork exists to apply the fixed mod-ID and DLL
allow-list restrictions described at the top of this README - those aren't
upstream behavior, they're what this project changed.
[EMMANIM_FORK.md](EMMANIM_FORK.md) has the full list of deliberate deviations
from upstream.

Harmony is distributed under the MIT license, and the original Emmanim `.rules`
tuning this mod builds on is likewise MIT-licensed. Full license text and
per-DLL notices live in `LICENSE`, `LICENSE.txt`, and `Mod/Code` and
`Mod/Loader`.
