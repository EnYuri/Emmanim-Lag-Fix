# Cosmoteer long-session memory diagnostics

## Sparse heat threshold follow-up (2026-08-31)

After the resource-search and desired-priority improvements, medium-sized heat
fields still appeared in `StatusDiffuser.PrepareLists` because the exact sparse
implementation only activated for bounds of at least 128x128 cells. Its
existing density guard already falls back to vanilla whenever the active
frontier is not genuinely sparse, so the lower bound is now 64x64. This avoids
allocating/growing and clearing vanilla's rectangular `_inputs` and
`_outputDeltas` buffers for medium sparse heat networks; calculation frequency,
diffusion values and row-major application order remain unchanged.

Last updated: 2026-08-31, Cosmoteer 0.30.4c, public Emmanim Lag Fix 2.0.15 plus local experiments.

## Live multiplayer render-stutter capture and local mitigation (2026-08-31 03:30 KST)

The active 2-player host session was captured for 20 seconds while the user
reported visibly worse stutter:

```text
Logs/multiplayer_lag_live_cpu_2026-08-31_03-30.nettrace
Logs/multiplayer_lag_live_cpu_2026-08-31_03-30.speedscope.json
```

MP queues remained healthy (`inputQueued` normally 3--5,
`connectionQueued=0`, hashes `0/0/0`, 7--10 KiB/s). The trace instead caught
two ~125 ms main-thread waits in
`ShipRenderer.DrawLayer -> AtlasQuadManager.DrawForEachTexture ->
D3D11GraphicsManager.Draw -> DeviceContext.MapSubresource`, plus separate
~126 ms static-GUI draw, ~125 ms GC-poll, and ~81 ms
`BlueprintPartStatProvider.GetToggleMode` samples. GPU utilization was only 9%
and 3.5/6 GiB VRAM was in use immediately afterward, so this was intermittent
dynamic-buffer synchronization, not sustained GPU saturation or VRAM
exhaustion. Over the whole 20 seconds, MP host update, receive and integrity
hash paths remained small.

Two conservative local patches were added from that evidence:

- `AtlasQuadRedundantWritePatch` exact-shape-transpiles the private managed
  atlas-quad setter. It compares the old/new unmanaged struct bytes and skips
  both the `GraphicsList` write and `AtlasQuadManager.ChangeCount` increment
  only when every byte is identical. Any actual position, color, UV,
  animation, damage or paint change follows vanilla. This targets avoidable
  full dynamic-buffer dirty uploads and cached ship-indicator invalidations;
  it does not skip drawing or lower visual frame rate.
- `BlueprintPartStatProviderRefreshPatch` extends the existing per-ship
  `PartsManager.UpdateCallbacks` gate with a separate 30-game-tick (one game
  second) cadence for stat-provider operational-toggle checks. Blueprint
  network ports retain their existing 300-tick cadence. Paused simulations
  allow both paths every frame, and the state remains one weak gate per ship
  callback container rather than one entry per blueprint component.

Release build and smoke coverage pass, including exact transpiler installation,
both prefixes on `BlueprintPartStatProvider.UpdateOperational`, and identical/
different `AtlasQuad` comparison behavior. Live effectiveness still requires
the same save and camera state; compare `MapSubresource`, managed-quad setter,
ship-indicator, and blueprint-stat samples in a fresh trace.

Cosmoteer was confirmed stopped before deployment. Root build, package DLL and
live DLL SHA-256 are identical:

```text
0CBA857E45E0AE07FE72B08BC568A692483F76B66C6801D2773769DE5FBBBD39
```

## Lazy PaintToolbox picker construction, batched build (2026-08-31)

The lazy `PaintToolbox` picker/decal-group work started 2026-08-30 was left
mid-edit (a `BuildBatch` off-by-one) at the previous session boundary. This
session finished it as a per-frame batched incremental build rather than the
earlier one-shot synchronous build: `PendingDecalGroup` now builds at most
`DecalItemsPerFrame = 128` decal buttons per pre-draw callback via
`StartIncrementalBuild`/`BuildBatch`, re-scheduling itself while its group tab
stays open, and only clearing its pending state once every decal in the group
has been created. `BuildImmediately` (used by `EnsureBuilt`/grab-decal/
programmatic selection through `EnsureGroupContaining`) still forces the whole
remaining batch synchronously. `Build()`/`Contains()` in the two lingering
source mirrors were renamed to `BuildImmediately()`/`ContainsPending()`
accordingly; the mirrors were out of sync with the compiled implementation and
have been re-copied from `EmmanimLagFix.Code/PaintToolboxLazyPickerPatch.cs`,
which remains the single source of truth for this file.

Verified this session: `dotnet build ModLoader.sln -c Release` (0 warnings, 0
errors) and the smoke test both pass, including the existing
`PaintToolboxAddDecalsGroupLazyItemsPatch`/`PaintToolboxSelectDecalTypeLazyItemsPatch`
prefix/postfix-installed assertions and the `AddDecalsLayers`/
`AddBasePaintLayer`/`OnSelfActivated` transpiler/postfix coverage added in the
prior session — no new smoke-test targets were needed since group/select
coverage already existed. Cosmoteer was confirmed not running before deploy.
Root, `Mod/Code`, and live `Mods/emmanim_lag_fix/Code` DLL SHA-256 (all
identical):

```
967407A50ADF373E0C412F3F3E594C0664A7C7B6018774E463CE33E38DDEF2FC
```

**Live-validated by the user (2026-08-31)**: normal operation confirmed —
paint mode opens without a Harmony/init exception, decal groups build
correctly across tabs. Published as part of release 2.0.15 (see
`CHANGELOG.md`); the live mod and repository are both at DLL SHA-256
`967407A50ADF373E0C412F3F3E594C0664A7C7B6018774E463CE33E38DDEF2FC`.

## Residual steady-state multiplayer stutter, correlated with stasis-churn GC (2026-08-31)

A 2.0.15 host session (`log 2026-08-31 00_42_12.txt`, host started 01:04:55)
was traced for 30 seconds immediately after game creation
(`Logs/multiplayer_host_steady_cpu_2026-08-31_01-06.nettrace`/
`.speedscope.json`). That window was genuinely idle — cumulative `CPU_TIME`
across all threads was only ~51.6 s over a 30 s x ~30-thread window,
`GameRoot.Update` inclusive was 1.9 s, and MP-specific paths
(`AdvanceNetworkTime`, `CheckGameSync`, `ForwardInputTick`) were all under
40 ms total — no bottleneck visible, because the trace missed the interesting
moment.

The always-on `multiplayer-memory-diagnostics.flag` output (one row/minute,
opt-in, no simulation/queue mutation) caught what the trace missed. Between
01:09:25 and 01:12:25, three consecutive minute-rows showed:

```
01:09→01:10  ships 260->130  parts 51068->27994  gc(gen0/1/2)=656/304/0
01:10→01:11  ships 130->197  parts 27994->39632  gc=510/191/1
01:11→01:12  ships 197->158  parts 39632->34914  gc=523/253/1
01:12→01:13  ships 158->157  parts 34914->35000  gc=332/67/0   (back to baseline)
```

Gen1 collections were 3-10x the surrounding minutes' 25-80, exactly overlapping
the interval where `ships`/`parts` swing hardest (large stasis
preload/unload, presumably a player crossing a sector boundary). `inputQueued`
also briefly ran above the 2-player count (`inputMax` 3-4) in the same window,
consistent with the host doing more per-tick work than usual. This lines up
with the CPU trace's own (idle-window) evidence that `PartsManager.AddPart`/
`RemovePart` and `BlueprintPartsManager.AddBlueprintPart`/`RemoveBlueprintPart`
are real, non-trivial costs (~1.8 s and ~0.9 s inclusive respectively, even in
a quiet 30 s sample) — a large stasis transition multiplies exactly that work.

**User confirmation**: felt only a very slight stutter at this point, and
explicitly stated this kind of hitch used to be constant/everyday before the
optimization patches. So the existing patches (sparse heat diffusion, lock-free
PerShipCount, visual/network throttles, etc.) already removed most of it; what
remains is this smaller stasis-churn-driven GC spike.

