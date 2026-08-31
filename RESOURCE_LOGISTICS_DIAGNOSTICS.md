# Resource logistics and path-search diagnostics

Last updated: 2026-08-29, Cosmoteer 0.30.4c.

This document records the large-storage/resource-logistics investigation, the
patches tried so far, and the behavioral constraints for future work. Read it
before adding any resource-location or path cache. The current evidence does
not justify retaining resource locations or routes across simulation ticks.

## Required behavior

The optimization must not lower crew, manipulator-beam, factory, construction,
or resource-search work rates beyond the mod's existing `.rules` settings. It
must also preserve:

- displayed and confirmed multiplayer resource counts;
- resource depletion and newly arriving resources;
- manual resource movement and storage bans;
- alliance/enemy filtering;
- traffic, doors, reachability and path-contiguity checks;
- deterministic ordering and lockstep simulation behavior.

Resources routinely move, run out, arrive in another location, or are moved by
the player. A long-lived cache keyed only by ship topology is therefore unsafe.
Changing a corridor or entering blueprint mode is not a sufficient invalidation
rule.

## Controlled observations

The representative slow ship is an 11,563-part factory/warehouse
megastructure. At 4x speed its original sector ran near 20 FPS. A non-factory
megawarship of comparable scale ran near 120 FPS, proving that hull size alone
is not sufficient to explain the slowdown.

Removing the warehouse-role ship while leaving a factory ship in the same
sector raised observed performance to roughly 100 FPS. Removing the remaining
factory ship and leaving only a cockpit removed most residual resource and
converter work. Conversely, keeping the enormous warehouse hull and storage
components while emptying resources and removing user-identified factories
remained expensive. Numeric resource quantity is therefore not required for
the bottleneck: registered storage tiles, sinks, sources and their network
topology are the important multipliers. Empty demand can still search.

Heat diffusion was a separate major cost on the same ship. Disconnecting the
wide heat network removed its diffusion frames, but resource source-search,
sink-job and path-contiguity work remained. Do not conflate the sparse-heat
patch with the resource-logistics work documented here.

## Vanilla resource-search shape

Each selected resource sink clears its candidate list and repeats source
validation, traffic-aware tile/path-set traversal, path-contiguity checks and
distance sorting. Large modded storage parts amplify this unexpectedly. A 5x5
`FlexResourceGrid` creates 25 `ResourceTile` objects, and those tiles can
register and search independently even though the player sees one physical
storage part.

The opt-in `ResourceSearchDiagnosticsPatch` identified storage tiles on ship
`C58BFDC7` as the dominant sinks, especially:

- `znayuri.lightweight_storage_5x5`;
- `SirCampalot.dpmstorage_5x5` and `SirCampalot.dpmstorage_5x4`;
- carbonsteel, tristeel and `SW.durasteel` searches.

Representative ten-second cumulative rows included 7,683 ms over 4,605
lightweight-storage carbonsteel searches and 6,035 ms over 3,019 DPM-storage
carbonsteel searches. Those searches returned only about two to four candidates
on average. The expensive part is repeatedly discovering and validating a
small result, not processing a huge returned list.

The diagnostic flag was removed after capture. The diagnostic code remains
inert unless `resource-search-diagnostics.flag` exists in the live mod root.

## Attempt 1: fixed-update aggregation cache — rejected

Version 2.0.11 cached repeated allied-ship `PerShipCount.GetCount` aggregation
only during one `ResourceManager.FixedUpdate`, invalidating it before every
`AddCount`. It did not retain paths or resource locations across ticks.

The exact-state comparison proved it was a regression. A shared cache lock
added more contention than the repeated aggregation it removed:

| Metric | Original 2.0.10 | Cached 2.0.11 |
| --- | ---: | ---: |
| `PerShipCount.GetCount` / patched wrapper | 1.30% | 2.64% |
| `Monitor.Enter_Slowpath` | 1.76% | 3.07% |
| `UpdateSinkJobs` | 1.40% | 2.68% |
| Resource fixed update | 2.20% | 2.61% |
| `GameRoot.Update` | 2.87% | 3.26% |

The implementation was removed completely in the 2.0.12 hotfix. Do not revive
another shared mutable cache around this read-heavy path.

## Attempt 2: copy-on-write `PerShipCount` snapshots — successful experiment

`EmmanimLagFix.Code/PerShipCountLockFreePatch.cs` replaces the private locked
list with immutable copy-on-write snapshots:

- reads take one volatile snapshot and use no exclusive lock;
- mutations publish a complete replacement with `CompareExchange`;
- concurrent additions are retried without losing updates;
- entry order, displayed/confirmed accumulation and allied/enemy sums match
  vanilla;
- dead weak-reference slots retain vanilla-style reuse and cleanup behavior;
- no resource location, candidate result, route or path is cached.

The smoke test verifies patch resolution, displayed/confirmed accumulation and
2,000 concurrent additions without lost updates. On the original megastructure
trace, compared with the original 2.0.10 state:

| Metric | Original | Lock-free experiment |
| --- | ---: | ---: |
| `PerShipCount.GetCount` | 1.30% | not sampled |
| `Monitor.Enter_Slowpath` | 1.76% | 0.85% |
| `UpdateSinkJobs` | 1.40% | 0.55% |
| Path-contiguity search | 0.42% | 0.40% |

