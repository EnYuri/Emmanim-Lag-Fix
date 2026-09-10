# Client stutter in multiplayer: what is established, and what is next

Written 2026-09-09; updated 2026-09-10. This supersedes the "crew AI saturates the CPU and starves Steam
networking" hypothesis for *this* symptom. That older chain still describes the
**disconnect** signature (10 s of missed acks); it does not describe the continuous
slow motion, which is a different failure with a different cause.

Read this before touching `FastParallelIdleParkPatch`, `NonDeterministicQueueShardingPatch`
or anything in the simulation tick.

---

## The symptom, in the user's words

Continuous, severe stutter — "the world is in slow motion", plus freezes. **Not** worse
in combat. **Not** noticeably worse late in a session (long-session degradation is real
but is no longer what the player feels). Present at part counts where a *singleplayer*
save with twice the parts is perfectly smooth.

## Why singleplayer at 2x the parts is smooth

`SPManager.AdvanceNetworkTime` is a four-line stub: no gate, no `_realDeltaTime`, no
penalty. Every mechanism below exists only in multiplayer. **Never reason from a
singleplayer comparison about a multiplayer stall.**

## The chain

1. The client renders at 10–22 fps.
2. `NetManager.GetTargetAdjustedDeltaTime` returns
   `Mathx.Min(value2, 1f / PhysicsUpdatesPerSecond)`, so **world time advances at most
   one physics tick per rendered frame**. At 11 fps the world runs at 11/30 speed
   whatever the CPU does.
3. `value2 = (dt > 1/TargetFps) ? (1/TargetFps)² / dt : dt` — a *quadratic* penalty, so
   with a 30 fps target a client at 11 fps ran at (11/30)² ≈ 13% speed. Turning the fps
   limit off (`FpsTarget0 = Disabled`) makes `1/0 = ∞` and the penalty never applies.
   **This is the one setting change that matters and it is already applied.**
4. Lockstep pins the host to the client: the host's `delay=` field is the fraction of
   its time spent waiting. Observed 72–92%, now 49–63%.
5. `BaseMPManager.AdvanceNetworkTime` sets `_realDeltaTime = Time.Zero` when
   `IsReadyForTick` fails, discarding accumulated budget, so there is no catch-up. Each
   advance is a discrete 33 ms jump every 170–270 ms. **Slow motion and freezing are one
   mechanism at two timescales.**

The host is not the bottleneck and never was: worst frame 52 ms in two hours, median
max 26 ms. The app never froze — only the world.

## The two machines

|                    | host                | client            |
|--------------------|---------------------|-------------------|
| logical processors | 16                  | 8                 |
| physical cores     | 12                  | 4                 |
| `FastParallel` workers (`PhysicalCores - 1`) | 11 | 3 |

The client's `co=` diagnostics field settled this. An earlier estimate of "4 logical
cores", read off the worker-thread count in a freeze dump, was wrong and cost a release
(2.1.11) whose gate never fired on the machine it was written for.

## The measurement, 2026-09-09

20 s `dotnet-trace` (`Microsoft-DotNETCore-SampleProfiler`) on the **host**, PID 16860,
converted to Speedscope. Trace and analysis scripts:
`<scratchpad>/simphase.nettrace`, `simphase.speedscope.json`, `an2.py`, `an3.py`.

The host is not the sick machine, but lockstep runs the identical deterministic
simulation on both, so the *composition* of a tick transfers even though the magnitudes
do not.

### Main thread, inside `SimRoot.Update` (7,178 ms of a 20 s window)

```
FastParallel.For                      3,410 ms   47.5%
ExecuteQueued                         1,002 ms   14.0%
  MultiHitEffectRules.DoEffect          690 ms    9.6%
WeaponManager.EmitBeams                 511 ms    7.1%
Physics2D World.Step                    262 ms    3.7%
SimNuggetManager.FixedUpdate            227 ms    3.2%
```

### Workers (11 threads, 20,611 ms of CPU in the same window)

```
IdleSpin           10,335 ms   50.1%   <- our own bounded spin before parking
RunParallelBatch    9,932 ms   48.2%   <- the actual work
PollGCWorker        3,070 ms   14.9%   <- GC rendezvous, nested in the above
```

### Inside `ParallelFixedUpdate` (6,022 ms across all workers)

