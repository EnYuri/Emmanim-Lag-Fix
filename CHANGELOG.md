# Changelog

## 2.1.11

**Idle-worker parking is off by default below 8 logical processors.**

Version 2.1.3 stopped `FastParallel`'s idle workers from spinning forever and
parked them on an event instead. That was measured on a 12-core machine, where 11
spinning workers starve everything else in the process and there are free cores to
receive a woken one. The trade is not the same on a narrow machine: with 3 workers
on 4 cores the game's own main, render and audio threads already want those cores,
so a woken worker waits for a scheduler slot and the wake latency is paid in full,
and one worker late is a third of the parallel width rather than a fourteenth.

The client measured on 2026-09-08 is exactly that machine - 3 `FastParallel`
workers, counted from its own freeze dump - and its numbers carry the signature of
lost parallelism rather than added work: whole-process CPU held flat at 1.4-1.7
cores while its update phase grew from 45 ms to 162 ms. More work raises CPU; lost
parallelism raises wall time and leaves CPU where it was. That client also reports
the session having been much faster before 2.1.3 shipped, on the same save.

Parking now defaults off below 8 logical processors. `fastparallel-park.txt`
overrides the rule in both directions - a positive spin budget forces parking on,
`0` forces it off - so the A/B needs no rebuild. The diagnostics line reports
`cores=` and `fppark=off(cores=N)`, or `off(override)` when the file did it.

This is a threshold chosen from the shape of the trade, not from a measurement on
a 4-core machine, and it is stated as such rather than presented as a fix.

## 2.1.10

**The lockstep runs at the slowest peer's frame rate, not its CPU.**

`NetManager.GetTargetAdjustedDeltaTime` ends in
`Mathx.Min(value2, 1f / PhysicsUpdatesPerSecond)`. The second term caps the game
time credited per rendered frame at exactly one input tick, so in multiplayer the
whole simulation advances no faster than the slowest peer *renders*, whatever its
CPU could actually simulate. Singleplayer has no equivalent:
`SPManager.AdvanceNetworkTime` is a four-line stub and `GameRoot.Update`'s
do/while loop runs the simulation from the real clock. That is why a save which
is smooth alone crawls in multiplayer.

