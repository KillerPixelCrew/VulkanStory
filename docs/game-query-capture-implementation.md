# Sun queries and screenshot capture port

Date: 2026-09-30. Source implementation only; no build, tests, native check or
game run. Further test work is deferred until integration is complete, following
the user's direction. Existing draft fixtures remain unrun.

The official sun renderer directly generates, polls, begins/ends and deletes
queries. Three checked original bodies (constructor, post draw and disposal)
now route those calls and their two color-mask calls to the adapter. Renderer
query ownership and released-state tracking preserve safe repeated deletion;
the retained query ring owns scope segmentation, availability and GPU lifetime.
The old injected platform query methods are not patch targets.

Three original platform screenshot overloads use their original Screenshot
service through an owned association. Its GrabScreenshot body substitutes one
pixel read and two window-size getters; original scaling, flip, PNG save and
EXIF metadata flow remain intact. SDL pixel size replaces the GLFW window.
The group contains three prefixes and four checked bodies with eleven expected
call matches. Original/incoming IL checks are implemented but unexecuted.

Capture reads complete native texels through the existing readback timeline and
decoder, then writes BGRA8 without a row flip. This boundary handles RGBA/BGRA
and HDR color formats instead of interpreting half-float bytes as packed color.
HDR channels are clamped to normalized bytes; image bounds and unsupported
formats reject explicitly. Native copy/submission/synchronization algorithms
remain unchanged. Actual capture pixels, encoding and query results are unproven.

Source inspection found no official PixelPackBuffer consumers outside its
initialization; screenshot uses a direct client-memory read. The replacement
default setup no longer creates GL pixel-pack buffers. This is scoped to the
official inspected version, not arbitrary mods using raw GL buffers.

Next work continues remaining graphics/post/scene routes and runtime host,
startup/window/frame/shutdown wiring. This incomplete subset cannot activate
the mandatory complete graphics transaction. Full game/provider/SDL acceptance
remains open. Provenance: retained VulkanClientPlatform.Leaf query/readback
policy, baseline 386e0d05386d0b228b439d09aeca851428f7bbf3, and reference-only
official SystemRenderSunMoon/Screenshot call sites. No donor/patched assembly
dependency is introduced; preserve inherited attribution and licenses.