Not yet investigated: whether the win is in `SimStasisManager`'s spawn/despawn
batching, in `PartsManager`/`BlueprintPartsManager` add/remove churn itself, or
is simply an acceptable residual (Gen1-only, no Gen2, self-resolving within a
minute). Do not implement anything here without a CPU trace actually spanning
a live transition — the 2026-08-31 01:06 trace explicitly did not, and the
existing `RESOURCE_LOGISTICS_DIAGNOSTICS.md` invalidation-proof requirement
applies equally to any stasis/parts-manager change. Next step: repeat the
30-second `dotnet-trace` capture triggered by a sector-transition report from
the user rather than a fixed time offset after host start.

## Current handoff summary (2026-08-30 22:44 KST)

The last controlled single-player validation process was PID 7484, started at
17:14:55. It has exited. Its log is:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\log 2026-08-30 17_14_57.txt
```

The representative career save `single 2` loaded at 17:16:18. The corrected
single-player diagnostic then produced uninterrupted one-minute rows from
17:17 through at least 18:05. The `game` and `sim` identities remained
`03BC01E1` / `0335DC79`, so this interval contains no GameRoot replacement or
resync transition.

The live series does **not** currently look like a simple unbounded managed
leak:

```text
Interval / event                 Private MiB   GC heap MiB   Ships   Parts       Stasis preloaded
17:17 first report                    9971          5091       138    44267             0
17:24 settled paused state            9709          4694       138    44267             0
17:31 world-population jump          10273          4924       239    63697            48
17:51 later plateau                  10932          5170       241    48343            75
18:05 latest                         11001          5193       254    44764            81
```

The first minutes actually released memory. The major rise at 17:31 coincided
with 101 additional live ships, about 19,654 additional physical parts and 45
additional preloaded stasis spawners. From 17:51 to 18:05, private memory rose
only 69 MiB and GC heap only 23 MiB while handles stayed essentially flat
(1446 -> 1449). Paint UI counts remained exactly `39/11475` for the entire
run. This excludes repeated PaintToolbox construction and handle growth in
this sample and again ties the large state transition to live/stasis ship
population. Heap fragmentation remained high (roughly 450--830 MiB in the
later interval) but oscillated instead of rising monotonically.

The strongest remaining single-player issue is allocation churn, not proven
retention. Active play commonly allocated 100--185 MiB/s process-wide and
triggered roughly 700--1240 Gen-0 collections per minute. Memory can therefore
plateau while allocation/GC pressure still causes frame-time instability and
long-session stutter. A prior corrected-build `gc-verbose` sample had already
removed the old 814.58 MiB/10 s toggle-delegate storm; its remaining sampled
types were led by `Vector3` (21.45 MiB), `Matrix` (15.74 MiB), `Single`
(13.31 MiB), `Vector2` (8.95 MiB) and `Color` (5.90 MiB). The same trace's CPU
view was dominated by `GraphicsManager.RefreshShaderConstants`, but skipping
shader refresh is not safe without proving redundant shader transitions and
render equivalence.

Current conclusions:

- no evidence yet of an old-GameRoot lifecycle leak in this run;
- no repeated paint GUI retention; the 11,475 items are one stable eager tree;
- no monotonic process-handle leak;
- live/stasis ship population explains the largest observed memory step;
- fragmentation is substantial but not monotonic;
- sustained allocation/GC churn is the next performance target;
- lazy per-`ShipRules` and per-decal-group PaintToolbox construction is now
  deployed as the strongest separately-testable baseline-memory and
  game-creation-freeze optimization; live UI validation remains pending.

Current local experimental DLL state:

```text
Public release: 2.0.14 / commit bf9bb4d2df59929316b5cfdeae83d6257f189998
Local root/package/live DLL SHA-256:
1333ED39D71285D3C7DEFE031CE0428F95CE0750B2C809D0B48749EFB0FE3464
```

The local DLL additionally contains resync receive-buffer/timing work and the
opt-in SP/MP memory reporters. It is uncommitted and unreleased. Both live
flags currently exist:

```text
multiplayer-memory-diagnostics.flag
singleplayer-memory-diagnostics.flag
```

The first SP reporter build crashed at its first minute because it read
`StasisSpawner.IsPreloaded` on a non-preloadable spawner. That exact diagnostic
bug is fixed in the current hash by checking `SupportsPreloading` first in both
SP and MP reporters. The corrected smoke test constructs a non-preloadable
stasis spawner and verifies the guarded helper returns false without invoking
the throwing property. The save itself was not damaged.

### Lazy PaintToolbox picker construction (2026-08-30, post-18:09 session)

Implemented the previously-identified strongest independent baseline-memory
candidate. `PaintToolboxLazyPickerPatch.cs` first adds two narrowly-scoped
Harmony transpilers plus one postfix:

- `PaintToolboxAddDecalsLayersLazyPatch` redirects the single call to
  `PaintToolbox.AddDecalPicker(ShipRules, GameGui, LayoutBox, Func<int>)`
  inside `AddDecalsLayers`'s per-ShipRules `foreach` loop to a static capture
  method that only records the (GameGui, LayoutBox, getLayer) construction
  context — the loop itself still runs once per ShipRules, but each iteration
  is now cheap instead of building a full decal-tab widget subtree.
- `PaintToolboxAddBasePaintLayerLazyPatch` does the same for the single call to
  `AddBaseTexturePicker(ShipRules, LayoutBox)` inside `AddBasePaintLayer`.
- `PaintToolboxOnSelfActivatedLazyPickerPatch` postfixes `OnSelfActivated` —
  confirmed to be the only place `_ship` is ever assigned non-null, and there
  is no code path that changes which ship is being painted without the
  toolbox deactivating and reactivating first (verified: `_ship =` appears at
  exactly two sites in the decompiled class, this assignment and the `null`
  clear in `OnSelfDeactivated`) — and lazily invokes the original, untouched
  `AddDecalPicker`/`AddBaseTexturePicker` for that ship's `ShipRules` on first
  use, tracked per-instance via a `HashSet<ShipRules>` so a class already
  built is never rebuilt.

The follow-up group-level layer reduces the retained graph even after paint
mode has been opened for a mod-heavy ship class:

- `PaintToolboxAddDecalsGroupLazyItemsPatch` passes a null item list through
  the original `AddDecalsGroup` call for normal groups. Vanilla still builds
  and wires the real group button, scroll page, tab selection and draw hooks;
  only its eager `foreach (decal) AddDecalButton(...)` loop is skipped.
- The original decal list is retained in patch-owned pending state. Selecting
  or activating that group invokes the original, untouched `AddDecalButton`
  once for every item, then releases the list and event hooks. Built groups
  remain resident and are never rebuilt or torn down.
- Favorite groups already pass `decals == null` in vanilla and therefore stay
  entirely on their original immediate/dynamic favorite-add/remove path.
- `PaintToolboxSelectDecalTypeLazyItemsPatch` first ensures the ship picker and
  the one pending normal group containing the requested ID exist before
  vanilla performs its widget search. This preserves grab-decal and other
  programmatic selection paths even for a group the player has not opened.
- Live validation exposed one ordering difference: adding a button to an
  already-active lazy page activates it before vanilla attaches the button's
  favorite-star refresh handler, leaving every star at its default-visible
  state. Group construction now temporarily sets only that page's `SelfActive`
  false, creates all items, then restores it. This reproduces vanilla's
  create-while-inactive -> attach handler -> activate sequence without
  changing favorite data or selection state.

This changes no rendered content, no favorite-decal wiring and no per-ship
`_updatingUIState` toggle logic (each lazy call still runs the real method,
which still does its own `Delegate.Combine`). `PaintToolbox` is resolved via
`AccessTools.TypeByName`/`AccessTools.Method`; group state remains outside the
game object in the patch-owned weak-table context.

Both transpilers require an exact single-call-site match (`replaced != 1`
throws) and the whole file requires no changes to `_groupBoxes`/
`_groupButtonsBoxes` or any other original private field — tracking of which
ShipRules have been built lives entirely in the patch's own
`ConditionalWeakTable<object, Context>`, keyed on the toolbox instance.

Release build and the extended smoke test (now also asserting both
transpilers, the `OnSelfActivated` postfix, the group prefix/postfix and the
`SelectDecalType` prefix are installed) pass with zero warnings/errors.
Deployed to package (`Mod/Code`,
`Mod/Source/EmmanimLagFix.Code/PaintToolboxLazyPickerPatch.cs`) and the live
mod after confirming Cosmoteer (PID 7484) had exited. Root/package/live DLL
SHA-256:

```text
1333ED39D71285D3C7DEFE031CE0428F95CE0750B2C809D0B48749EFB0FE3464
```

**The group-level layer is not yet live-validated.** Next launch should confirm
no Harmony/init exception, open paint mode on one ship class, switch through
several normal groups, add/remove a favorite, and use grab-decal on an item in
an unopened group. A same-state baseline/follow-up diagnostic pair should show
only the active/visited groups' item counts; that pair has not been captured.

### Extended multiplayer observation (2026-08-30)

The 2.0.14 host process completed about 3 h 15 min of two-player multiplayer
before the players deliberately left the room. Twenty-nine seconds later, while
constructing a new creative game's paint/decal GUI, the freeze detector recorded
11,519,295,488 bytes in use and a stall longer than ten seconds. The main thread
was creating `TexturePicker.TextureItem` rows through
`PaintToolbox.AddDecalButton`/`AddDecalsGroup`, not running multiplayer network
code or a GC frame. High retained memory likely amplified that large GUI
construction, but the stack does not prove that GC caused the stall.

In a later fresh process, the remote player reportedly became progressively
slower after about 85 minutes; restarting the room/process restored play. The
host log cannot measure the remote process's heap and contains no memory sample
for that interval. Obtain the client's game log and preferably two low-overhead
`gc-collect` traces from the same client process to prove its growth and compare
it with the previously established Gen-2/stasis-ship retention pattern.

An opt-in `multiplayer-memory-diagnostics.flag` was subsequently added to
correlate once-per-minute process/GC memory with MP input, hash, connection and
recording queue sizes. If those queues stay flat while the client heap rises,
the symptom is not a multiplayer transport leak even though it appears only
during long multiplayer sessions. The live host flag alone is useful for a
control, but the slow client's flag/log is the decisive sample.

This file records the live evidence and the exact continuation point for the
long-session slowdown investigation. Do not treat the earlier small-heap startup
sample as evidence that there is no leak; the later same-process comparison below
shows long-lived retention.

### Opt-in single-player correlation (post-2.0.14 local experiment)

When `singleplayer-memory-diagnostics.flag` exists beside the mod's `Code`
directory at process start, a read-only `GameRoot.Update(Action)` postfix logs
one row per wall-clock minute in non-multiplayer games. It records private,
working, managed and GC heap memory, fragmentation, process handles, Gen-0/1/2
collection deltas and process-wide allocation MiB/s. The same row records the
current game/simulation identity, mode and tick, live ship and
physical/blueprint-part counts, total/preloaded stasis spawners and paint decal
picker/item counts. It does not run for `BaseMPManager` games, where the separate
multiplayer diagnostic supplies queue data and the same simulation populations.

Interpretation for a same-process single-player run:

- memory rising with `stasis`/`parts` is live or preloaded world population;
- flat populations with rising `fragmentedMiB` is GC heap fragmentation;
- flat retained memory but high `allocatedMiBs` and frequent Gen-0 collections
  is allocation churn rather than a leak;
- a changed game/simulation identity followed by memory that never settles can
  indicate an old-game lifecycle root and justifies a controlled heap dump;
- stable counts and fragmentation with rising managed/heap memory indicates an
  unmeasured retained graph and is the strongest reason for another same-state
  baseline/follow-up dump pair.

The first live attempt exposed an exact diagnostic-only bug at the first
one-minute report: `StasisSpawner.IsPreloaded` deliberately throws
`NotSupportedException` for spawners that do not support preloading. Both the
single-player and multiplayer reporters now test `SupportsPreloading` before
reading `IsPreloaded`. The save had already loaded and run for 61 seconds at
about 138 FPS; the exception was in the reporter, not save loading or game
simulation, and does not indicate save corruption.

## Freeze evidence

The game log at
`E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\log 2026-08-27 13_51_35.txt`
records a freeze longer than ten seconds at 15:06:46 with 13,406,388,224 bytes in
use. The main thread was in:

```text
ObjectEqualityComparer.IndexOf
BucketList.Remove
SceneComponent.OnDeactivated
SceneNode.NodeComponents.Remove
MultiMediaEffectNode.Reset
SimRoot.ExecuteQueued
```

That sample proves a large media-effect cleanup stall, but not by itself which
object type was retained.

## Same-process comparison

Both samples came from PID 9728. The PID is historical; resolve the current PID
again on every future run. Both traces were 15-second, low-overhead `gc-collect`
captures in a non-combat/paused state.

| Metric | 15:26 baseline | 15:58 follow-up | Change |
| --- | ---: | ---: | ---: |
| Private bytes | 11.722 GiB | 12.495 GiB | +0.773 GiB |
| Working set | 7.656 GiB | 7.975 GiB | +0.319 GiB |
| Managed heap | 6,211,672,400 | 6,440,958,664 | +229,286,264 bytes |
| Gen 2 | 5,833,120,664 | 6,062,353,512 | +229,232,848 bytes |
| LOH | 378,412,920 | 378,412,920 | unchanged |
| GC handles | 2,776,953 | 2,797,857 | +20,904 |
| GC starts / 15 s | 129 | 172 | +33% |
| Gen 0 / Gen 1 / Gen 2 | 128 / 1 / 0 | 171 / 1 / 0 | higher Gen 0 pressure |

The managed growth lands almost entirely in Gen 2. This is leak-like long-lived
retention, not only a large transient allocation burst. It is not yet proven
whether the retained objects are a true unreachable leak or an unbounded live
cache; a heap type/retainer analysis is still required.

Source traces:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_baseline_2026-08-27_15-26-00.nettrace
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_followup_2026-08-27_15-58-14.nettrace
```

