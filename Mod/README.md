# Emmanim Lag Fix

Reduces multiplayer lag and `WaitingForAck` disconnects in heavily modded games.

Version 2.0 adds an optional, source-available .NET 10 code layer. Its dedicated
loader only accepts this exact mod ID and only loads the bundled Harmony library
and `EmmanimLagFix.Code.dll`; it does not execute DLLs from other mods.

The first code patch caches the costly selected-ship resource aggregation behind
the upper-right resource list for one second. The surrounding widget still runs
every frame, so fading, flashing and layout remain smooth. Only the displayed
numbers can be up to one second old.

Version 2.0.1 also limits the full resource snapshot used by the ship-to-ship
transfer window and station trade tab to 5 Hz. At 60 FPS this removes about 92%
of their repeated all-resource scans. Transfer buttons and typed quantities use
their own immediate handlers; ship/resource totals and transfer progress can be
up to 0.2 seconds old.

Version 2.0.2 makes the crew role-priority list lazy. Opening a category now
creates only one collapsed header per part; the eleven priority buttons for a
part are created the first time that header is expanded. Expanded priority rows
refresh their selected-button state at 10 Hz instead of every rendered frame.
Role changes, undo history, and multiplayer inputs still use the original game
handlers.

Version 2.0.3 fixes the dedicated loader scanning its own installer payload and
therefore skipping `EmmanimLagFix.Code.dll`. It also spreads the main-thread
insertion of ship-transfer and station-trade resource rows across frames, two
rows at a time, instead of allowing every completed background row to enter the
layout queue in one burst.

Version 2.0.4 smooths those windows further: resource snapshots now refresh at
2 Hz, only one completed row enters the layout per rendered frame, and the
background row builder yields briefly after every resource. Buttons and typed
amounts retain their immediate handlers; displayed totals can be up to half a
second old while a large modded catalog fills progressively.

Version 2.0.5 protects the first multiplayer synchronization. The host's
simulation-creation worker and the client's data-decoding/simulation-creation
worker temporarily run below normal priority, leaving scheduling time for Steam
networking while very large saves are being constructed. Their elapsed times are
written to the log. This changes local scheduling only, not simulation data.

## Why you drop

When a session drops, the game log (`Logs/log *.txt`) records this:

```
Lost connection to <peer>:
    Cause: WaitingForAck
    Oldest Outgoing: 10.00 sec
    Cur Packet Loss: 0
    Latency: 0.047 - 0.708
```

Zero packet loss and normal latency at the moment of the drop. **This is not a network problem.**
The peer sent no acks for ten seconds, and that ten second timeout is hardcoded in the game's C#
code — no mod can extend it.

Why the acks stop is something Steam reports itself. The assert dumps in the Steam `dumps` folder
repeat this message:

```
Assertion Failed: SteamNetworkingSockets service thread waited 65ms for lock!
This directly adds to network latency!  It could be a bug, but it's usually
caused by general performance problem such as thread starvation.
```

The game thread is monopolising the CPU, so Steam's networking thread never gets scheduled. Acks go
out late, and past ten seconds the peer is gone.

The biggest consumer of that CPU is **crew job and resource searching**. Mod packs that add thousands
of parts multiply the candidate set, so the cost balloons. Version 1.3.0 restores the intentional
reduced assignment, search and expensive-check rates after the missing-mining-pickup defect was
traced to an Extended Tech Tree beam rather than crew throughput.

## Changes

| Field | Vanilla | With Huge Crews | This mod |
|---|---|---|---|
| JobAssignmentsPerSecond | 120 | 1000 | 90 |
| LowPriorityJobAssignmentsPerSecond | 30 | 250 | 70 |
| ResourceSearchesPerSecond | 120 | 1000 | 90 |
| ManualTransferJobExpensiveCheckInterval | 1.0 | – | 0.5 |
| SalvageJobExpensiveCheckInterval | 1.0 | – | 0.5 |
| MaxInputTickDelay | 60 (2 s) | – | 180 (6 s) |
| InputTickDelayLatencyFactor | 1 | – | 1.5 |

The two `*ExpensiveCheckInterval` fields are **rates, not intervals**, whatever the name says. The per-frame check
budget accumulates as `jobCount * dt * <field>` — multiplied, not divided — so the number is checks
per job per second, and a larger value means *more* work. Version 1.0.0 set them to 2.0 and doubled
the work; version 1.1.0 correctly reduced them to 0.5. Version 1.2.0 restored vanilla 1.0 while the
mining-pickup defect was still being attributed to crew response. Version 1.3.0 returns both to 0.5
after the actual beam defect was identified.

Versions 1.2.1–1.2.5 raised the assignment and search budgets while investigating that same symptom,
including a temporary low-priority value of 600. Version 1.3.0 removes those compensations and
restores the 1.1.0 optimization baseline. Version 1.3.1 then sets only the low-priority queue to 70,
for a final assignment/search profile of 90/70/90.

This does not create missing pickup markers. Cosmoteer creates the automatic nugget-transfer job at
the instant salvage damage destroys an asteroid part, before these assignment budgets are used. The
observed partial failure had a different cause: Extended Tech Tree's Q-series mining beams deal a
second asteroid-only hit typed `unbreak`. Parts killed by the first `salvage` hit register normally;
parts killed by the following `unbreak` hit drop nuggets without passing through the salvage
callback. Mods QoL 1.4.7 repairs those two beam definitions. Assignment-rate tuning cannot repair a
transfer job that was never created.