Measured on 2026-09-08 with a 12-core host (11 FastParallel workers) and a 4-core
client (3 workers, counted from the client's own freeze dump): the client
rendered 10-13 fps against the host's 90-105, its simulation tick cost about 6.5x
the host's, and the session ran at 4-8 of the nominal 30 input ticks per second.
The host was waiting on the client 72-92% of every frame while itself idle.

A new `ticks-per-frame.txt` beside the mod folder, holding an integer from 1 to
4, raises that cap. It is off by default, and the postfix leaves any frame that
was already inside its own delta time untouched, so a host rendering above 30 fps
is unaffected and only a client the cap was actually binding changes at all. The
trade is explicit: at N ticks per frame the slow client's frame costs N
simulation ticks, so its own rendering falls while the world advances faster for
everyone.

Deterministic and lockstep-safe. `Sim.OnInputTick` takes a fixed
`InputTickInterval`, so N ticks in one frame compute exactly what N ticks in N
frames compute; only local pacing changes. Peers may run different values, as
they already may for "Minimum Target F.P.S.", which feeds the same expression,
and `IsReadyForTick` still gates every tick so a client can never run ahead of
the inputs it holds.

The diagnostics line reports `tickcap=<N>/<frames>` (`tc=` in the peer relay):
the configured value and how many frames it actually raised. Zero frames means
the vanilla cap was never binding on that machine.

## 2.1.9

**The nugget-pickup overlay capped its lines and not its icons.**

`ResourcePickupOverlayPatch` has rendered at most 128 connection lines since
2.0.11, but kept one icon per *distinct* scheduled nugget with no cap, and walks
that list on every rendered frame. A large manual collection makes it thousands
of entries. Both lists are now capped at 128.

The evidence is a client freeze dump from 2026-09-08 at 23:21:03: of 58 threads,
the only mod frame on the main thread is this prefix, under
`SimOverlayRenderer.OnDrawCrewUnderlays` in the draw path. That client's draw
phase ran a 19.6 ms median against the host's 3.8 ms on the same game. A single
stack sample cannot prove the loop was what spent the ten seconds - a resync was
deserializing 73 MiB on a 12.3 GiB process in the same window - so no share of
that freeze is claimed. An unbounded per-frame walk is a defect on its own.

The diagnostics line reports `pickups=` (`pk=` in the peer relay): the largest
visible-job count any refresh saw since the last sample. A value far above 128
means the cap is carrying real work on that machine.

**Multiplayer initialization no longer lowers its worker to BelowNormal.**

Version 2.0.13 dropped the launch worker's thread priority so Steam's networking
thread would keep acking while the simulation was built. Measured with two
players on 2026-09-08 it did the opposite: the host finished its own creation in
8.74 seconds while the client spent **51.00 seconds** at `BelowNormal` on the
same game, against an ack window of 10-30 seconds. Lowering a thread that holds
locks the networking path also needs inverts the priority rather than relieving
it - which is what the Steam assert this mod was written around,
`service thread waited 65ms for lock`, actually describes.

The worker now runs at the priority the runtime chose. Releasing the receive
buffer before `CreateGame` is unaffected and stays; that half was a measured
memory improvement, not a scheduling guess.

**`fastparallel-park.txt` set to `0` now leaves FastParallel entirely unpatched.**

The override already disabled the idle-park transpiler, but the `AddToLive` wake
postfix stayed attached and ran on every dispatch to find an empty sleeper list.
It is now skipped too, so `0` is a clean A/B against vanilla's spin. The patch is
lockstep-neutral - it changes when work runs, never what it computes - so one
peer may disable it while the other does not.

**The visual smoothed-value throttle read a wall clock per manager per frame.**

`PartSmoothedValueVisualThrottlePatch` gated its 20 Hz refresh on
`Stopwatch.GetTimestamp()` - a `QueryPerformanceCounter` read for every manager
on every rendered frame - while feeding the values accumulated *game* time. A
20-second CPU trace on a 346-ship host put this prefix at 91.1 ms of self time,
1.7% of everything under `GameRoot.Update`, second only to infrastructure.

The gate is now the accumulator itself: add the frame's game time, refresh once
it reaches 1/20 s, drain it. No clock read at all. Delivered time is unchanged -
the accumulator is drained, never discarded - so smoothing rates are identical.

The two clocks also disagreed whenever the simulation ran slower than real time,
which is the ordinary state of a client that has fallen behind: the wall clock
passed the gate while there was less than a refresh interval of game time to
deliver. Tying the gate to the accumulator removes that by construction. No
sample has been taken that isolates how much work this second half was costing,
so no figure is claimed for it.

**The update phase is now split into the simulation step and the game mode.**

`GameRoot.Update` is a `do { ... } while (NetManager.AdvanceNetworkTime(out
isInputTick))` loop, so a peer that has fallen behind runs several simulation
ticks inside one rendered frame. A large `phaseMs` update figure was therefore
ambiguous between two unrelated problems - one heavy tick (simulation scaling)
or several cheap ones (a catch-up spiral) - and the collected data could not
tell them apart. The 2026-09-08 session that prompted this had the host at
4.1 ms of update per frame and the client at 56.2 ms, with the session tick rate
falling from 21/s to 5/s as the sector grew from 6 to 346 ships.

`sim=<ms>/<steps per frame> mode=<ms>/<iterations per frame>` now appears in the
diagnostics line and in the peer relay. `Mode.Update` runs unconditionally on
every iteration, so its count is the true loop count; `Sim.Update` is
additionally gated on the simulation not being frozen, so its count is the
number of real steps.

**The peer relay spends its 195 characters differently.** The per-player block
was 63 of the 167 characters a full session actually sent, and every entry in it
read `d=0%` throughout - the host measures the same delays from the other side
and more accurately. It is gone, and `fp=` now carries only the timeout share
rather than a cumulative wake count. The freed room holds the sim/mode split.
Measured across the same session, the park handshake added in 2.1.4 is healthy:
0% timeouts on the peer, 0.2% locally.

## 2.1.8

**Three defects in 2.1.7's lost-ship deferral.**

2.1.7 moved the lost-ship save from vanilla's background worker to the
Director's main-thread queue. The move was right; three details of it were not.

**The exit drain was bypassed.** `GameApp.OnExiting` waits for outstanding
lost-ship saves by spinning on `LostShipSaver.IsReadyToExit` while pumping the
Director's queue. That property reports only vanilla's `ThreadedTaskQueue`,
which 2.1.7 stopped using, so it read true unconditionally, the loop was skipped
and a ship lost in the closing frames was never written. A postfix on the getter
now also reports this patch's own outstanding count.

**Vanilla's guard was re-evaluated late.** Vanilla decides
`HasUnsavedChanges && Settings.SaveLostShips && mode.ShouldSaveLostShip(ship)`
at the moment the ship is removed and queues only the save. 2.1.7 deferred the
whole call, so the guard ran a frame or more later, against a
`SimModeManager` that may since have been torn down — `ShouldSaveLostShip`
returns false once `Game` is null, silently dropping a ship that should have
been saved. The guard is now evaluated on the removing thread, exactly where
vanilla evaluates it, and only the save itself is posted.

**The posted callback could throw into the frame loop.**
`QueueingSyncContext.PostInfo.Call()` does not catch, so an escape from the
deferred action would be an unhandled main-thread exception — the same failure
class 2.1.7 exists to remove. The callback is now wrapped, logging through
`Logger.LogError` and always decrementing its pending count.

The exit-drain postfix lives in its own patch class. A class-level
`TargetMethod` combined with a method-level `[HarmonyPatch]` lets Harmony bind
both patch methods to one target; the smoke test now asserts exactly one patch
on each of `OnShipPotentiallyLost` and `IsReadyToExit`, so that cannot regress
silently.

One property of 2.1.7 held up on review: `SynchronizationContext.HandleQueue()`
runs at the top of the frame, before `UpdateStates` and before the
input/update/draw phases, so a posted save lands outside the simulation and
cannot re-enter the stasis sweep that queued it.

## 2.1.7

**Lost ships are saved on the main thread, not on a background worker.**

Two crashes on 2026-09-07, at 21:46 and 22:14, each landed in the same second
as a new file in the Lost Ships folder. The second one carried a complete stack:

```
System.InvalidOperationException: Can't modify components while they are being saved.
   at Part.RemoveComponents                          (Part.cs:757)
   at PartToggledComponents.OnToggleChanged
   at PortHandler.DeregisterPort
   at ShipPartNetworkManager.ProcessPendingOperationsOnce
   at CommonBasePartsManager.OnShipDeactivated
   at SceneNode.NodeChildren.RemoveAt
   at SimShipsManager.Remove
   at SimStasisManager.UpdateStasis
```

No save was running. The last autosave had finished twelve minutes earlier and
`AutoSaveInterval` is twenty minutes. What *was* running is vanilla's lost-ship
saver: `SimShipsManager.Remove` ends with

```csharp
if (handleLostShip)
    LostShipSaver.OnShipPotentiallyLost(ship, Sim.Mode, dispose, asynchronous: true);
```

and `asynchronous: true` posts the whole save onto a private
`ThreadedTaskQueue`. That worker runs `Ship.SaveDesign` — and, when
`disposeWhenDone` is set, `Ship.Dispose` — against a ship the simulation is
still tearing down. `Part.WriteTo` brackets its component loop with

```csharp
Flags |= PartFlags.DebugSavingComponents;
foreach (var component in _components) component.WriteTo(...);
Flags &= ~PartFlags.DebugSavingComponents;
```

no lock, no try/finally. A stasis sweep that culls a ship in the same instant
flushes the ship's queued network-port operations, flips a
`PartToggledComponents` toggle, reaches `Part.RemoveComponents`, and reads a
flag another thread set. The exception is unhandled and takes the process down.

The flag is only the assertion that catches it; serializing a ship's parts while
the simulation mutates them is unsound with or without the flag.

A prefix on `OnShipPotentiallyLost` keeps the feature and keeps the work off the
teardown, but moves it from the background worker to
`Director.SynchronizationContext`, so it runs on the main thread at a frame
boundary. It posts rather than executing inline — running the save in place
would re-enter the stasis sweep still unwinding the removal — and the deferred
call passes `asynchronous: false`, which the prefix lets through to vanilla
untouched.

This removes a thread rather than adding a wait. `Ship.SaveDesignToTextureData`
already marshals its render capture to the main thread and busy-waits on it, so
the capture was never off-thread to begin with; only the PNG encode and the file
write were, and those are rare — a handful of ships per session. Vanilla itself
treats a synchronous save as supported: its unhandled-exception handler saves
every ship that way.

Unchanged: `SaveLostShips` stays on, the save still happens, and nothing about
what is written changes.

## 2.1.6

**The frame is split into input, update and draw.**

`frameMs` said a peer's frame took 34 ms. It never said why, and the two
explanations call for opposite fixes: a client stuck in `draw` is GPU-bound and
its simulation is fine, while one stuck in `update` is simulation-bound and the
tick rate it can offer the host is its own ceiling. `update` also contains the
lockstep wait, so a peer that is merely waiting is distinguishable from one that
is working.

`Halfling.Application.Director` already runs exactly those three phases, so the
probe is six `Stopwatch.GetTimestamp` calls per frame into three longs, read
once per reporting window:

```
phaseMs=1.2/28.4/19.9@29     input/update/draw ms per frame, at 29 fps
```

It reaches the host through the peer relay as `ph=`. To make room inside the
chat limit the relay drops `sh=` and `pt=` — the host logs the same ship and
part counts for the same simulation — and `fp=` becomes wakes plus the share of
parks that ended on the backstop rather than the full triple. Worst case is now
187 characters against the 195 the relay allows.

The probe rides the existing memory-diagnostics flags rather than adding one of
its own, so a peer that already opted in needs no new file. With neither flag
present `Prepare` declines and the three methods are not patched at all. The
smoke test now asserts all three resolve, so a game update that renames one
fails there rather than quietly reporting dashes.

## 2.1.5

**The non-deterministic drain visits only the shards that were written.**

A 20-second host trace put this mod's own `NonDeterministicQueueShardingPatch.Drain`
seventh in main-thread self time, at 2.9%:

```
Thread.PollGCWorker                 17.2%
SceneNode.DoWorldTransformChanged    7.9%
Monitor.Enter_Slowpath               7.3%   <- 2.1.3's shared monitor, gone in 2.1.4
D3D11GraphicsManager.SetRenderTarget 5.6%
...
NonDeterministicQueueShardingPatch.Drain   506.3 ms   2.9%
```

That is loop overhead, not the callbacks — those are their own frames. The
drain swept all sixteen shards and then swept them again to observe emptiness,
so every tick paid at least thirty-two `TryDequeue` calls on mostly empty
queues whether or not anything had been posted.

Each shard now sets a bit when it is written, and the drain visits only the
shards that bit names. A tick that posted nothing costs one interlocked read.
The bit is set **after** the enqueue, so a drain that observes it is guaranteed
to observe the callback as well; the bit can only ever be set spuriously, never
lost while an item is still queued. Delivery order, timing and the drain's
reentrancy are unchanged, and a callback posted during the drain is picked up in
the same drain exactly as before.

**`fppark` is now on the multiplayer diagnostics line too.**

It shipped in 2.1.4 on the single-player line only, which is the one place the
FastParallel park least needed watching. It is now on the multiplayer line and
in the peer relay as `fp=`, so a client's park/wake/timeout counters reach the
host's log. A timeout share near 100% means the wake handshake is not firing.

## 2.1.4

**The idle park uses one event per worker, not one shared monitor.**

2.1.3 was measured in the running game and the spin is genuinely gone —
`SpinWait.SpinOnce` does not appear anywhere in a 20-second trace — but total
process CPU did not move. The cost had been relocated, not removed:

```
Monitor.Wait            19.07 s  27.6%   <- was SpinWait.SpinOnce
Thread.PollGCWorker      8.11 s  11.7%   (was 27%)
Monitor.Enter_Slowpath   3.97 s   5.7%
Monitor.PulseAll         0.42 s   1.7% of the main thread
```

Ten workers parked on a single monitor and `AddToLive` woke them with
`PulseAll`, so every dispatch was a thundering herd: all ten wake, nine of them
spin in `Monitor.Enter_Slowpath` re-acquiring the one lock, and the `PulseAll`
plus part of that contention lands on whichever thread dispatched — usually the
**main** thread, which is the one thread that could least afford it.

The 5 ms backstop compounded it. Nothing under `Bin/` calls `timeBeginPeriod`,
so the OS rounds that up to its ~11 ms timer granularity, and the trace counted
**12,316 timeouts against 180 wakes** in 20 seconds — 62 park/probe/re-park
cycles per worker per second, driven by the timer rather than by work arriving.

Each worker now owns a `ManualResetEventSlim` created with no spin count, and
neither `IdleSpin` nor `Wake` takes a lock at all: `Wake` walks a copy-on-grow
array and sets only the events of workers that say they are parked. The backstop
rises to 200 ms, since the park/wake handshake is exact and a lost set cannot
lose work either — vanilla's loop probes again the moment the park returns.

The diagnostics line now carries `fppark=<parks>/<wakes>/<timeouts>` so the next
measurement is read off the log rather than inferred from a sampled trace. A
timeout share near 100% means the handshake is not firing.

**What the same trace says about the rest of the process**, recorded here because
it redirects where the next work goes: the main thread ran at 90% of one core
while the ten workers sat at about 9% each, so parallel capacity is not the
constraint — the main thread is. Its largest single item is `Thread.PollGCWorker`
at **20.5%**, i.e. GC suspension, which the allocation rate drives and this patch
does not. Half of the main thread's CPU is inside `FastParallel.For`, with 4.4%
of it spinning in `_WaitUntilFinished` waiting for workers to pick up batches.

## 2.1.3

**FastParallel workers park when idle instead of spinning forever.**

Halfling starts one worker thread per physical core minus one and never lets any
of them sleep. `FastParallel.RunThread` is an unbounded loop whose idle branch is
a single instruction pair:

```csharp
while (true)
{
    if (!RunUntilEmpty(profilerJobs))
    {
        if (!IsRunning) { s_wait.WaitOne(); spinWait.Reset(); }
        else spinWait.SpinOnce(-1);      // <- never blocks
    }
    else spinWait.Reset();
}
```

`-1` is `SpinWait`'s documented "never call `Thread.Sleep(1)`" sentinel, so past
the yield threshold every idle worker alternates `Thread.Yield` and
`Thread.Sleep(0)` for as long as the game runs.

That is the largest single CPU item measured in this installation. A 20-second
sampled trace of a degraded 5.7-hour session recorded 65.9 s of process CPU
across 16 logical cores, of which `SpinWait.SpinOnce` was **39.0 s (59%)** with
`Thread.PollGCWorker` **17.8 s (27%)** underneath it, against about 26.9 s of
real work. The second figure is why this costs more than watts: a spinning thread
runs in cooperative GC mode and every collection has to rendezvous with all
sixteen of them, while a thread blocked in a wait is already preemptive and costs
the collector nothing. It is also the mechanism behind the multiplayer drops —
sixteen permanently hot threads are what starves Steam's networking thread, which
is what the `SteamNetworkingSockets service thread waited 65ms for lock!` asserts
say directly.

A transpiler now replaces that one `SpinOnce(-1)` with a bounded spin. Below a
budget of 60 `SpinWait` iterations — roughly 0.7 ms, measured on this machine as
254 us to climb to count 20 plus about 12 us per step after — the worker spins
exactly as vanilla did, which covers the back-to-back `For` dispatches inside one
frame since vanilla calls `SpinWait.Reset` the moment a batch is found. Past it
the worker parks on a monitor that a postfix on `AddToLive` pulses, so it wakes on
the next dispatch rather than on a timer. `Mod/fastparallel-park.txt` overrides
the budget and the backstop timeout for calibration runs; a budget of zero leaves
vanilla behaviour untouched.

**Raising `SpinOnce`'s `sleep1Threshold` instead was measured and rejected.** It
is a one-token IL change and would have been far simpler, but Cosmoteer never
calls `timeBeginPeriod` — no binary under `Bin/` does except the runtime and our
own doorstop — so the process runs at the default timer resolution, and
`Thread.Sleep(1)` on this machine takes **10.6 ms**. That would park a worker for
most of a 60 fps frame. The monitor path wakes in microseconds and keeps its
timeout only as a backstop.

The handshake is written to be exact rather than usually right, because a missed
wake does not fail — it silently costs parallelism. A worker publishes its park
with `Interlocked.Increment` (a full fence) *before* testing the live-task pool,
and `Wake` issues `Thread.MemoryBarrier` after the task is in the pool and before
reading the sleeper count, so x86 store-load reordering cannot let the two miss
each other. Whichever order they interleave in, either the dispatcher sees a
sleeper and pulses it, or the sleeper sees the task and does not park.

Nothing here can deadlock or drop work. `For` runs batches on the calling thread
and spins on `PendingBatches` until its task is complete, so a dispatch with every
worker asleep still finishes — serially. Only parallelism is at risk, bounded by
the wake latency. Nothing touches simulation state either, so this is
lockstep-neutral: it changes when work runs, never what it computes.

Smoke: the transpiler's `Applied` flag is asserted (the shape guard requires
exactly one `ldloca` / `ldc.i4.m1` / `SpinOnce(int32)` site, so a future build that
bounds its own spin is left alone rather than rewritten), the rewritten
`RunThread` is forced through `RuntimeHelpers.PrepareMethod` so malformed IL
fails there instead of on a worker thread whose exception nobody can catch, and
the park/wake handshake is exercised live against the real `FastParallel` type —
a worker below the budget must spin and not park, a worker past it must park, and
`Wake` must observe that it has.

