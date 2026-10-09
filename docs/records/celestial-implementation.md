# Retained night sky and celestial draws

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Celestial` migrates night-sky, sun and moon mesh rendering
onto the retained native mesh cache. Fixed state remains unculled with no depth
test/write, opaque night sky and standard-blended celestial bodies. Primary
motion-slot participation follows the current motion window, with replacement
blending on motion. Texture handles come from existing program binding records;
the standard sun program resolves its declared sampler names. Cached names are
removed with program disposal. Missing native prerequisites use the existing
stated mesh route.

`CelestialConsumerPatches` replaces the one original night-sky mesh call and two
original sun/moon calls. Original and incoming IL must retain these exact typed
anchor counts. Original sun position, orbital matrices, lighting/fog, projection,
shader uniforms and state restoration continue to execute in their existing
renderer bodies. The wrapper chooses standard/celestial draw state from the
active original program, without injecting platform methods or fields.

This is a `graphics-scene-celestial` subset. Terrain, clouds, entity/hand,
particle/decal and dense motion routes plus the remaining host factories must
still join the complete scene/startup profile. No new code or guard has been
compiled or run, and no rewritten celestial/world pixels are accepted.
