# Sky pattern source investigation — 2026-10-01

Implementation/source inspection only. No builds, tests, probes or game runs.
The preceding provider handoff run recorded faint horizontal patterning in light
sky regions. The original 2560x1528 PNG was inspected, confirming this is not
solely the inline preview's downscaling. Visual parity remains unaccepted.

## Findings

Native sky.glsl and include/dither.glsl match donor source byte-for-byte:

- sky.glsl: 6CE7F9AFD82C4BAFCCA62FF6E42DBB24D34B9738AB3F45D07A2DDE18CB5FCE4C
- dither.glsl: F3620C4F70B92147BE7F85711A4923E4C598CA030BADD3C3D951A202985A1A1E

The retained sky color include applies NoiseFromPixelPosition using gl_FragCoord,
ditherSeed and horizontalResolution. The original SystemRenderSkyColor method
still advances the seed and uploads game.Width. Its draw substitution leaves
that body intact. FrameGlobals still declares both values as int, and uniform
writes to frame locations update the shared bytes/version. The native sky pass
state/texture binding matches the donor route. DLSS still receives the retained
color/depth/motion/output seam and invalidates dynamic state after SDK evaluation.

No concrete migration mismatch was established by this source review. This does
not prove runtime uploaded values, nor establish whether the observed pattern
originates before reconstruction, in temporal inputs, or after it. Do not change
the retained dithering/shader/provider algorithm from this observation alone.

## Diagnostic boundary addition

Existing opt-in, once-per-second JSON samples now include actual primary target
and display dimensions, provider jitter/reset, selected quality and CPU-side
game DitherSeed/FrameWidth. These identify the resolution/temporal state associated
with future artifact samples. Game uniform fields are explicitly labeled as
game-side values; they are not claimed as a GPU readback of the uploaded block.
No additional readback, native/device probe or per-draw logging was introduced.

The diagnostic source addition is unbuilt/unrun. Existing images/logs retain their
original scope. A later bounded capture with retained input attachments is needed
to localize the pattern; no rerun occurred in this implementation turn.

The subsequent [offline analysis](sky-pattern-offline-analysis.md) found horizontal
structures already in the raw SR-output texture, before final composition/FG.
The existing Primary crop is predominantly fine-grained dither. Investigation is
now focused on reconstruction and its temporal/input boundary; no cause or fix
has been proved.

[Existing motion/depth analysis](sky-motion-depth-offline-analysis.md) found
finite far depth and zero RG motion in the selected static-camera sky crop,
alongside nonzero reactive B and zero writer-depth alpha. No generic MV sign/scale
correction is supported by that evidence; animated-cloud/history coverage remains
unproved.
