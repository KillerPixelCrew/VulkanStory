# Held-item rigid motion identity

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

The original held-item RenderMultiTextureMesh call now has a checked wrapper
that supplies retained rigid history with its final model matrix and actual
mesh shape. Weak identities are separate per renderer/attachment point. Cached
compiled getters read the protected matrix and attachment association without
per-draw reflection or game field injection.

Original item transforms, temperature/color uniforms, texture selection, culling
and rendering still execute. History is applied only to an active compiled writer
in the temporal window on a writable primary target, outside shadow passes.
The wrapper owns only a newly opened motion scope and closes it in finally;
an already open caller scope is preserved. Shape/view/reset rules remain in the
retained history implementation.

This is a graphics-motion-held-items subset. Other rigid/instance/particle draw
identities, liquid motion, compiled shader-mode publication and remaining host
integration still prevent dense validity and complete startup registration.
No code/guard has been compiled or run, and no held-item/motion/provider result
is accepted from this increment.
