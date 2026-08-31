# Changelog

## 2.0.16

- Stop a resource-source path traversal once it has visited every registered tile for the requested concrete resource. This preserves every source and its vanilla ordering while avoiding the guaranteed-empty tail of very large ship path networks; stackable wildcard searches retain vanilla behavior.
- Snapshot each relevant resource's ship-wide desired-amount status once for the duration of a sink-job update pass, avoiding repeated off-ship-crew scans for every storage priority comparison. The snapshot is published read-only immediately before the vanilla parallel pass and discarded immediately afterward.
- Remove the duplicate `HashSet.Contains` probe before `HashSet.Add` in path-contiguity breadth-first searches; traversal membership, order and iteration distances are unchanged.
- Extend the existing exact sparse-heat implementation to sparse heat bounds from 64x64 upward. Dense fields still use vanilla through the existing density guard, while medium sparse networks avoid rectangular input/output array preparation.

- Suppress byte-for-byte redundant `AtlasQuadManager` writes before they dirty
  the complete dynamic GPU buffer. Real quad changes retain the vanilla write
  and change counter; unchanged assignments no longer force a later full
  `Map`/copy/`Unmap` upload or invalidate the cached ship indicator icon.
- Refresh blueprint stat-provider operational toggles once per game second
  during unpaused play, using the existing single gate per ship callback
  container. Paused simulations retain vanilla per-frame feedback, and no
  per-component cache or GC handle is created.

## 2.0.15

- Extended the initial-multiplayer-sync buffer optimizations (exact-size
  preallocation, zero-copy handoff) from first launch to also cover a
  mid-session resync, and added opt-in timing logs for a resync's three
  expensive background phases (host save, host load, client load) so a slow
  resync can be attributed without changing serialization, scheduling, or
  game state.
- Added two opt-in, low-frequency memory/allocation diagnostics (multiplayer
  and single-player), each gated behind a flag file in the live mod root and
  otherwise fully inert. Neither ever mutates a queue, the simulation, or any
  retained collection; they only log process/GC memory, live ship/part counts
  and stasis-preload counts once per minute for correlating memory growth with
  world state.
- Deferred `PaintToolbox`'s per-ShipRules decal-tab and base-roof-texture
  picker construction from the paint toolbox's constructor (eager for every
  ShipRules across every installed ship-adding mod) to the first time the
  player actually opens paint mode on a ship of that class. Measured as one
  stable eager tree of 11,475 decal widgets on this installation; see
  `MEMORY_DIAGNOSTICS.md`. Implemented as two narrowly-scoped Harmony
  transpilers that redirect the single per-ship builder-method call site to a
  cheap context-capture call, plus a postfix on `OnSelfActivated` (the only
  place the painted ship is ever assigned) that lazily invokes the original,
  untouched builder methods. Both transpilers require an exact single-call-site
  match and disable themselves if the game code shape changes. Favorite
  decals, per-ship `_updatingUIState` toggle wiring and `SelectDecalType`/
  grab-decal behavior are preserved. A second lazy layer now creates normal
  decal buttons only when their group tab is first opened, in batches of at
  most 128 items per rendered frame while the tab stays open rather than all
  at once; unopened groups remain as lightweight tabs/pages. Favorite groups
  retain vanilla's dynamic add/remove path, programmatic decal selection
  (grab-decal, `SelectDecalType`) forces the remainder of the matching group's
  batch immediately, and built groups remain resident without unsafe widget
  teardown. Lazy item creation re-activates only the newly added item after
  construction, restoring vanilla's activation order so non-favorite items do
  not incorrectly retain the favorite star's default-visible state.

## 2.0.14

- Reduced the peak memory and copying cost of initial multiplayer
  synchronization. The client preallocates its incoming stream from the exact
  announced payload size, the host preallocates each outgoing stream from the
  remaining serialized length, and the completed client stream can hand its
  existing byte array directly to the read-only deserialization stream instead
  of making a second full-size copy. Exact stream-state and .NET runtime-shape
  guards fall back to the safe 2.0.13 copy path on any mismatch.
