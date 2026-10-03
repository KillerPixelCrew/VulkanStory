# Process runtime and SDL/Vulkan session wiring

Date: 2026-09-30. Source implementation only; no builds, test work, probes or
game runs. Test work remains deferred until integration is complete.

The managed bootstrap now loads RuntimeBootstrap, which retains the original
startup observation and creates one entry-thread runtime. An ordinary mod can
read its published status/active state. A lightweight client ModSystem and
metadata are now in the solution; they report missing/inactive early loading
without loading a second renderer or requiring a server mod. Its settings/world
attachment remains unfinished. A complete startup plan must be registered
before the first window request; the existing six mandatory transaction groups
still validate before mutation. No complete plan is registered yet, so this
change alone does not activate SDL/Vulkan or prove revised B0 live behavior.

GameRenderSession owns the real SDL window, retained device and original-platform
graphics/input sidecars. Preparation initializes the device while routes remain
dormant, attaches explicit shader/framebuffer/input services, and supplies resize
and graphics-drain callbacks. Session services require the actual provider,
controller, temporal and settings owners; no unfinished component is substituted
with a production empty action. Those factories still need their concrete wiring.

The frame-loop group replaces the original private frame callback and the single
GameWindow.Run call in original client startup. SDL pumping remains once per frame.
Pacing precedes input; vendor caps update only when cap/latency selection changes.
The frame dispatch preserves the original handler, private render flags and
profiler, opens the retained Vulkan frame and calls the post/UI/FG completion
hook before Present. GLFW mouse polling and SwapBuffers are absent on that route.
Before commit, original flow remains available; after commit, a missing/stopping
session rejects instead of attempting an original GL frame.

Resize recreates device presentation/default targets and invokes the original
framebuffer rebuild through its migrated routes. Shutdown stops provider work,
drains/disposes the device while SDL is alive, detaches graphics/input, disposes
the window and then removes patches. Diagnostic failures cannot reverse graphics
ownership. Failure-path teardown and runtime behavior are source-only, unverified.

The later [startup-window increment](startup-window-routing-implementation.md)
adds original window-request/shutdown substitutions and platform Start routing.
The [settings/upscaler increment](settings-upscalers-implementation.md) adds the
mandatory DeviceCreated callback after Vulkan initialization; requirements are
still contributed by ConfigureDevice before initialization.

Remaining work: remaining window consumers, complete graphics/post/scene
coverage, real service factories, mod/settings attachment and provider/frame
identity wiring. The bootstrap remains
awaiting-complete-profile; no first-window/menu/world/provider acceptance exists.
Preserve the full port scope and inherited renderer/SDL/provider implementations.

The later [session-provider increment](runtime-providers-implementation.md)
supplies concrete session-owned SR/FG objects and pre-input frame identity,
sleep/markers. The post/UI hook now precedes the session's FG preparation rather
than owning FG itself. Temporal producer, scene/post and controller factories
are still incomplete; these changes remain source-only.
