# Cosmoteer long-session memory diagnostics

Last updated: 2026-08-27, Cosmoteer 0.30.4c, Emmanim Lag Fix 2.0.5.

This file records the live evidence and the exact continuation point for the
long-session slowdown investigation. Do not treat the earlier small-heap startup
sample as evidence that there is no leak; the later same-process comparison below
shows long-lived retention.

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

The corrected build is live on PID 14820 and its baseline/follow-up pair has
been captured. Resume as follows:

1. Keep the current baseline/follow-up dumps until any additional root checks
   against the simulation/ship graph are complete.
2. Launch the deployed data-only `StasisPreloadRange = 3000` override at the
   same saved location, allow preload to settle, and compare the active
   stasis manager's preloaded count and process memory. Keep
   `StasisLiveRange = 2500` unchanged.
3. Do not implement global weak-event, inactive-widget or media-effect cleanup:
   the current roots lead to active/current or stasis-managed ships, and blind
   detachment would corrupt live simulation state.

Repository changes at handoff are intentional and uncommitted. In particular,
keep the source and `Mod/Source` copies of `ToggleModeDelegateCachePatch.cs`
identical, and do not overwrite unrelated worktree changes.
