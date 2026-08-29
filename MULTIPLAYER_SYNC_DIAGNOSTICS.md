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

The last two changes are currently prepared and smoke-tested in source. Deploy
them only after Cosmoteer exits.

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
