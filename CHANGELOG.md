# Changelog

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
