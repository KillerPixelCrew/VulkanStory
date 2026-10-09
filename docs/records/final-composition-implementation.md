# Retained final composition

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is finished.

`GameGraphicsAdapter.FinalComposition` migrates the retained scene composition
into the original primary target or the current upscaled scene. It reads luma,
selected glow, bloom, god rays and the selected AO/debug input. Primary glow
stays outside the writable attachment scope. Only scene color is written; the
remaining G-buffer/motion channels are preserved. Upscaled composition adopts
the display target viewport before the draw.

Uniform values retain the original bloom, AO-in-scene/debug, inverse size,
gamma/contrast/brightness/sepia, wind/glitch, sun/view and damage/frost rules.
Native and stated fallback routes use the same selected inputs and values.
The original loaded final shader program remains the source of identity; full
shader asset/native-manifest delivery and mod-override handling are still part
of the remaining concrete host integration. The fallback is an adapter draw,
not a call to a modified superclass or injected platform method.

State restoration returns the original world draw-buffer mask. The upscaled
composite is published only after a successful draw. The post-processing subset
now prefixes both original post effects and original final composition, with
typed signature/body guards and owner-specific removal.

## Remaining integration

Native blit/FSR/debug/sharpen selection must join this subset before it can form
the mandatory complete graphics-post group. Dense scene/motion producers,
compiled AO mode publication, shader delivery and remaining controller/settings
service factories also remain open. The startup profile has not been registered.
No final scene/UI pixels or rewritten menu/world result is accepted from this work.
