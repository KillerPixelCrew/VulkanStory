# FPS presentation from actual counters

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The process session attaches the retained backend presentation observers to
atomic cumulative real-present and SDK-present/report counters. These counters
are independent of the profiling counters that periodic stats snapshots reset.
The session samples them over one-second windows after Present, resetting the
sample on provider changes or counter disablement.

Real FPS comes from recorded real presentation calls. FG output FPS comes from
vendor-reported display presents; a window with no SDK reports shows unavailable
output rather than estimating a requested multiplier or reporting a false zero.
Observer callbacks are removed after native teardown/drain.

The ordinary API-only mod owns `RendererFpsHud`, created on world-ready and
disposed on leave/unload. It consumes only framework bridge snapshots, updates
dynamic text when changed and draws through the original HUD API. The shared
settings panel exposes Show FPS counter. Font/outline and separate real/output
sampling follow the retained FPS counter behavior.

Provenance: retained HudDebugScreen FPS patch and VulkanStats observer hooks at
baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. No second renderer, native
counter polling or SDK dependency is added to the ordinary mod.

Visible placement, UI/FG composition, counter rates, vendor callback ordering,
world rejoin and disposal remain unverified. Native/runtime packaging and full
live renderer/provider acceptance remain open.