## 2.1.2

**Mods QoL code layer: rebind a proxy that vanilla unbound for the wrong part.**

`ProxyHandler<T>.OnProxiedPartRemoving(Part part)` never reads its `part` argument:

```csharp
private void OnProxiedPartRemoving(Part part)
{
    if (_proxiedPart != null)
    {
        if (ProxiedComponent != null) ProxiedComponent = null;
        else _proxiedPart.ComponentAdded -= OnProxiedPartComponentAdded;
        _proxiedPart = null;
        _proxyableIndex = -1;
    }
}
```

`ActivateProxy` registers it with `RegisterCellRemovingHandler`, and **two parts occupy every
cell** - the placed part plus the structure tile `base_part` puts underneath it. So the
structure leaving unbinds a proxy whose real target is still sitting at that cell, and nothing
ever rebinds it. `ExcludeCategories = [structure]` in the `.rules` fixes only the add path.

Observed consequence, with a Mods QoL wire terminal pointed at a Star Wars large hyperdrive:
`TerminalKnownBatteryStoragePresent` came unbound, so `TerminalKnownStorageOperational` went
false and the capacity-aware transfer switched off, while `TerminalPowerUserPresent` stayed
true - which turned the projectile fallback on. A projectile path cannot back-pressure, so the
contact drained its staging buffer at 3 power/sec forever into an already-full hyperdrive,
never let `AvailableCapacity` reach zero, and therefore took a share of every network push.
The generator's own battery never accumulated. Rotating that one wire tile away made it fill
immediately.

