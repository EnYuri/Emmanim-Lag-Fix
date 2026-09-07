# Emmanim Lag Fix — session investigation log

This file is the raw, chronological debugging diary migrated out of the main game
installation's `CLAUDE.md` on 2026-09-03. It grew there session by session from
2026-08-27 onward until it was over half that file's length and largely duplicated
`MEMORY_DIAGNOSTICS.md`, `MULTIPLAYER_SYNC_DIAGNOSTICS.md` and
`RESOURCE_LOGISTICS_DIAGNOSTICS.md`. Nothing below was rewritten — it is preserved
verbatim as evidence (specific PIDs, hashes, trace filenames, per-session findings)
behind the condensed lessons that now live in `CLAUDE.md` under "Emmanim Lag Fix:
engineering lessons from the memory/CPU investigation".

Prefer the three topic-specific docs for anything you'd actually build on; come here
only when you need the exact history behind a claim.

---

## Emmanim Lag Fix memory investigation (2026-08-28)

The working repository is
`E:\User\Saved Games\Cosmoteer\76561198111307314\Dev\emmanim_lag_fix_code`.
The first blueprint-toggle delegate optimization used a per-component
`ConditionalWeakTable` and was rejected after a 15-second `gc-collect` trace
showed +32,312 GC handles and +53,939,344 Gen-2 bytes. The repository/package
now uses one mutable callback target per participating thread in
`ToggleModeDelegateCachePatch.cs`; build and smoke tests pass. The running game
still had the rejected live DLL during this investigation, so deploy the new
DLL/PDB only after Cosmoteer exits, then repeat the low-overhead baseline.

An approved full dump was captured at
`E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_heap_native_2026-08-28_03-10.dmp`
(13,413,371,581 bytes). Of 5.43 GB managed heap, 4.56 GB / 63.36 million
objects were live. There is a separate large reachable Halfling GUI/render/
event graph: 5.24M `EventHandler<EventArgs>`, 2.47M shader-constant handlers,
826,579 shader-constant collections (plus nine typed dictionaries and wrappers
per collection), 911,636 `QuadNode`/`GuiSprite` pairs and 683,574 weak-event
states. A representative root path is `CareerGameModeManager -> PaintToolbox
-> TexturePicker -> TextureItem -> GuiSprite -> QuadNode -> Material`; inactive
widgets were also still rooted. The rejected mod CWT contributes only one
roughly 4 MiB entry array, so it does not explain the separate multi-GB graph.

Do not patch this by clearing all weak events or inactive widgets: current and
intentionally cached UI are mixed into the snapshot. One dump proves reachable
retention but not which owner grows over time. The next safe experiment is a
same-build, comparable-state baseline dump followed by another after 30--60
minutes, then compare type counts and representative roots. Full details and
installed diagnostic-tool caveats are in the repository's
`MEMORY_DIAGNOSTICS.md`.

### New-session handoff

Resume from the repository at
`E:\User\Saved Games\Cosmoteer\76561198111307314\Dev\emmanim_lag_fix_code`.
Do not redo the first heap investigation or restore the rejected
per-component `ConditionalWeakTable` implementation.

Current state at handoff:

- Cosmoteer PID 11932 was still running and responsive. Its working set was
  about 7.2 GiB and private memory about 11.61 GiB after the dump.
- The corrected thread-local delegate-cache DLL was deployed to the live mod
  after exit; its SHA-256 is
  `EBD03A709ACCF10B61A0720CD37B4C26BD4AE3D8328FA133866E88D952A52306`.
- Fresh live verification passed: no Harmony/init exception appeared, the
  15-second `gc-collect` delta was Gen 2 `0` and GC handles `-10`, and the old
  toggle delegate type was absent from the top 25 of a 10-second allocation
  trace. Exact trace paths and comparisons are in `MEMORY_DIAGNOSTICS.md`.
- The valid corrected-build baseline dump is
  `E:\User\Saved Games\Cosmoteer\76561198111307314\Logs\memory_heap_threadlocal_baseline_2026-08-28_03-33-49.dmp`
  (11,094,658,890 bytes, SHA-256
  `268B80D5F61841BA3AAA26715926D2C0A4E0D6F27141554A6F84CCD8A13EF044`).
  The older rejected-build dump and incomplete failed dump were removed after
  this file passed `dotnet-dump analyze`; their recorded statistics remain in
  `MEMORY_DIAGNOSTICS.md`.
- A same-PID follow-up after 35.1 minutes is
  `memory_heap_threadlocal_followup_2026-08-28_04-09-31.dmp`
  (13,036,417,130 bytes; SHA-256
  `755D5BC16376466C0CF24159BB8D31B849003F149E94714875D5CD05285F195F`).
  The delegate fix remained clean (15-second Gen 2 delta 0, handle delta 0).
  The old shader/GUI graph was nearly flat; growth instead followed a much
  larger live ship/blueprint/part/decal/effect graph. Representative
  `MultiMediaEffectNode` roots lead to active current or stasis-managed ships,
  not orphaned inactive nodes. Exact counts and roots are in
  `MEMORY_DIAGNOSTICS.md`.
- The ship-graph growth is now explained quantitatively: the same active
  `SimStasisManager` went from 47 to 538 fully preloaded ships (+491), while
  live `Part` count rose by 53,927 (109.8 parts per added preloaded ship).
  Mission cache count was only 11 -> 14 and its ten-second expiry runs every
  frame. Existing stasis cancellation disposes completed preloads. This is a
  bounded, location-density-dependent preload cache, not an orphaned-ship leak.
  The narrow next experiment is a data-only `StasisPreloadRange = 3000`
  override (vanilla 3750) while keeping `StasisLiveRange = 2500`; do not apply
  it without treating synchronous-spawn hitch risk as the tradeoff.
- That override is now deployed in live/package Emmanim Lag Fix 2.0.6. The
  Release build and Harmony smoke test pass, and the DLL remains hash-identical
  to the verified thread-local build. Next launch the same save/location, let
  preload settle, and compare against the prior 538 fully preloaded ships.
- Live/package 2.0.7 additionally filters transfer/trade row construction to
  resource types present on either ship (plus existing transfer-job types),
  admits blueprint-purchase cards to scroll layouts one per frame, and limits
  card price/prerequisite refreshes to 2 Hz. Release build and Harmony smoke
  test pass. Deployed DLL SHA-256:
  `4479BA6D383277C8AC9BB5D55958CAFBE52E789FA229BD27E7C0609D9DBE31CA`.
- A 15-second CPU sample during large manipulator-beam collection is
  `Logs/mass_collection_common_cpu_2026-08-28_15-54-40.nettrace` (with a
  Speedscope conversion beside it). `DrawResourceNuggetPickups` consumed 7.52
  seconds inclusive while the whole `GameRoot.Update` consumed 1.84 seconds;
  `QueueLine` alone used 2.35 seconds and `RemoveExcess` 1.06 seconds. The
  dominant hitch was the visual transfer-job overlay, not crew assignment.
- Live/package 2.0.8 is built, smoke-tested, and deployed.
  `ResourcePickupOverlayPatch` refreshes
  candidate jobs once per second, displays at most 128 pickup lines/icons,
  continues updating their endpoints every frame, and avoids clearing the
  shared line cache from an empty companion overlay. Assignment rates and
  simulation state are untouched. Staged DLL SHA-256:
  `16C7EB5263D86AAFCDD2279A3006D56449C6E2A743CE3ED73FCFBD24C0545CE8`.
- A separate 15-second idle 4x trace is
  `Logs/four_x_idle_cpu_2026-08-28_16-02-18.nettrace`. The collection overlay
  was no longer dominant. `GameRoot.Update` used 2.20 seconds, of which hidden
  `BaseBlueprintPartNetworkPort.UpdateOperational` used 1.36 seconds and
  `IsBlueprintToggleOn` 1.10 seconds. Package 2.0.8 now also gates those
  rules-based port refreshes to once per 300 deterministic simulation ticks
  during normal play, while paused simulations keep vanilla per-frame refresh.
  State is one weak gate per ship callback container, not per component. Live
  and package DLL SHA-256:
  `E5DBF798CD6344092DFF0EF428C67D2EDAF3B6B2276C229FF756F8E2A33A8737`.
- Live/package 2.0.9 is now deployed. Scheduled nugget collection retains the
  orange outline for every distinct reserved nugget while limiting only the
  connection lines to 128. Build-toolbox display statistics refresh at 4 Hz;
  editor input, construction, affordability, and simulation data are
  untouched. Main Release build, mirrored code-project build, and Harmony
  smoke test pass. Live/package DLL SHA-256:
  `9CF3F800426FACDBCC21F3D3019BD6A52BBA6C3FFAB3C55483DACD84645F8A28`.
- The post-2.0.8 15-second trace is
  `Logs/post_208_current_cpu_2026-08-28_21-18.nettrace`. The hidden blueprint
  network-port path fell from 1.36 seconds to 90 ms and toggle lookup from
  1.10 seconds to 107 ms, proving that patch is active. Current load was
  instead dominated by real simulation resource-source searches (2.94 s),
  build overlay work (2.27 s), blueprint overlay refresh (1.09 s), and build
  stat aggregation (628 ms). Resource search already runs at 90/s rather than
  Huge Crews' 1000/s; do not reduce it further without accepting changed
  logistics responsiveness. Grab-part crew paths and multi-overlays already
  refresh only when the hovered part changes, so blind periodic throttling
  would show stale data for the wrong part.
- A controlled normal-play 4x sample on live 2.0.9 is
  `Logs/four_x_normal_209_cpu_2026-08-28_21-35-09.nettrace` (Speedscope beside
  it). Over 15 seconds, `GameRoot.Update` occupied 10.93 s versus 3.31 s in
  draw. Parallel inclusive hotspots were resource-source search 7.55 s,
  status diffusion 6.18 s, and resource conversion 3.16 s; main-thread
  `ResourceManager.FixedUpdate` was 3.99 s. Hidden blueprint toggle lookup was
  only 37 ms and the pickup overlay 2 ms, so neither explains the remaining
  4x slowdown. Process working set was 6.31 GiB/private 9.48 GiB and the latest
  log contained no freeze or exception. Parallel inclusive figures overlap
  and must not be summed.
