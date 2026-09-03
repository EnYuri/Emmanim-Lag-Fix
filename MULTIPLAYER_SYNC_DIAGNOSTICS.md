# Multiplayer synchronization diagnostics

Last updated: 2026-08-29, Cosmoteer 0.30.4c.

## Initial game transfer

Cosmoteer multiplayer uses deterministic lockstep after launch, but the initial
simulation is transferred as a complete `GameInit` object graph:

1. The host serializes the full `GameInit` into one `MemoryStream`.
2. The host copies that stream to a reliable `ChannelStream` for every client.
3. Each client accumulates the complete payload in `ChannelStream._inBuf`.
4. After receipt, vanilla copies the whole payload into a second
   `MemoryStream`.
5. A worker thread deserializes that copy into `GameInit`, then calls
   `GameInit.CreateGame` to construct the complete local simulation.
6. The client sends `ClientReadyRpc` only after creation finishes; the host
   waits for every client before sending `StartGameRpc`.

`ChannelStream` uses reliable messages and limits unconfirmed in-flight data to
approximately 1,024,000 bytes by default. The payload itself is nevertheless
fully materialized at both ends before game creation.

The vanilla client retains the copied byte buffer for the entire worker task,
including `CreateGame`. Peak client memory can therefore contain the original
channel buffer, copied buffer, deserialized `GameInit` graph and partially or
fully created game graph at the same time. This is a plausible explanation for
a low-memory/low-performance client freezing during multiplayer initialization
despite running the same save acceptably in single player.

## Connection liveness coupling

Internet sessions use `SteamNetworkingMessages`; LAN/direct sessions use UDP.
Both feed Halfling `NetworkMessenger`. Raw UDP receipt is asynchronous, but
Halfling packet parsing, reliable-message acknowledgement generation, ACK
sending and the ten-second unresponsive-session check are pumped with the game
frame. Steam message receipt is also polled from `FrameStarting`.

A long client frame or stop-the-world GC can therefore stop application-level
ACKs even when latency and packet loss are otherwise healthy. Recorded drops
show `WaitingForAck`, exactly 10.00 seconds, zero current packet loss and normal
latency.

## Implemented mitigations

### Session timeout

`MultiplayerSessionTimeoutPatch` replaces the six reads of Halfling
`NetworkMessenger.SESSION_TIMEOUT` with 30 seconds: four disconnect checks in
`ProcessUnresponsiveSessions` and two ACK-expiry caps in
`EnqueueOutgoingAcks`. Packet format, ordering, resend cadence and lockstep
inputs are unchanged. The transpiler applies only on the exact expected 4/2 IL
shape; otherwise it preserves vanilla and logs a warning.

### Initialization worker scheduling

`MultiplayerInitializationPatch` runs only the host/client simulation-creation
workers at `BelowNormal` priority and restores the original priority in a
finalizer. This leaves more scheduling time for the UI and Steam networking
service during large-game construction.

### Client buffer lifetime and copy allocation

The client worker now performs the same `BinarySerializer.Read<GameInit>`, but
disposes and releases the copied initialization buffer immediately after the
read and before `CreateGame`. This reduces peak memory without changing the
serialized representation or created simulation.

`MultiplayerStreamCopyCapacityPatch` reserves exactly
`ChannelStream.UnreadBytes` in the fresh destination `MemoryStream` before the
otherwise-vanilla `CopyTo`. This avoids geometric backing-array growth and
repeated intermediate copies. A smoke test copies 65,553 deterministic bytes
through a synthetic `ChannelStream` and verifies exact capacity and contents.

These two changes are included in public release 2.0.13.

### First receive, host-send capacity, and client zero-copy handoff

The next local follow-up uses the same already-known payload length earlier in
the transfer. `StartDataStreamRpc(long totalBytes)` now reserves `totalBytes`
in the client's first `ChannelStream._inBuf` before reliable chunks arrive.
When the host copies its serialized `MemoryStream` into each fresh client
`ChannelStream`, the patch also reserves the remaining serialized length in
that stream's `_outBuf`. This removes geometric backing-array growth and
intermediate full-buffer copies at both ends without changing chunking,
reliability, flow control, packet contents, or message ordering.

