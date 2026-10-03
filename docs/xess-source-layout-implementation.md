# XeSS source-layout replacement and provider failure retention — 2026-10-01

Implementation only: no builds, tests, probes, bundles, snapshots or game runs.

## XeSS-FG input layout changes

EnsureXessPresenter previously reused the active presenter whenever display-color
width/height matched. Its three shared image sets were created from all five
source image dimensions and fixed shared formats. Changing SR quality can replace
depth/motion at a different render resolution without changing display size,
leaving the presenter on its old input layout. Its copy path permits blits, so
this can silently resample inputs with mismatched pixel-based constants instead
of recreating the proper shared resources.

The presenter now stores immutable width/height/format descriptions for color,
depth, motion, HUD-less scene and UI. Reuse requires every description to match.
Changed source layouts cause replacement through the existing drained presenter
shutdown/create path. The frame ring's old submit gate is cleared before disposing
its semaphore owner. Same-layout replacement texture IDs continue to reuse the
presenter; no shader, frame-count or interpolation algorithm changes.

## Provider availability

Upscaler registry now retains the exact bring-up exception or backend unavailable
reason before removing an unsupported provider. Plan/runtime failure paths preserve
that reason too, so settings do not reduce hardware/SDK-specific FSR/XeSS failures
to a generic unavailable-device message. Existing session fallback and backend
shutdown remain intact; unsupported hardware is not reported as successful.

No new provider capability has been accepted from this source edit. Matching-source
reuse/replacement, quality changes, SDK dispatch and visuals need future bounded
headless evidence. Latest native bundle from headless-20261001-161108 can be reused
because this increment changes no native ABI. Installed user game untouched.
DLSS hidden interpolation, supplied hook warnings, other feature/platform and release
gates remain open. This addresses a concrete lifetime/layout gap rather than
replacing any requested provider with a simpler rendering path.