```
ResourceManager.FixedUpdate                30.4%
  SearchForSources                         20.5%
  UpdateSinkJobs                            8.0%
ShipStatusManager.FixedUpdate              27.6%
  ModulateStatusValues                     15.3%
    List<(T,float)>.AddWithResize           5.7%   <- regrown every tick
  StatusDiffuser.PerformDiffusion           7.3%
  StatusEffectApplicator.DoEffects          3.3%
    Monitor.Enter_Slowpath                  3.2%   <- lock contention
ShipCrewManager.FixedUpdate                12.5%
CommandManager.SetThrusterActivations      10.2%
WeaponManager.FireWeaponsAsyncStep          5.3%
ResourceConverterManager.FixedUpdate        4.4%
JobManager.FixedUpdate                      3.6%
```

## What the numbers establish

**Half the simulation tick is parallel work.** The client spreads the same 9,932 ms of
batches over 3 workers instead of 11. That is why the two gaps differ:

```
             per frame   per sim tick   draw
host          13.3 ms      5.2 ms       4.0 ms
client        85.5 ms     33.0 ms      14.6 ms
gap                        6.4x         3.7x
```

The draw gap (3.7x) is roughly the hardware ratio. The sim gap is **1.7x worse than the
hardware ratio**, and the parallel share is the reason: `draw` is mostly serial, `sim`
is half parallel, so the 11-vs-3 worker ratio is amplified into `sim` alone.

**The target is 25%, not 6x.** 30 ticks/s needs a tick under 33.3 ms; the client is at
33.0 ms and running at 78% speed (about 23 ticks/s, 2.00 ticks per rendered frame).
Cutting a quarter off the per-tick cost reaches full speed. That is a realistic target
and the three items above are 68% of the parallel work.

Reducing *total* work helps the client roughly **3.7x more than the host**, because the
host has workers to spare. Prefer that over any threading change.

## The 2.1.11 / 2.1.12 gate is probably backwards

2.1.11 disabled idle-worker parking below 8 logical processors; 2.1.12 corrected the
input to the worker count (host 11, client 3) so it would actually fire. Both were
written on the argument that a woken worker on a narrow machine waits for a scheduler
slot, so the wake latency is paid in full.

**That argument omits the other side of the trade.** With parking disabled the
transpiler is not applied and vanilla's `SpinWait.SpinOnce(-1)` — an *unbounded* spin —
returns. Measured previously: 39.0 s of 65.9 s of process CPU. With parking on, the
host's workers use 9.4% of their capacity. Three workers spinning without bound on a
4-core machine burn three of its four cores, competing directly with the main and render
threads. **Parking should be more valuable on a narrow machine, not less.**

Against that: the client's regression is dated to 2026-09-07, which is exactly when
parking shipped (2.1.3 / 2.1.4). Both effects are real and which one wins is empirical,
so the A/B is still worth running — but **"parking off" is not a neutral baseline, it is
vanilla's unbounded spin**, and a large regression on the client is the expected result
if the reasoning above is right.

If it does regress, revert the gate and go the other way: keep parking on everywhere and
*shrink* the spin budget as the worker count falls, rather than removing the park.

`IdleSpin` at 50.1% of worker CPU — equal to the real work — says the 60-iteration
budget is worth re-tuning on its own merits, on the host too.

## Ruled out

- **Part count.** A singleplayer save with twice the parts is smooth; see above for why
  the comparison is void, but the ranking is still right — the client's problem is not
  scene size.
- **`ModsQol.Code` sentinel binder, and any other per-part-event code.** The user reports
  combat does not make the stutter noticeably worse. Per-part event paths would.
- **Lost-ship saving (2.1.7).** Intermittent by nature; the symptom is continuous.
- **`MultiplayerStreamCopyCapacityPatch`.** Shipped 2.0.13–2.0.15 (08-29/31), outside the
  regression window.
- **`MultiplayerInitializationPatch` as the cause of the 23:20 resync disconnect.** The
  patch targets `GameLaunchFlow`; a resync runs `GameResyncFlow`, and the host log shows
  the BelowNormal line at launch and not at the resync. (The BelowNormal block was
  removed in 2.1.9 regardless.)
