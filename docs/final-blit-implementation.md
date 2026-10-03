# Retained final blit and post group

Date: 2026-09-30. Implementation only. No builds, tests, probes, packages or
game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Blit` migrates plain presentation, FSR 1 EASU/RCAS and TAA
debug selection. The existing FSR and debug shader sources are copied unchanged
into embedded resources and use the adapter's owned program loader. Native and
stated fallback draws consume the same inputs and texel-size/debug values.

FSR resolves reduced primary color into slot 18, then RCAS writes the default
target. A failed stage disables that path for the session and uses ordinary
presentation. Only successful FSR returns before the plain path. TAA sharpen
runs after final composition and late overlays; successful FSR supplies RCAS
and never receives another sharpener. Debug presentation samples current motion,
depth and scene. Unwritten sharpen/debug/FSR outputs are not selected.

Shader reload retires the owned FSR/debug programs and their pipeline caches.
The original default binding/viewport and branch-specific blend restoration
remain. The existing UI postfix captures the resulting HUD-less scene and opens
its separate UI target afterward.

`PostProcessingConsumerPatches` now owns the `graphics-post` group covering
original RenderPostprocessingEffects, RenderFinalComposition and
BlitPrimaryToDefault, with exact typed guards and dormant prefixes. This connects
AO → SR/TAA → bloom/god rays/luma → final composition → late overlays → final
blit/sharpen → scene/UI separation at the existing game call sites.

## Remaining integration and acceptance

Scene/motion producers, compiled AO mode publication, shader/native runtime
delivery and remaining controller/settings host factories still prevent complete
startup registration. The post group is implemented in source, not accepted in
a compiled or running game. No menu/world/post/FSR/debug pixels or enabled vendor
execution are accepted from this increment.
