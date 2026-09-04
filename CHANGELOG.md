# Changelog

## 2.0.32

- Stopped boxing a dictionary enumerator on every status lookup. A part keeps
  its statuses in a `Dictionary<StatusType, IStatusLocationInfo>` but exposes it
  as `IReadOnlyDictionary`, and the ship status manager exposes its handler
  dictionaries as `IEnumerable`; enumerating either through the interface boxes
  the struct enumerator. `Part` shows this was unintended - its two loops that
  read the private field allocate nothing, while the three that go through the
  public property allocate one each, on the damage-resistance,
  status-resistance and penetration-resistance paths.
- A ten-second allocation trace on a 900-ship host made these the two largest
  entries in the whole profile: 21.24 MiB of `Enumerator<StatusType,
  IStatusLocationInfo>` and 9.96 MiB of `Enumerator<StatusType,
  TileStatusHandler>`, together 19% of everything the process allocated. That
  matters more than its own cost, because the same session spent 40% of all CPU
  in `PollGCWorker` and 24% in `SpinOnce`: Halfling's parallel workers spin
  rather than block, so every stop-the-world pause is multiplied by the worker
  count and the lever is the allocation rate, not the hotspot.
- A transpiler replaces only the interface `GetEnumerator` call with a pooled
  wrapper around the dictionary's own struct enumerator, so the iteration, its
  order and its collection-modified check are unchanged and the result is
  bit-identical. The wrapper returns itself to a per-thread free list when the
  foreach disposes it; a nested enumeration gets its own instance and a double
  disposal cannot put one on the list twice. Anything that is not the expected
  dictionary falls through to vanilla's boxed enumerator.
- The fifteen call sites are the complete set in `Cosmoteer.dll`, across
  thirteen methods: three on `Part`, `PartCrew.IsBlockedByStatuses`, both
  `HitEffectParams.Alloc` overloads, both `PopulateStatuses` overloads on each
  status effect data provider plus the tile provider's local function, and
  `ShipStatusManager`'s player-source and junked-ship clears. Every target is
  required to yield at least one rewrite, so a shape change disables the patch
  rather than silently covering less than it claims.

## 2.0.31

- The two memory diagnostics switches now ship enabled. They were gitignored, so
  a clean CI checkout built every release without them and the release workflow
  additionally refused to publish if one was present - which meant nobody who
  installed from a release ever produced a diagnostics line. Nothing about the
  logging changed; only whether the file that turns it on reaches a player.
- This matters for multiplayer specifically. The patch is role-agnostic and the
  host's line already names which player is holding the lockstep readiness gate,
  but it cannot say why. That needs the same minute in that player's own log,
  which needs the switch on their install. A recent host capture showed the
  remote peer holding the gate 40-79% of frames with an empty queue and normal
  latency; the other half of that evidence was unobtainable.
- The Korean IME capture stays local and is now excluded explicitly rather than
  by the blanket rule: it logs every composition event and produced 99.3% of one
  session's log lines. `Pack.ps1` drops it from the payload, the release workflow
  fails on any flag that is not one of the two memory switches, and both flag
  files were rewritten as player-facing notes explaining what is logged, where it
  is written, and that deleting the file turns it off.

## 2.0.30

- Applied 2.0.29's visited-set fix to the place it turned out to cost the most.
  `ResourceManager.SearchForSources` marks the sources it has already considered
  in a pooled `TempHashSet<SourceInfo>`; the pool is global per closed type, so
  once one sink on a large ship has grown the set, every later sink pays a
  `HashSet.Clear` that zeroes the whole bucket array however few sources it
  actually saw. This runs once per sink per fixed update, in parallel, inside
  `ResourceManager.FixedUpdate` - 52.9% of all `ParallelFixedUpdate` time on a
  421-ship host - and a 20-second profile attributed 613.4 ms to that single
  `Array.Clear`, the largest zeroing cost anywhere in the process.
