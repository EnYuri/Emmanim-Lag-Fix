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

Version 2.0.6 narrows the background stasis-ship preload radius from 3750 to
3000 while leaving the actual 2500-unit live/spawn radius unchanged. In the
measured dense modded sector, the engine retained 538 fully constructed
preloaded ships and 131,159 live part objects; the same process had only 47
preloaded ships immediately after loading. The existing cancellation path still
disposes ships outside the smaller radius. This lowers peak memory at the cost
of less background lead time before a nearby ship must spawn.

Version 2.0.7 prevents heavily modded transfer menus from creating rows for
the thousands of stackable resource definitions that neither ship actually
holds. Existing in-progress transfer types are retained, and all transfer and
trade execution stays on the vanilla path. The blueprint-purchase tab now
admits one already-created technology card into its scroll layout per frame and
refreshes visible card prices and prerequisite state at 2 Hz. This spreads the
two large one-frame UI spikes without changing purchase validation or
multiplayer inputs.

Version 2.0.9 limits the purely visual scheduled-resource-pickup overlay. Its
candidate transfer-job list refreshes once per second, every distinct scheduled
nugget retains its orange selection outline, and at most 128 pickup connection
lines are rendered. Visible outlines and line endpoints still follow moving
objects every frame. It also preserves the shared line renderer's geometry
cache when the companion selected/hover overlay is empty. Resource transfers,
manipulator beams, crew assignment rates, and simulation state are unchanged.
The same release also reduces hidden blueprint-network maintenance during
normal play. Every live and stasis-preloaded ship retains a repair/construction
blueprint even when no blueprint is visible; its rules-based network ports were
checking saved toggle metadata every rendered update. They now refresh once per
ten game-time seconds while running, but retain vanilla per-frame refresh while the
simulation is paused so blueprint editing remains immediate.

The build toolbox's display-only ship statistics now refresh at 4 Hz instead
of recomputing every blueprint total before every input frame. Editor input,
construction state, affordability checks, and authoritative ship data remain
unchanged; only the visible labels and bars may be up to 0.25 seconds old.

Version 2.0.10 optimizes vanilla heat diffusion on exceptionally large ships.
Vanilla scans the complete rectangular area between the outermost active heat
cells on every 30 Hz physics tick, including cells that cannot produce a heat
change. For heat bounds of at least 128x128 cells, the code layer now evaluates
only active heat cells and their four direct neighbours. The diffusion rate,
coefficients, application order, status callbacks, and small-ship behavior are
unchanged.

Version 2.0.11 reduces repeated resource-logistics bookkeeping on storage-heavy
ships. During one resource-manager fixed update, Cosmoteer can ask for the same
source's allied-ship anticipated-pickup total many times, and vanilla locks and
scans the complete weak ship-count list for every request. The code layer now
reuses that total only within the current fixed update and invalidates it before
any count change. It does not cache resource positions or paths across ticks,
and resource-search and crew-work rates remain unchanged.

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

Download the release archive, extract it anywhere, and run **`Install.bat`**.

That is the whole procedure. The installer:

- copies the mod into your Cosmoteer user `Mods` folder, resolving it the same
  way the game does (`%USERPROFILE%\Saved Games` when that exists, otherwise the
  redirected *Saved Games* known folder, then the SteamID64 profile beneath it);
- copies the code loader (`winmm.dll`, `ModLoader.dll`) into `Cosmoteer\Bin`,
  requesting administrator rights only if that folder is not writable;
- clears the Mark of the Web from the extracted files;
- records a SHA-256 manifest so the uninstaller can prove what it may delete.

Then start the game and enable **Emmanim Lag Fix** under `Options > Mods`.

`Install.bat` refuses to run while Cosmoteer is open, refuses to overwrite a
`winmm.dll` or `ModLoader.dll` it did not place, and refuses to replace a mod
folder that is not this mod. **`Uninstall.bat`** reverses it, deleting only files
whose hashes still match the manifest.

Switches, for a non-default setup:

| Command | Effect |
|---|---|
| `Install.bat -NoLoader` | `.rules` optimizations only, no DLL in `Bin` |
| `Install.bat -LoaderOnly` | loader only, mod folder untouched |
| `Install.bat -GameBin "...\Cosmoteer\Bin"` | override game detection |
| `Install.bat -ModsFolder "...\Cosmoteer\<id>\Mods"` | override user-folder detection |
| `Uninstall.bat -KeepMod` | remove the loader, keep the mod |

### Two halves, two different requirements

| Half | Where | Multiplayer requirement |
|---|---|---|
| `.rules` values | user `Mods` folder | **Every player needs the same version.** These feed the deterministic lockstep simulation. |
| code loader | `Cosmoteer\Bin` | **Per player, optional.** UI caching and thread priority only; `Bin` is outside `datahash`, so you stay in sync with peers who skip it. |

Cosmoteer multiplayer is deterministic lockstep — each client runs the same simulation independently.
If one player's simulation values differ, the session desyncs. For the same reason, lag comes from
the slowest PC's compute speed, not from connection quality.

### After a game patch or a Steam file verification

Both wipe added files out of `Cosmoteer\Bin`. Run `Install.bat` again; it will
skip whatever is already correct. Do the same after updating the mod, so the
loader in `Bin` and the code module in the mod folder stay the same build.

### If your antivirus objects

A proxy `winmm.dll` beside a game executable has the same shape as a DLL
hijack, because that is the mechanism it uses. The source for both the loader
and the code module is in `Source/`, and upstream is linked under *Credits*.
Running `Install.bat -NoLoader` gives you the `.rules` optimizations with no
native DLL at all.

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