The local ignored helper `.tools/gc_trace_summary.ps1` reads `GCStart`,
`GCHeapStats` and sampled allocation events directly with the TraceEvent assembly
bundled in `dotnet-trace`:

```powershell
& .\.tools\gc_trace_summary.ps1 <baseline.nettrace> <followup.nettrace>
```

## Confirmed allocation storm

A ten-second `gc-verbose` trace sampled about 1 GiB of managed allocation. The
dominant sample was about 814.58 MiB across 8,016 allocation ticks attributed to:

```text
System.Func<ID<PartToggleGuiRules>, int?>
```

Trace files:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_allocations_2026-08-27_16-01-48.nettrace
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_allocation_stacks_2026-08-27_16-03-29.nettrace
```

The combined allocation/thread trace identifies these relevant hot methods:

```text
PartsManager.UpdateCallbacks.Update                    1.35% exclusive
BlueprintPartStatProvider.OnUpdate                     0.16% exclusive
BlueprintPartStatProvider.GetToggleMode                present in the sampled path
BaseBlueprintPartNetworkPort.GetToggleMode             also present, much smaller
```

Decompiling
`Cosmoteer.Ships.Blueprints.Logic.Values.BlueprintPartStatProvider` shows the
allocation site in `UpdateOperational`:

```csharp
_operationalToggle.IsBlueprintToggleOn(base.Part.Rules, GetToggleMode)
```

Passing the instance method group constructs a new
`Func<ID<PartToggleGuiRules>, int?>` on every update of every blueprint stat
provider. Large ships multiply this by their provider count and update rate.
Mods QoL adds heat `StatProvider` components to advanced ETT parts, which can
increase the number of affected providers, but the allocation site itself is
vanilla Cosmoteer code.

### First patch and retention correction (2026-08-28)

`EmmanimLagFix.Code/ToggleModeDelegateCachePatch.cs` adds a narrowly-scoped
Harmony transpiler that replaces the `ldarg.0; ldftn GetToggleMode; newobj
Func<...>::.ctor` sequence with a call into a thread-local reusable callback,
so the callback is built once per participating
thread instead of once per tick or once per blueprint component.
This covers both allocation sites: `BlueprintPartStatProvider.UpdateOperational`
(the dominant one, ~815 MiB in the trace below) and the internal
`Cosmoteer.Source.Ships.Blueprints.BaseBlueprintPartNetworkPort.UpdateOperational`
(smaller, same shape). The transpiler throws at patch time — i.e. immediately,
not lazily — unless it finds **exactly one** matching site in the target
method, so a future game update that changes this code disables the
optimization instead of silently patching nothing or the wrong spot.

The first implementation used a per-instance `ConditionalWeakTable`. A live
post-patch trace confirmed that it removed the dominant `Func<...>` allocation,
but it was not lifetime-safe for this workload: blueprint components are
reconstructed continuously. In the same 15-second low-overhead `gc-collect`
window, the old build added 32,312 GC handles and promoted 53,939,344 bytes to
Gen 2. Equivalent pre-patch windows changed handles by only -2 and +2 and
promoted no additional Gen 2 data. The old cache was therefore replaced before
release with one mutable callback target per thread. This retains at most one
component per participating thread and creates no per-component table entry.

Verified with the smoke test (`EmmanimLagFix.Code.SmokeTest`), which also
asserts a Transpiler with the mod's Harmony ID is installed on both target
methods — reaching that assertion without an exception already proves the
single-site match succeeded against the current 0.30.4c build. Build and
smoke test both pass.

**Current deployment state**: the corrected DLL and PDB were deployed to the
live mod on 2026-08-28 after Cosmoteer exited. The live DLL SHA-256 is
`EBD03A709ACCF10B61A0720CD37B4C26BD4AE3D8328FA133866E88D952A52306`, matching
both the Release output and `Mod/Code`. A fresh launch loaded Emmanim Lag Fix
2.0.5 and a representative career save without a Harmony shape-mismatch or
initialization exception in the game log.

### Thread-local live verification (2026-08-28)

The corrected build's fresh 15-second `gc-collect` baseline is:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_threadlocal_baseline_2026-08-28_03-30-35.nettrace
```