- The method is far too large to reimplement safely, so it is repaired in place:
  a transpiler routes its allocation and its three `Add` calls through helpers
  that record what was added, and a replacement pool deinitializer empties the
  set in proportion to that record instead of to its capacity. The set is only
  ever probed with `Add` and never enumerated, so nothing observable can depend
  on its internal layout, and an emptied set is indistinguishable from a cleared
  one. Any round the patch did not see from allocation to disposal - a nested
  allocation, a set recycled by another call site, a shape that stopped matching
  - falls back to Halfling's own deinitializer, so behaviour is identical to
  vanilla in every case and a peer on a different build still simulates the same.
- Verified 2.0.29's two optimizations on a live 20-second host trace: the
  contiguity search's 647 ms `Array.Clear` is gone, and the resource-desire
  snapshot's preparation fell from 277 ms to nothing.

## 2.0.29

- Replaced the contiguous-set breadth-first search's visited marker. Vanilla
  `PathContiguityManager.SearchSetsFrom` marks visited sets in a pooled
  `TempHashSet<ContiguousPathSet>`, and that pool is global per type: once a
  single whole-ship search has grown it, every later search pays a
  `HashSet.Clear` that zeroes the entire bucket array no matter how few sets
  were actually visited. The resource source search runs this thousands of times
  per second, and a 20-second host profile of a two-player session attributed
  647 ms - about 41% of all source-search time - to that one `Array.Clear`.
  The search now empties its visited set in proportion to the sets it really
  visited, falling back to a plain clear when the set was filled densely enough
  that clearing is the cheaper of the two.
- That replacement is behaviour-identical rather than an approximation: the seed
  loop, queue order, yielded values, deferred execution and exception behaviour
  are preserved, including vanilla's unconditional enqueue of a repeated search
  origin. The visited set is only ever probed with `Add` and never enumerated,
  so nothing can depend on its internal layout. A peer running a different build
  therefore still simulates identically.
- Narrowed the resource-desire snapshot's own preparation. It discovered which
  resource types to snapshot by walking every sink and every source on the ship,
  which on a large ship cost 222 ms of the same profile - more than the work it
  was preparing, whose totals cost about 4 ms. It now reads the ship's own
  resource desires directly, a few dozen entries. Desires are a superset of the
  types that walk could reach, so every lookup that resolved before still
  resolves, with the same value.
- The multiplayer diagnostic log line now carries a per-player breakdown when
  `multiplayer-memory-diagnostics.flag` is present: each player's queued input
  ticks, the fraction of frames vanilla itself recorded them as delaying the
  game, and their latency. The existing summed `inputQueued` cannot say which
  peer is failing to supply inputs, which is exactly the question an input-tick
  stall poses. It is read-only and changes no queue or simulation state.

## 2.0.28

- Stopped the tutorial/lore codex from running its IronPython show-conditions on
  every frame. `CodexHudGui.OnUpdatingUIState` is subscribed to
  `BeforeFrameInput`, so it walks every codex page once per frame, and
  `CodexPageRules.UpdateState` returns early only for a page that is already
  shown or has no condition. Every other page builds a fresh script scope, sets
  three variables on it and evaluates Python - vanilla ships 67 such conditions,
  some of which ask the simulation real questions such as
  `sim.HasShipWithLabelInSight('abandoned')` and `sim.StationInSight`.
- The cost is not mainly CPU. A fresh scope per page per frame makes IronPython
  rebind through the DLR, so `BuiltinFunction.BindToInstance`,
  `ScopeStorage.GetMemberNames` and `DynamicOperations.TryGetMember` emit dynamic
  methods that become garbage the moment the scope is disposed, and they come
  back on the finalizer thread as `DynamicResolver+DestroyScout.Finalize`, which
  frees JIT-compiled code. On a 20-second capture of a two-player host session
  that finalizer accounted for 2,403.8 ms - 5.7% of all real (spin-excluded)
  process CPU, and 56.1% of all worker-thread CPU spent inside frames longer
  than 20 ms. Every sample of it landed inside such a frame and none outside
  one. In the paired allocation trace every stack that created dynamic code ran
  through IronPython, the largest through `CodexPageRules.UpdateState`, and the
  codex subsystem allocated about 14.6 MiB in ten seconds against 136 MiB for
  the whole process.
