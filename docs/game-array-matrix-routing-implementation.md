# Array/matrix shader uniform call-site port

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

Eight retained original operations join the ten scalar/vector routes: scalar
float arrays, Uniforms2/3/4, float-array and by-reference Matrix4 uploads,
matrix arrays and 4×3 matrix arrays. Signature and expected single-setter checks
apply to original and incoming Harmony IL. A receiver load precedes each
replacement call so active dispatch retains the original shader object's program
ID, including matrix methods without an original active-shader check.

Original counts, array pointers, location lookups and guards remain in the game
body. Branch labels and beginning exception blocks move to the inserted receiver
load; end blocks stay on the replaced call. Dormant wrappers preserve original
GL calls and transpose values; active wrappers require the original false
transpose convention and forward to retained backend write operations. Matrix4
fields flatten in original M11…M44 row order without a new transpose.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`, original
ShaderProgramBase array/matrix bodies and VulkanClientPlatform.Shaders adapter.
This is a boundary adaptation; backend uniform layout/storage math is unchanged.
Preserve inherited notices/license scope.

The existing fixture expands installation to eighteen uniform targets and adds
array/matrix owner/removal checks. It has not run after this change. It does not
exercise successful writes, element counts, matrix layout or draw pixels.
Next validation is one bounded game-test/profile-build batch. Successful
uniform runtime, samplers/textures/UBOs/disposal/draw, complete graphics/startup,
menu/world/providers/SDL controls and release remain open.

The [first bounded validation](validation-g1-array-matrix-routing-2026-09-30-01.md)
passed 30 game cases and a clean profile build. All eighteen uniform signatures,
original/incoming anchors and installations passed, with array/matrix owner/removal
checks. Actual receiver/count/matrix writes and native pixels remain unverified.