After the complete client payload arrives, the marked initial-game
`ChannelStream` now hands its existing `_inBuf` backing array directly to the
fresh deserialization `MemoryStream`. Vanilla's second full-payload copy is
skipped. Disposing the original `MemoryStream` does not clear its byte array;
the read-only destination owns the same array until deserialization finishes,
then the existing early-release patch disposes it before `CreateGame`.

This adoption is restricted to a `ChannelStream` marked by the exact
`ClientLaunchFlow.StartDataStreamRpc(totalBytes)` patch. It verifies the
expected empty destination, zero input position, complete unread length and
.NET 10 `MemoryStream` field shape. Any mismatch logs once and falls back to
the public 2.0.13 preallocated-copy behavior.

The input/output capacity helpers preserve existing bytes and apply only when
the required capacity fits a `MemoryStream`. Harmony resolution and synthetic
capacity/content tests pass on 0.30.4c. The zero-copy test additionally proves
that the destination shares the exact source array and remains readable after
the source stream is disposed. This follow-up is deployed locally but is not
part of the public 2.0.13 asset. A single-host multiplayer room launched
and entered the game normally, confirming mod load and the host initialization
baseline. With no connected client, that run did not execute the new
client-specific send/receive buffer paths; actual multiplayer validation
remains pending.

## Remaining deeper option

The fundamental liveness fix would split networking into two layers:

- a dedicated transport pump that receives packets, parses reliable IDs and
  sends ACK/ping responses during a game-thread stall;
- main-thread-only delivery of user/game messages and lockstep inputs.

Calling the existing complete receive function from a background thread is not
safe because `ProcessReceivedUserMessage` immediately invokes connection
message events. A future implementation must separate transport acknowledgement
from user-message dispatch and serialize access to `NetworkMessenger` session
state. Do not move the whole existing pump to another thread.

## Continuous lockstep traffic audit

Ordinary multiplayer does not continuously transmit the complete game state.
At `InputTicksPerSecond = 30`, every peer sends one reliable `InputTick` for
each lockstep tick, including empty ticks; the host forwards client ticks and
sends one reliable `HostUpdate` per tick containing input delay, latencies and
queued integrity hashes. Actual player actions are serialized into the tick's
`Inputs` byte arrays.

Halfling already implements the obvious transport optimizations:

- raw socket callbacks retain pooled `MessageData` and enqueue it through a
  `ConcurrentQueue` for main-thread processing;
- `QueuedMessage`, `MessageData`, `InputTick` and temporary streams/lists use
  object pools;
- consecutive ACK IDs are combined into ranges of up to 65,536 IDs;
- multiple queued messages are combined into one packet up to the socket MTU;
- large messages are split into reliable fragments and reassembled.

Blind compression, larger packets or another batching layer is therefore not
an evidence-backed next optimization. The continuous data volume is normally
small; protocol changes would require identical code on all peers and could
alter reliability or lockstep latency.

The significant multiplayer-only computation found in this audit is integrity
hashing. `BaseMPManager.AdvanceNetworkTime` calls
`CheckGameSync(IntegrityHashPhase.TickStart, 0)` after every input tick, hence
30 times per second. Both host and client then call `GameRoot.GetIntegrityHash`,
which walks `SimRoot`, ships, crew, nuggets, bullets, map, mode and player
state. The host queues each hash into its next `HostUpdate`; the client computes
the same hashes and compares the two queues.

On very large saves this whole-state 30 Hz walk is a plausible source of the
performance gap between single player and steady multiplayer. Reducing its
cadence does not change deterministic simulation, but delays desync detection
and requires the same code DLL on every peer; a vanilla-code peer produces a
different hash sequence. Incremental hashing would preserve cadence but is a
substantially larger correctness project because every deterministic mutation
would need coverage.

### Local 6 Hz integrity-hash experiment

`MultiplayerIntegrityHashThrottlePatch` replaces the exact single normal
`CheckGameSync` call in `BaseMPManager.AdvanceNetworkTime` with an evenly spaced
6 Hz gate. At vanilla's 30 input ticks per second it hashes ticks
`1, 6, 11, 16, 21, 26, ...`, reducing whole-game hash calls by 80%. Input tick,
HostUpdate, simulation and action-processing rates remain at 30 Hz. Normal
desync detection can be delayed by at most about 0.167 seconds; debug-only
per-bucket hash calls are untouched.