- The conditions now run four times a second instead of sixty, removing about
  93% of that churn. In full, what the delay can cost: a codex page appears up
  to 250 ms later, its button is added or removed up to 250 ms later, and a page
  carrying `AutoPause` enqueues its pause input up to 250 ms later. That last is
  an ordinary queued player input, the same kind the pause button sends, so it
  stays ordered by the lockstep protocol; no ship, crew, resource or
  integrity-hash state is touched. The gate is one weak entry per `CodexHudGui`,
  and the smoke test checks that the first update runs, an immediate second one
  is skipped, and a second GUI keeps its own gate.
- This was found by auditing everything added since 2.0.22 against a live
  session rather than by reasoning about it. Measured there, the crew-search
  reach raised in 2.0.23 costs at most 0.38% of real CPU, the part-colour
  subscription change in 2.0.25 costs 0.01%, and the status-regulator cache in
  2.0.24 costs 0.01%; the mod's entire code footprint is 1.85%. None of them is
  the stutter, and none was changed.
- Both this and the 2.0.27 queue sharding were confirmed on a live 31-minute
  single-player session (62,000 parts, 312 ships) before release, with no
  exception and no shape-guard fallback in the log. `DestroyScout.Finalize` fell
  from 2,403.8 ms to 0.0 ms and disappeared from the profile entirely;
  `SimRoot.EnqueueNonDeterministic` fell from 1,433.9 ms (7.5% of real CPU) to
  12.5 ms (0.02%), with the whole sharding mechanism - `ShardedEnqueue` plus the
  `Drain` postfix - costing 43.3 ms where vanilla's single queue tail cost
  1,433.9 ms. The effect-anchor subtree fell from 2,839.5 ms to 305.6 ms and the
  codex and IronPython paths to about 10 ms combined. Frame durations are not
  comparable across the two captures because the scenes differ, but the ratio of
  the 99th percentile to the median - how spiky a frame time is, which is what a
  player feels as stutter - improved on all three roots: update 3.9x to 3.0x,
  draw 8.2x to 4.8x, fixed update 6.4x to 3.1x.

## 2.0.27

- Removed the multi-producer contention on the simulation's single
  non-deterministic callback queue. `SimRoot` holds one
  `ConcurrentQueue<Action> _queuedNonDeterministic`; anything that runs on a
  FastParallel worker but must touch the scene graph posts to it, and the main
  thread drains it in `ExecuteQueued`. A 20-second CPU trace on a 170-minute
  two-player host session measured 1,433.9 ms in `EnqueueNonDeterministic` -
  7.5% of all real (spin-excluded) process CPU, and 50.5% of the whole
  effect-anchor subtree, more than the anchor's own vector maths. Every sampled
  call came from `MultiMediaEffectNode.EffectAnchor.Update`, which runs in
  update bucket 8 under `SimRoot.ParallelUpdate`: one anchor per playing media
  effect, every frame, across sixteen threads. Draining the queue on the main
  thread cost only 507.9 ms, so the expense was entirely on the producer side -
  sixteen cores contending for one queue tail at roughly ten times the latency
  of an uncontended enqueue. A transpiler now shards that queue by thread. Each
  thread always maps to the same shard, so a given thread's callbacks still run
  in the order it posted them, which is the only ordering vanilla actually
  establishes; ordering between threads is not preserved and carries no
  happens-before, because two workers enqueuing concurrently already race for
  the tail. Callbacks are neither reordered within a thread, deduplicated,
  dropped nor delayed by a frame, the main-thread inline branch is untouched,
  and the drain runs inside the same `ExecuteQueued` call at the same point in
  the tick. `Applied` is set only once the enqueue site matched its exact
  expected shape, and the smoke test asserts that flag, forces the rewritten
  method through `RuntimeHelpers.PrepareMethod`, and checks that 2,000
  callbacks posted from eight threads each run exactly once and in per-thread
  order.

## 2.0.26

