# Retained bloom, god rays and luma

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs; new tests remain deferred until integration is finished.

`GameGraphicsAdapter.NativePostStages` directly migrates the retained native
bloom, god-ray and luma/blit bodies. Framebuffer indices use the official enum
names. Uniform/sampler placements, transient bloom targets, blur ping-pongs,
blend decisions, input texel sizes and viewport restoration are preserved.
The blur keeps the original full-resolution frameSize across the lower-resolution
draws. God rays retain the selected sample limit and original sun/light/time
inputs. Luma selects raw primary color for FXAA only when neither TAA nor SR
resolved the frame; otherwise it copies the selected scene.

`GameGraphicsAdapter.PostSupport` supplies the retained multi-attachment pipeline
helper and concrete fallback bodies. Fallback shaders use the existing migrated
shader/uniform/sampler routes and stated fullscreen draw, with an explicit Post
pass that disallows splitting. The god-ray sample uniform is written only when
the shader declares it, allowing the ordinary shader's 180-sample behavior.
The retained optional sample cap is represented in renderer settings.

The session attaches the shared settings state to the post adapter during dormant
preparation and resets its TAA-result flag each frame. `RenderPostTail` sequences
bloom, god rays, luma and the original blend/primary-target/error-check epilogue.
The complete post prefix must invoke this after AO and TAA/SR selection; it is
not yet connected to that original game call site.

## Remaining integration

AO/GTAO/SSAO, TAA resolve and sharpen, final composition, final blit/FSR/debug
selection, dedicated scene/motion producers and complete service/profile
registration remain open. These new bodies have not been compiled or exercised.
They do not establish rewritten menu/world/post pixels or provider operation.