- Reduced normal whole-game multiplayer integrity hashing from 30 Hz to 6 Hz.
  Input ticks, player actions and deterministic simulation remain at 30 Hz;
  normal desync detection can be delayed by at most about 0.167 seconds, while
  debug-only bucket hashes retain their vanilla cadence. Every participant
  must use the identical 2.0.14 DLL because peers must produce the same hash
  sequence.
- Aligned the host's normal `HostUpdate` construction, serialization and
  reliable transmission with the same 6 Hz schedule. Input-delay calculation,
  lockstep ticks and actions remain at 30 Hz, and desync-debug sessions retain
  vanilla 30 Hz updates.
- Cached the host's immutable sender-exclusion predicate per session and
  client. Forwarded `InputTick` cadence, payload, ordering, reliability and
  recipients are unchanged; after warm-up this removes one closure and one
  delegate allocation per received client tick.
- Added exact patch-resolution, tick-schedule, buffer-ownership and forwarding
  filter smoke coverage. Local host-room startup passed; actual remote-client
  joining and forwarding validation remains pending.

## 2.0.13

- Replaced the resource manager's exclusive `PerShipCount` list lock with
  immutable copy-on-write snapshots. Parallel source-search and sink-job reads
  no longer serialize on that lock, while count mutations retain the original
  entry order, allied-ship sums, and dead weak-reference cleanup. In the
  original 11,563-part megastructure trace, `PerShipCount.GetCount` fell from
  1.30% of sampled CPU time to zero samples, `Monitor.Enter_Slowpath` fell 52%,
  and `UpdateSinkJobs` fell 61%. A ten-second allocation trace attributed only
  2.03 MiB to the replacement arrays, with no Gen-2 or LOH growth.
- Limited non-deterministic `PartSmoothedValue` presentation updates to 20 Hz,
  passing the full accumulated game-time delta on each update. Deterministic
  fixed-update values, factory conversion ticks, production, and simulation
  state remain unchanged.
- Extended Halfling's application-level multiplayer session timeout from ten
  to thirty seconds. Exact IL guards preserve vanilla behavior if the expected
  game-code shape changes. Packet format, resend cadence, input ordering, and
  simulation state are unchanged; every peer should use the same mod version.
- Reduced the client's initial multiplayer synchronization memory peak. The
  received `GameInit` copy is preallocated to the known payload size, then its
  backing stream is disposed and released immediately after the unchanged
  deserialization step and before the simulation is constructed.
- Added the resource-logistics/path-search and multiplayer-synchronization
  diagnostic records, including the safety constraints for future patches.

## 2.0.12

- Removed the 2.0.11 fixed-update `PerShipCount` cache. A controlled 20-second
  4x trace on the original 11,563-part storage megastructure showed that its
  shared cache lock raised `Monitor.Enter_Slowpath` from 1.76% to 3.07% and
  `UpdateSinkJobs` from 1.40% to 2.68%. The patched count path itself cost
  2.64%, versus 1.30% for vanilla. All other 2.0.11 optimizations remain.

## 2.0.11

- Reuses each source's allied-ship anticipated-pickup total during a single
  `ResourceManager.FixedUpdate`. Vanilla repeatedly locks and scans the same
  weak ship-count list while searching sources and updating thousands of
  storage sinks. The cache is active only inside that fixed update, is
  invalidated before every count mutation, and never stores resource locations
  or crew paths across simulation ticks. Resource-search and crew-work rates
  are unchanged.

- Replaced the full rectangular scan used by vanilla heat diffusion with an
  exact sparse stencil for heat bounds of at least 128x128 cells. Only active
  heat cells and their direct neighbours can produce a diffusion delta, but
  vanilla prepared and processed every intervening cell on every 30 Hz physics
  tick. Diffusion coefficients, tick rate, row-major application order, status
  events, and small-ship behavior are unchanged.
- Limited display-only build-toolbox blueprint-stat aggregation to 4 Hz. A
  post-2.0.8 trace attributed 628 ms over 15 seconds to repeated stat totals;
  editor input, construction state, affordability checks, and ship data remain
  on their vanilla paths.