- Stopped a starved audio thread from crashing the whole game.
  `XA2StreamingSoundInstance.UpdateBuffers` computes
  `num5 = (int)(totalSubmittedSamples - samplesPlayed)` from a
  `_totalSubmittedSamples` snapshot taken before its release loop and a
  `samplesPlayed` value read from the live voice, then derives
  `sampleStart = (num2 + num5) % TotalSamples`. When the audio updater thread is
  starved long enough for the voice to play past everything submitted, `num5`
  goes negative; C#'s `%` keeps the sign of its dividend, so `sampleStart`
  follows, and `XA2StreamingSound.ReadSamples` throws
  `ArgumentOutOfRangeException` on the audio thread with no handler above it.
  That is a hard process crash. It was observed on a four-core client after
  2h43m of multiplayer, which then deadlocked in `XA2AudioManager.Dispose`'s
  `Thread.Join` during shutdown and only left the session when the host's ack
  timeout expired. A guarded prefix maps an out-of-range start back into the
  sound with the wrap-around the caller already intended. In range - including
  the end-of-sound value vanilla itself accepts - it changes nothing; out of
  range, one buffer is read from the wrapped position instead of terminating the
  process, and the first correction is logged. No simulation, network or
  lockstep state is touched.

## 2.0.25

- Removed the box allocated on every shader-constant update. Halfling's
  `D3D11BufferConstant` has eight non-generic `Update(gfx, value)` overloads,
  all funnelling into one `IsDataDirty<T>(in T value) where T : unmanaged`. The
  type parameter carries no `IEquatable<T>` constraint, so the only `Equals` in
  scope is `object.Equals(object)` and the compiler emits `box !!T` before every
  comparison — one heap allocation per constant, per shader, per draw call. A
  fifteen-second allocation trace on a two-player session put 28.1% of all
  process allocation (196 MiB) under `RefreshShaderConstants`, and an earlier
  capture 44.9%. All eight value types implement `IEquatable<T>` and each one's
  `Equals(object)` override delegates to that same typed comparison, so calling
  it directly is semantics-preserving: the boxed operand is always exactly `T`,
  so no other branch of an `Equals(object)` override is reachable. Only the
  `call IsDataDirty<T>` instruction changes; the evaluation stack there is
  already `(this, ref T)`, so no control flow and no local is touched, and the
  slot address is vanilla's own `_bufState.Data + _bufOffset`.
- Stopped building an XML reader for UI text that contains no markup.
  `TextBuilder.BuildLines` picks between `AddTextToLines(list, Text, ...)` and
  `XmlReader.Create(new StringReader(Text), ...)` plus `ParseXmlToLines`, and
  almost every widget sets `XmlFormatting`, so almost every text refresh built
  an `XmlTextReaderImpl` with its own character and node buffers. In the same
  trace `WidgetTextRenderer.OnRefresh` was 28.6% of all allocation (199 MiB), of
  which `XmlTextReaderImpl.FinishInitTextReader` was 12.2% (85 MiB) and the
  reader constructor a further 2.1% (15 MiB). For a string with no markup the
  XML branch's loop body reduces to a single
  `AddTextToLines(lines, reader.Value, ...)` with the same format state and the
  same null `prevChar`, and nothing runs after the loop, so the two branches
  produce the same lines. Only the branch condition changes: plain text now
  takes vanilla's own plain-text path. The test is deliberately narrow —
  anything containing `<`, `&`, a carriage return (XML normalises line endings
  inside text nodes), a character illegal in XML 1.0, a surrogate, or more than
  1024 characters (`XmlTextReaderImpl` may split long text across several nodes)
  keeps vanilla's XML path. Nothing is cached or reused across frames, and
  wrapping, ellipsing, fonts and geometry are untouched.