Its first-to-last heap delta was only +33,288 bytes total, entirely Gen 1;
Gen 2 changed by 0 bytes and GC handles changed by -10. It recorded 16 GC
starts (15 Gen 0, one Gen 1, no Gen 2). In the rejected per-instance-cache
trace, the equivalent deltas were +53,939,344 Gen-2 bytes and +32,312 handles,
with 290 GC starts. The retention regression is therefore absent from the
thread-local build.

A separate 10-second `gc-verbose` allocation sample is:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_threadlocal_allocations_2026-08-28_03-31-46.nettrace
```

`Func<ID<PartToggleGuiRules>, int?>`, formerly about 814.58 MiB in ten
seconds, was absent from the top 25 sampled allocation types. The largest new
sample was only 21.45 MiB (`Halfling.Geometry.Vector3`). This confirms that the
per-tick toggle delegate allocation remains removed. As with every
`gc-verbose` capture, its handle figures are not retention evidence.

Post-patch allocation trace (rejected per-instance cache build):

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_postpatch_2026-08-28_02-37-38.nettrace
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_postpatch_stacks_2026-08-28_02-43.nettrace
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_postpatch_retention_baseline_2026-08-28_02-51.nettrace
```

The `gc-verbose` profile itself creates large numbers of temporary weak handles
while sampling allocations, so handle deltas inside those traces are diagnostic
overhead and must not be used as leak evidence. The `gc-collect` trace above has
no allocation sampler and is the valid retention comparison. After deploying
the thread-local build, capture a new 15-second `gc-collect` baseline and a
same-process follow-up after at least 30 minutes. The delegate type should also
remain absent from the top allocation list.

## `gcdump` limitation and next leak step

`dotnet-gcdump` 9.0.661903 attaches to the .NET 10 game, but both collection
attempts timed out. The requested induced Gen 2 collection began, while the
runtime continued emitting many ordinary allocation-triggered collections; the
tool never observed completion for its target collection and reported:

```text
ETL file shows the start of a heap dump but not its completion.
```

Do not repeatedly retry `gcdump` during gameplay: it forces GC and caused a
noticeable diagnostic pause without producing a file.

If Gen 2 retention remains after the delegate-cache patch, collect a full heap
dump with `dotnet-dump collect --type Heap`. At the time of this investigation,
drive E: had about 135 GiB free, but expect roughly 8--15 GiB of output and a
pause of tens of seconds or longer. Obtain explicit approval immediately before
collection. Analyze the largest Gen 2 types and GC-root/handle retainers; the
roughly 2.8 million GC handles and their +20,904 growth are the strongest lead.

## Installed tools

Official global tools are under `C:\Users\Nayuri\.dotnet\tools`:

```text
dotnet-counters 9.0.661903
dotnet-gcdump   9.0.661903
dotnet-dump     9.0.661903
dotnet-trace    9.0.661903
```

`dotnet-counters monitor` currently throws a tool-side `NullReferenceException`
against this .NET 10 process, even with `EventCounters\System.Runtime`. Use
`dotnet-trace` plus `.tools/gc_trace_summary.ps1` until that incompatibility is
resolved.

## Full-heap result: separate retained GUI/render graph (2026-08-28)

With explicit approval, a full native heap dump was captured from PID 11932:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_heap_native_2026-08-28_03-10.dmp
13,413,371,581 bytes (about 12.49 GiB)
```

`dotnet-dump collect --type Heap` was incompatible with this .NET 10 process
and left only an incomplete 1.2 MiB dump. A small ignored local helper under
`.tools/heap-dump-writer/` instead invoked Windows `MiniDumpWriteDump` with full
memory, and `dotnet-dump analyze` successfully read the result.

This rejected-build dump and the incomplete `dotnet-dump` file were removed
after a valid corrected-build baseline replaced them, reclaiming
13,414,578,608 bytes. Their measurements below remain as historical comparison
data.

The managed heap contained 63,705,945 objects / 5,428,900,202 bytes. A live
walk found 63,357,603 objects / 4,559,320,864 bytes, so the dominant graph is
reachable after GC rather than merely waiting to be collected. Important live
populations include:

```text
EventHandler<EventArgs>                         5,241,611 / 335,463,104 B
String                                          6,204,525 / 262,677,170 B
Halfling.Graphics.RenderTrees.QuadNode            911,636 / 182,327,200 B
EventHandler<ShaderConstantEventArgs>           2,469,786 / 158,066,304 B
ShaderConstantCollection                         826,579 /  72,738,952 B
Material                                         923,883 /  59,128,512 B
GuiSprite                                        911,636 /  51,051,616 B
WeakEventHandler.EventState<EventArgs>            683,574 /  38,280,144 B
WeakEventHandler.Subscriber<EventArgs>          1,455,168 /  58,206,720 B
```

Each of the 826,579 shader-constant collections also has nine typed
`Dictionary<ShaderConstantID, T>` instances and nine nested constant-
dictionary wrappers. Those repeated dictionaries account for hundreds of MiB
in addition to the collection objects themselves.

Root inspection establishes that this is separate from the rejected Emmanim
delegate cache. A representative runtime path is
`CareerGameModeManager -> PaintToolbox -> TexturePicker -> TextureItem ->
WidgetSingleSprite -> GuiSprite -> QuadNode -> Material`; representative weak
event states are likewise reachable through live GUI/tool-tip/widget trees.
One inspected inactive `ScrollBox<TextureItem>` (`SelfActive = false`, parent
rendering inactive) still retained its parent/root and numerous event fields.
The old per-instance Emmanim `ConditionalWeakTable` is visible as one roughly
4 MiB entry array, but is far smaller than this engine GUI/render/event graph.
`MultiMediaEffectNode` is also only about 15 MiB live and is not the dominant
retainer despite appearing in an earlier freeze stack.

This dump proves a distinct reachable-retention population, but a single
snapshot cannot distinguish legitimate current large-ship/editor UI from
widgets accumulated over time. Do **not** globally clear Halfling weak-event
tables or detach inactive widgets: inactive widgets can be deliberately cached
for later reuse, and blind cleanup would corrupt UI state. The safe next test is
two controlled full dumps from the same corrected build and comparable game
state (baseline after load, then 30--60 minutes later), followed by per-type and
representative-root deltas. That comparison should decide whether the growing
owner is a persistent toolbox/list, ship GUI reconstruction, or another UI
lifecycle path before any Harmony cleanup patch is attempted.

Static decompilation later identified why this graph is already enormous at
baseline. Every `PaintToolbox` constructor calls `AddDecalsLayers`, which loops
over every entry in `GameApp.Rules.Ships`. For every ship ruleset it constructs
all decal group tabs and `AddDecalsGroup` immediately constructs one
`TexturePicker.TextureItem` per decal. Each item owns its sprite/material/text
renderers, selection state, tooltips, alternate-click handlers, favorite-state
handlers, and brush-state subscriptions. This happens while the toolbox itself
is inactive and even if paint mode is never opened.

The corrected-build pair also shows that this is primarily large eager
baseline retention, not the source of that interval's 436 MiB Gen-2 growth:
the main shader/sprite/render populations changed by only about one thousand
objects. A direct `dumpheap -stat -type PaintToolbox` comparison is even more
decisive: both baseline and 35-minute follow-up contain exactly one
`PaintToolbox`, 11,475 `DecalTypeInfo` values, 11,475
`<>c__DisplayClass51_0` button closures and 11,475
`<>c__DisplayClass51_1` closures. The complete PaintToolbox-named population is
identical at 34,552 objects / 2,213,360 shallow bytes in both dumps. Its much
larger transitive sprite/material/widget graph is therefore one stable eager
tree, not repeated toolbox instances.

A safe memory reduction therefore needs lazy construction per
`ShipRules`, not global event cleanup. The narrow candidate is to intercept
`AddDecalPicker` during toolbox construction, retain its original arguments,
and invoke it only for the selected ship's rules on first toolbox activation.
`SelectDecalType` is used by the active grab-decal tool, so construction must
complete before paint input is accepted. Building all buttons incrementally
would reduce the opening freeze but not final retained memory; true lazy
per-rules construction addresses both baseline memory and new-game/resync GUI
construction cost. Keep this as a separately smoke- and UI-tested experiment,
because favorite subscriptions, tab selection, and the `_updatingUIState`
delegate are installed inside `AddDecalPicker`.

### Corrected-build full-heap baseline (2026-08-28)

After loading the representative career save on corrected-build PID 14820, a
new controlled full-memory baseline was captured at 03:33:49:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_heap_threadlocal_baseline_2026-08-28_03-33-49.dmp
11,094,658,890 bytes
SHA-256 268B80D5F61841BA3AAA26715926D2C0A4E0D6F27141554A6F84CCD8A13EF044
```