- The paired factories-off sample is
  `Logs/four_x_factories_off_209_cpu_2026-08-28_21-41-22.nettrace`. Turning
  both user-identified factory ships off reduced `UpdateSinkJobsAsync` by
  81.4%, status diffusion by 20.8%, converter ticks by 12.7%, subnetwork pushes
  by 15.5%, and multi-storage adds by 18.7%. It did not reduce source search:
  `SearchForSources(SinkInfo)` changed from 7.55 s to 7.78 s (+3%, sampling
  noise). Total update rose 5.9% because movement/thruster work also increased
  substantially in the second window. Factory toggles therefore stop much of
  the actual job creation but do not unregister their resource sinks/sources
  or network/status components; they are only a partial cause of normal 4x
  load.
- A new-career starter-ship 4x baseline is
  `Logs/new_career_starter_4x_baseline_209_cpu_2026-08-28_21-50-45.nettrace`.
  With the same enabled mods and career AI background, `GameRoot.Update` was
  only 404.5 ms over 15 s (3.7% of the old save), fixed update 253.2 ms (2.9%),
  resource-manager fixed update 93.2 ms (2.3%), and source search 126.7 ms
  (1.7%). There were no sampled status-diffusion, converter, multi-storage, or
  subnetwork-push frames. This rules out generic career NPC/AI and mod baseline
  overhead; the old-save 4x load is overwhelmingly introduced by its ship
  population/configuration. Next branch repeatedly from this exact baseline
  save and import factory ship A only, B only, then A+B.
- In the original save, moving to a sector containing a huge non-factory ship
  but no factory ships raised observed 4x FPS from about 20 to about 110. The
  paired trace is
  `Logs/four_x_no_factory_sector_huge_ship_209_cpu_2026-08-28_21-55-57.nettrace`.
  Versus the factory sector, resource-manager fixed update fell 91.8%, source
  search 93.2%, sink-job update 92.3%, path-contiguity search 77.7%, crew fixed
  update 68.6%, and status diffusion disappeared entirely. Fixed update fell
  47.4%. Converter ticks fell only 22% and subnetwork pushes only 9.2%, showing
  those generic mechanisms also exist on the non-factory huge ship. Together
  with the factories-off test (source search unchanged), this implicates the
  active presence/registration of the factory ships' resource sinks and status
  networks, not merely stored resource quantities or current production.
- Correction/refinement: the trace
  `Logs/four_x_factory_warehouse_removed_cockpit_209_cpu_2026-08-28_22-00-19.nettrace`
  was captured with the factory ship still present in the same sector and the
  camera focused on it, while only the factory/warehouse (warehouse-role) ship
  had been removed and replaced by a cockpit. Observed FPS remained about 100.
  Versus the original slow state, source search fell 88.1%, resource-manager
  fixed update 86.8%, sink-job update 74.9%, converter ticks 59.5%, subnetwork
  pushes 62.1%, multi-storage adds 74.9%, and status diffusion disappeared.
  This rules out the remaining factory ship's mere presence and points to the
  removed warehouse ship's storage/resource-network configuration. It still
  does not distinguish numeric resource quantity from occupied storage slots,
  source registrations, resource-type spread, or storage-network topology;
  reload that exact warehouse design empty to make the next controlled split.
- The same-sector direct factory-ship removal control is
  `Logs/four_x_factory_and_warehouse_removed_cockpit_209_cpu_2026-08-28_22-05-48.nettrace`.
  This state retained only the cockpit after both warehouse and factory ships
  were removed. Versus the immediately preceding warehouse-removed but
  factory-present trace, `GameRoot.Update` fell 96.1% (7.57 s -> 296 ms), fixed
  update 93.5%, update-bucket work 98.6%, resource-manager fixed update 90.1%,
  source search 84.7%, and sink-job update 98.2%. Converter, multi-storage,
  subnetwork push, part-smoothed-value, and audio-effect update frames all
  disappeared from the sample; draw fell 63.9%. The factory ship is therefore
  independently expensive even without the warehouse ship. The warehouse ship
  primarily added extreme source search and status diffusion; the factory ship
  adds smoothed part values, per-effect audio updates, converters, storage, and
  resource-network work. Numeric resource quantity remains unisolated from
  component/slot topology.
- The empty, factory-removed megawarehouse test is
  `Logs/four_x_empty_factoryless_megawarehouse_209_cpu_2026-08-28_22-12-32.nettrace`.
  The huge warehouse hull/storage structure remained, all stored resources
  were removed, and user-identified factory parts were removed. Versus the
  cockpit-only control, `GameRoot.Update` was 29.2x, fixed update 32.7x,
  resource-manager fixed update 28.8x, source search 24.8x, path-contiguity
  search 163x, and sink-job update 110x. Status diffusion alone sampled 9.0 s
  inclusive and 2.66 s preparing its node lists. Converter, multi-storage, and
  subnetwork-resource frames also remained, meaning generic components on the
  nominally factoryless design still use those systems. Numeric stored-resource
  quantity is therefore not required for the slowdown. Raw geometric size is
  not yet isolated either: the fast non-factory megaship shows that active
  storage/status/resource-network component density and topology, rather than
  armor/structure area alone, are the leading explanation. Empty sinks may
  even search more because demand remains unsatisfied.
- Decompiled implementation detail for the 9.0 s status-diffusion hotspot:
  vanilla `cosmoteer.heat` has `Diffusion` enabled and ticks every 0.25 game
  seconds. `StatusDiffuser.PrepareLists` inflates the active-status bounds and
  sizes its input arrays to the full bounding-rectangle area; diffusion then
  visits every cell in that rectangle, including empty cells. This creates a
  width-times-height cost for a sparse but widely distributed active heat
  status. However, the user confirmed that the fast non-factory megawarship
  also contains shields, overclock heat piping, command rooms, and thrusters,
  with even more defensive equipment. Heat/overclock presence alone is thus
  ruled out as a sufficient cause. The remaining split is (a) heat being
  actively nonzero or geometrically distributed differently on the warehouse,
  versus (b) the fully emptied warehouse leaving power/resource sinks
  unsatisfied and repeatedly searching its huge storage/path network. Preserve
  the fast warship as the matched control and change one subsystem per sample.
- Partial heat-network removal test:
  `Logs/four_x_heat_sources_sinks_disconnected_209_cpu_2026-08-28_22-26-30.nettrace`.
  On the same empty, factoryless megawarehouse, the user removed or disconnected
  every visible heat source and heat sink that was practical, while retaining
  the ship. `StatusDiffuser.Diffuse` (2.31% of all-thread sampled time before)
  and `PrepareLists` (0.68%) disappeared from the top 100 entirely. Main-thread
  `GameRoot.Update` fell from 2.22% to 1.45% (~34.7%), fixed update from 1.66%
  to 1.21% (~27.1%), and `SimRoot.UpdateForBucket` from 0.54% to 0.23%
  (~57.4%). This confirms that the active, widely distributed heat network was
  a major independent cause, despite heat hardware also existing on the fast
  megawarship. It was not the sole cause: source search rose from 0.87% to
  1.04%, resource-manager fixed update 0.39% to 0.49%, path-contiguity 0.40%
  to 0.49%, and sink-job work 0.20% to 0.25% (roughly comparable trace
  denominators). The empty warehouse's resource/storage search remains a
  separate bottleneck. Converter, multi-storage, and subnetwork-push frames
  also left the top 100, so the removed heat-source/sink parts themselves used
  some of those generic mechanisms; do not attribute all of that secondary
  disappearance solely to heat diffusion.
- Correction to the interpretation of the remaining resource work: the user
  reports that power is currently being supplied. Removing the heat piping also
  removed its power-strengthening/overclock benefit, so more ordinary power
  deliveries and their source/job/path searches may be required to satisfy the
  same consumers. The small increase in resource-manager metrics therefore does
  **not** establish repeated failed searches caused by an empty power supply.
  This sample cleanly establishes the heat-diffusion reduction, but it does not
  isolate whether the remaining resource work is caused by storage topology,
  unsatisfied demand, or increased successful power-delivery throughput.
- Heat optimization implementation prepared as mod version 2.0.10 in the code
  repo. `SparseHeatDiffusionPatch` replaces `StatusDiffuser.PerformDiffusion`
  only for `cosmoteer.heat` bounds at least 128x128. It enumerates active status
  cells plus their four direct neighbours, snapshots all deltas before applying
  them, and sorts output cells in vanilla row-major order. Thus the 30 Hz rate,
  coefficients, per-tick numerical stencil, callback behavior, and deterministic
  application order remain unchanged. If that frontier occupies at least 25%
  of the vanilla inflated rectangle, it falls back to vanilla's array/parallel
  implementation to avoid making dense heat fields slower. Scratch collections
  are thread-static rather than retained per diffuser. Build completed with no
  warnings and the Harmony smoke test resolved the new prefix. Packaged archive:
  `build/Emmanim-Lag-Fix-2.0.10.zip`, SHA-256
  `1E3A2ACBF4970739CB1613C1D00757F3E21643037DF74351743F48D021AED8C6`.
  The game was still running, so the live mod DLL was intentionally not replaced;
  deploy after process exit, then validate on the restored heat-network save.