- Stopped each part's colour handler from unsubscribing itself out of a
  thousand-entry multicast event. `PartGraphics` subscribes `UpdateColor` to
  `Ship.Renderer.BeforeDraw` when a colour goes dirty, applies the colour on the
  next draw, and on the draw after that — finding itself no longer dirty —
  removes itself. The two halves are wildly asymmetric: `Delegate.Combine`
  allocates an array one longer and copies pointers, while `Delegate.Remove`
  walks the invocation list comparing delegates. On a twenty-second capture of a
  degraded session the combine side totalled 2.1 ms and the remove side
  1,469 ms — 19.9% of all CPU spent drawing, every sample of it arriving through
  `SceneRoot.Draw -> PartGraphics.UpdateColor -> remove_BeforeDraw ->
  MulticastDelegate.RemoveImpl`. With the list in the thousands and dozens of
  parts settling per frame, that is a full linear scan per settling part per
  frame. The self-unsubscribe is now removed: a settled part stays subscribed
  and returns immediately. Its sibling `PartToggledBlendSprites.UpdateColor`
  already works exactly this way in vanilla — permanently subscribed, guarded by
  a plain `bool` — so this is the engine's own pattern rather than a new one.
  The same rewrite also drops the flag clear that went with the unsubscribe, so
  `Registered` keeps meaning "this handler is in the invocation list":
  `OnColorChanged` correctly skips the resubscribe, and `OnPartDetaching` still
  removes the handler exactly once, so nothing is leaked and each part
  contributes at most one entry. What moves to the other side is one no-op
  invocation per settled part per frame against a linear scan per settling part;
  detaching still pays one scan, but that happens per part destroyed or
  deconstructed, far below the churn measured here.
- The same method's dirty test is `_colorUpdateStatus.HasFlag(Dirty)`, and
  `Enum.HasFlag` takes an `Enum`, so the IL boxes both operands — two
  allocations per handler call per frame. It becomes an `and` against the same
  literal. This half is required: without it, leaving handlers subscribed would
  trade a burst of scans for a permanent allocation rate, which is the opposite
  of the intent, so the two rewrites are applied together or not at all. The
  smoke test checks the substitution's arithmetic against the real type rather
  than the IL — that `ColorUpdateFlags` is backed by `int32` and that `Dirty` is
  the single bit 1, which is what makes a mask exactly `HasFlag` — and asserts
  that `OnPartDetaching` was left at vanilla, since that unsubscribe is what
  bounds the list.
- None of these patches changes simulation state, so lockstep and multiplayer
  hashing are unaffected; all are render/GUI-path only. Both are guarded by exact
  shape checks that fall back to vanilla instructions and log once, and both
  expose a flag set only when the rewrite actually happened, which the smoke
  test asserts — a transpiler that installed but fell back to vanilla is still
  installed. The smoke test additionally verifies the two substitutions'
  semantic assumptions directly: that each shader-constant type's boxing
  `Equals(object)` agrees with its typed `Equals` on real values, and that a
  real `XmlReader` with the game's own settings returns exactly one text node
  holding the identical string for every input the plain-text test accepts.
- Motivation. The problem being attacked is stutter, not average framerate. In
  that session median frame work was healthy (draw 2.0 ms, update 2.6 ms, fixed
  update 5.5 ms) while p99 was 23.9 / 34.4 / 35.0 ms — a 30-60 ms frame roughly
  every hundred frames. The main thread alone spent 2,075 ms of a 20-second
  window across 908 separate `Thread.PollGCWorker` events, about 45 stops per
  second, because sixteen `FastParallel` workers spin rather than block and must
  rendezvous at every collection. Allocation rate is the lever on that, which is
  why these two sites were picked. They do not address the other two stall
  causes in the same trace, synchronous stasis ship spawning (36.9 ms) and
  render submission (55.7 ms); neither is GC-related.

## 2.0.24

- Stopped `StatusValueRegulator` re-deriving its affected-cell list on every
  trigger. `GetAffectedCells()` walked the part's region with
  `Rules.Region.GetExactArea` and then sorted the result by squared distance to
  `Part.LocalCenter`, so a shield or similar regulator paid a region scan plus an
  O(n log n) sort each time it fired. Both halves are pure functions of the
  part's own fixed geometry — the region rules are static and `LocalCenter` does
  not move — so the list is invariant for the life of the instance and is now
  built once and copied out of a per-instance cache.
- The returned cells are byte-identical in the same sorted order, so status
  application, its callbacks and lockstep state are unchanged.
- This is a stall fix, not a throughput fix. A 60-second CPU trace on a large
  save recorded individual spans up to 146 ms whose leaf frame was
  `List<IntVector2>.Sort` inside `GetAffectedCells`. A thread inside a tight sort
  loop cannot reach a GC safe point, so a collection requested during one of
  those spans waits for it while every other thread burns CPU in
  `SpinWait.SpinOnce`/`Thread.PollGCWorker`. In single player that costs a frame;
  in lockstep multiplayer it freezes both peers and feeds the documented
  ack-delay path behind `WaitingForAck`.