- Reduced hidden blueprint-network operational-toggle refreshes to once per
  ten game-time seconds during normal play. Cosmoteer keeps a repair/construction
  blueprint for every live and stasis-preloaded ship even when blueprint mode
  is closed; a 15-second idle 4x CPU trace attributed 1.36 seconds of the 2.20
  second game update to these ports, including 1.10 seconds in repeated toggle
  lookup. Paused simulations retain vanilla per-frame refresh for immediate
  blueprint editing. The gate is stored once per ship callback container, not
  per component.
- Capped scheduled resource-pickup connection lines at 128 and refresh their
  candidate transfer-job list once per second, while retaining the orange
  selection outline for every distinct scheduled nugget. A 15-second CPU trace during a
  large manipulator-beam collection attributed 7.52 seconds to
  `DrawResourceNuggetPickups`, versus 1.84 seconds to the whole game update.
  The patch also prevents an empty selected/hover companion overlay from
  clearing the shared line geometry cache every frame. Pickup endpoints still
  update every rendered frame; resource jobs and assignment rates are
  unchanged.
- Limited ship-transfer and station-trade row construction to resource types
  actually present on either ship or referenced by an existing transfer job.
  Vanilla previously created a `TransferWidget` for every stackable resource
  from every loaded mod and immediately hid nearly all of them.
- Spread blueprint-purchase technology-card insertion across frames at one row
  per frame, avoiding a single large scroll-layout activation burst. Visible
  card price/prerequisite refreshes now run at 2 Hz; the vanilla purchase input
  and authoritative validation paths are unchanged.
- Narrowed `Simulation/StasisPreloadRange` from vanilla 3750 to 3000 while
  keeping `StasisLiveRange` at 2500. Same-process heap comparison traced the
  long-session ship-graph growth to 491 additional fully constructed stasis
  preloads (47 -> 538), matching +53,927 live `Part` objects at about 109.8
  parts per added ship. This uses the engine's existing cancellation/disposal
  path and trades preload lead time for lower peak memory.
- Reused the toggle-mode callback per thread for
  `BlueprintPartStatProvider.UpdateOperational` and the internal blueprint
  network-port equivalent previously reconstructed on every simulation tick of
  every stat provider / network port on the ship. A ten-second allocation
  trace attributed roughly 815 MiB of managed allocation to this delegate
  alone; see `MEMORY_DIAGNOSTICS.md` for the trace evidence. Implemented as a
  narrowly-scoped Harmony transpiler (`ToggleModeDelegateCachePatch.cs`) that
  requires an exact single-site IL match and disables itself if the game code
  shape changes. A rejected per-instance `ConditionalWeakTable` prototype
  removed the allocation but caused rapid GC-handle and Gen 2 growth as
  blueprint components were reconstructed; the shipped implementation keeps no
  per-component cache entries.

## 2.0.5

- Replaced the loader-only scripts with a one-click `Install.bat` that installs
  the mod folder and the code loader together, resolving the user folder the
  same way `Cosmoteer.Paths` does, including a redirected *Saved Games* known
  folder.
- The installer now self-elevates only when `Cosmoteer\Bin` is not writable,
  refuses to run while the game is open, and clears the Mark of the Web from
  extracted files.
- Added `Uninstall.bat`, which can also remove the installed mod folder, still
  guarded by the install manifest hashes.
- Added `Pack.ps1` to build the GitHub release archive and regenerate the
  bundled LGPL source tree.
- Distribution moved to GitHub Releases; the Steam Workshop item is no longer
  the delivery channel.

- Reduced Steam networking thread starvation during initial multiplayer game
  construction by lowering only the host/client initialization workers to below
  normal priority.
- Added separate timing logs for host simulation creation and client data
  decoding plus simulation creation.

## 2.0.1 - pre-release

- Added a dedicated .NET 10 loader restricted to `nayuri.emmanim_lag_fix`.
- Added a one-second cache for the upper-right resource display aggregation.
- Limited ship-transfer and station-trade full resource snapshots to 5 Hz.
- Added safe install and hash-verified uninstall scripts.
- Added current-game Harmony target smoke tests.

## 1.3.2

- Restored vanilla loose-resource consolidation as an independent performance
  measure.
- Retained the 90/70/90 crew assignment and resource-search profile.