- Resource-network phase: code inspection confirmed that every selected sink
  clears its candidate list and repeats traffic-aware tile/path-set traversal,
  source validation, and per-set distance sorting. The engine exposes no single
  safe topology/resource revision for a long-lived route cache. A temporary,
  behavior-neutral `ResourceSearchDiagnosticsPatch` was therefore deployed to
  the live 2.0.10 DLL, enabled only by
  `resource-search-diagnostics.flag` in the live mod root. It aggregates elapsed
  search time by ship ID, part count, sink part/type, resource type, call count,
  and returned candidate count, logging the top 12 every ten seconds. Temporary
  live diagnostic DLL SHA-256:
  `7EA922D39D3DD02B973B4C62B2AA22B45ABFFAE60A16A79EA4B91767AD2227A3`.
  The packaged 2.0.10 release remains the non-diagnostic build. After one
  20-30-second 4x warehouse run, read the log, remove the flag, and implement
  only the dominant safe fast path rather than caching resource locations
  blindly.
- Original resource-heavy megastructure capture:
  `Logs/four_x_original_megastructure_heat_sparse_and_resource_diag_210_cpu_2026-08-28_22-58-29.nettrace`.
  Opt-in diagnostics identify the dominant searches as storage tiles acting as
  sinks on ship `C58BFDC7` (11,563 parts), especially
  `znayuri.lightweight_storage_5x5` and `SirCampalot.dpmstorage_5x5/5x4` for
  carbonsteel, tristeel, and SW.durasteel. Representative cumulative rows were
  7,683 ms / 4,605 searches for lightweight 5x5 carbonsteel and 6,035 ms /
  3,019 searches for DPM 5x5 carbonsteel, despite only 2--4 returned candidates
  on average. These parts use a 5x5 `FlexResourceGrid`, so one physical storage
  part registers many independently searched storage tiles. The temporary flag
  was removed after capture; the diagnostic DLL remains inert without it.
- A behavior-preserving first resource optimization is now deployed for an
  exact-state comparison. `PerShipCountFixedUpdateCachePatch.cs`
  caches repeated allied-ship pickup-count aggregation only inside one
  `ResourceManager.FixedUpdate`; `AddCount` invalidates before mutation and the
  cache is disabled on leaving that update. It does not cache resource
  locations, paths, or results across ticks. Release build has zero warnings
  and the Harmony smoke test resolves the FixedUpdate scope, GetCount
  prefix/postfix, and AddCount invalidation. The original trace attributed
  1.30% of all-thread samples to `PerShipCount.GetCount`, separate from the
  1.91% source search and 1.40% sink-job update. The experimental live DLL
  SHA-256 is
  `C4C64BEBCAE2CF117E3EB8205662AC273A49ADE9ACA0848C7508F42EFAE6AE9F`;
  repeat the exact original-state 4x trace before making it a release.
- Release 2.0.11 was committed as
  `e89b66ba1b8d6e107e3d8514b1eaa58890ac98fb`, pushed to `origin/main`, tagged
  `v2.0.11`, and published at
  `https://github.com/EnYuri/Emmanim-Lag-Fix/releases/tag/v2.0.11`. The one-click
  archive is `Emmanim-Lag-Fix-2.0.11.zip` (1,244,136 bytes), SHA-256
  `CA95593B21F2D3291FB247D8AD181476028D11AFD51E0A83A45AC2ADF65BFEAE`.
  GitHub reports the same asset digest. The live mod metadata now says 2.0.11
  and its DLL SHA-256 is
  `C4C64BEBCAE2CF117E3EB8205662AC273A49ADE9ACA0848C7508F42EFAE6AE9F`.
- Post-release exact-state 20-second 4x trace:
  `Logs/four_x_original_megastructure_211_resource_cache_cpu_2026-08-29_02-27-19.nettrace`.
  The `PerShipCountFixedUpdateCachePatch` is a confirmed regression and must be
  removed rather than tuned in place. Versus the 2.0.10 original trace,
  `PerShipCount.GetCount`/patched wrapper rose 1.30% -> 2.64%, with
  `PerShipCountFixedUpdateCache.TryRead` accounting for 2.63%; shared-lock
  contention raised `Monitor.Enter_Slowpath` 1.76% -> 3.07% and
  `UpdateSinkJobs` 1.40% -> 2.68%. Fixed update rose 2.20% -> 2.61% and
  `GameRoot.Update` 2.87% -> 3.26%. Heat and converter work differed between
  windows, but the direct patched call stack and doubled lock/sink cost make the
  cache regression unambiguous. Publish a 2.0.12 hotfix removing
  `PerShipCountFixedUpdateCachePatch`; do not attempt another shared cache.
- Hotfix 2.0.12 removed `PerShipCountFixedUpdateCachePatch` from root source,
  bundled source, smoke tests, and DLL. Commit
  `df74ce112bc918ab562f436823e4de9c1eaaff17` is tagged `v2.0.12`; the live mod
  is 2.0.12 with DLL SHA-256
  `7FDECD6B12390B9C8229316EA4DCD7C008F2454B7D8CA99F4BCF5065A8ED0EE7`.
  The first tag-driven GitHub Actions run 33195265637 passed all validation,
  packaging, archive inspection, and release-publication steps. Public release:
  `https://github.com/EnYuri/Emmanim-Lag-Fix/releases/tag/v2.0.12`; CI-produced
  asset SHA-256
  `532F573501E9BAF9A1740F803CB5242CC666015566BB8B6048EEE6BFCB7D2BB0`.
  `.github/workflows/release.yml` now packages committed verified binaries on a
  matching `v*` tag because proprietary Cosmoteer references are unavailable to
  hosted runners. A follow-up main commit `1f25479` updates checkout v4 to the
  official current checkout v7/Node runtime after the first run's deprecation
  warning; future tags use v7.
- Next resource experiment is deployed locally but intentionally uncommitted and
  unreleased: `PerShipCountLockFreePatch.cs`. It replaces the private
  `PerShipCount` list lock with a copy-on-write immutable entry snapshot:
  lock-free reads, CAS-published writes, same entry order/allied sum/dead weak
  reference cleanup, and no path/location/tick cache. The smoke test validates
  patch resolution, displayed/confirmed accumulation, and 2,000 concurrent
  adds without loss. Its latest combined experimental live DLL also includes
  the multiplayer timeout and visual smoothed-value patches; the current hash
  is recorded below.
  Re-run the original 11,563-part megastructure 4x CPU trace, then collect a
  short allocation/GC comparison because copy-on-write makes an array on each
  count mutation. Do not commit or release unless both CPU and allocation
  results improve over vanilla 2.0.12.
- The full resource/storage/path-search investigation is now preserved in
  `emmanim_lag_fix_code/RESOURCE_LOGISTICS_DIAGNOSTICS.md`. It records the
  controlled ship-removal samples, 5x5 `FlexResourceGrid` tile amplification,
  diagnostic sink rows, rejected 2.0.11 shared cache, successful lock-free
  experiment, trace inventory and safety constraints. Read it before adding a
  resource-location, candidate, sink-job or route cache.
- Lock-free resource-count experiment passed its first original-state CPU test.
  CPU trace:
  `Logs/four_x_original_megastructure_lockfree_per_ship_count_cpu_2026-08-29_02-48-50.nettrace`.
  Versus the original 2.0.10 megastructure trace,
  `PerShipCount.GetCount` fell 1.30% -> sampled 0%, `Monitor.Enter_Slowpath`
  1.76% -> 0.85% (-52%), and `UpdateSinkJobs` 1.40% -> 0.55% (-61%).
  Path-contiguity search remained ~0.42% -> 0.40%; heat remained comparable
  3.21% -> 3.01%. Total fixed/update percentages were roughly flat/slightly
  higher (2.20% -> 2.50%, 2.87% -> 2.97%) because other live workload varies,
  so claim only the direct resource lock/sink improvement.
- Initial allocation trace
  `four_x_original_megastructure_lockfree_per_ship_count_gc_verbose_2026-08-29_02-49-56.nettrace`
  sampled only 2.03 MiB of the new `Entry[]` over 10 seconds, with zero Gen 2
  collections and no Gen 2/LOH growth. Low-overhead 15-second trace
  `four_x_original_megastructure_lockfree_per_ship_count_gc_collect_2026-08-29_02-50-24.nettrace`
  recorded 48 collections (34 Gen0, 14 Gen1, 0 Gen2), Gen2/LOH deltas 0, and
  unchanged process working set/private bytes across the window. Ignore the
  handle delta from these EventPipe profiles as previously documented. The
  The experiment remains successful in practical play according to the user;
  continue to preserve it while validating longer sessions.
- A reported one-off freeze during ordinary gameplay on the lock-free
  experiment produced no Harmony/game exception, FreezeDetector entry, new
  Steam assert or crash dump, Windows Application Hang, display-driver reset,
  WHEA, or disk warning. A `03:04:20 Saved game as "single 2"` entry and its
  43,373,247-byte file were initially suspected, but the user explicitly
  confirmed that the observed freeze was unrelated to manual or automatic
  saving; treat that save as coincidental, not causal evidence. The ordinary
  game log did not timestamp the freeze, so its duration and call stack cannot
  be recovered retrospectively. Plausible but unproven candidates include a
  blocking GC or the experimental `AddCount` copy-on-write CAS retry loop under
  a short burst of concurrent mutations. Capture a CPU trace spanning the next
  occurrence before attributing it or releasing the experiment.
- Post-freeze low-overhead GC captures recorded a burst of pre-existing
  long-session retention during ordinary gameplay. In
  `post_freeze_gc_collect_2026-08-29_03-10.nettrace`, only 15 seconds produced
  +202,161,768 Gen-2 bytes and +119,737 handles, with 34 Gen-1 collections and
  no Gen-2 collection. An immediate confirmation window
  (`post_freeze_gc_collect_confirm_2026-08-29_03-09-44.nettrace`) was quieter
  but still added +4,794,936 Gen-2 bytes and +5,129 handles. From the 02:50
  short baseline to the first post-freeze capture, live Gen 2 rose from
  4,411,117,040 to 4,900,951,960 bytes and handles from 2,447,360 to 2,732,938.
  This does not retrospectively prove the exact freeze stack or any causal
  connection to `PerShipCountLockFreePatch`. The user reports that this brief
  freeze pattern was common in the unpatched game, that resource behavior is
  normal, and that the lock-free patch has been very successful in practical
  play. Treat the GC figures as part of the already-established broader
  Cosmoteer long-session retention problem, not as a regression verdict on this
  patch. Keep the experimental DLL and implementation; do not revert it based
  on this unrelated freeze. A full heap dump still requires explicit approval
  because the current process is multi-gigabyte and the dump will freeze it.