## 2.0.23

- Raised the crew search reach one step, `MaxCrewSearchIterations` 50 to 100.
  It is not a frequency and not a distance: the budget is spent in dequeued
  cells, one per tile, so vanilla's 50 reaches 50 tiles of geometry however the
  ship is built. The search also starts at the resource **source**, not at the
  part being supplied, so every job drawing on one central store competes inside
  the same bubble while crew idling beside the starved part are never
  enumerated. A moving walkway cannot compensate — `CrewSpeedFactor` changes
  which tiles are visited, never how many. The extra budget is self-limiting
  because `GetCrewForJob` exits early through `_HasBestPossibleCrew()`, so only
  searches that currently fail pay for it.
- Raised `EqualPriorityJobDistanceThreshold` 10 to 20 with it, deliberately.
  `JobManager._TestAndInsert` keeps an incumbent only while its remaining
  distance is within this margin, and `ResourceTransferJob.GetRemainingDistance`
  adds the whole source-to-sink leg for a crew not yet carrying anything, so a
  crew recruited from further away is trivial to displace and walks half the
  ship for nothing. Widening the reach without widening the margin would convert
  idle crew into pointless walking.
- Both fields feed deterministic crew assignment, so every player in a
  multiplayer session must run this version.
- Stopped shipping the opt-in diagnostic switches. Three `.flag` files had been
  committed since 2.0.20 and were packaged into the release archive, so every
  installation had multiplayer, single-player and Korean IME diagnostics turned
  on; the IME capture alone accounted for 99.3% of the log lines in one session.
  They are now local-only, and the release workflow refuses to publish when any
  `.flag` is present rather than checking one filename.

## 2.0.22

- Stopped building a throwaway per-thruster activation dictionary on every
  uncacheable acceleration query. Vanilla
  `ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached` fills that
  dictionary to the ship's thruster count *before* checking whether the
  direction may be cached at all, and discards it when it may not. The dominant
  caller, `MoveCommand.SetThrusterActivations`, passes an arbitrary vector
  toward a move target, which is never one of the six axis directions or the
  fixed flight angles, so the discarded path is the common one for every moving
  ship on every tick. It measured 13.1% of all allocation.
- The repair hoists vanilla's own guard by cloning its existing
  `ldsfld/ldarg/Contains` triple in front of the construction and branching to
  the target the original test already uses. Simulation state, thruster
  activation levels and lockstep are unchanged; when the direction is cacheable
  the only difference is a second HashSet lookup.

## 2.0.21

- Removed the closure allocated on every resource-ID comparison. Vanilla
  `ResourceIDComparer` looks cached but its index lookup captures its parameter
  in a lambda, so Roslyn constructs a display class in the method prologue and
  every call allocates even at a 100% cache hit rate. It is reached twice per
  sorted-dictionary comparison, for every part, on every input frame, because
  the build toolbox re-aggregates the whole blueprint's cost. Returned values
  are identical to vanilla, including the cached `-1` for an unknown ID, so
  ordering, cost aggregation and lockstep state are unchanged.
- Measured in blueprint mode at 35,000 parts against a 23,741-part baseline:
  total allocation fell from 1,053 MiB to 269 MiB per ten seconds (-74%) and
  Gen0 collections from 1,993--2,402 to 448--539 per minute (-80%). The
  comparison path itself fell from 55.3% of all allocation to zero.

## 2.0.20

- Fixed Microsoft Korean IME text entry through ImeSharp's IMM32 backend. The
  code layer now delivers the committed `GCS_RESULTSTR` text that ImeSharp
  otherwise drops, replaces only a still-visible composition preview, and
  preserves already-committed syllables when `WM_IME_ENDCOMPOSITION` arrives
  before the final result string. This is a local input/UI change and does not
  affect simulation or multiplayer state.
- Added an opt-in Korean-IME event trace for diagnosing future input-method
  differences. It is inactive unless a local diagnostic flag is created and is
  excluded from the release payload.
- Log each visible global or team chat message once in Cosmoteer's regular log;
  muted and locally invisible messages remain omitted.

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
