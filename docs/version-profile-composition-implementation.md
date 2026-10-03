# Original 1.22.7 profile composition

Updated 2026-10-01. Source implementation only; no builds, tests, probes,
packages or game runs. Tests remain deferred until integration is finished.

`SceneProfileComposition.Create1227` now combines the migrated original scene
and content routes into one `graphics-scene` group. It requires the Essentials
and Survival assembly identities and an exact set of 15 leaves: transparency,
sky/celestial, particles/decals, terrain, volumetric/cloud-map, entities, held
items, mechanical instances, rigid content, dropped/falling objects, direct
animated content and raw scene state.

All leaf guards run before installation. Leaves retain their original/incoming
IL checks; attempted installations are recorded before each mutation and removal
runs in reverse order, collecting rollback failures.

`StartupProfileComposition.Create1227` now assembles the existing texture,
shader/source, mesh, state, framebuffer, query/capture, platform-start, temporal,
motion-uniform, UI, scene and post groups with the concrete session settings.
The outer construction/window/frame/shutdown transaction remains unchanged.

## Remaining platform coverage is mandatory

The factory also requires an explicit `graphics-platform-remaining` group, and
graphics composition rejects its absence. This prevents the new scene aggregate
from activating an incomplete general platform API. No such group is supplied
yet, and no profile is registered by this increment.

Static inspection of the original platform identified additional legacy texture
bind/unbind and mip-generation methods, line state and stencil operations to
resolve against the retained backend and actual callers. Existing replacement
bodies cover many private GL helpers by bypassing their original callers; those
relationships must be recorded before the remaining group is accepted.

This is source composition, not runtime acceptance or proof of all GL coverage.
Complete remaining API/window routing, profile registration, motion coverage,
analog/hint consumers and runtime packaging remain open. No menu/world/provider
activation is claimed.
