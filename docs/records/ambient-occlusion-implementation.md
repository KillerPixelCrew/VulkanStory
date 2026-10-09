# Retained ambient occlusion and post entry

Date: 2026-09-30. Source implementation only; no builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.AmbientOcclusion` directly migrates the retained GTAO host
and native SSAO, bilateral blur and scene-composite bodies. The existing backend
GtaoRenderer/Settings and shaders are unchanged. Primary depth/normal inputs,
preset/environment overrides, temporal noise, upscaler-specific settings, sampler
setup, device-failure fallback to vanilla SSAO and debug output IDs are retained.
Vanilla SSAO uses the original noise/kernel and allocated target sizes, blur
quality/iterations and white clear. The composite preserves GTAO attenuation,
debug and multiply blending and publishes AO-in-scene only after a successful draw.

The session supplies the real temporal owner and settings. AO preset/debug
settings are represented in the new JSON configuration. GTAO remains gated by
the mode actually compiled into scene shaders; a source settings selection does
not assert compatible G-buffer class channels. The remaining scene/shader host
must publish that compiled mode. The scene-ssao source is embedded unchanged in
the game project, with matching owned program defines and shader reload handling.

Framebuffer rebuilding releases the session-owned AO targets. Shutdown releases
the renderer while the Vulkan device is alive, before device/window teardown.

`PostProcessingConsumerPatches` routes the original RenderPostprocessingEffects
method into the session. AO precedes SR/TAA selection and the migrated bloom,
god-ray and luma tail. The prefix is dormant until transaction routing is active.
It is deliberately a `graphics-post-processing` subset; it does not satisfy the
mandatory complete `graphics-post` group on its own.

## Remaining work

Native final composition/blit/FSR/debug selection, complete post-group composition,
compiled scene-shader mode publication, dense scene/motion producers and remaining
controller/settings factories are still required before startup registration.
No new code has been compiled or run, and no AO/post pixels or enabled GTAO result
are accepted in the rewritten game host.
