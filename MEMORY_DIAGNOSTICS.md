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

### Recommended first patch

Implement a narrowly-scoped Harmony transpiler for
`BlueprintPartStatProvider.UpdateOperational` that replaces the repeated
`ldftn GetToggleMode` + `newobj Func<...>` construction with an instance-cached
delegate. The target type is internal, so locate it through `AccessTools` and
use a `ConditionalWeakTable<object, Func<ID<PartToggleGuiRules>, int?>>` (or an
equivalent lifetime-safe cache). Verify the exact IL and require exactly one
replacement; fail initialization if the shape changes.

This should remove most short-lived allocation and Gen 0 GC pressure. It does
not by itself explain the observed Gen 2 and GC-handle growth, so re-run a fresh
same-process baseline/follow-up after implementing it.

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