The fix registers a second cell-removing handler beside vanilla's. `PartsManager` combines
cell handlers with `Delegate.Combine`, so ours runs straight after vanilla's, and when the
part that left is not the one being proxied it replays vanilla's own `OnProxiedPartAdded`
against the part still there - restoring `_proxiedPart`, `_proxyableIndex` and either
`ProxiedComponent` or the `ComponentAdded` subscription exactly as the original binding had
them. A sentinel binding is re-applied on top, since vanilla's path cannot resolve one.

Notes:

- This applies to **every** proxy with a `PartLocation` and no `ProxyToggle`, not only ones
  naming a sentinel, because the defect is vanilla's and hits named proxies just as hard.
  Proxies carrying a `ProxyToggle` are still left entirely to vanilla.
- `ProxyHandler` is still never patched - the 2.1.0 shared-canonical-generic collision.
  `OnProxiedPartAdded` is *called* by reflection, which is safe; only patching it is not.
- The smoke test now asserts `OnProxiedPartAdded(Part)`, `_proxiedPart`, and the
  `Register`/`UnregisterCellRemovingHandler` signatures.

## 2.1.1

- Fixes 2.1.0's sentinel storage proxy, which never bound anything. `ProxyHandler`
  is generic over reference types only, so the runtime shares one canonical body
  between `ProxyHandler<IResourceStorage>` and `ProxyHandler<PartComponent>`: their
  `MethodInfo`s differ but their `RuntimeMethodHandle` and native entry point are
  identical. Patching "each" instantiation therefore patched one method twice, and
  Harmony could not see the collision because its registry is keyed by `MethodInfo`.
  A postfix declaring a typed `__instance` would also have received the other
  instantiation's object. A 20-second CPU trace of a live game confirmed it: no
  frame from this module appeared on any of 63 threads, and
  `ProxyHandler`1[__Canon].OnProxiedPartAdded` ran without a `_Patch` suffix.
- The module now patches nothing generic. It hooks the attach and detach methods of
  the two ordinary non-generic components that own a `ProxyHandler` -
  `ResourceStorageProxy` and `ComponentPresenceToggle` - and registers its own
  cell-add handler beside vanilla's, so the sentinel pass runs right after vanilla's
  on every part that appears at the proxied cell. Behaviour is otherwise unchanged:
  a part exposing the named component still binds through vanilla and never reaches
  the sentinel, so exactly one view of a store exists.
- A proxy carrying a `ProxyToggle` is now left entirely to vanilla rather than
  having its activation lifecycle mirrored. No Mods QoL proxy uses one.
- The smoke test asserts the four patch targets are non-generic, that the two
  `ProxyHandler` instantiations still share one canonical method, and that this
  module patches neither of them - so the 2.1.0 defect cannot come back silently.

## 2.1.0

- The package now carries a second, independent code module, `ModsQol.Code.dll`,
  alongside `EmmanimLagFix.Code.dll`. It has its own Harmony id and patches a
  disjoint set of methods; the loader's allow-list was widened from one name to a
  set so either module can be shipped, updated or dropped without touching the
  other. Nothing about Emmanim's own patches changed in this release.
- What the new module does is teach `ProxyHandler` a sentinel `ComponentID` of the
  form `znayuri_any_<resource>`, meaning "whatever component on that part actually
  stores this resource" rather than a component name. Vanilla can only find a
  neighbouring part's storage by naming its component - `RelativePartCriteria`
  matches part identity only, and `ProxyableComponents` breaks on the first
  criteria match rather than trying each name in turn - so a data-only mod that
  wants to deliver power to arbitrary parts has to enumerate every recipient by
  hand, which does not scale as more mods are installed.
- The scan resumes from the entry vanilla stopped on and honours any sentinel
  further down the same list, so a part that does expose the conventionally named
  component still binds through vanilla and never reaches the sentinel. That is
  what lets one proxy component carry both paths instead of two proxies that would
  both bind and report double the part's real capacity.
- The module is inert without Mods QoL 1.65.0 or later: the sentinel ID appears in
  no other mod's rules, so every patch falls through on its first branch. Mods QoL
  is equally inert without this module - the unresolved ID simply never matches and
  its wire network keeps the rate-limited fallback it shipped in 1.64.1. Neither
  side needs to know the other's version.
- Both modules now ship a matching `.pdb`, and `Pack.ps1` refreshes them in the
  same pass as the DLLs and requires them to be present. `EmmanimLagFix.Code.pdb`
  had been shipping as an untracked leftover that nothing regenerated, so it drifted
  out of step with its assembly. That is worse than shipping none: a stale portable
  PDB still loads and reports confident but wrong file names and line numbers in
  every stack trace written to the game log, which is the main way problems in this
  mod get diagnosed. `.gitignore` now keeps `Mod/Code/*.pdb` out of the blanket
  `*.pdb` rule so a fresh clone can still pack, as it already could for the DLLs.
- Note for multiplayer: the sentinel changes how much power a wire delivers, which
  is simulation state. Peers running Mods QoL 1.65.0 must either all have this
  loader or all lack it. That is the same rule the mod's own `.rules` half has
  always had, but it now extends to the optional loader for anyone using Mods QoL.

## 2.0.38

- The diagnostics line now carries frame time and CPU load, on both peers.
  Every field it had was a size or a count - memory, GC, ships, parts - and
  across a 132-minute two-player session none of them explained the host's tick
  rate: private bytes correlated -0.42, parts -0.37, ships -0.29, and Gen0
  collections *positively* at +0.47, which only says a faster tick allocates
  more. What did explain it was the fraction of frames the host spent waiting on
  the client, at -0.975 across three independent sessions.
- The relay made that fraction readable from both sides for the first time, and
  the two sides disagree completely: the host held a median of zero input ticks
  from the client and reported it late on 95 of 134 samples, while the client
  held a median of nine of the host's and reported the host late on almost none.
  The starving peer is the host. Note a player's own entry always reads 0.0% -
  the local player generates its own input tick and so is never the one missing -
  so only each side's view of the *remote* player carries information.
- What none of it can say is why the far peer is late, because every field is a
  quantity rather than a duration. `frameMs=<mean>/<p95>` and `cpuCores=<busy>`
  are durations: at or above the core count the machine is CPU-bound, near zero
  it is waiting on something else, and the percentile separates a machine that is
  hitching from one that is uniformly slow. The relayed copy carries the same two
  fields and drops the per-player queue average to stay inside the chat limit.
- `MinInputTickDelay` is raised from vanilla's 2 to 6. At the best latency seen
  on a real session, 52 ms, the host computed a delay of only three input ticks
  and was measured holding zero, so it stalled on every jitter spike. This is a
  floor rather than a value - whenever latency is high enough to ask for more,
  the computed delay already exceeds it, so it binds only on a quiet connection.
  The cost is command latency, and note it divides by the *actual* tick rate
  rather than the nominal 30: about 440 ms at the 13.7 ticks/s a real session
  averaged. It is also honest about its limits - a deeper buffer absorbs jitter
  and nothing else. If the far peer is simply simulating slower, the fast peer
  drains any buffer and waits again, which is what the measurements above
  suggest is happening.

## 2.0.37

- The client now reports what actually diverged when the game desyncs. Only the
  client detects one - `MPClientManager.ValidateIntegrityHashes` compares the two
  hash queues pairwise and logs `Out of sync!` with both hashes - and the
  out-of-sync RPC it then fires is a bare `RpcActionInvoker` with no payload, so
  the host learns only that the game diverged, never where. The host's log has
  been recording resyncs with no cause while the one line naming the diverging
  `FixedUpdateBuckets` sat in the other player's log file.
- The report travels over the diagnostics chat relay that is already there, so
  nobody has to find and send a log and neither peer has to turn on
  `EnableDesyncDebugging`, which needs both machines and hashes every bucket at
  full rate. It arrives in the host's log as
  `[EmmanimLagFix.PeerDiagnostics] from=<peer> DESYNC our=<tick>/<input tick>
  <phase> <bucket> h=<hash> their=...`.
- The bucket is the point of it. `Resources` or `Jobs` would implicate this
  mod's own sink-job sharding; `Physics`, `Statuses` or a `TickStart` phase would
  exonerate it. One report settles what no amount of reading the patches can.
- The mismatching pair is captured by value, because the hashes are pooled and
  released the instant after they are compared, and only a mismatch found inside
  the validation loop is ever reported. Nothing is added to the lockstep input
  path and no simulation state is read or written.

## 2.0.35

- Sharded ResourceManager's sink-job collection per thread. The per-sink pass
  runs on every FastParallel worker and each worker published its result through
  one of two shared lists, guarded by a lock whose whole body is a single
  `List.Add`. A 20-second host trace measured 637 ms of
  `Monitor.Enter_Slowpath` underneath `UpdateSinkJobs` - the largest single lock
  in the process, ahead of the audio mixer - so the cost was the convoy rather
  than the work. Each thread now writes to its own list and the shards are
  merged back before vanilla reads them.
- That merge is deterministic by construction, not by argument. Vanilla sorts
  both lists immediately after the parallel pass and before touching them,
  `_jobUpdates` by sink index and `_highPriorityFlags` by value, and a given sink
  index is added at most once, so each sort is a total order over distinct keys.
  Whatever order the shards drain in, the sorted result is identical to
  vanilla's. No job is added, dropped, reordered or delayed by a tick.
- The two halves fail independently. If the drain site is not the expected
  shape the shard hands back the real list and vanilla's contended lock stands;
  if the shard site is not, no shard is ever created and the drain finds
  nothing.
- The minimap no longer asks every object in the sector whether it is visible on
  every drawn frame. `Minimap.OnUpdateMinimap` runs once per frame and began with
  a full scan whose per-ship answer reduces to intersecting the ship's bounding
  circle against the team's radar circles; that scan cost 220 ms of the same
  20-second trace, 1.5% of the main thread. Membership is now rescanned at 10 Hz
  and, in between, only the sources that were visible last time are re-tested.
- A per-frame memo would have saved nothing there - each source is already asked
  exactly once per frame - which is why this throttles the decision rather than
  caching it.
- Disappearance stays immediate: a retained source is dropped as soon as its own
  `ShowOnMinimap` goes false or it leaves the scene, and any change in scene
  population forces a full rescan on the spot. Only a ship already in the sector
  drifting into radar range can appear up to 100 ms late. Blip positions and the
  minimap's own redraw are untouched.

## 2.0.34

- The client now relays its own diagnostics line to the host once a minute, so
  the host's log holds both sides of a session without anyone having to find and
  send a file.
- This is what the shipped diagnostics switch was for and did not finish. The
  host's line already names which player is holding the lockstep readiness gate,
  and across two independent sessions that fraction predicted the host's tick
  rate with r = -0.970 and r = -0.971, while ships, parts, private memory and GC
  counts did not agree even on sign. What it cannot say is why that player is
  late; only that player's own line can, and it was sitting on their disk.
- The relay travels as a chat message. `ChannelChatProvider` opens its own
  reliable channel, so nothing about the lockstep input path, the packet formats
  or the simulation changes. Lines carry an `#ELFDIAG#` marker, are logged as
  `[EmmanimLagFix.PeerDiagnostics]` and are removed from the chat window on every
  peer, so players never see them. Both peers necessarily run this build:
  `ModData.Equals` compares mod ID *and* version, so a peer on a different
  version cannot join at all.
- The payload is built to fit the game's own 200-character chat limit and is
  truncated before sending rather than being cut mid-field on receipt. It carries
  the client's tick, private and heap size, GC counts, input queue depth,
  connection backlog, ship and part counts, and its own view of which player is
  delaying the game.
- Sending and logging are both wrapped: a relay failure logs one line and a
  logging failure is swallowed, because this code runs inside the chat receive
  hook and must never be able to break chat or a session.

## 2.0.33

- Fixed the crash 2.0.32 introduced. The first beam weapon to hit anything after
  unpausing threw `InvalidOperationException: Must first set the static Allocator
  property before calling Alloc()` from `HitEffectParams.Alloc`, taking the game
  down.
- Harmony moves a patched method's body into a dynamic method owned by another
  type, which removes the implicit static-constructor trigger that a call to a
  static method carried. `HitEffectParams` installs its own pool's `Allocator`,
  `Initializer` and `Deinitializer` from its static constructor, and its `Alloc`
  is static, so patching it left `ObjectPool<HitEffectParams>.Allocator` null and
  nothing ever ran the constructor.
- `Prepare` now runs the class constructor of every static target's declaring
  type, which restores what the call site used to guarantee. A type without one
  is a no-op and running one twice is too, and only static targets can lose the
  trigger, since an instance method cannot be reached before its type is
  initialized. Of this patch's targets only `HitEffectParams` has a constructor
  at all; it allocates three delegates and nothing else, so running it at mod
  load is safe.
- The smoke test now asserts `ObjectPool<HitEffectParams>.Allocator` is non-null
  with the patch applied, and was confirmed to fail without the fix.

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