- A cautious multiplayer connection-timeout patch is implemented as
  `MultiplayerSessionTimeoutPatch.cs`. Decompilation showed six reads of Halfling
  `NetworkMessenger.SESSION_TIMEOUT`: four in
  `ProcessUnresponsiveSessions` and two in `EnqueueOutgoingAcks`. The Harmony
  transpiler replaces those reads with 30 seconds only when each method has
  exactly the expected 4/2 references; otherwise it logs and preserves the
  vanilla ten-second behavior. It changes no packet format, resend cadence,
  ordering or simulation input. The smoke test asserts that both transpilers
  matched on Cosmoteer 0.30.4c. Build and smoke test pass with zero warnings. It
  was deployed after game exit; assume both multiplayer peers need the same
  build.
- The next path-search audit rejected whole-result sharing between tiles of one
  `FlexResourceGrid`: each tile has a distinct one-cell destination, resource
  preference/current type, remaining capacity, anticipated delivery, priority
  and traffic-aware distance, so candidate order can legitimately differ even
  inside one 5x5 part. This was documented in
  `RESOURCE_LOGISTICS_DIAGNOSTICS.md`; no unsafe path/result cache was added.
- A safer factory/large-part follow-up is prepared in source as
  `PartSmoothedValueVisualThrottlePatch.cs`. Vanilla already separates
  deterministic smoothed values (fixed update) from non-deterministic visual
  values (render update), but walks every visual value every frame. The patch
  leaves the deterministic list untouched and updates only the non-deterministic
  list at 20 Hz wall time using the full accumulated game-time delta. This
  reduces visual list/event fan-out for factory, radiator and large modded
  thruster assemblies without changing converter ticks or production. It uses
  one weak state per ship manager. Build and patch-resolution smoke test pass;
  it was deployed after game exit and still needs a live trace.
- Combined experimental deployment after the 2026-08-29 game exit contains
  `PerShipCountLockFreePatch`, `MultiplayerSessionTimeoutPatch`,
  `PartSmoothedValueVisualThrottlePatch`, the client initialization-buffer
  early release, and `MultiplayerStreamCopyCapacityPatch`. Build output,
  repository `Mod/Code`, and live `Mods/emmanim_lag_fix/Code` DLLs all have
  SHA-256
  `5341B8D070DD75DAB0E214006A151CB53B3A33B5A115FD376C9CD545D23283F2`.
  The five relevant C# files are byte-identical in root source, bundled `Mod/Source`,
  and the live mod source bundle. This remains uncommitted and unreleased under
  the 2.0.12 metadata until live validation is complete.
- Multiplayer initial-sync architecture and mitigations are documented in
  `emmanim_lag_fix_code/MULTIPLAYER_SYNC_DIAGNOSTICS.md`. The host serializes
  the entire `GameInit` to a `MemoryStream` and streams it reliably to each
  client. The client accumulates the full payload in `ChannelStream._inBuf`,
  copies it to a second `MemoryStream`, deserializes a `GameInit`, and constructs
  the full game before sending `ClientReadyRpc`. Vanilla retains the copied
  buffer through `CreateGame`, creating a high client memory peak.
- Two cautious client-initialization improvements are built, smoke-tested and
  deployed: the existing client launch worker replacement now
  releases its copied stream immediately after the unchanged
  `BinarySerializer.Read<GameInit>` and before `CreateGame`; and
  `MultiplayerStreamCopyCapacityPatch` preallocates the destination to
  `ChannelStream.UnreadBytes` before the unchanged copy, avoiding geometric
  backing-array growth. A synthetic 65,553-byte ChannelStream smoke test
  verifies exact copied contents and capacity. Do not attempt to call the whole
  `NetworkMessenger.ReceiveMessages` pump on a background thread: user-message
  processing invokes connection callbacks immediately. A future ACK thread
  must split transport parsing/ACK from main-thread game-message dispatch.
- The ignored `.tools/heap-dump-writer/` helper uses Windows
  `MiniDumpWriteDump` because `dotnet-dump 9` fails against this .NET 10 game
  at the final collection stage. Official diagnostics tools are installed in
  `C:\Users\Nayuri\.dotnet\tools`.
- Release 2.0.13 was committed as
  `d5c3a73681dca62ecef0858f7f7f51b4ce74cf05`, pushed to `origin/main`, tagged
  `v2.0.13`, and published at
  `https://github.com/EnYuri/Emmanim-Lag-Fix/releases/tag/v2.0.13`. GitHub
  Actions run 33225034286 passed tag/payload validation, archive construction,
  archive inspection, and release publication. The CI-produced one-click asset
  is `Emmanim-Lag-Fix-2.0.13.zip` (1,238,704 bytes), SHA-256
  `AA7A348375BFBD9EF68BE0022098CA622C44EB0869F23DEF0EC5568803E45130`.
  The live mod metadata is 2.0.13 and its code DLL SHA-256 is
  `5341B8D070DD75DAB0E214006A151CB53B3A33B5A115FD376C9CD545D23283F2`.
  The repository is clean and `main` matches `origin/main`.
- Post-2.0.13 multiplayer buffer experiment is deployed locally and remains
  uncommitted/unreleased. `MultiplayerStreamCopyCapacityPatch` now also
  preallocates the client's first `ChannelStream._inBuf` from the exact
  `StartDataStreamRpc(totalBytes)` value and each host-side client
  `ChannelStream._outBuf` from the remaining serialized `MemoryStream` length.
  This changes no chunking, flow control, reliability, packets, or ordering;
  it only prevents geometric `MemoryStream` growth and intermediate full-array
  copies at both ends. Release build and smoke tests pass with zero warnings;
  synthetic tests verify input/output capacity and existing-byte preservation.
  Root build, packaged DLL, and live DLL SHA-256:
  `135BEF2626E9E17C2961CC8747309D0DDD3A53EEE14180D149AA89AFF9889621`.
  The root, bundled, and live source mirrors are intended to remain identical.
  A single-host multiplayer room subsequently launched and entered the game
  normally, validating mod load and the host initialization baseline. Because
  no client connected, the new per-client output and client input-buffer paths
  were not exercised; keep their live multiplayer status pending.
- The same post-2.0.13 experiment now eliminates the client's second complete
  initialization-data copy. Only streams marked by the exact
  `ClientLaunchFlow.StartDataStreamRpc(totalBytes)` patch are eligible. At
  completion, the fresh deserialization `MemoryStream` adopts the existing
  `ChannelStream._inBuf` byte array as a read-only buffer; disposing the source
  stream does not clear that array, and the existing early-release worker
  disposes the adopted buffer immediately after `BinarySerializer.Read` and
  before `CreateGame`. Expected input position/length and .NET 10
  `MemoryStream` private-field shape are guarded; any mismatch logs once and
  falls back to the 2.0.13 preallocated full copy. Synthetic smoke proves exact
  array identity, payload equality, read-only state, and readability after the
  source stream is disposed. Actual client join validation remains pending.
- Continuous multiplayer audit: normal play sends reliable `InputTick` objects
  at `InputTicksPerSecond = 30` (including empty ticks), the host forwards them
  and sends one reliable `HostUpdate` per tick. Halfling already pools
  `MessageData`/`QueuedMessage`/temporary objects, coalesces consecutive ACK IDs
  into ranges, packs multiple messages to the socket MTU, and fragments large
  messages. Blind compression/batching is therefore low-value and risky.
  The leading steady-multiplayer-only cost is instead whole-game integrity
  hashing: `BaseMPManager.AdvanceNetworkTime` invokes `CheckGameSync` after
  every input tick, so host and client each walk `GameRoot`/`SimRoot`, ships,
  crew, nuggets, bullets, map, mode and player state 30 times per second. This
  is absent from single player and is a plausible large-save bottleneck.
  Lower cadence delays desync detection and requires identical code DLLs on
  every peer because vanilla produces a different hash sequence; incremental
  hashing is much more invasive.
- A post-2.0.13 6 Hz integrity-hash experiment is now built and deployed
  locally. `MultiplayerIntegrityHashThrottlePatch` replaces the exact single
  normal `CheckGameSync` call in `BaseMPManager.AdvanceNetworkTime`; at the
  vanilla 30 Hz input clock it selects ticks `1,6,11,16,21,26,...`, reducing
  whole-game hash calls by 80% while leaving input ticks, HostUpdates,
  simulation, actions, and debug-only per-bucket hashes unchanged. Maximum
  normal desync-detection delay is about 0.167 s. Exact-shape fallback preserves
  vanilla if the call count is not one. Smoke verifies the selected tick set
  and Harmony installation. All peers must run the identical DLL.
  `integrity-hash-diagnostics.flag` logs ten-second
  `calls/window/rate/total/avg/max` rows; a single-host MP game suffices to
  measure local whole-hash cost. Current root/package/live DLL SHA-256:
  `72FC7128A90E10EC7AD345ADCC60D7B125B663ED4953EB81C15B8788809F0DA6`.