The transpiler applies only when exactly one expected `CheckGameSync` call is
present, otherwise it logs and preserves vanilla. The smoke test verifies the
exact six selected ticks over every 30-tick window and patch installation on
0.30.4c. This experimental build is not part of public 2.0.13 and every
multiplayer peer must use the identical code DLL.

An opt-in `integrity-hash-diagnostics.flag` beside the live mod's `Code`
directory records a line every ten wall-clock seconds:

```
[EmmanimLagFix.IntegrityHashDiagnostics] tick=... calls=... window=... rate=...Hz total=...ms avg=...ms max=...ms
```

The call rate proves the cadence; `total`, `avg` and `max` measure the complete
`GameRoot.GetIntegrityHash` call. A single-host multiplayer game is sufficient
to measure this local computation. For attribution inside the hash tree, pair
it with an EventPipe CPU trace and inspect `GameRoot.GetIntegrityHash` and
`SimRoot.GetIntegrityHash` inclusive samples. Remove the flag after capture.

Single-host live measurement on the large-save test reached stable ten-second
windows of 44--53 calls at 4.31--5.28 wall-clock Hz because the network input
clock itself advanced at only about 22--26.5 ticks/second under the current
load. Every reported tick was `1 mod 5`, proving the intended exact one-in-five
30-to-6 Hz gate. After warm-up, a whole-game hash averaged only 0.24--0.27 ms,
with 11.2--13.0 ms cumulative cost per ten-second window and maxima of
0.31--0.39 ms. At the same observed tick throughput, vanilla 30 Hz cadence
would be roughly five times that cumulative cost (about 56--65 ms per ten
seconds). The patch works and saves measurable work, but integrity hashing is
not a major bottleneck in this single-host sample. The temporary diagnostic
flag was removed after capture.

### Local 6 Hz HostUpdate experiment

After lowering normal integrity hashes, the host still constructed, serialized
and reliably sent a mostly empty `HostUpdate` after every 30 Hz input tick.
`MultiplayerHostUpdateThrottlePatch` aligns these updates with the same
`1, 6, 11, 16, 21, 26, ...` six-per-second schedule. `InputTick` messages,
player actions, lockstep simulation and the host's authoritative input-delay
calculation remain at 30 Hz. Clients retain the last input-delay and latency
values for at most about 0.167 seconds before the next update.

When desync debugging is enabled, vanilla 30 Hz HostUpdates are preserved
because debug-only bucket hashes can accumulate several times per tick. Smoke
tests verify the normal six-tick selection, debug bypass, and Harmony prefix on
`MPHostManager.OnTick`. This is expected to be a modest allocation,
serialization and packet-count reduction rather than a large CPU fix. It is
part of the uncommitted post-2.0.13 local experiment and still needs an actual
client join test.

### InputTick forwarding allocation experiment

Empty `InputTick` objects, their `Inputs` lists, serialization `MessageData`,
and received `DeserializedMessage<InputTick>` wrappers are already pooled by
vanilla. Empty ticks do not allocate an input payload byte array. Reducing their
30 Hz cadence is therefore not justified as an allocation optimization and
would break the existing lockstep readiness protocol.

One remaining repeated allocation was found on the host: every received client
tick calls `MPHostManager.ForwardInputTick` with a newly captured
`Predicate<MessengerID>` that excludes the sender. The local experiment caches
that immutable predicate once per host-session/sender pair in a
`ConditionalWeakTable`. Tick cadence, recipients, reliability, ordering and
serialized bytes are unchanged. The cache disappears with its host manager.
This removes one closure plus one delegate allocation per received client tick
after warm-up (normally 30 pairs per second per client), but is expected to be
a small GC-pressure improvement rather than a frame-rate fix. The subsequent
extended two-player session validated this forwarding path in normal play.

## First extended 2.0.14 multiplayer session and resync (2026-08-30)

Two-player host logs show a 3 h 15 min session, an approximately 1 h 25 min
session, and a final 56 min session without `WaitingForAck`, connection-loss,
loader, Harmony, or patch exceptions. This is the first extended real-peer
validation of 2.0.14's steady multiplayer changes. A SteamNetworkingSockets
70 ms service-thread starvation assert occurred once immediately before the
second room started; no equivalent assert occurred during either long active
session. Two Steam IPC asserts at final process shutdown were pipe-close
aftermath rather than gameplay failures.