`dotnet-dump analyze` opened it successfully and reported a
5,328,539,112-byte allocated managed heap. A live walk found 62,486,846
objects / 4,436,883,691 bytes. The comparison populations at baseline are:

```text
EventHandler<EventArgs>                         5,217,709 / 333,933,376 B
EventHandler<ShaderConstantEventArgs>           2,464,293 / 157,714,752 B
ShaderConstantCollection                         824,786 /  72,581,168 B
QuadNode                                         909,782 / 181,956,400 B
GuiSprite                                        909,782 /  50,947,792 B
Material                                         913,151 /  58,441,664 B
WeakEventHandler.EventState<EventArgs>            680,440 /  38,104,640 B
WeakEventHandler.Subscriber<EventArgs>          1,458,355 /  58,334,200 B
```

Each of the nine `ShaderConstantDictionary<T>` wrapper families has 824,786
live instances, as does each corresponding typed dictionary family. These
figures are the corrected-build baseline for the same-process 30--60 minute
follow-up; do not infer a growth rate by comparing only against the earlier
rejected-build long-session dump.

### Corrected-build 35-minute follow-up (2026-08-28)

The same PID 14820 was paused after 35.1 minutes of play. Its low-overhead
follow-up trace and full dump are:

```text
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_threadlocal_followup_2026-08-28_04-09-04.nettrace
E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_heap_threadlocal_followup_2026-08-28_04-09-31.dmp
13,036,417,130 bytes
SHA-256 755D5BC16376466C0CF24159BB8D31B849003F149E94714875D5CD05285F195F
```

The 15-second follow-up window again showed no delegate-cache retention
regression: Gen 2 changed by 0 bytes and GC handles changed by 0. It recorded
40 GC starts versus 16 in the fresh baseline, so allocation/collection pressure
was about 2.5 times higher. Across the two snapshots, the last reported Gen 2
size rose by 436,042,544 bytes and GC handles rose by 132,496; the independent
long-session retention therefore remains despite the successful delegate fix.

The original large GUI/render graph was almost flat over the interval:

```text
Type                                      Baseline   Follow-up      Delta
EventHandler<EventArgs>                  5,217,709   5,666,177   +448,468
EventHandler<ShaderConstantEventArgs>    2,464,293   2,467,329     +3,036
ShaderConstantCollection                   824,786     825,789     +1,003
QuadNode                                   909,782     910,805     +1,023
GuiSprite                                  909,782     910,805     +1,023
Material                                   913,151     919,220     +6,069
WeakEventHandler.EventState<EventArgs>     680,440     682,391     +1,951
WeakEventHandler.Subscriber<EventArgs>   1,458,355   1,455,392     -2,963
```

This rules out the shader-collection/QuadNode/GuiSprite population as the
source of the roughly 436 MiB Gen-2 delta in this interval. The dominant live
growth instead tracked a larger simulation/ship graph:

```text
Type                                                    Live-count delta
Action<CommonBasePart>                                         +564,076
Int32[]                                                        +325,958
BlueprintPart GeoTree node                                     +239,072
DecalState GeoTree node                                        +203,616
Action<Part, int, int, int, int>                               +196,120
ManagedShipQuad                                                +139,843
Part GeoTree node                                              +130,784
BlueprintPart                                                   +77,857
PartDeathEffects                                                +75,306
PartGraphics                                                    +36,641
Part                                                            +53,927
MultiMediaEffectNode                                            +25,200
```

The corresponding live byte deltas among these types were led by
`Action<CommonBasePart>` (+34.43 MiB), `Int32[]` (+32.96 MiB), the blueprint
GeoTree nodes (+23.71 MiB), decal GeoTree nodes (+20.20 MiB),
`ManagedShipQuad` (+13.87 MiB), and `MultiMediaEffectNode` (+13.07 MiB).

Representative `MultiMediaEffectNode` roots do not look like orphaned inactive
effects. Inspected nodes had `SelfActive = true`, a non-null parent and the
current `SimRoot`. Paths ran through current or stasis-managed ships, including:

```text
CareerGameModeManager -> MissionManager -> Dictionary<MetaShip, ...> ->
Ship -> List<SceneNode> -> SceneNode[] -> MultiMediaEffectNode

SimRoot -> SimStasisManager -> HashSet<StasisSpawner> ->
StasisShipSpawner -> Task<Ship> -> Ship -> ... -> MultiMediaEffectNode
```

A separate static ship/effect-batch dictionary also reached the same current
ship. This comparison therefore does **not** justify a GUI cleanup patch or a
blanket media-effect detach patch. The two snapshots were at different points
of actual play, and the retained growth is dominated by more live ship,
blueprint, part, decal and effect state. The next experiment must hold the
loaded ship/stasis population constant, or explicitly test whether those
populations shrink after leaving/reloading the career session, before calling
this an unbounded lifecycle leak.

#### The growth is stasis preload, not an unbounded owner leak

Direct inspection of the same active `SimStasisManager` in both dumps explains
the ship-graph delta:

```text
Metric                                Baseline   Follow-up      Delta
preloaded stasis spawners                   47         538       +491
root locations                               3           3          0
temporary preload points                     0           0          0
live Part count                         77,232     131,159    +53,927
mission ship-association cache              11          14         +3
```

The 53,927 additional live parts divided by 491 additional fully preloaded
ships is 109.8 parts per ship. This also explains the proportional growth in
blueprints, GeoTrees, callbacks, managed ship quads and media-effect nodes.
The active stasis manager held about 28,162 spawners total at follow-up, but
only the 538 inside preload range retained fully constructed `Ship` graphs.

The ownership code has the expected cleanup paths. `MissionManager.Update()`
calls `ExpireMissionAssociationCaches()` every frame; cache keys unused for ten
seconds are removed. `SimStasisManager.UpdateStasis()` calls `CancelPreload()`
when a spawner leaves the preload cells, and both `SerializedStasisShip` and
`StasisShipSpawner` dispose the completed ship when cancelling. The observed
increase is therefore a bounded, location-density-dependent preload cache, not
evidence that old ships fail to detach.

Vanilla exposes the narrow data-side control in `Data/cosmoteer.rules`:

```text
StasisCellSize = 2500
StasisLiveRange = 2500
StasisPreloadRange = 3750  // 1.5x live range
```

Reducing only `StasisPreloadRange` is the safest experiment: it leaves the
actual live/spawn radius unchanged and lets the existing cancellation/disposal
path release farther preloaded ships. It trades memory for less advance time
to build ships in the background; setting it equal to the live range risks
synchronous spawn hitches. A first test at 3000 (1.2x live range) preserves a
500-unit preload lead and, under uniform density, would reduce the preloaded
area by about 36%. Do not implement a hard global ship cap or detach active
scene nodes before testing this existing engine knob.