- Single-host live integrity-hash measurement produced stable ten-second
  windows of 44--53 calls at 4.31--5.28 wall-clock Hz because the network input
  tick itself advanced at only about 22--26.5 Hz under load. Every reported
  tick was `1 mod 5`, confirming the exact one-in-five gate. Warm hashes
  averaged 0.24--0.27 ms, totalled 11.2--13.0 ms per ten seconds, and peaked at
  0.31--0.39 ms. Vanilla cadence at the same observed tick throughput would be
  roughly 56--65 ms/10 s. The 6 Hz patch works and saves measurable work, but
  whole-game integrity hashing is not a major bottleneck in this sample. The
  temporary live diagnostic flag was removed after capture.
- Next steady-MP experiment is deployed locally: `MultiplayerHostUpdateThrottlePatch`
  aligns normal host `HostUpdate` construction/serialization/reliable sending
  with the 6 Hz integrity-hash ticks (`1,6,11,16,21,26,...`) instead of sending
  a mostly empty update at every 30 Hz input tick. InputTicks, actions,
  simulation, and the host's input-delay calculation stay at 30 Hz; clients
  can retain the last delay/latency values for at most ~0.167 s. Desync-debug
  sessions preserve vanilla 30 Hz HostUpdates because multiple debug hashes
  may queue per tick. Smoke verifies the normal schedule, debug bypass, and
  Harmony target. This is a modest allocation/serialization/packet reduction,
  not expected to be a major CPU fix. Actual client validation is pending.
  Current root/package/live DLL SHA-256:
  `E2EB097BF331A5B916E5DAA7E56FEA0DA7EA6228F5821303B33EABC2448E7AAE`.
- InputTick allocation audit found that vanilla already pools the tick object,
  its persistent `Inputs` list, serialization `MessageData`, and received
  `DeserializedMessage<InputTick>` wrapper; an empty tick creates no input
  payload byte array. Do not lower the 30 Hz empty-tick cadence without a new
  lockstep readiness protocol. One safe repeated allocation remained on the
  host: `MPHostManager.ForwardInputTick` creates a captured sender-exclusion
  predicate for every received client tick. The local
  `MultiplayerInputTickAllocationPatch` caches one immutable predicate per
  host-session/sender pair using a `ConditionalWeakTable`, without changing
  cadence, payload, reliability, order or recipients. After warm-up this avoids
  one closure plus one delegate per received client tick (normally 30 pairs/s
  per client). Smoke verifies patch installation, delegate identity reuse and
  sender/other recipient semantics. Actual multi-peer forwarding validation is
  still pending. Current root/package/live DLL SHA-256:
  `2323283848785A2206409EEA1977EACBAAF1DF0E85122BDDD219CAB9ADCA4812`.
- Release 2.0.14 was committed as
  `bf9bb4d2df59929316b5cfdeae83d6257f189998`, pushed to `origin/main`, tagged
  `v2.0.14`, and published at
  `https://github.com/EnYuri/Emmanim-Lag-Fix/releases/tag/v2.0.14`. GitHub
  Actions run 33239837759 passed tag/payload validation, archive construction,
  archive inspection, and release publication. The CI-produced one-click asset
  is `Emmanim-Lag-Fix-2.0.14.zip` (1,247,905 bytes), SHA-256
  `F7B8784AE6C6F2DB37AEE3F9FF87C98B61F75C033CBE4FCF015372F420AAC00C`.
  The live mod metadata is 2.0.14 and root/package/live code DLL SHA-256 is
  `2323283848785A2206409EEA1977EACBAAF1DF0E85122BDDD219CAB9ADCA4812`.
  The repository is clean and `main` matches `origin/main`. Local host-room
  startup was validated before release; actual remote-client join/forwarding
  validation remains pending.
- Extended real-peer validation on 2026-08-30 supersedes that pending note:
  two-player rooms ran for about 3 h 15 min, 1 h 25 min, and 56 min without
  `WaitingForAck`, connection loss, loader/Harmony exceptions, or in-play Steam
  IPC failures. The steady 2.0.14 forwarding/hash/HostUpdate paths therefore
  worked in normal remote play. One SteamNetworkingSockets 70 ms starvation
  assert occurred immediately before the second room started, and two Steam
  IPC pipe-close asserts occurred only after final game shutdown.
- The second room triggered one actual OOS resync at 05:12:03; vanilla reaches
  the logged host `Resyncing multiplayer game as host...` path through
  `OnOutOfSyncRpc`. It successfully rebuilt/pushed the game at 05:16:25 but took
  262 seconds. A different 6 Hz hash sequence would diverge immediately, so an
  isolated event after ~85 minutes does not resemble a 2.0.14 cadence mismatch.
  The historical log lacks phase timing and cannot identify whether save,
  transfer, host load, or client load dominated.
- Post-2.0.14 resync experiment is built and deployed locally. Initial launch
  and resync use separate client flow types; 2.0.14 marked only launch streams,
  leaving resync to geometrically grow its receive buffer and copy the complete
  game into a second `MemoryStream`. `MultiplayerReceiveBufferCapacityPatch`
  now also targets `GameResyncFlow.ClientResyncFlow.StartDataStreamRpc`, applying
  the same exact-size input preallocation and guarded zero-copy adoption with
  the existing safe fallback. `MultiplayerResyncTimingPatch` records host save,
  host load and client load durations, and the client logs announced payload
  bytes. Build/smoke pass with zero errors. This is now combined with the
  multiplayer memory diagnostics described below; current root/package/live
  DLL SHA-256 is
  `405ECA1F9EBB04D15BF933A0FDE4DFC5A7E200E31429193318941086BD04CDF6`.
  This is uncommitted/unreleased and needs another real resync plus both peers'
  logs before considering compression or reordering host load to overlap the
  transfer.
- The first ~3 h 15 min room was deliberately ended. Twenty-nine seconds later,
  while constructing a creative game's paint/decal GUI, the freeze detector
  recorded 11,519,295,488 bytes and a >10 s main-thread stall in
  `PaintToolbox.AddDecalButton`/`AddDecalsGroup` and `TexturePicker.TextureItem`
  construction. It was not a multiplayer/network stack and did not show a GC
  frame, though the high retained heap likely amplified it. The later remote
  player's reported gradual slowdown cannot be proven from the host log; obtain
  the client's log and same-process `gc-collect` baseline/follow-up next time.
- `MultiplayerMemoryDiagnosticsPatch` is deployed as an opt-in diagnostic and
  the live `multiplayer-memory-diagnostics.flag` is enabled for the next launch.
  It logs once per wall-clock minute: host/client role, private/working/managed
  and GC heap/fragmentation memory, process handles, Gen0/1/2 collection deltas,
  total/max player InputTick queues, local outgoing inputs, host/client hash
  queues, connection receive queues, send throughput and `.rec` length. It is a
  read-only `BaseMPManager.Update` postfix. Current audit found no obvious
  unbounded steady MP queue: ticks are disposed after execution, recordings are
  file-backed/flushed, hashes are paired/released and normal message buffers are
  pooled. The diagnostic now also logs game/simulation identities, live
  ship/physical/blueprint-part counts, total/preloaded stasis spawners and
  paint decal picker/item counts. This separates MP queue buildup from stasis
  population growth, resync replacement and repeated GUI construction. If all
  populations stay flat while heap rises, inspect fragmentation or another
  retained graph. The slow client's flag and log are decisive; the host-only
  flag is a control. Current root/package/live DLL SHA-256 is
  `7FC467A7F15508749E9804342FDD421D2E5D7BDB40921D36BD98AA4C18BFFE94`.
- Static decompilation explains the enormous baseline paint GUI graph.
  `PaintToolbox` eagerly iterates every `GameApp.Rules.Ships` entry during each
  `GameRoot` construction, builds every decal group and every
  `TexturePicker.TextureItem`, and attaches renderers, materials, tooltips,
  selection state and favorite/brush event handlers even when paint mode is
  never opened. The controlled 35-minute heap pair kept the main GUI/render
  populations nearly flat, so this is primarily large baseline retention and
  a new-game/resync construction-freeze amplifier, not the measured interval's
  main growing leak. Direct heap comparison found exactly one `PaintToolbox`
  and exactly 11,475 decal button closure/value triplets in both the baseline
  and 35-minute follow-up, proving it is one stable eager tree rather than
  repeated toolbox retention. The subsequent local experiment implements lazy
  per-`ShipRules` construction and a second per-normal-group layer; its current
  deployment and required live checks are recorded below. Do not globally
  clear widget or weak-event state.
- A complementary opt-in single-player diagnostic is deployed locally under
  `singleplayer-memory-diagnostics.flag`. Its read-only `GameRoot.Update(Action)`
  postfix logs once/minute and skips multiplayer games. It records process/GC
  memory, fragmentation, handles, process-wide allocation MiB/s, GC deltas,
  game/simulation identities, mode/tick, live ships and physical/blueprint
  parts, total/preloaded stasis spawners and paint picker/item counts. Use a
  normal 30--60 minute single-player run to separate live world growth,
  fragmentation, allocation churn and old-GameRoot retention before requesting
  another full dump. Build and smoke pass with zero warnings/errors; current
  root/package/live DLL SHA-256 is
  `256E4999B464B4DC9119959910DAEEE8B9FC3687B8D29785227647FD743CF62A`.
  The first live run reached the loaded career game normally but crashed at the
  first 60-second report because the reporter read `IsPreloaded` on stasis
  spawners whose `SupportsPreloading` is false; vanilla deliberately throws
  `NotSupportedException` in that case. Both SP and MP reporters were corrected
  to guard the property with `SupportsPreloading`. This was a diagnostic-code
  exception after successful load, not save corruption.