The second room did perform one automatic out-of-sync resynchronization from
05:12:03 to 05:16:25 (262 seconds), then successfully pushed the rebuilt career
game and remained usable until the players returned to setup. This proves the
resync completed, but is not evidence that no desync occurred: vanilla reaches
the host `ResyncGame` path only through `OnOutOfSyncRpc`. Because a peer running
a different hash cadence would diverge immediately rather than after about 85
minutes, the late event does not resemble a 2.0.14 hash-sequence mismatch.

Audit found that the resync flow is separate from the first-launch flow. The
2.0.14 host output preallocation happened generically, but client resync did
not mark its `ChannelStream`, so it still grew the receive buffer and copied
the complete saved game into a second `MemoryStream`. The post-2.0.14 local
patch now applies the same exact-size preallocation and guarded zero-copy buffer
adoption to `GameResyncFlow.ClientResyncFlow.StartDataStreamRpc`. Wire format,
save/load behavior and resync decisions are unchanged; any stream/runtime-shape
mismatch retains the safe preallocated copy fallback.

The same local experiment logs the announced resync byte count and separately
times host save serialization, host game reload, and client game reload. A
future host/client log pair can therefore distinguish serialization/rebuild
cost from the residual transfer/wait time. The 262-second historical log did
not contain these phase timers, so it cannot establish which phase dominated.

### Opt-in steady multiplayer memory correlation

The historical host log cannot show the remote client's heap, and the steady
multiplayer audit found no queue that is unconditionally unbounded: processed
`InputTick` objects are disposed back to their pool, recordings are written and
flushed to the `.rec` file, integrity hashes are paired/released, and normal
serialized message buffers are pooled. Blindly clearing any of these structures
would lose inputs or create an out-of-sync game.

When `multiplayer-memory-diagnostics.flag` exists beside the mod's `Code`
directory at process start, `MultiplayerMemoryDiagnosticsPatch` writes one row
per wall-clock minute. It correlates private/working/managed/GC heap and
fragmentation, process handles and GC collection deltas with total/max queued
player input ticks, unsent local inputs, host/client integrity-hash queues,
connection receive queues, send throughput and recording-file length. The
postfix never mutates multiplayer state. Each row also records the current game
and simulation identities, live ship and physical/blueprint part counts,
total/preloaded stasis spawners, and eagerly retained paint decal picker/item
counts. These distinguish a multiplayer queue backlog from location-dependent
stasis growth, a game/resync replacement, or repeated GUI construction. If
memory rises while queues and these object populations remain bounded,
fragmentation or another retained graph is the next target; if one population
rises with memory, patch its exact ownership/release path. Enable the flag on
the slow client in particular, because a host-only row cannot prove client
growth.

## Steady-play stutter is an input-tick stall, not a host performance problem (2026-09-03)

Reported symptom: on a two-player session running the released 2.0.28 on both
peers, motion is jerky ("버벅임") while the frame rate itself looks fine. The
user established the decisive constraint before any tracing: **single player
does not stutter, even with substantially more parts and larger ships.** That
falsifies every scene-size explanation outright, because scene cost, draw cost,
`FastParallel` dispatch overhead and main-thread saturation all exist in single
player too. Only mechanisms that exist exclusively in multiplayer can explain it.

### The evidence

Host log `Logs/log 2026-09-03 18_00_22.txt`, with
`multiplayer-memory-diagnostics.flag` enabled, 12 consecutive minutes. Zero
exceptions, no Harmony shape-guard fallback.

Differencing the `tick=` field between consecutive once-per-minute rows gives
the achieved network input-tick rate against the vanilla 30/s target:

```
18:32  16.5/s   18:35   8.8/s   18:38  11.9/s   18:41  15.7/s
18:33  17.1/s   18:36  10.5/s   18:39  11.8/s   18:42  16.1/s
18:34  11.1/s   18:37  16.1/s   18:40  19.8/s
```

