# Successful original UBO integration fixture

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

A new fixture attaches a real retained device's CPU uniform-shadow state to a
constructor-bypassed official platform and installs the actual shader/UBO group.
It calls original CreateUBO, generic Update<Payload> (whole/range), object-range
Update and Dispose through their patches. It checks exact sixteen-byte contents,
nonzero destination offset, neighboring-byte preservation, cleared generic binding,
base Disposed flag, removal and repeat disposal. It uses a sixteen-byte struct
with distinct fields so swapping/overwriting ranges cannot accidentally pass.

The renderer exposes an internal copied-shadow observation and the adapter maps
the original UBO identity to it; no mutable backing storage escapes. Thread-bound
UBO state also has an internal observation. These are diagnostics for verifying
the changed game/renderer boundary, not replacement runtime behavior.

No Vulkan context, SDL window, scene or SDK feature is created. A successful
fixture would establish owned dispatch/pinning/range/disposal behavior, not GPU
snapshots, descriptors, draw pixels or game startup. Generic type/JIT coverage
outside this struct and failure/exception paths remain separate acceptance work.

Next validation: one bounded Release game-test batch and profile-tool build.
The new fixture has not compiled or run. Full resource/state/draw/startup,
menu/world/provider/SDL/release objective remains open.

The [first batch](validation-g1-ubo-integration-2026-09-30-01.md) compiled and
passed 30 regressions, but the new case failed its first generic whole-write
bytes. Creation/zero shadow passed; generic helper Pointer is a GCHandle token,
not a pinned payload address. Scoped helper lifetime/pinning adaptation is
required next; later range/disposal assertions and GPU acceptance remain open.

The [second batch](validation-g1-ubo-integration-2026-09-30-02.md) passed all
31 game cases after the scoped token repair, including exact whole/range/object
bytes, cleanup and repeated disposal. The original fixture failure is resolved;
GPU snapshot/pixel and full game acceptance remain open.