- The lazy PaintToolbox experiment is now implemented and deployed locally in
  two layers. The toolbox constructor captures per-`ShipRules` decal/base
  picker contexts and builds a ship class only on first paint activation.
  Within that class, normal decal tabs/pages are constructed immediately but
  their `AddDecalButton` loops run only when each group is selected/activated.
  Favorite groups retain vanilla's dynamic path; `SelectDecalType` first
  materializes the matching pending group, preserving grab-decal behavior;
  built groups remain resident. Build and extended smoke test pass with zero
  warnings/errors, including prefix/postfix installation checks. Cosmoteer was
  confirmed stopped before deployment. Current root/package/live DLL SHA-256:
  `1333ED39D71285D3C7DEFE031CE0428F95CE0750B2C809D0B48749EFB0FE3464`.
  Package/live source mirrors also match the repository source. This group
  layer opened and switched groups successfully in live UI. That test exposed
  all favorite stars as visible because lazy buttons were added to an already
  active page before vanilla attached their activation-time refresh handler.
  The current hash temporarily deactivates only the page during synchronous
  item creation and reactivates it afterward, restoring vanilla event order.
  The star correction and favorite add/remove still need a short live retest.
- Intentional repository changes were still uncommitted: `CHANGELOG.md`,
  `EmmanimLagFix.Code.SmokeTest/Program.cs`, `MEMORY_DIAGNOSTICS.md`,
  `Mod/Code/EmmanimLagFix.Code.dll`, and the two mirrored
  `ToggleModeDelegateCachePatch.cs` files. Preserve unrelated/user changes.
- Superseding live state (2026-08-31 03:30): a 20-second trace taken during
  reported multiplayer stutter found repeated ~125 ms ship-renderer
  `MapSubresource` waits, a ~125 ms GC-poll interval and a ~81 ms
  `BlueprintPartStatProvider.GetToggleMode` interval while MP queues remained
  empty/healthy. A conservative local post-2.0.15 experiment now suppresses
  only byte-identical `AtlasQuadManager` writes (including their false
  `ChangeCount` invalidation) and gates blueprint stat-provider operational
  checks to once per game second when unpaused; paused feedback remains
  vanilla and no per-component cache is created. Build/smoke pass, package and
  live source mirrors match, and current root/package/live DLL SHA-256 is
  `0CBA857E45E0AE07FE72B08BC568A692483F76B66C6801D2773769DE5FBBBD39`.
  Live effectiveness still needs the same-save/camera comparison trace.

Next-session sequence:

1. Read `emmanim_lag_fix_code/MEMORY_DIAGNOSTICS.md` first; its top-level
   `Current handoff summary (2026-08-30 22:44 KST)` supersedes the older
   continuation notes below it.
2. Cosmoteer was stopped at handoff and the current experimental DLL/source
   were deployed to package and live mod. Resolve the process again before any
   later live replacement.
3. The corrected 49-minute SP series showed a world-population step, then a
   plateau: 17:51 -> 18:05 private 10932 -> 11001 MiB, heap 5170 -> 5193 MiB,
   handles 1446 -> 1449, stasis preload 75 -> 81, paint items fixed at 11475.
   This is not current evidence of a simple unbounded managed or handle leak.
4. The main remaining single-player problem is 100--185 MiB/s allocation churn
   with roughly 700--1240 Gen-0 collections/minute. Attribute the remaining
   allocation stacks under vector/matrix/color samples before changing code.
5. Live-validate the deployed lazy PaintToolbox layers: confirm normal paint
   opening, switch multiple normal decal groups, add/remove a favorite, and use
   grab-decal on an item from a not-yet-opened group. Diagnostic item counts
   should include only active/visited groups rather than all 11,475 items.
6. Current uncommitted root/package/live DLL SHA-256 is
   `1333ED39D71285D3C7DEFE031CE0428F95CE0750B2C809D0B48749EFB0FE3464`.
   It includes the corrected SP/MP stasis-preload guard, resync buffer/timing
   work and both opt-in memory diagnostics. Public release remains 2.0.14.
7. Do not globally clear weak events, inactive widgets, media effects, MP
   queues, or add unsafe long-lived resource/path caches. Keep
   `StasisLiveRange = 2500` and `StasisPreloadRange = 3000`.

## Long-session CPU degradation is real, and only a process restart clears it (2026-09-01)

Measured on a two-player host session with `multiplayer-memory-diagnostics.flag` enabled. The
diagnostic's `tick=` field is the network input tick; differencing it between consecutive
once-per-minute lines gives the achieved simulation rate, and `TargetFps = FpsTarget30` in
`settings.rules` is the target. That ratio, not FPS, is what the player feels as lag.

Over one 4-hour session the rate collapsed while the world got **smaller**:

```
시각    tick/s   private   heap     live parts
00:44    29.7    10.9 GiB  5.4 GiB   28,836
02:02    26.4     9.2 GiB  3.3 GiB   63,512   <- 4x the parts, 2.3x faster
04:44    11.3    12.5 GiB  5.5 GiB   15,606
```

Parts fell 4x and ships 70 -> 33 while the rate more than halved, so this is process age, not world
size. A fresh process on the same save ran 53.0 `SimRoot.FixedUpdate`/s with 43,763 parts — 4x the
rate at 2.8x the load — and stayed flat (24.3 -> 27.8 tick/s) over its first 12 minutes while parts
grew 37%.

### What does and does not reclaim

**A multiplayer game restart reclaims nothing.** The 02:05 resync is the natural experiment: it
logged `Ending game for reason: Resyncing` / `Game popped off stack.` / `Game pushed onto stack.` —
a complete `GameRoot` teardown and rebuild, the strongest in-process reset available — and private
memory went *up*, 9.4 -> 11.1 GiB. Leaving to the menu and rejoining is the same path.

**An FTL jump does**, but only the departed sector: 01:51 dropped heap 5.37 -> 3.03 GiB and stasis
spawners 4,891 -> 1,640, and the rate recovered 21.4 -> 26.4. It returned to 12.8 GiB within 2.5
hours and never came back down, even as parts fell to 15,606.

So the only remedy is exiting the executable. Tell the user that explicitly — "restart" is ambiguous
and the in-game restart is the one that does not work.

### Reading the degraded state in a CPU trace

`dotnet-counters` is still broken against this .NET 10 process. Use:

```powershell
dotnet-trace collect --process-id <pid> --providers "Microsoft-DotNETCore-SampleProfiler" \
  --duration 00:00:00:20 -o <out>.nettrace
dotnet-trace convert <out>.nettrace --format Speedscope -o <out>
```

`--profile cpu-sampling` is rejected by `collect`; name the provider. The Speedscope export is
**evented**, not sampled — `profiles[].events` with `O`/`C` records, no `samples`/`weights` arrays,
so a sampled-format aggregator silently produces zeros. Walk the events keeping a stack and
attribute each interval, and attribute inclusive time **only to intervals whose leaf frame is
`CPU_TIME`**; otherwise `WaitHandle.WaitOne` and friends dominate and mean nothing. Opening and
closing `g___Present|2`, `SimRoot.FixedUpdate` and `Director.DoUpdate` gives exact per-second counts
and per-call durations, which is how the 13.4/s vs 53.0/s figure above was obtained.

Degraded 5.7-hour process, 20 s, 65.9 s CPU across 16 logical cores:

```
SpinWait.SpinOnce                          39.0 s  59%   FastParallel workers idle-spinning
  |- Thread.PollGCWorker                   17.8 s  27%   GC suspension rendezvous
real work                                 ~26.9 s
main thread: Draw 38% / Update 25% / FixedUpdate 14% / Present 9% / LimitFps sleep 18%
largest work leaf: MultiMediaEffectNode+EffectAnchor.Update  4.0 s (15% of real work)
Monitor.Enter_Slowpath 4.75 s, status diffusion 2.26 s, ResourceManager.UpdateSinkJobs 1.97 s
```

`EffectAnchor.Update` is a childless loop inside `SimRoot.ParallelUpdate` — the visual media-effect
anchor set walked in full every frame. It is the leading candidate for the growing cost and the next
thing to attack, well ahead of the resource and heat paths.

**A trace taken in the first ten minutes is not comparable.** Harmony's
`MonoMod...JitHookDelegateHolder.CompileMethodHook` was still 8.2% at 3 minutes and 7.2% at 11, and
`GC.RunFinalizers` 5.8% at 3 minutes. Compare only traces at similar process age.

### Server GC: applied and confirmed active