The 3000 override was added to package/live Emmanim Lag Fix 2.0.6 after the
game exited. Release build and the full Harmony smoke test pass. It requires a
fresh game launch before measurement; compare the settled preloaded-spawner
count at the same saved location against the prior 538 and watch for new
approach/spawn hitches.

## Continuation checklist

1. Preserve and read the remainder of `log 2026-08-30 17_14_57.txt` after the
   current PID 7484 exits. Compare the last 10--15 rows with the 17:51--18:05
   plateau before calling any later rise a leak.
2. For retained-memory proof, use another 15-second low-overhead `gc-collect`
   window only after holding ship, part and stasis-preload counts reasonably
   constant. Do not use `gc-verbose` handle deltas as retention evidence.
3. For allocation attribution, capture a short `gc-verbose` plus CPU trace
   during a representative 150+ MiB/s interval and resolve stacks beneath the
   remaining vector/matrix/color samples. This is the primary next
   single-player performance investigation.
4. Treat `GraphicsManager.RefreshShaderConstants` as an audit lead, not an
   approved throttle target. Shader changes can be render-critical.
5. Design the PaintToolbox experiment separately: defer `AddDecalPicker` per
   `ShipRules` until first paint-tool activation, build the active ruleset
   before accepting paint input, and preserve favorites, tab selection,
   `SelectDecalType` and `_updatingUIState`. Incremental construction alone
   reduces a freeze but does not reduce final retained memory.
6. Keep `StasisLiveRange = 2500` and the already-deployed
   `StasisPreloadRange = 3000`. Do not add a hard ship cap or a long-lived
   resource/path cache.
7. Do not globally clear weak events, inactive widgets, media effects or MP
   queues. Existing heap roots lead to current/stasis-managed objects, and
   blind cleanup can corrupt UI or simulation state.
8. For the reported slow multiplayer client, copy the current experimental mod
   and enable the MP diagnostic on that client. Host-only rows cannot prove the
   remote heap trend.
9. Keep both corrected-build full dumps until the lazy-paint and allocation
   questions are resolved:
   `memory_heap_threadlocal_baseline_2026-08-28_03-33-49.dmp` and
   `memory_heap_threadlocal_followup_2026-08-28_04-09-31.dmp`.
10. The repository is intentionally dirty with post-2.0.14 resync and memory
    diagnostic work. Do not reset it or overwrite unrelated changes. Keep root
    source, `Mod/Source`, package DLL and live DLL synchronized after the game
    exits; never replace the live DLL while Cosmoteer is running.

## 2026-09-02 20:48 KST — 2.0.24 멀티플레이 종료 직전 측정 (다음 세션 시작점)

같은 프로세스 PID 16300 을 68분 시점과 135분 시점에 각각 20초 CPU + 15초 gc-verbose 로 측정.

부하가 2.6~3.7배 늘었는데 tick 은 절반만 떨어졌다. **이 세션에서 프로세스 노화는 원인이 아니다.**

| | 19:42 (68분) | 20:48 (135분) |
| --- | ---: | ---: |
| 함선 / 파츠 | 216–333 / 25.7k–37.7k | 400–646 / 67.7k–95.1k |
| tick/s (목표 30) | 14.4–18.2 | 9.7–12.6 |
| private / heap | 9.02 / 2.89 GiB | 10.75 / 3.99 GiB |
| 할당률 | 50 MiB/s | 46.4 MiB/s |

### 버벅거림은 평균이 아니라 꼬리에 있다

사용자 표현: "프레임과 별개로 버벅거림이 심한 게 문제". 메인 스레드 구간 분포가 정확히 그 모양이다.

```
GameRoot.Draw       n=2227  p50=2.0ms  p90=3.9  p99=23.9  max=59.2
GameRoot.Update     n= 717  p50=2.6ms  p90=9.0  p99=34.4  max=44.3
SimRoot.FixedUpdate n= 331  p50=5.5ms  p90=9.0  p99=35.0  max=42.2
```

중앙값 2~5ms 는 정상이고 p99 가 그 10~15배다. 평균 FPS 를 지표로 삼으면 이 문제는 보이지 않는다.
메인 스레드 배타적 시간 상위: `PollGCWorker` 2,075ms / n=908 (초당 45회 정지),
`Monitor.Enter_Slowpath` 1,316ms / n=618, `set_Shader` 573ms, `OnCurrentTextureConstantChanged` 567ms.

30ms 이상 스톨의 원인은 서로 다른 세 갈래다. 할당을 전부 잡아도 2·3번은 남는다.

1. 할당이 GC 를 부르고 그 GC 가 메인 스레드를 세움 — 스택 말단이
   `Dictionary.Resize` / `List.CopyTo` / `List<Vector2>.AddWithResize -> Array.Copy` -> `PollGCWorker` (29~36ms).
2. 스타시스 함선 동기 스폰 — `SimStasisManager.UpdateStasis -> StasisDoodadSpawner.Spawn ->
   SimShipsManager.Add -> PolygonCollider.AddCollider` 36.9ms. `stasis=11307`, 4분에 함선 400->646.
3. 렌더 제출 — `OnScenePreDrawCallback` 55.7/44.2ms, `ShipPrimitiveBatch.Draw -> DrawInterleaved` 53.9ms.

### 할당 표적은 둘이고 크기가 거의 같다

15초 7,129 샘플 ≈ 696 MiB. 할당하는 메서드 기준:

```
28.1%  196 MiB  GraphicsManager.RefreshShaderConstants
12.2%   85 MiB  XmlTextReaderImpl.FinishInitTextReader
 5.6%   39 MiB  ShaderConstantCollection..ctor
 3.0%   21 MiB  TextBuilder.BuildLines
 2.9%   20 MiB  List<TextChar>.AddWithResize
 2.7%   19 MiB  List<Quad>.AddWithResize
 2.1%   15 MiB  XmlTextReaderImpl..ctor
```

- **셰이더 상수** — 기준선 44.9% 에서 28.1% 로 비중은 내려갔지만 여전히 단일 1위.
  원인은 `D3D11BufferConstant.IsDataDirty<T>` 의 `box !!T` + `constrained. callvirt object::Equals(object)`
  (IL 로 확인 완료). 수정안은 비제네릭 `Update(...)` 8개 오버로드에 프리픽스를 걸어
  타입드 `Equals(in T)` 를 호출하는 것. 관련 값 타입은 모두 `Equals(object)` 를 `Equals(in T)` 로 위임하므로
  의미 보존이 자명하고, 렌더 경로 전용이라 락스텝과 무관하다.
- **GUI 텍스트 XML 재파싱** — 호출자는 `TextBuilder.ParseXmlToLines`. Halfling 이 UI 텍스트 마크업을
  매 위젯 갱신마다 `XmlTextReader` 를 새로 만들어 파싱한다. `WidgetTextRenderer.OnRefresh` 포함 기준
  28.6% / 199 MiB 로 셰이더 상수와 동급. 착수 전에 `TextBuilder` 를 디컴파일해
  캐시 무효화 조건(텍스트/폰트/폭 변경)을 먼저 확정할 것.

### 다음 세션 순서

1. 셰이더 상수 박싱 패치 — 원인이 확정돼 있으므로 바로 구현 가능. 트랜스파일러가 아니라
   오버로드별 프리픽스이므로 `PrepareMethod` 검증은 불필요하지만, 가드 통과 플래그는 스모크에서 확인할 것.
2. `TextBuilder` 디컴파일 후 텍스트 파싱 캐시.
3. 1·2 이후에도 남을 스타시스 동기 스폰(36ms)과 렌더 제출(55ms)은 별도 과제.

기준선 메모는 임시 폴더 `scratchpad/mp_baseline_1942.md` 에 있었고 트레이스 파일
(`mp_end.nettrace`, `mp_end_alloc.nettrace`) 도 같은 폴더라 정리되면 사라진다. 위 수치가 보존본이다.

## 2026-09-02 23:20 KST — 2.0.25 두 할당 지점 구현 (미배포)

위 두 표적을 모두 구현했다. 이후 `PartGraphics` 색상 이벤트와 함께
2.0.25 로 게시되었다 — 아래 2026-09-03 항목의 릴리스 기록을 볼 것.

