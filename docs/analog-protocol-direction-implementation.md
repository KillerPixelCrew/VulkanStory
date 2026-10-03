# Shared analog protocol and direction consumer

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

`VulkanStory.Input` is a new API-only assembly containing the retained analog
movement contract. Packet IDs/version, byte encoding, bounded factor selection,
weak control identity, 600 ms axes expiry, direction normalization and walking/
flying plane-lock mathematics are unchanged apart from namespace/class names.
It references the official game API privately and has no renderer, SDL, provider
or Harmony dependency. This boundary can be shared by the ordinary input companion
and client integration without loading graphics into a server.

`AnalogDirectionConsumerPatches` attaches the retained direction application as a
postfix to original `EntityControls.CalcMovementVectors(EntityPos, float)`. Only
controls with live axes state and a vanilla movement intent change. Inactive
routing and untagged controls retain original movement. The typed virtual target
is guarded and the group is required by the version profile.

The early dependency resolver now explicitly binds the first-party Game,
Contracts, Input, SDL and Vulkan assemblies from the contained managed payload.
Official game/API/Harmony bindings remain pinned to the existing installation.

Provenance: `optimum-api-contracts/OptimumAnalogMovement.cs` and its original API
direction seam at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`.

## Remaining work

Client control/physical-input arbitration, speed scaling, probe/ack/state packet
dispatch and ordinary server-companion attachment are still unfinished. The new
consumer does not acknowledge a server or invent axes for unnegotiated controls.
No analog movement or network behavior has run in the new host. Runtime packaging,
shader override compatibility and live renderer/provider acceptance remain open.