`Bin/Cosmoteer.runtimeconfig.json` (and the mod loader's) ship **no** `System.GC.Server`, so the game
runs Workstation GC on a 5.66 GiB heap with 22% fragmentation — which is what the 17.8 s of
`PollGCWorker` costs. `"System.GC.Server": true` was added to `Bin/Cosmoteer.runtimeconfig.json` on
2026-09-01; the vanilla file is backed up and Steam file validation restores it. This is a JSON
config, not a shipped binary, but it is still under `Bin/` — get the user's consent before touching
it, and note the Bash tool's auto-mode classifier blocks writes there (the Write tool is the route).

**Confirmed active**, but do not try to confirm it by Gen0 count or by raw thread count. Gen0
collections stayed at 238--603/min, the same range as Workstation GC, and total thread count is
useless because the pool fluctuates (70, 74, 75, 83, 99 were all observed on Server GC runs).

The reliable check needs no restart, no comparison run and no multiplayer partner. Group the threads
created within two seconds of `Process.StartTime` by `PriorityLevel` and sum
`TotalProcessorTime` per group:

```
  Idle         n=16   totalCPU= 0.05s   max 0.03s     <- Server GC heap threads
  Normal       n=17   totalCPU=15.84s   max 15.27s    <- FastParallel workers etc.
  AboveNormal  n=2    totalCPU= 5.92s
```

The count equals the logical processor count (16 here) and the whole group has burned essentially no
CPU, because GC heap threads park until a collection. Halfling's `FastParallel` workers are the
confounder — it also starts roughly one per core at init — but they spin, so they accumulate seconds
each, as the Normal group shows. Workstation GC has one background GC thread, not sixteen.

Whether it actually helps is still open: the 12-minute session that followed was far too short to
compare decay slopes, and every improvement measured that day is fully explained by the restart
alone.

### Korean IME: the commit fix works, one symptom remains

The 22:54 `_isImeComposing` gate in `KoreanImeResultStringPatch.CommitTo` was deployed and live chat
confirms the eaten-character class is gone (`쮸아아아아아아아아압 쮸아아압` keeps its space and full
length; the old build produced `동기화가빠른` and `잘 될가 없는`). No `Korean IME commit failed` was
logged.

A stray leading jamo still appears (`ㅍ흐물흐물프룸`, `ㅎ쮸아아아아압`). It looks like residue of the
previous message's first consonant, i.e. composition state surviving a send and being retyped on the
next session's first `GCS_COMPSTR`. Diagnosing it needs `korean-ime-diagnostics.flag`, which produced
**94,871 of 95,526 log lines (99.3%)** in one session — keep it off during any performance
measurement and enable it only for a short dedicated IME run.

## `ResourceIDComparer` allocates a closure on every comparison (2026-09-01)

A 10-second `gc-verbose` trace on a 61-minute single-player session recorded ~1,053 MiB of
allocation (105 MiB/s) and attributed **55.3% of it to one method**,
`Cosmoteer.Resources.ResourceIDComparer.<Compare>g___GetIndex|3_0`:

```
GameRoot.Input                                    63.9%
  BuildToolbox.OnBlueprintModeUpdatingUIState     62.8%
    ShipUpdateInfo.GetTotalPhysicalCost           55.3%
      Extensions.AddCount  (SortedDictionary)     51.4%
        ResourceIDComparer.Compare
          _GetIndex                               55.3%   <- leaf
```

The method looks cached and is not:

```csharp
static int _GetIndex(ID<ResourceRules> id)
{
    if (!s_idIndexes.TryGetValue(id, out var value))
        s_idIndexes.TryAdd(id, value = GameApp.Rules.Resources.FindIndex(rr => rr.ID == id));
    return value;
}
```

`rr => rr.ID == id` captures the parameter, so Roslyn emits a display class — confirmed present as
`Cosmoteer.Resources.ResourceIDComparer.<>c__DisplayClass3_0` — and **constructs it in the method
prologue, not inside the `if`**. Every call allocates, at a 100% cache hit rate. It runs twice per
`SortedDictionary` comparison, O(log n) times per part, over every part, every input frame.

`ID<T>` is a `readonly struct` implementing `IEquatable<ID<T>>`, so this is not a boxing problem and
the `ConcurrentDictionary` really is hitting. Do not "fix" the dictionary.

This is what makes a **paused** game allocate 300--365 MiB/s: the build toolbox re-aggregates the
whole blueprint's cost every input frame. The existing `BuildToolboxStatsThrottlePatch` does **not**
cover it — that one gates a `StatsGui.Update`, a different path.

### Fixed and verified

`ResourceIdComparerAllocationPatch` replaces the body of `ResourceIDComparer.Compare` with a call to
an allocation-free generic helper. A **transpiler**, not a prefix: the parameter type
`ID<ResourceRules>` is an internal generic struct that C# cannot name in a patch signature, while a
transpiler works at IL level and takes the type argument from the original method's own signature at
patch time. On a cache miss the helper invokes vanilla `_GetIndex` by reflection, so the value —
including a `-1` for an unknown ID, which vanilla also caches — is identical by construction and
vanilla's own cache is filled too; a miss happens at most once per resource ID per process.

Guards: instance method, two parameters of the same type, `int` return, and the local function
resolvable. The local function is found **by shape, not by its mangled name**
(`<Compare>g___GetIndex|3_0` is not a stable contract). Any mismatch returns the original
instructions unchanged and logs once. The smoke test asserts the transpiler installed, that
`_vanillaGetIndex` is non-null (proving the guards passed rather than falling back), and that
ordering is unchanged with a pre-seeded cache.

Measured on this installation, comparing minute-by-minute single-player diagnostics at comparable
scene size:

```
                 before (parts 23,467--23,741)   after (parts 33,188--40,463)
allocation           300--365 MiB/s                   66--81 MiB/s      -78%
Gen0 collections     1,993--2,402 /min                448--539 /min     -80%
```

Sustained across six hours. The allocation trace also shows the method itself falling from 5,963
samples (55.3%) to exactly zero.

Confirmed again in blueprint mode itself, at 35,000 parts against the pre-fix 23,741:

```
                                  before    after
total allocation / 10 s        1,053 MiB   269 MiB   -74%
ResourceIDComparer                 55.3%      0.0%
Extensions.AddCount                51.4%      0.0%
ShipUpdateInfo.GetTotalPhysicalCost 55.3%     2.8%
BuildToolbox.OnBlueprintModeUpdatingUIState 62.8%  21.8%
GameRoot.Input                     63.9%     23.9%
```

When comparing two allocation traces, read the `parts` field out of the diagnostics line for the
matching minute first — a trace taken on a small blueprint proves nothing, because the comparer is
barely called. Note that `parts=A/B` needs an anchored regex; a greedy `.*parts=([0-9]+)` backtracks
and captures a single digit, which briefly made a 32,086-part scene look like a 0-part one here.

The rejected alternative was a prefix on `ResourceIDComparer.Compare` doing an allocation-free index lookup
(manual loop on miss instead of `FindIndex` with a closure, caching the result including `-1`
exactly as vanilla does). Returned values are identical, so ordering, `AddCount` aggregation and
lockstep state are unaffected. Same shape as the existing `ToggleModeDelegateCachePatch`.

Secondary allocators in the same trace: `MulticastDelegate.RemoveImpl` 3.6% (reached through
`SceneComponent.remove_BeforeDraw`, which is also 2.3% of CPU — O(n) delegate removal that rebuilds
the invocation array), `ShaderConstantCollection..ctor` 3.4%,
`SortedSet<PartInfo>.AddIfNotPresent` 3.1%.

### Allocation rate, not GC mode, drives `PollGCWorker`

With Server GC confirmed active, `PollGCWorker` rose from 27% to **39%** of all process CPU, and
total CPU from 3.30 to 5.94 cores, because this session ran 2,300 Gen0 collections per minute
(38/s). Sixteen `FastParallel` threads must rendezvous at each one. Attack the allocation rate
before touching GC configuration again — the mode was never the lever.

Reading a `gc-verbose` Speedscope export needs a different aggregator than a CPU trace: allocation
samples are zero-duration events, so summing interval weights yields zero. **Count stacks**, one per
open/close pair, and multiply by the ~100 KiB `AllocationTick` granularity.

## The thruster acceleration cache builds its snapshot before checking it may cache (2026-09-01)

Second-largest allocation site after the resource-ID comparer, at 13.1% of a ten-second
blueprint-mode trace. `ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached` caches per
`(direction, srfFactors, activationRangeType)`, keeping each thruster's uncommitted activation level
so a hit can replay it. On a miss:

```csharp
value.Item1 = CalculateMaximumAcceleration(...);
value.Item2 = new Dictionary<Thruster, float>();          // built and filled first
foreach (var t in _orderedThrusters) value.Item2.Add(t, t.UncomittedActivationLevel);
if (s_allowedCachedDirections.Contains(direction))        // only then asked
    _cachedAccelerations.Add(key, value);
```

`s_allowedCachedDirections` is only the six axis vectors plus the fixed `ShipFlightDirection`
angles. **Read the caller before assuming this is a UI cost**: the trace put 100% of it under
`MoveCommand.SetThrusterActivations` -> `Command.GetDesiredLinearSRA`, on the parallel fixed update,
which passes an arbitrary vector toward a move target. So the dictionary is discarded on essentially
every call, for every moving ship, every tick. `_cachedAccelerations` is also cleared on
`ship.Parts.PartsChanged`, so it misses constantly while a blueprint is being edited too.

`ThrusterAccelerationCacheAllocationPatch` hoists vanilla's own guard rather than writing a new one:
it clones the existing `ldsfld s_allowedCachedDirections / ldarg.1 / callvirt Contains` triple in
front of the construction and branches to the label the original `brfalse` already carries — which
is the instruction after the cache insert. Cloning keeps the original operands, so no field or
method is resolved by name. The insertion point is the tuple-address load before the `newobj`, where
the evaluation stack is empty and which sits outside the enumerator's protected region, so no branch
crosses a `try`.

Skipping leaves `Item2` at the `null` the failed `TryGetValue` already wrote; it is only read back
out of the cache this branch does not populate, and the return value is `Item1` plus the ramp times.
Activation levels come from `CalculateMaximumAcceleration`, untouched.

### Verify rewritten IL by compiling it, not by patching it

Harmony installs a transpiler without proving the result is valid; an unbalanced stack or a bad
branch surfaces as `InvalidProgramException` at the *first call*, which for this method is on a
moving ship mid-game. The smoke test now forces every rewritten method through
`RuntimeHelpers.PrepareMethod`, so malformed IL fails in the test instead. Do this for any
transpiler that reorders control flow.

Each transpiler also exposes a static flag (`Applied`, `_vanillaGetIndex`) set only when every shape
guard passed, and the smoke test asserts it. Checking that a patch is *installed* is not enough —
a guarded transpiler that fell back to vanilla is still installed.

### Release 2.0.22 and the Server GC verdict (2026-09-01)

Published at `https://github.com/EnYuri/Emmanim-Lag-Fix/releases/tag/v2.0.22`. Commits
`1675b16` (2.0.21, resource-ID comparer) and `7343f66` (2.0.22, thruster cache guard) on `main`,
tag `v2.0.22`; Actions run 33469859892 passed validation, packaging, archive inspection and
publication. Asset `Emmanim-Lag-Fix-2.0.22.zip`, 1,301,381 bytes, SHA-256
`C82FB149996AA4BF5D707B862710DE2A5796FDEA9F7BAA9DD45E961410989F23`. Live/package code DLL SHA-256
`44b5DD768B83E17009BE98F9...` (`44b5dd76…`). 2.0.21 exists as a commit only; its fix ships inside
the 2.0.22 payload.

**Server GC was reverted.** `Bin/Cosmoteer.runtimeconfig.json` is byte-identical to the shipped file
again (SHA-256 `C0ECF8B9D1A52C17060BAD8FADE57C750C33949712AD2C9B45EA2AD8F8C8F910`). It was confirmed
active — see the thread-priority check above — but never showed a benefit and twice showed the
opposite:

```
                            allocation   Gen0/min   PollGCWorker (per 20 s)
Workstation, pre-fix              ?          ?       27%   17.8 s
Server GC,   pre-fix         350 MiB/s     2,300     39%   46.4 s
Server GC,   post-fix        145 MiB/s       970     47%   80.3 s
```

Collections more than halved while GC rendezvous CPU nearly doubled. The plausible mechanism is that
Halfling's sixteen `FastParallel` workers **spin** in `SpinWait.SpinOnce` rather than blocking, so
every stop-the-world pause is multiplied by the number of spinning threads, and Server GC's
per-core heaps make each pause longer. A single-heap Workstation GC may genuinely suit this engine
better.

That is a hypothesis, not a result: the three samples are different scenes, and parallel workload
alone moves this number. The controlled test needs one restart on the same save and location with
the setting off, comparing tick/s (the metric that matters), `allocatedMiBs`/`gen0` (to prove the
loads are comparable) and absolute `PollGCWorker` ms. It has not been run.

The lasting lesson is that the Server GC hypothesis was wrong from the start. The lever was never
the GC mode; it was the allocation rate, and fixing two allocation sites bought -74% allocation and
-80% collections. Measure what allocates before touching how it is collected.

## Sixteen workers, one queue tail: `EnqueueNonDeterministic` (2026-09-03)

`SimRoot` holds a single `ConcurrentQueue<Action> _queuedNonDeterministic`. Anything that runs on a
FastParallel worker but must touch the scene graph posts to it, and the main thread drains it in
`ExecuteQueued`. There are exactly two references to the field — the enqueue and that drain — so
`ExecuteQueued` is the only place shards would need to be drained from.

A 20-second CPU trace on a 170-minute two-player host session:

```
real work (spin-excluded)                       19,119.9 ms
  EffectAnchor.Update subtree                    2,839.5 ms  14.9%
    SimRoot.EnqueueNonDeterministic              1,433.9 ms  50.5% of subtree, 7.5% of all real CPU
    EffectAnchor.Update itself                   1,310.9 ms  46.2%
  SimRoot.ExecuteQueued subtree                    507.9 ms   2.7%
```

**The enqueue cost more than the anchor's own vector maths**, and 100% of it came from
`MultiMediaEffectNode.EffectAnchor.Update` — update bucket 8, under `SimRoot.ParallelUpdate`, one
anchor per playing media effect, every frame, across sixteen threads. Draining is cheap, so the
whole expense is on the producer side: ~240 ns per enqueue against ~20 ns uncontended.

**Do not look for a change-detection win here — vanilla already has one.** `EffectAnchor.Update`
sets `flag` only when location, rotation, intensity or colour actually differ, and enqueues only if
`flag`. It reads as a missing guard and is not. On a moving ship the location genuinely changes
every frame, which is why the guard does not help.

Fixed in 2.0.27 by `NonDeterministicQueueShardingPatch`: a transpiler swaps the single
`_queuedNonDeterministic.Enqueue(callback)` for a per-thread shard (power-of-two count from
`ProcessorCount`, indexed by `Environment.CurrentManagedThreadId & mask`), and an `ExecuteQueued`
postfix drains every shard where vanilla's own drain ends.

The ordering argument is the whole safety case: **vanilla's cross-thread order carries no
happens-before.** Two workers racing for one queue tail produce a total order, not a meaningful one,
so nothing may depend on it. Same-thread order does carry one, and thread-id sharding preserves it
exactly. Callbacks are not deduplicated, dropped or delayed a frame, and the main-thread inline
branch (`callback()` when not in a parallel update) is not rewritten.

A shard lookup on every enqueue would eat the win, so the SimRoot→shards pair is cached in one
immutable object (single reference read, single compare) behind a `ConditionalWeakTable` fallback.

`SimRoot` is `internal`, so the patch types `__instance` as `object` and resolves the type by name —
the same shape as `StreamingSoundSampleStartPatch`. Released as
`https://github.com/EnYuri/Emmanim-Lag-Fix/releases/tag/v2.0.27`, asset
`Emmanim-Lag-Fix-2.0.27.zip` 1,325,335 bytes, SHA-256
`4fec7f6c31c4978c3a0d0e67ba7c62f000cdd908c9ace1fe7b5e649ce32e1a83`; commit `a34eff2`, Actions run
33674818601. Live effect is **not yet measured** — repeat the same-save 20-second trace and compare
`EnqueueNonDeterministic` against its 1,433.9 ms baseline.

### The source mirror is `Mod/Source/EmmanimLagFix.Code/`, not `Mod/Source/`

`cp <patch>.cs Mod/Source/` succeeds and silently puts the file one directory too high, where
nothing builds or ships it. Verify parity by comparing every `EmmanimLagFix.Code/*.cs` against
`Mod/Source/EmmanimLagFix.Code/` before committing.

## Multiplayer stutter is an input-tick stall (2026-09-03)

Two-player session, both peers on released 2.0.28. Motion jerky while frame rate looks fine. The
user's own constraint settles the shape of the problem before any tracing: **single player does not
stutter, with more parts and larger ships than the multiplayer session.** That falsifies every
scene-size explanation at once — scene cost, draw cost, `FastParallel` dispatch overhead and
main-thread load all exist in single player too. Only multiplayer-exclusive mechanisms can explain
it, which in this engine means the lockstep input gate.

**Read `inputQueued` against `inputMax`, never either alone.** `MultiplayerMemoryDiagnosticsPatch`
sums `player.QueuedInputTicks` into `inputQueued` and takes the largest single player's into
`inputMax`. With two players, equality means one player holds every queued tick and the other holds
zero — the host cannot advance network time and the other side's inputs pile up behind the gate.
**70 of 75 rows** in `Logs/log 2026-09-03 18_00_22.txt` were exactly equal. A depth of 4–7 read on
its own looks like healthy buffering and is the opposite; that misreading cost a whole round of
wrong conclusions here.

Differencing `tick=` between consecutive rows gave 8.8–19.8 ticks/s against the vanilla 30/s target.

The host is not the limiter. Wall-clock phases of its main thread over 20 s
(`Logs/stutter_mp_228_2026-09-03.nettrace`): Draw 7,162.8 ms / 35.8%, Update 6,557.3 ms / 32.8%,
**LimitFps 2,955.9 ms / 14.8%** (2,659.3 ms of it unmanaged wait), Present 1,584.6 ms / 7.9%. It is
asleep in the frame-rate limiter one seventh of the time — work finished, idling by choice. Summing
only Draw and Update and calling that saturation is wrong; count the limiter sleep and Present
first. Mod code was 439.6 ms, 1.13% of real process CPU.

Network path clean on every row: `connectionQueued=0`, `outgoingInputs=0`, `hashes=0/0/0`,
`sentKiBs` 4.2–12.0, no `WaitingForAck`, zero exceptions. The zero receive queue is the informative
one — bursty delivery would accumulate and drain. Nothing waits, so the missing inputs were never
sent rather than arriving late.

Not yet established: which player is at zero. The summed field cannot say. The host generates its
own inputs locally with no network hop and idles 14.8%, so it is implausible as the starved side,
but that is inference. **Next step is the client's log** — have the remote player create an empty
`multiplayer-memory-diagnostics.flag` beside their mod's `Code` directory before launching, play
~30 minutes, then diff `tick=` on their `role=client` rows against the host's 8.8–19.8/s. Matching
rates confirm the client is the limiter; a much higher client rate refutes it and reopens the
search. Their rows also carry `privateMiB` and `parts=`, which say whether a four-core machine can
simulate this world at 30 ticks/s at all.

Full record, including the two profiling traps below, is in
`emmanim_lag_fix_code/MULTIPLAYER_SYNC_DIAGNOSTICS.md`.

### Two profiling traps that produced false leads here

**A profiler hotspot can be a stack-walk artifact — cross-check OS per-thread CPU.** The sample
profile attributed 8,684.4 ms, 22.4% of all real process CPU and the largest single item, to
`MonoMod.Core.Interop.CoreCLR+V60.InvokeCompileMethod` on one background thread, 99.5% with no
managed caller. Windows `TotalProcessorTime` sampled 10 s apart showed that thread using under
100 ms — it is not among the 14 busiest threads. Confirmed independently: collecting
`Microsoft-Windows-DotNETRuntime:0x10:5` for 10 s and for 3 s gave 43,700,602 and 43,691,750 bytes,
essentially identical, because the payload is the end-of-session method rundown, not live JIT. On a
warm process actual new JIT is about a kilobyte per second. Harmony's JIT hook is not a running
cost.

**Sample counts are not call counts.** Speedscope converted from `Microsoft-DotNETCore-SampleProfiler`
synthesises open/close events by diffing consecutive stack samples, so a frame "duration" is a run
of contiguous samples and two calls less than a sample interval apart merge into one. Percentiles
from them approximate frame time only loosely, and counts of things like
`FastParallel.RunParallelBatch` undercount short calls. Do not quote them as rates.

### `ships=` is simulation population, not what is on screen

`MultiplayerMemoryDiagnosticsPatch` prints `sim.Ships.Count` — every ship fully simulated inside
`StasisLiveRange = 2500`, most of it off-camera. A row reading `ships=236` says nothing about how
many are visible, and advising the player to keep fewer ships on screen is meaningless. In this
save the count swings 6 → 236 and `parts=` 14,174 → 53,829 within minutes as the camera moves
through a sector holding 5,411 stasis spawners. Normal for a populated system, and not on its own
evidence of anything.
