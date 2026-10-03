# First original-game texture routing subset

Date: 2026-09-30. Implementation only; no builds/tests/probes/packages/game runs.

`VulkanStory.Game` now references the compiled backend. Game/Harmony dependencies
stay in this integration project and official game references remain `Private=false`.
`GameGraphicsAdapter` associates the original sealed platform with a borrowed
`VulkanDevice` through mod-owned weak-key state. The process session retains
device lifetime. Attach requires explicit routing and mipmap-setting callbacks;
calls check owner thread/active gate, and detachment requires routing disabled.

The retained `VulkanClientPlatform.Textures` bodies for `GenTexture`,
`BuildMipMaps` and `GLDeleteTexture` move into this adapter. Texture formats use
the existing neutral conversions; min/mag/wrap/max-level GL token values remain
sampler data. Raw texture ID assignment, mipmap enable/level policy and deferred
device deletion are retained. Baseline source revision:
`386e0d05386d0b228b439d09aeca851428f7bbf3`. This is an adapter migration;
preserve inherited provenance and license scope.

`TextureConsumerPatches` pins three declared official platform signatures and
installs owner-specific Harmony prefixes. Dormant calls return to original code;
active calls with no adapter throw before reaching GL. The named
`graphics-textures` subset intentionally cannot satisfy the required
`graphics-api` transaction group. The complete resource/shader/state/draw group
must compose this subset before a session can activate. No live installer,
startup caller or graphics adapter attachment exists yet.

An unrun fixture verifies real Harmony installation, active missing-owner
rejection before the original GL deletion, and owner-specific removal. It
constructor-bypasses the platform and does not call original GL or create a
native window/device. It cannot establish successful texture routing.

Next validation: one bounded Release game-test batch and profile-tool build.
Target signatures and new references/adapter/prefix compilation must be proved.
Actual resource calls, bitmap/Cairo upload paths, cubemaps/arrays, mipmap pixels,
shader/mesh/framebuffer/state routing, complete startup groups, real menu/world
and all provider/control/release acceptance remain open. No milestone closes
from this three-target subset.

The [first bounded batch](validation-g1-texture-routing-2026-09-30-01.md)
compiled the game adapter but failed test compilation on the fixture's direct
Harmony API use: the test project lacks an explicit official Harmony reference.
No tests ran and the profile build was skipped. Repair that reference in a later
implementation turn; target bindings and real patch behavior remain unverified.

## Fixture reference repair — 2026-09-30

The game test project now directly references the official installation's
`Lib/0Harmony.dll` through `VintageStoryPath`, with `Private=false`, matching
the integration project's reference. The existing hash-checked official-game
resolver handles runtime binding. No SDK/package Harmony copy was introduced.
This repairs the recorded missing-reference cause in source; no build/test ran.
Next validation remains one bounded game-test/profile-build batch. The earlier
failure remains recorded; fixture execution and texture signatures are unverified.

The [next bounded batch](validation-g1-texture-routing-2026-09-30-02.md)
passed 29 game cases and a clean profile build. All three texture signatures
resolved and the actual Harmony fixture passed installation, missing-owner
rejection and deletion-prefix removal. The reference failure is resolved;
successful native texture calls and complete graphics/game routing remain open.