### `ShaderConstantBoxingPatch`

- 대상은 `Halfling.Graphics.D3D11.D3D11Shader+D3D11BufferConstant` 의 비제네릭
  `Update(...)` 8개. 타입 인자는 `RawBool`(SharpDX), `int`, `float`,
  `Halfling.Geometry.Vector2/3/4`, `Halfling.Graphics.Color`, `Halfling.Geometry.Matrix`
  로 IL 에서 확인했다.
- 프리픽스가 아니라 **트랜스파일러**로 갔다. 선언 타입이 `internal` 클래스의 중첩 타입이고
  그 어셈블리(`HalflingPlatformWDX`)를 이 프로젝트가 참조하지 않으므로 C# 에서
  `__instance` 도 파라미터 타입도 이름 지을 수 없다. 트랜스파일러는 `call IsDataDirty<T>`
  한 줄만 바꾸고, `T` 는 호출부 자신의 제네릭 인자에서 가져온다. 그 지점의 평가 스택은
  이미 `(this, ref T)` 라 대체 대상의 시그니처와 정확히 일치한다 — 제어 흐름도 지역 변수도
  건드리지 않는다.
- 슬롯 주소 `_bufState.Data + _bufOffset` 은 바닐라와 같은 식을 `DynamicMethod` 로
  한 번만 컴파일해 쓴다. 호출마다 리플렉션으로 읽으면 없애려는 박싱보다 비싸다.
- `PatchedCount` 는 모든 형태 검사를 통과해 실제로 재작성된 오버로드 수. 스모크가 8 을 단언한다.
  설치 여부만 보는 검사는 무의미하다 — 폴백한 트랜스파일러도 설치는 되어 있다.
- 스모크는 의미 동등성도 직접 검증한다. 재작성된 각 타입에 대해 0 바이트/0x3F 바이트로 채운
  실제 값 네 쌍을 만들어 박싱 `Equals(object)` 와 타입드 `Equals` 의 결과가 일치하는지 본다.
  8개 전부 통과.

### `TextBuilderPlainTextPatch`

- `TextBuilder` 에는 캐시가 **없다**. `BuildLines` 는 매번 새로 만든다. 다만
  `WidgetTextRenderer.OnRefresh` 는 이미 `_needsRefresh` 로 게이트돼 있으므로
  "매 프레임 전부 다시" 가 아니라 "갱신이 매우 잦다" 가 맞다. 그래서 캐시가 아니라
  **분기 조건**을 고쳤다.
- `BuildLines(bool xmlFormatting)` 의 IL 에서 `ldarg.1` 은 정확히 한 번, 분기 테스트로만
  읽힌다(`IL_003e: ldarg.1 / IL_003f: brtrue.s`). 그 뒤에 `ldarg.0`, `call NeedsXmlParse`
  두 명령을 끼워 `xmlFormatting && !IsPlainText(Text)` 로 바꾼다.
- 동등성 근거: `ParseXmlToLines` 는 루프 뒤에 아무 코드도 없고(디컴파일로 확인),
  텍스트 노드 하나뿐인 입력에서 실행되는 본문은
  `AddTextToLines(lines, reader.Value, stateStack.Peek(), maxWidth, ref prevChar)`
  단 한 줄이다. 스택 최상단은 평문 분기가 넘기는 바로 그 상태이고 `prevChar` 는 양쪽 다 null 로 시작한다.
- `IsPlainText` 는 의도적으로 좁다. 거부하면 바닐라 경로로 갈 뿐이라 비용이 없고,
  틀리면 화면이 깨진다. 거부 조건: `<`, `&`, `\r`(XML 은 텍스트 노드 안의 줄바꿈을 정규화한다),
  XML 1.0 불법 문자, 서로게이트, 1024자 초과(`XmlTextReaderImpl` 이 긴 텍스트를 여러 노드로
  쪼갤 수 있다).
- 스모크는 실제 `XmlReader` 를 게임 자신의 `XML_READER_SETTINGS` 로 돌려, 통과시킨 모든 문자열이
  정말 텍스트 노드 하나로 원문 그대로 돌아오는지 확인한다. 한국어/탭/개행/따옴표/`>`/`%` 통과,
  마크업·엔티티·CRLF·제어문자·이모지·4096자 거부.

### 상태

- `dotnet build -c Release` 경고 0 / 오류 0, 스모크 PASS.
- 재작성된 메서드는 `RuntimeHelpers.PrepareMethod` 강제 컴파일 목록에 추가했다
  (`Update` 8개 + `BuildLines`). 잘못된 IL 은 게임 안이 아니라 스모크에서 터진다.
- 아직 하지 않은 것: 패키지/라이브 DLL 교체, `Mod/mod.rules` 버전 표기, 실측 재트레이스.
  배포 후 같은 세이브·같은 위치에서 15초 `gc-verbose` 를 떠서
  `RefreshShaderConstants` 와 `XmlTextReaderImpl` 이 상위에서 사라졌는지 확인할 것.
  비교 전에 진단 로그의 `parts=` 를 먼저 읽어 장면 규모가 맞는지 확인한다 —
  규모가 다른 트레이스는 아무것도 증명하지 못한다.

## 2026-09-03 — 렌더 제출 경로: `PartGraphics` 색상 이벤트 (미배포)

스타시스 스폰을 먼저 검토했으나 **표적으로 기각**했다. 근거:

- 무거운 절반은 이미 비동기다. `ShipDoodadRules.StartAsyncSpawn` 이 `CreateShip`
  (역직렬화 / 청사진 로드 / `MatchPhysicalToBlueprints` / `InitializeToMax`) 을
  `SingleThreadTaskQueue` 에서 돌린다. 메인 스레드에 남는 건 `SpawnShip` →
  `sim.Ships.Add` → `ship.Parent = Sim` 의 씬 활성화뿐이고, 그 안의
  `PolygonCollider.AddCollider` → `ShipPhysics.AddCustomCollider` 는 콜라이더마다
  `new PolygonShape` + `_fixtureTree.AddGetKey` 하나씩인 실제 설정 작업이다.
  셀 픽스처는 `ProcessUpdatedParts` 가 이미 10×10 까지 탐욕적 사각형 병합을 한다.
  없앨 낭비가 없고, 씬 활성화는 스레드 안전하지 않아 옮길 수도 없다.
- `UpdateStasis` 는 `SimStasisManager.FixedUpdate` 에서 돈다. 결정론적 락스텝
  상태이므로 무엇이 언제 스폰되는지를 바꾸면 무결성 해시가 바뀐다.
- 틱 분산(스폰 개수 상한)은 안전하지 않다. `StasisUpdateInterval = 1.0` 이라
  1초 단위 팝인이 되고, 더 결정적으로 `ForceSpawnsNear(dest)` 는 반환 즉시 그
  지점 주변이 전부 라이브라고 가정한다 — FTL 로컬 점프가 이를 `using` 으로 감싸고
  안에서 `GetJumpInLoc(destCircle, ...)` 으로 착지점을 고르며, 섹터 진입 경로도
  같다. 상한을 두면 아직 뜨지 않은 함선 위로 착지할 수 있다.

### 측정: 드로우 CPU의 19.9%가 델리게이트 제거

`Logs/mp_servergc_long_cpu_2026-09-01.speedscope.json` (20초, 5.7시간 경과 프로세스,
전체 CPU_TIME 118,781 ms, `Director.DoDraw` 7,379 ms). Speedscope 내보내기는
**이벤트 형식**이므로 스택을 유지하며 걸어야 하고, 리프가 `CPU_TIME` 인 구간만
집계해야 한다. 드로우 서브트리 self 시간 상위:

```
1,469.5 ms  19.9%  MulticastDelegate.RemoveImpl
  834.4 ms  11.3%  SceneComponent.IRenderableSceneObject.Draw
  660.1 ms   8.9%  RuntimeTypeHandle.InternalAllocNoChecksWorker
  406.1 ms   5.5%  Monitor.Enter_Slowpath
  353.9 ms   4.8%  CastHelpers.ChkCastClass
```

`RemoveImpl` 의 **100%** 가 단일 경로다:

```
SceneRoot.Draw -> SceneComponent.Draw -> PartGraphics.UpdateColor
  -> SceneComponent.remove_BeforeDraw -> MulticastDelegate.RemoveImpl
```

