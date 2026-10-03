# Controller panel entry — 2026-10-01

Implementation only; no builds, tests, previews, packages or game launches.
The preceding goal turn made source progress on axis glyphs/badge bounds.

Added `.vulkanstory controller` to the ordinary client mod command. It requests
the existing connected controller's panel through a framework delegate and the
runtime's owner-thread control queue. The lightweight ModSystem gains no renderer,
SDL, Game or native assembly reference. Disabled controller support/inactive
runtime produces an explicit command error; a disconnected controller produces
a chat message at dispatch. The controller chord remains a toggle; the command
opens an existing panel without closing it.

Panel refresh runs before the optional gamepad focus/input early return, so its
queued recomposition is independent of controller input arbitration. Switching
devices or worlds disposes the preceding settings dialog before dropping it.
Axis/button profile mapping and glyph drawing retain the existing implementation.
The package README now advertises the keyboard-accessible entry.

Source is unbuilt and actual dialog/command behavior is unverified. Previous ZIP
predates this change. Full controller/renderer/provider/SDL acceptance stays open.