Version 1.2.2 raised `MaxPerNugget` for loose resources to reduce the number of simulated objects.
Version 1.2.4 removes those overrides after testing showed that nugget size was not the cause of
missing automatic pickup markers. The temporary 1.2.3 low-priority budget increase likewise did
not make Q-beam drops enter the skipped salvage callback. The current low-priority value is the
separately chosen compromise of 70.

Version 1.3.2 restores nugget consolidation as an independent performance measure, not as a pickup
fix. Every tangible vanilla resource nugget may now contain one complete storage stack. Total
quantity, value and storage capacity are unchanged; mined and salvaged fields simply need fewer
loose physics objects. Battery charge and fire-extinguisher charge are excluded because they have no
normal `MaxStackSize`. Mod-defined resources are handled by Mods QoL instead.

That check is not only a CPU cost. It re-runs `IsSinkReachable` and, when that fails, truncates the
outstanding request to whatever the crew is already carrying — it cancels the rest of the transfer.
Running it twice as often as vanilla showed up in game as crew being pulled off a delivery and
swapped for someone else, wasting the walk they had already made.

### What is deliberately left alone

`MaxCrewSearchIterations` and `SourceRefreshesPerTick` stay at vanilla, and this is intentional.
They are not frequencies — they are the point at which a search gives up and how much of the
resource source list gets refreshed. Lowering them does not make crew slower, it makes them **stop
looking**, so crew can stand idle beside work they never scanned. That risk grows with ship size,
which is exactly what Huge Ships creates. The idle-crew cost outweighs the CPU saved.

`CrewUpdatesPerSecond` stays at 6. Below that, crew get stuck inside airlocks.

## Install

1. Drop the folder into your mods folder (`Options > Mods` opens it).
2. Enable it and put it **last in the list**.
3. **Every player must install it.**

The `.rules` optimizations work without DLL injection. To enable the optional
code optimization, close the game and run `Install-Loader.ps1`. The installer
refuses to overwrite a different `winmm.dll` or `ModLoader.dll`. Run
`Uninstall-Loader.ps1` to remove only files whose hashes still match this mod.

Cosmoteer multiplayer is deterministic lockstep — each client runs the same simulation independently.
If one player's simulation values differ, the session desyncs. For the same reason, lag comes from
the slowest PC's compute speed, not from connection quality.

## Load order (there is no UI for it)

The mod screen only has Activate/Deactivate. **Load order is the ASCII sort order of mod IDs** —
digits first, then uppercase, then lowercase:

```
00.00.SW
DCSB.Warhammer40K
SW.1.StarWars
cosmoteer.huge_crews         <- lowercase 'c'
nayuri.emmanim_lag_fix       <- lowercase 'n', loads after, wins
sbg2005.korean_translation
```

This mod's ID is deliberately lowercase so it sorts after `cosmoteer.huge_crews` and wins the
fields they share. Version 1.3.1 deliberately replaces Huge Crews' 1000/250/1000 rates with the
optimized 90/70/90 values.

If you fork or rename this mod, **keep the ID lowercase**. To force it even later, pick an author
name further down the alphabet.

You can verify the order in `Logs/log *.txt` under "Enabled mods:" — the list is printed in load
order.

## Using it with Huge Crews

`Huge Crews` raises crew assignment and resource-search rates to support its much larger crew cap.
This mod keeps the larger crew cap but overrides those rates with 90/70/90. That trades some crew
reaction speed for substantially less candidate-search work on large modded ships; it no longer
spends extra CPU trying to compensate for the unrelated ETT mining-beam defect.

## Tuning

Every value in `mod.rules` carries its vanilla number in a comment. Restart the game to apply.

| Symptom | Adjustment |
|---|---|
| Crew AI consumes too much CPU | Lower `JobAssignmentsPerSecond` / `ResourceSearchesPerSecond` below 90 cautiously |
| Marked mining or salvage pickup is slow | Raise `LowPriorityJobAssignmentsPerSecond` above 70 cautiously; this increases crew-search work |
| Only some Q-beam-mined resources have no pickup marker | Use Mods QoL 1.4.7 or later; assignment rates cannot replace the skipped salvage callback |
| Transfer or salvage orders feel sluggish | Raise the corresponding expensive-check rate above 0.5 cautiously |
| Still dropping | See "Beyond this mod" below |
| Crew stuck in airlocks | Do not touch `CrewUpdatesPerSecond` (never below 6) |

## Beyond this mod

The root problem is thread starvation, so freeing CPU can help more than this mod does. In the game's
options (editing the settings file directly gets overwritten on exit):

- **Cap FPS at 60** — hands CPU back to the simulation and networking threads. The most direct lever.
- **Cap background FPS at 30** — stops waste while alt-tabbed.
- **Turn off Fancy Particles & VFX** — significant CPU cost in large battles.
- **Reconsider Huge Ships** — that mod's own description warns about it.

## Code-patch boundaries

The dedicated loader makes selected C# methods reachable, but that does not make
every stall safe to patch. Simulation patches can change lockstep behaviour and
must be identical on every peer; UI-only caching is safer and may differ between
clients. The hardcoded ten-second transport timeout remains deliberately
untouched until its ownership and failure behaviour are fully verified.

## Credits

The crew tick rate approach, and the finding that `CrewUpdatesPerSecond` below 6 traps crew in
airlocks, come from Hailstorm46's **Reduce Crew Lag**.
