# Analog client control and negotiation

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

`AnalogClientConsumerPatches` connects the original player-control loop and entity
packet receiver to the shared retained protocol. State belongs to weak sidecars
and the process controller owner, with no injected fields.

- The original movement-speed field write feeds a wrapper that preserves the
  original base speed, applies the retained bounded factor and tags actual controls
  with retained axes. The original action flags, mounting/control selection and
  vanilla server control packets remain.
- Physical movement is checked through the source-aware input bridge using the
  original current forward/back/left/right key bindings. It suppresses analog
  direction and scaling. Without negotiation, ordinary digital controls remain.
- Three probe attempts at the retained two-second cadence use the original entity
  packet API. Acknowledgment requires the reserved packet ID, protocol version,
  current player entity and matching pending world/player probe.
- Encoded factor/axes send after original control packets when values, player or
  base speed change, with the retained 250 ms nonzero-axis refresh interval.
- Changed control objects clear previous axes. Original world disposal clears
  player/mount axes and acknowledgment/movement state, preventing readiness from
  leaking into another world or respawned player.

The profile requires this typed/anchor-guarded client group alongside the shared
direction consumer. Provenance: retained `SystemPlayerControl.cs.patch` and
`ClientSystemEntities.cs.patch` at baseline
`386e0d05386d0b228b439d09aeca851428f7bbf3`. Packet IDs, codecs and movement math
remain with the already migrated shared input assembly.

## Remaining work

The ordinary server companion, server packet authorization/speed/direction
attachment and its packaging remain unfinished. No client packets or movement
have run in this checkout. Renderer/provider output, shader override compatibility,
runtime delivery and ordinary settings/world UI remain unaccepted.
