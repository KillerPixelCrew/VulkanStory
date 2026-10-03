# Shader texture/sampler binding and retained draw state

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

The shader group adds original `GenSampler(bool)` and explicit-unit shader
`BindTexture2D/BindTextureCube` prefixes. The original two-argument 2D helper
continues to call its three-argument method. `ShaderProgramBase.Stop` gets a
second independent checked call-site replacement for its single GL.BindSampler
loop instruction. Original/incoming checks coexist with selection replacement;
original UBO cleanup and current-shader reset remain in the game body.

Active binding preserves the retained sampler-unit assignment, per-unit texture,
2D custom-sampler choice/clear and clamp-T behavior. Cube binding preserves the
old renderer's cube sampler handling. Program/sampler texture identity is recorded
in mod-owned state for later scene producers; no such producer consumes it yet.
Dormant prefixes/call-site wrappers keep original GL behavior.

`StatedRenderState` and its `NativeStatedDraw` helper are copied from the retained
platform reference. The only type adaptation is the existing neutral RenderBlendMode
alias; native texture/sampler/pipeline algorithms are retained. Backend friend
access for VulkanStory.Game permits this existing helper to use native internal
descriptions without adding any game/Harmony reference to the renderer. This
does not attach actual game draws or state setters.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`,
`Optimum.Render.Vulkan/Platform/StatedRenderState.cs`, shader texture binding
bodies and GenSampler from the platform State file. Changes are mechanical and
adapter changes; preserve inherited license/notices. No rendering algorithm,
shader arithmetic or resource layout redesign is included.

The group fixture adds sampler/2D-prefix ownership/removal assertions. It has
not run after this increment and never exercises successful native bindings.
Next validation is one bounded game-test/profile-build batch. Expanded source,
signatures, Stop call anchor and Harmony installation remain unverified.
UBOs/disposal, platform texture/state/mesh/framebuffer/draw routes, full startup,
real menu/world/provider/SDL-control/release acceptance remain open.

The [first bounded validation](validation-g1-sampler-routing-2026-09-30-01.md)
failed game compilation on the retained state's sibling `Graph` qualifier.
No tests ran and the profile build was skipped. Repair the namespace alias in
a later implementation turn; sampler signatures/Stop anchor remain unverified.

## Graph alias repair — 2026-09-30

`StatedRenderState` now explicitly aliases `Graph` to the renderer's graph
namespace. The existing default-target/UI redirect condition and generic draw
behavior are unchanged. Source-only repair; no builds/tests/probes ran.
Next validation remains one bounded game-test/profile-build batch. The prior
compile failure and sampler/Stop binding acceptance remain open until it passes.

The [repaired bounded batch](validation-g1-sampler-routing-2026-09-30-02.md)
passed 30 game cases and a clean profile build. Expanded bindings and Stop's
single original/incoming BindSampler anchor matched and installed. Sampler/2D
ownership/removal passed; native binding state, generic draws and pixels remain open.
