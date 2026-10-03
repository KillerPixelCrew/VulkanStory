# Concrete session framebuffer host

Updated 2026-10-01. Implementation only; no build, test, probe, package or game
run. Tests remain deferred until integration is finished.

The process render session now constructs `GameFramebufferHost` directly after
the graphics, temporal, SR, FG and SDL owners are attached. The unimplemented
external framebuffer factory has been removed from `GameSessionServices`.

- Display pixels come from the actual SDL window.
- SSAO, shadow quality and SSAA come from original `ClientSettings`; render scale,
  TAA, FG and handheld shadow tier come from the owned renderer settings state.
- Upscaler plans and runtime disablement call the actual owned SR registry.
- FG reset, AO target retirement, temporal resets and logging bind directly to
  their session owners and original platform logger.
- TAA allocation failure disables TAA for the session and resets history/coverage.
- Successful target replacement retires OIT attachments, resets FG resources and
  clears motion coverage. TAA readiness must agree with the adapter's published
  allocation. The existing framebuffer finish path then requests temporal reset.

This completes the concrete framebuffer callback boundary in source. It does not
activate the incomplete startup plan. Remaining platform/controller callbacks,
other session service construction, complete scene/profile registration and
runtime/native packaging remain open. No actual allocation, settings transition,
provider switch or live resize is accepted by this increment.