The direct count-lock and sink-job improvements are clear. Total update time
was roughly flat in that short window because unrelated live heat, factory and
simulation work varied, so do not claim the percentages above as an equivalent
whole-game FPS increase.

A ten-second allocation sample attributed only about 2.03 MiB to the new
`Entry[]` allocations. The first 15-second low-overhead capture had no Gen-2 or
LOH growth. Later ordinary gameplay continued to behave correctly and the user
reports a major practical improvement. A brief freeze observed in that session
was a pre-existing Cosmoteer behavior and is not evidence against this patch.

Current status: the lock-free implementation is included in release 2.0.13.
The root source, bundled source, packaged binary, and live installation were
synchronized after the game exited.

## What remains expensive

The lock-free patch removes the count-list lock; it does not remove actual
source discovery or geometric path work. The remaining high-value target is
same-moment duplication caused by the many tiles of one physical storage part.

A 2026-08-29 code-shape audit found that those searches are similar but not
identical enough to share wholesale. Every `FlexResourceGrid.ResourceTile` has
its own one-cell delivery destination, preferred/current resource type,
capacity, anticipated delivery, priority and traffic-aware distance. Candidate
ordering can therefore legitimately differ between two tiles of the same 5x5
part. Keying a cache only by physical part and resource type would change job
selection. No such cache was implemented.

The safest next design to investigate is a strictly fixed-update-local sharing
layer for identical storage-part/resource searches. It may reuse discovery
work only while all relevant state is known unchanged, and every returned
source must still pass the original current inventory, relationship,
reachability, traffic and path validation. If those invariants cannot be
demonstrated, retain the vanilla search.

Do not implement any of the following without a new invalidation proof:

- multi-second resource-location caches;
- route caches invalidated only by construction or corridor changes;
- assuming an empty source remains empty;
- assuming a previously valid source is still stocked or reachable;
- coalescing sink jobs in a way that changes priorities or delivery ordering.

## Trace evidence

All paths below are under
`E:\User\Saved Games\Cosmoteer\76561198111307314\Logs`:

- `four_x_original_megastructure_heat_sparse_and_resource_diag_210_cpu_2026-08-28_22-58-29.nettrace`
- `four_x_original_megastructure_211_resource_cache_cpu_2026-08-29_02-27-19.nettrace`
- `four_x_original_megastructure_lockfree_per_ship_count_cpu_2026-08-29_02-48-50.nettrace`
- `four_x_original_megastructure_lockfree_per_ship_count_gc_verbose_2026-08-29_02-49-56.nettrace`
- `four_x_original_megastructure_lockfree_per_ship_count_gc_collect_2026-08-29_02-50-24.nettrace`
- `four_x_factory_warehouse_removed_cockpit_209_cpu_2026-08-28_22-00-19.nettrace`
- `four_x_factory_and_warehouse_removed_cockpit_209_cpu_2026-08-28_22-05-48.nettrace`
- `four_x_empty_factoryless_megawarehouse_209_cpu_2026-08-28_22-12-32.nettrace`
- `four_x_heat_sources_sinks_disconnected_209_cpu_2026-08-28_22-26-30.nettrace`
# Exact resource-path tail elimination (local experiment, 2026-08-31)

`ResourceManager.SearchForSources(SinkInfo)` asks `PathManager` for cells in
traffic-aware nearest-first order and checks the requested resource's tile
dictionary at each cell. Vanilla continues that cell enumeration even after
every key in the dictionary has already been visited. On a megastructure this
can traverse a long, guaranteed-empty remainder of the path network.

The local `ResourceSearchTraversalPatch` redirects only this call and stops
the iterator after yielding the final registered tile for the current concrete
resource type. It does not cache a route or resource location across updates,
does not stop merely because the requested quantity was satisfied, and does
not omit any possible source. Per-sink capacity, priority, current inventory,
anticipated pickup, reachability and job validation remain vanilla. Wildcard
`Stackable` searches retain the full vanilla traversal because their concrete
resource dictionary can change during enumeration. If any registered source
tile is unreachable or beyond the iteration cap, the traversal naturally runs
to the same end as vanilla.

## Fixed-update-local desired-priority snapshot

A post-traversal trace moved the remaining steady resource cost into
`UpdateSinkJobs`: `IResourceSink.CheckPriority` called
`BaseResourceStorage.GetSortPriority`, whose local `_HasUnmetDesired` helper
called `ResourceManager.GetResourceTotal` for every sink/source comparison.
That request includes `OffShipAssigned`, so the same off-ship crew list could
be scanned many times in one parallel job-update pass.

`ResourceDesiredPrioritySnapshotPatch` calculates the unmet-desire boolean once
per relevant concrete resource immediately before `UpdateSinkJobs` starts its
vanilla parallel work. Only the completed dictionary is published to workers;
it is cleared in a finalizer as soon as the pass ends. Job creation, validation,
amounts, sorting and application remain vanilla. This is neither a route cache
nor a cross-tick resource cache.

## Path-contiguity duplicate hash lookup

The remaining `PathContiguityManager.SearchSetsFrom` breadth-first traversal
checked every adjacent set with `HashSet.Contains` and, only when absent,
immediately called `HashSet.Add` for the same value. Because `Add` already
returns whether insertion occurred, the exact-shape transpiler branches on that
result directly. It does not cache the graph, change its traversal order, or
alter the reported iteration distance; it removes one hash-table probe per
examined adjacency.
