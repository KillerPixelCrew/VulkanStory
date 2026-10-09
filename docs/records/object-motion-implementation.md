# Retained per-object motion histories

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`ObjectMotionHistory` directly migrates retained entity bone/model/warp history
and rigid-object model/shape/view history into session-owned objects. Rotation
occurs once per frame, with original previous-frame, reset, view and shape checks.
Previous camera/warp values and per-draw validity/reactive values retain their
original uniforms. A supplied previous-bone upload action replaces the injected
API buffer dependency; its real buffer/call-site wiring remains unfinished.

`MotionUniformConsumerPatches` captures named current model matrices and warp
values at original scalar/matrix setters. Existing uniform routing still performs
the upload. The ref-matrix path copies through a stack span. The required group
is dormant and histories remain disabled until the shader owner publishes actual
compiled motion support.

Standard/held-item draw identities, previous-animation buffers, instance/particle
histories, liquid redraw and complete motion shader coverage still need their
call sites. Dense motion validity remains false. Neither source nor metadata
guards have been compiled or run, and no motion/provider result is accepted.