**In 70 of 75 sampled rows, `inputQueued` exactly equals `inputMax`.**
`inputQueued` sums `player.QueuedInputTicks` over all players and `inputMax` is
the single largest; with exactly two players, equality means one player holds
every queued tick (3–11 deep) and the other holds **zero**. The host cannot
advance network time until the missing player's input for the next tick arrives,
so the other player's inputs pile up behind the gate. The two rows that break
the pattern (`19/15` and `8/7`) are the only moments both peers had inputs
buffered.

This reading corrects an earlier mistake in the same investigation: a queue depth
of 4–7 was first read as "healthy buffering, so the host is not starved." Depth
alone means nothing here — **the split between players is the signal**, and it
requires comparing `inputQueued` against `inputMax` rather than looking at either
in isolation.

### The host is not the limiter

Wall-clock phase breakdown of the host main thread (thread 18000) over a 20-second
sample profile, `Logs/stutter_mp_228_2026-09-03.nettrace`:

```
Draw        7,162.8 ms  35.8%
Update      6,557.3 ms  32.8%
LimitFps    2,955.9 ms  14.8%   of which 2,659.3 ms is unmanaged wait
Present     1,584.6 ms   7.9%
other       1,308.3 ms   6.5%
NextFrame     442.0 ms   2.2%
```

The host spends 14.8% of its main thread asleep in the frame-rate limiter, so it
has finished its work and is idling by choice. `TargetFps = FpsTarget30` and
`VSync = Off` in `settings.rules`. Mod code accounted for 439.6 ms — 1.13% of
real process CPU — so the mod is not implicated either.

Attributing only Draw plus Update and calling the main thread "saturated" is
wrong; the limiter sleep and Present must be counted before drawing that
conclusion.

### The network path is clean

Every diagnostic row: `connectionQueued=0`, `outgoingInputs=0`, `hashes=0/0/0`,
`sentKiBs` 4.2–12.0, no `WaitingForAck`, no connection loss. A zero receive queue
is the important one — bursty delivery would show messages accumulating and then
draining. Nothing is waiting, so the missing inputs have **not been sent yet**
rather than being late in transit.

### What is not yet established

The summed `inputQueued` cannot identify *which* player is at zero. The host
generates its own inputs locally with no network hop and is idling 14.8%, so it
is implausible as the starved side, but that is inference, not measurement.

The decisive next step is the client's own log. Have the remote player create an
empty `multiplayer-memory-diagnostics.flag` beside their mod's `Code` directory
before launching, play for roughly 30 minutes, then compare the `tick=`
differences on their `role=client` rows against the host's 8.8–19.8/s. Matching
rates confirm the client is the limiter; a substantially higher client rate
refutes the inference and the search must reopen. Their rows also carry
`privateMiB`, `parts=` and GC deltas, which say whether their four-core machine
is simply unable to simulate this world at 30 ticks per second.

### Profiling traps encountered

Two measurement errors were made and corrected during this investigation; both
would mislead any repeat attempt.

**`InvokeCompileMethod` time can be a stack-walk artifact.** The 20-second sample
profile attributed 8,684.4 ms — 22.4% of all real process CPU, the single largest
item — to `MonoMod.Core.Interop.CoreCLR+V60.InvokeCompileMethod` on one
background thread, 99.5% of it with no managed caller. Windows per-thread
`TotalProcessorTime`, sampled 10 seconds apart, showed that thread consuming
under 100 ms in that window; it does not appear among the 14 busiest threads.
Collecting `Microsoft-Windows-DotNETRuntime:0x10:5` for 10 seconds and for 3
seconds produced files of 43,700,602 and 43,691,750 bytes — essentially
identical, because the payload is the end-of-session method rundown, not live
JIT. Actual new JIT is on the order of a kilobyte per second on a warm process.
**Always cross-check a suspicious profiler hotspot against OS per-thread CPU
before acting on it.**

**Sample counts are not call counts.** A Speedscope export converted from
`Microsoft-DotNETCore-SampleProfiler` synthesises open/close events by diffing
consecutive stack samples. Frame "durations" are therefore runs of contiguous
samples, and two calls separated by less than a sample interval merge into one.
Percentiles computed from them approximate frame time only loosely, and counts
of dispatch operations such as `FastParallel.RunParallelBatch` are sample counts
that undercount short calls. Do not quote them as rates.
