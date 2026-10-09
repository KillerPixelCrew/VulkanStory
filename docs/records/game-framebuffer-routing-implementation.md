# Original framebuffer ownership and binding

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

Four targets join a dormant framebuffer subset: CreateFramebuffer,
DisposeFrameBuffer, public CurrentFrameBuffer setter and private
CurrentFrameBufferKeepVw setter. A cached typed field accessor writes the
original `curFb` field so existing getters/readers stay consistent. Public
nonnull binding sets target-sized viewport; null binding and the private setter
retain it. CurrentTargetId supplies the renderer's native default sentinel or
the owned target ID; previous native stage scope ends before a target transition.

CreateFramebuffer retains the original renderer branch: allocate target,
create missing attachment textures with their original filters/wraps, attach
color/depth, derive the per-target draw mask and check completion. Raw game
formats convert through existing neutral contracts. Disposal retains optional
attachment deletion and framebuffer-state invalidation. Ownership stays in a
ConditionalWeakTable; no injected game fields or GL object handles are added.

Ownership adds explicit released-state protection: a repeated disposal cannot
delete an ID reused by another target, and binding released/foreign targets
rejects before state mutation. This is a documented lifecycle change. A disposed
current target is unbound while retaining viewport; its original public Disposed
flag is set. Renderer texture/target retirement algorithms stay unchanged.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`,
VulkanClientPlatform.FrameBuffers.CreateFramebuffer/DisposeFrameBuffer and
original platform current-target setters. Preserve inherited notices/license scope.

An unrun CPU fixture installs the four targets, exercises actual null binding,
checks original field visibility/viewport retention and rejects foreign targets.
It creates no native framebuffer. Next validation is one bounded game-test/
profile-build batch. Positive binding/attachment pixels, repeated owned disposal
and in-flight lifetimes remain unverified. Complete default framebuffer set,
shared-depth list disposal, load/unload/clear/query/capture and post-chain,
startup/menu/world/provider/SDL/release acceptance remain open. The subset alone
cannot activate the required complete graphics-api group.

The [first bounded validation](validation-g1-framebuffer-routing-2026-09-30-01.md)
passed 35 game cases and a clean profile build. Four signatures/curFb metadata
and installation matched; actual null binding, original field visibility,
viewport retention and foreign-target rejection passed. Positive/native target
behavior, private setter execution and GPU lifetime remain unverified.