- **The peer's graphics settings as the *primary* driver.** They were a real secondary
  cost (draw 19.6 ms/frame against the host's 3.8) and are now 14.6 ms, but `sim`
  dominates the frame at 77%.

## Separate incident: Hyperdrive jump can stall the client

Observed on 2026-09-10 in the 00:36 multiplayer session. The players associated
the event with a modded Hyperdrive jump; the host log contains no vanilla
`Making FTL jump` marker, so the trigger cannot be confirmed from the standard
FTL log path.

This was not the ordinary continuous simulation slowdown. At 02:06 the host
stopped advancing at tick 3560 while remaining responsive at 112–132 fps and
using only 0.7–0.9 CPU cores. Its view of the remote player had an empty input
queue and `delay=100%`; outgoing host inputs accumulated from 4 to 28. The
client's once-per-minute relay disappeared for roughly two and a half minutes,
then three reports arrived together at 02:09:35. The first reported
`ft=991.9/373`, `ph=4.0/113.1/837.4@1`, `sim=109.0/2.00`, and `cpu=0.4`; the next
two immediately returned to 50–54 fps. The dominant 837.4 ms draw phase with
low CPU distinguishes this from the client's usual simulation-bound 70–120 ms
update phase.

Working conclusion: **a Hyperdrive jump can temporarily wedge the remote
client's draw/Present or transition-loading path, freezing lockstep until it
recovers.** It did recover without disconnecting. The host cannot identify the
exact client-side call stack, and client-side tracing is unavailable, so do not
claim which Hyperdrive effect, asset load, graphics-driver call, or Present wait
is responsible. Treat Hyperdrive jumps as a known trigger and keep this incident
separate from both long-session simulation scaling and the host-side stasis
serialization spike.

## Still open

- `NonDeterministicQueueShardingPatch` 2.1.5 (09-07) added an `Interlocked.Or` on one
  shared word per enqueue to let the drain skip empty shards. On the 12-core host that
  was a measured win (506.3 ms of 17.4 s of main-thread self time). On 3 workers there is
  almost no queue-tail contention to relieve, so it is close to pure overhead there. Same
  structural shape as the parking gate: **benefit scales with worker count, cost does
  not.** Untested.
- No per-tick breakdown exists *on the client*. The peer diagnostics relay is capped at
  195 characters. The host trace substitutes for it on composition only.
- Long-session degradation is confirmed real but is not the current complaint.

## What each release actually did

- **2.1.9** — capped the resource pickup overlay's icon list (the line list was already
  capped in 2.0.11) and removed the BelowNormal client-creation block. **This is what
  produced the observed improvement**: 10–11 fps to 12–22 fps, host `delay=` 72–92% to
  49–63%. The client's `pk=2047` field is the proof — it was scanning two thousand
  transfer jobs to build the overlay.
- **2.1.10** — `ticks-per-frame.txt`, opt-in, off by default. Retracted as a
  recommendation: at 10 fps, halving the frame rate to double ticks per frame buys
  11 to 13 ticks/s. Left in the build because it costs nothing when unset.
- **2.1.11** — parking gate on logical processors. Never fired on the client (`co=8`).
- **2.1.12** — same gate on worker count. Fires. See the caveat above.

## 2.1.13: measure the combined low-core corrections

Version 2.1.13 reverses 2.1.12's gate without returning to one fixed
policy: parking remains enabled, but a three-worker client spins for 20 iterations
instead of 60 before parking. It also replaces transfer-row `Sleep(1)` with
`Thread.Yield()` and sizes resource sink-job shards from the four actual producers
(three workers plus the caller) instead of the eight logical processors.

1. **Play one session with both peers on the identical candidate DLL.** Read the
   client's `fp=<timeout-share>%@20` (confirms the narrow budget) and
   `sim=<ms>/<ticks>` field. Divide to compare per-tick cost against the 33.0 ms
   baseline. The client's own long line also reports `sinkShards=4`.
   - clearly lower  -> keep the combined low-core corrections, then isolate only
     if release attribution matters.
   - clearly higher -> use `fastparallel-park.txt` to separate the 20-iteration
     park from the shard change before reverting either.
   - unchanged      -> neither scheduler policy nor shard width is the primary
     continuous slowdown; move to the work-reduction items below.
2. **`ShipStatusManager.ModulateStatusValues`** — the `List<(T,float)>.AddWithResize` at
   5.7% of parallel work is a list regrown every tick. Preallocating or pooling it cuts
   both that cost and GC pressure (`PollGCWorker` is 14.9% of worker CPU). Cheapest
   confirmed win, and it reduces total work, so the client gains about 3.7x what the host
   does.
3. **`StatusEffectApplicator.DoEffects` -> `Monitor.Enter_Slowpath`** at 3.2%: lock
   contention inside a parallel batch. Worth reading before touching — a lock in a
   parallel section is either necessary for determinism or a missed per-thread buffer.
