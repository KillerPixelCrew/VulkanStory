# World finalization renderer query — 2026-10-01

Implementation only: no build, tests, probes, package, deployment or game run.

The second real-world batch preserved GL.GetString(Renderer) in
ClientSystemStartup.HandleLevelFinalize, after AmbientManager.LateInit. This query
checks the Intel Arc advisory. Existing platform renderer/info methods are
already routed, but the startup consumer bypasses those methods.

The graphics-platform-start subgroup now resolves the private official method
with its Packet_Server signature, validates exactly one GetString call immediately
preceded by the Renderer constant, and replaces only that call with a same-signature
wrapper. Labels and exception blocks stay on the existing instruction. Validation
and transpilation both guard the anchor; rollback uses the existing subgroup owner.

Active routing returns session.Device.RendererString, the selected Vulkan device's
real name. Dormant routing calls the original GL function. Lost active-session
state is reported explicitly. Client-system finalization, event callbacks,
focus/pause behavior, ambient initialization and the Intel Arc advisory remain in
the original body. No synthetic renderer identity or OpenGL context is introduced.

Source search in the original ClientSystem*.cs files found this was their only
GL call. Other GetString call sites in the original library occur in platform
hardware/reporting methods already covered by PlatformStartupRoutingPatches.
ClientMain's direct DepthRange calls have existing UiConsumerPatches routes.
These source observations are not a broad mod/GL compatibility certification.

New Game source is unbuilt/undeployed. Installed candidate remains from
runtime-world-20261001-152735. Use the existing matching native bundle from
runtime-world-20261001-152358 for the next bounded validation, and the user's
foggy village story save. Playable world, pixels, real SR/FG, settings/input and
world-session shutdown acceptance remain open. SDK hook-map warnings remain.
