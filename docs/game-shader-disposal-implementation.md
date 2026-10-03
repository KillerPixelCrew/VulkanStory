# Original shader disposal call-site port

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

ShaderProgramBase.Dispose now has a checked transpiler for its three DetachShader,
three DeleteShader, one loop DeleteSampler and one DeleteProgram call sites.
Original and incoming Harmony instructions must match those exact typed counts.
Only static calls change; the disposed guard/flag, conditional stage checks,
custom-sampler enumeration and surrounding control flow remain original.

Active stage detach/delete are checked renderer-owner operations with no separate
GL shader object to destroy: retained SPIR-V modules belong to the linked program.
Sampler/program deletes use retained device methods, including program resource
retirement and pipeline invalidation. Mod-owned captured textures for the deleted
program are removed. Dormant wrappers call the original GL operations.
The original program Dispose has no UBO disposal loop; UBO lifecycle continues
through its separately ported original methods/callers.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`, original
ShaderProgramBase.Dispose and VulkanClientPlatform.Shaders.DisposeShaderProgram.
This is a boundary adaptation; resource retirement algorithms and stage-module
ownership are unchanged. Preserve inherited notices/provenance. Dedicated scene
pipeline caches not yet transplanted still need their original invalidation hooks.

The existing group fixture adds shader disposal ownership/removal checks. It
has not run after this increment and does not execute successful program disposal.
Next validation is one bounded game-test/profile-build batch. Expanded anchors,
installation and compilation remain unverified. Real linked-program/sampler
lifetime, disposal state at runtime, GPU resource retirement, full graphics/
startup/menu/world/provider/SDL/release acceptance remain open.

The [first bounded validation](validation-g1-shader-disposal-2026-09-30-01.md)
passed 31 game cases and a clean profile build. Original/incoming disposal
release counts matched, with actual transpiler installation/ownership/removal.
Successful linked-resource retirement and runtime disposed state remain unverified.