4. **`ResourceManager.SearchForSources`** at 20.5% is the single largest item. It is
   already patched here (`ResourceSearchTraversalPatch`, `ResourceSourceVisitedSetPatch`,
   `ResourceSinkJobShardingPatch`), so measure before assuming more is available.
5. Have the host tick "Enable Desync Debugging" in the multiplayer setup so the next
   desync names a `FixedUpdate` bucket instead of the coarse `TickStart (0)`.

## 2.1.15 late-session correction (2026-09-10)

The earlier four-shard recommendation above is superseded by the client's own
30-second late-session trace,
`multiplayer_later_2.1.15_2026-09-10_19-02-21.nettrace`. CPU-only attribution
measured 6,155 ms in `Monitor.Enter_Slowpath`, including 803 ms directly under
the supposedly sharded `ResourceManager.UpdateSinkJobs(int)` path. Four slots
leave no tolerance for producer-thread turnover or an unexpected helper, so the
development candidate restores a minimum of eight. Vanilla sorts the merged
unique sink indexes before using them, so the extra empty slots cannot change
simulation order.

The same trace measured 3,565 ms in `FastParallelIdleParkPatch.Wake`. `AddToLive`
arrives in bursts, and the old loop called `AutoResetEvent.Set` again for every
dispatch while the first signal was still pending. Auto-reset events retain only
one signal, so those kernel calls could not wake the worker more than once. The
development candidate coalesces them with one atomic pending bit per waiter;
the task publication, sleeper handshake, timeout backstop, and queue probe are
unchanged.

Together with the path-contiguity cleanup and subsequent generation-stamped
source visited set recorded in `RESOURCE_LOGISTICS_DIAGNOSTICS.md`, the profiled
regions now targeted by the development candidate total 6,536 ms per 30
seconds, or 8.4% of the trace's 77,849 ms process CPU. The new visited set must
still perform membership checks, and every replacement has overhead, so this is
a ceiling rather than a predicted saving or frame-rate gain. `WaitUntilFinished`
is mainly a consequence of worker-side work and cannot itself be removed safely.

## Resync accumulation audit (2026-09-10)

Two client resyncs rebuilt the game while the old graph was still resident, producing
temporary private/managed-memory spikes. The heap then fell materially after each
replacement, while simulation time remained high. Vanilla's lifecycle explains this:
the resync flow creates a new `GameRoot`, pops and disposes the old one, and queues a GC;
FTL's `SwitchSimulation` also disposes the old `SimRoot` and explicitly calls
`GC.Collect`. Therefore another forced collection is not a safe or supported cure for
the sustained slowdown, and the two samples do not establish an exponential leak.

The static-reference audit found two bounded mod-owned retention windows and closes
both in the development candidate:

- `NonDeterministicQueueShardingPatch._hot` strongly held the last `SimRoot` until the
  replacement first used the sharded queue. A `SimRoot.Dispose` postfix now removes its
  shard table and pending closures and clears that hot entry immediately.
- Multiplayer diagnostics could keep samples for players from both managers during a
  one-minute window spanning resync. Manager identity is now weakly tracked; a switch
  immediately clears player, frame, and CPU-window samples.

Neither structure could grow once per resync without bound, so these are lifecycle
corrections, not evidence that they caused the lasting post-resync simulation cost.
The lasting component still matches the current simulation's resource search, status,
worker-wake, and lock-contention load measured in the late-session trace. The combined
development candidate attacks those measured paths; only an identical-state client
trace before and after deployment can establish how much of the reported escalation it
removes.

## Method notes that cost time to learn

- The Speedscope export from a CPU trace is **evented**, and its time unit is
  **milliseconds** (`profile['unit']`). Attribute inclusive time only to intervals whose
  leaf frame is `CPU_TIME`; there is no `BLOCKED_TIME` frame, so a blocked wait appears as
  `UNMANAGED_CODE_TIME`.
- The export over-attributes `CPU_TIME` by roughly 2x against
  `Process.Threads[].TotalProcessorTime`. **Use the trace for proportions and the OS
  counters for magnitudes.**
- `dotnet-trace collect --profile cpu-sampling` is rejected on this version; pass
  `--providers Microsoft-DotNETCore-SampleProfiler`.
- A thread showing several seconds of CPU under
  `MonoMod...JitHookDelegateHolder.CompileMethodHook` with almost no events is the
  **end-of-session method rundown**, not live JIT. Exclude it. (Thread 8816, 8,854 ms, in
  this trace.)
- Sample counts are not call counts.