반대편 `CombineImpl` 은 같은 창에서 **2.1 ms** 뿐이다 (경로:
`PartGraphics.OnColorChanged -> add_BeforeDraw`). 700배 비대칭의 원인은
`Combine` 이 memcpy 인 반면 `Remove` 는 호출 목록을 델리게이트 동등 비교로
선형 탐색하기 때문이다. `Delegate.Equals` 107.5 ms + `MulticastDelegate.Equals`
70.5 ms 가 그 비교 비용이고, 합치면 드로우의 약 22%다.

역산: 비교 하나 ~5 ns, 목록 길이 N ≈ 수천이면 제거 하나 ≈ 25 µs → 20초에 약
58,000 회, 초당 2,900 회, 프레임당 약 58 부품이 "정착"한다는 뜻이다. 즉 목록은
이미 길고 바닐라도 이미 그 N 개를 매 프레임 호출하고 있다.

### 상태 기계와 불변식

`_colorUpdateStatus` 를 건드리는 곳은 `PartGraphics` 안에 정확히 네 군데뿐이다
(`OnColorChanged`, `UpdateColor` 의 정착 분기, `UpdateColor` 끝의 Dirty 해제,
`OnPartDetaching`). 불변식은 `Registered ⟺ 핸들러가 호출 목록에 있음`.

`PartGraphicsColorEventPatch` 는 `UpdateColor` 에 두 가지 정확한 재작성을
**전부 아니면 전무**로 적용한다.

(a) 더티 검사 디박싱. `Enum.HasFlag` 는 `Enum` 을 받으므로 IL 이 양쪽 피연산자를
    박싱한다 (`IL_0006`, `IL_000c`) — 프레임마다 핸들러 호출당 두 할당. 같은
    리터럴에 대한 `and` 로 바꾼다.

(b) 자기 구독 해제 블록(`IL_0018`–`IL_0043`) 삭제. 조기 `ret` 은 남는다.

(b) 만 하면 정착한 핸들러가 영구히 프레임마다 두 번 박싱하므로, 탐색 폭주를
영구 할당률과 맞바꾸는 셈이 된다 — 의도의 정반대다. 그래서 둘은 함께 적용된다.

(b) 가 플래그 해제까지 같이 지우므로 불변식이 유지된다. `OnColorChanged` 는
`Registered` 가 여전히 서 있는 것을 보고 재구독을 건너뛴다 — 핸들러가 실제로
아직 목록에 있으므로 옳다. `OnPartDetaching` 은 여전히 `Registered` 를 보고
정확히 한 번 제거하므로 누수가 없고, 부품 하나가 기여하는 항목은 최대 하나라
목록 길이는 함선 부품 수로 유계다.

**바닐라 자신이 이미 이 패턴을 쓴다.** 형제 클래스
`PartToggledBlendSprites.UpdateColor` 는 구독을 아예 해제하지 않고 평범한
`bool _needsColorUpdate` 로 조기 반환한다(IL 55바이트, `remove_BeforeDraw` 없음).
따라서 "구독을 남긴다" 는 새 발명이 아니라 엔진 자신의 관용구다.

분리 비용에 대한 반론 검토: `OnPartDetaching` 은 `Part.RemoveComponent(s)` 와
`Part.OnDetaching` 에서 오고, 후자는 개별 부품 제거(파괴/해체/교체) 경로다.
함선 통째 폐기는 렌더러와 그 이벤트 딕셔너리를 함께 버리므로 부품별 제거를
돌지 않는다. 파괴 속도는 위에서 잰 초당 2,900 회 churn 보다 훨씬 낮다.

### 스모크

- `Applied` 는 두 재작성이 모두 성공했을 때만 선다. 설치 여부만 보는 검사는
  무의미하다 — 폴백한 트랜스파일러도 설치는 되어 있다.
- IL 이 말해주지 않는 산술 가정을 실제 타입에 대고 확인한다:
  `ColorUpdateFlags` 의 기반 타입이 `int32` 인지, `Dirty == 1`(단일 비트라
  마스크가 곧 `HasFlag`), `Registered == 2`(바닐라 해제 마스크가 -3).
- `OnPartDetaching` 이 바닐라로 남았는지 단언한다. 그 구독 해제가 목록 길이를
  유계로 만드는 유일한 장치다.
- 재작성된 `UpdateColor` 를 `RuntimeHelpers.PrepareMethod` 강제 컴파일 목록에
  넣었다. 잘못된 분기나 스택 불균형은 게임 안이 아니라 스모크에서 터진다.
- 형태 불일치 사유는 `FailureReason` 에 남고 스모크 메시지에 그대로 나온다.
  `Logger.Log` 는 게임 없이 초기화되지 않으므로 try/catch 로 감쌌다.

### 상태

- `dotnet build -c Release` 경고 0 / 오류 0, 스모크 PASS.
- 릴리스 2.0.25 게시. 커밋 `65136f1`, 태그 `v2.0.25`, Actions 실행
  33652438058 이 태그/페이로드 검증, 아카이브 구성, 아카이브 검사, 릴리스 게시를
  모두 통과했다. 자산은 `Emmanim-Lag-Fix-2.0.25.zip` (1,317,618 바이트), SHA-256
  `C78417763F34F760018C0E93E6DA3CCE59B84058DF8B47FFF9907C51019F811C`.
  공개 주소: `https://github.com/EnYuri/Emmanim-Lag-Fix/releases/tag/v2.0.25`.
  패키지 `Mod/Code/EmmanimLagFix.Code.dll` SHA-256
  `2D29F9CF430FACAABCB9A66421FD3C62BC8F470D2732E09500145AFF5513A160`.
- **라이브 모드는 아직 2.0.24 다.** 게시 시점에 Cosmoteer PID 11568 이 실행 중
  (워킹셋 약 6.99 GiB) 이라 로드된 DLL 을 교체할 수 없었다. 게임 종료 후
  `Mod/Code/EmmanimLagFix.Code.dll` 과 `.pdb` 를
  `Mods/emmanim_lag_fix/Code/` 로 복사하고, 세 개의 새 `.cs` 를 라이브 소스
  번들로 미러링하고, 라이브 `mod.rules` 의 `Version` 을 2.0.25 로 올릴 것.
- **이 릴리스는 실제 게임에서 검증되지 않았다.** 세 패치 모두 형태 가드와
  스모크(의미 동등성 + `PrepareMethod` 강제 컴파일)를 통과했지만 실행 중인
  게임에서 한 번도 돌지 않았다. 배포 후 첫 실행에서 로더/Harmony 예외가 없는지,
  선박 색상(손상/건조/토글)과 GUI 텍스트가 정상인지 먼저 눈으로 확인할 것.
  그 다음 같은 세이브·같은 위치에서 20초 CPU 트레이스와 15초 `gc-verbose` 를
  떠서 `MulticastDelegate.RemoveImpl`, `RefreshShaderConstants`,
  `XmlTextReaderImpl` 이 상위에서 사라졌는지 본다. 비교 전에 진단 로그의
  `parts=` 와 프로세스 경과 시간을 먼저 맞출 것.
- 아직 하지 않은 것: 패키지/라이브 DLL 교체, `Mod/mod.rules` 버전 표기, 재측정.
  재측정은 같은 세이브·같은 위치에서 20초 CPU 트레이스를 떠서 `RemoveImpl` 이
  드로우 서브트리 상위에서 사라졌는지 본다. 프로세스 경과 시간이 비슷한
  트레이스끼리만 비교할 것 — Harmony 의 `CompileMethodHook` 이 3분 시점 8.2%,
  11분 시점 7.2% 로 초반 트레이스를 오염시킨다.

### 같은 트레이스에 남은 드로우 비용 (미착수)

`InternalAllocNoChecksWorker` 660 ms(할당 자체), `Monitor.Enter_Slowpath`
406 ms, `ChkCastClass` 354 ms, `GraphicsManager.set_Shader` 260 ms,
`CreateGraphicsArray` 244 ms. 그리고 프로세스 전체로는 `SpinWait.SpinOnce`
30,734 ms + `PollGCWorker` 46,365 ms 가 여전히 지배적이다 — 16개 `FastParallel`
워커가 블록하지 않고 스핀하므로 모든 GC 정지가 그 수만큼 곱해진다. 레버는
여전히 할당률이다.
