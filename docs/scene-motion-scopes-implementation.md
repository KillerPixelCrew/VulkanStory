# Scene motion scopes and rigid content histories

Updated 2026-10-01. Source integration only; no builds, tests, packages or game
runs. New tests remain deferred until integration is complete.

## Scene draw scopes

The checked original terrain pool, decal pool and particle draw substitutions
now supply previous world projection/camera, camera displacement, jitter/render
size and previous warp uniforms before opening the primary motion attachment.
The scope is restricted to compiled terrain/topsoil/decal/cube-particle writers.
Shadow and quad-particle programs remain outside it. Animated multi-texture entity
draws open the attachment for the compiled animated writer; the retained animation
upload route supplies its per-entity model and joint history.

Each wrapper closes only the motion window it opened, after its native pool/pass
cleanup, using `finally`. A nested draw preserves the enclosing motion owner.
This follows the retained terrain, particle, decal and entity motion behavior
at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`.

## Rigid Survival content

`RigidContentMotionConsumerPatches` adds checked original draw substitutions for
bloomery, firepit, fruitpress and forge contents, quern tops and resonators.
Cached original `ModelMat` bindings and renderer/mesh identities feed the retained
`StandardMotionHistory` at the final draw. Both single and multi-texture meshes
use the original rendering bodies. Original transform, lighting, burn/fuel/press
logic and texture selection remain unchanged.

History capture requires the compiled standard writer and an open temporal motion
window. Late resonator overlays therefore do not advance the previous transform.
The forge's smithing-program draw is also outside the standard writer scope.
Original/incoming draw counts and typed matrix/method bindings guard the subset;
cleanup closes owned attachment writes on exceptional exits.

Provenance: the original Survival sources under `BlockEntityRenderer` and their
matching retained patches at the baseline above. No injected fields or modified
Survival assembly are required.

## Remaining integration

Dropped/falling items and blocks, other content renderer motion paths, complete
scene/profile composition and frame coverage publication remain unfinished.
No complete frame is declared motion-valid by this increment. Visible output,
shader loading, exceptional cleanup and SDK motion inputs remain unverified.
