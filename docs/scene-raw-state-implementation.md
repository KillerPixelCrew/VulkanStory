# Remaining scene and UI raw state routes

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

## Client projection transitions

The existing UI group now substitutes the single raw `GL.DepthRange` call in
each original `ClientMain.OrthoMode` and `PerspectiveMode`. The original matrix
stack and projection calculations remain. The retained Vulkan viewport covers
their effective range: OpenGL clamps the original 0..20000 range to 0..1.
Original/incoming call counts guard these two targets. Screen-manager routing
and pre-Done composition stay in the same group.

## Boat water-surface mask

`SceneRawStateConsumerPatches` connects the two original `GL.DrawBuffers` calls
in Survival's `EntityBehaviorHideWaterSurface.OnRenderFrame` to the owned
attachment mask. An empty list disables color writes for the depth-only hull
mask; the original six identity-routed OIT attachments are restored afterwards.
Original shadow-program, model/depth, mesh and texture work continues through the
existing routed APIs. A finalizer restores the saved attachment mask and depth
state if the original body throws.

## Framebuffer depth debug

The six original texture-comparison parameter calls in
`SystemRenderFrameBufferDebug.OnRenderFrame2DOverlay` now update the native depth
texture declared for `debugdepthbuffer.depthSampler`. The ordinary sampler is
selected while showing depth, then the original comparison mode is restored.
The adapter rejects changed target/parameter/value profiles instead of allowing
a raw GL call through the active route. Original overlay draws and texture
selection remain.

Both scene targets form the dormant `graphics-scene-raw-state` subset and check
typed targets and original/incoming call counts. Complete scene/profile assembly
is still unfinished. Remaining startup/platform GL routes and graphics resource
coverage must be composed before activation. No water-mask/debug pixels or live
client projection transition has been accepted by this source increment.

Source basis: official 1.22.7 snapshot bodies for the methods named above, with
the retained Vulkan state/texture implementation. No game assembly replacement,
injected field or Optimum runtime dependency is introduced.
