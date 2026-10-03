# Ordinary server input companion

## Integrated single-player delivery source correction — 2026-10-01

Staging now includes the server-only companion under Mods/vulkanstoryinput in the
client package as well as its separate dedicated-server archive. This closes a
delivery gap: a separate server ZIP alone did not install the handler for the
integrated single-player server. Client metadata remains server-only and remote
servers still need no companion for digital fallback.

The companion's Input DLL and the private early-runtime Input DLL use the exact
same build source. The original mod loader uses Assembly.UnsafeLoadFrom; actual
shared assembly identity and integrated-server negotiation still need runtime
evidence. No renderer/SDL/native dependencies were added to the companion.
Deployment ownership now includes this exact mod folder, retaining conflict/hash
checks. The headless runner copies both staged mods and excludes both standard
installed folders before discovery.

Implementation only: no staging, archives, deployment, builds, tests or launches.
Previously created packages remain stale. Older notes below describe their
historical source checkpoints.

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

`VulkanStory.Input.Companion` is a separate ordinary server mod project. It
references only the shared API-only input assembly, official game API and shipped
Harmony. It contains no renderer, SDL, vendor SDK, early loader, game-lib reference
or unrelated server optimizations. Metadata makes it optional for clients and
servers and pins the current reference game version.

## Server attachment

Public player-ready/respawn events attach `AnalogPlayerBehavior` through the
original entity behavior API, including already-online players. The original
server dispatches entity packets to the behavior; no ServerMain transplant is
needed. Reserved packets are handled only for the connection's actual player
entity and matching player UID.

A valid protocol-version probe sends the retained acknowledgment through the
public server network API. State packets require prior negotiation and a retained
one- or three-byte payload. The bounded decoded factor applies to the original
player world-data speed, while the shared contract supplies normalized axes and
direction/plane-lock math. Control dirty state is preserved.

A server-only Harmony seam prepares the owned speed and applies direction around
the original API movement-vector calculation. Untagged/unnegotiated controls are
unchanged. Active payload state uses the same 600 ms expiry boundary as axes;
expiry restores ordinary base speed and removes the owned tag. Respawn clears
input; disconnect/despawn revokes connection state. Unload clears tags, removes
owned behaviors and unpatches the companion owner.

Provenance: retained ServerMain analog packet behavior and shared analog contract
at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`, adapted to public entity/
player/network hooks. No injected fields or replacement game assemblies are used.

## Remaining work

The companion is not yet packaged or installed for integrated/remote servers.
Live negotiation, movement/speed, respawn, packet expiry and unload remain
unverified. The renderer remains usable in its intended digital fallback when
the optional companion does not acknowledge. Native renderer/provider delivery,
shader override compatibility and ordinary renderer settings/world UI remain open.
