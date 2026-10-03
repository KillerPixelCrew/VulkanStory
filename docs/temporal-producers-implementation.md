# Original-client temporal producers

Date: 2026-09-30. Source implementation only; no builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameRenderSession` now owns `GameTemporalOwner` directly. SR and FG obtain
their snapshots from that owner rather than an unfinished service callback.
Framebuffer/settings changes and resize request resets on the same state.

`TemporalConsumerPatches` supplies a required `graphics-temporal` group covering
six original ClientMain methods. MainRenderLoop entry/finalization bounds scene
work. The Before-stage prefix advances history after the shader uniforms have
been updated. A checked transpiler captures the camera, player position and
unjittered world projection immediately after the original PMatrix.Top getter,
at the same point as the retained integration. Culling receives the unchanged
matrix. Original and incoming IL must retain two stack-Top calls, eight render
stage calls and one projection-stack/Top sequence.

Set3DProjection's two-argument overload records the world/hand view. The original
CurrentProjectionMatrix getter applies retained jitter only when the current
matrix exactly matches the last perspective projection; shadow/ortho/custom
projections pass through. RenderAfterPostProcessing closes the jitter window.
An exceptional scene also closes it and invalidates history. Client disposal
detaches world references while the process session survives for menus/rejoin.
FOV, dimension, resize and client changes request their corresponding resets;
retained camera capture still handles teleport/rebase history invalidation.

`GameGraphicsAdapter.NativeFullscreen` also migrates the retained fullscreen
uniform/sampler/pipeline cache and explicit blit-pass descriptions. It is the
shared helper for the remaining native post/UI passes; it does not itself route
the final blit or establish post-chain completion.

## Remaining work

Camera history is not dense motion coverage. Motion validity remains false until
the complete terrain/entity/hand/particle/liquid/sky writer chain publishes its
coverage. Those writers, shader hooks, TAA resolve, AO, bloom/final/blit and UI
capture/composition still need their original call sites and state boundaries.
Shader reload and world-specific reset hooks must accompany that integration.

The complete startup profile still has not been registered. This source has not
been compiled or installed against the official client in a running game.
