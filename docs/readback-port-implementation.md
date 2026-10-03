# Retained readback and indexed pixel check

Date: 2026-09-30. Implementation only: no builds, tests, probes, packages, or game runs.

`Transfer/ReadbackManager.cs` now joins the backend compile list. Its ticket/timeline lifetime, per-slot arena allocation/growth, deferred retirement, format alignment, image transitions, copy recording and wait/copy bodies are retained from baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. `TextureReadbackFormats` extracts the existing raw texel-size table from the staged dump helper; both paths share it, avoiding a game/debug contract dependency in readback. Full texture dumping and the device/game capture facade remain staged.

The retained `PixelOrder` helper and its original RGBA/BGRA regression join compiled source/tests. Five alignment cases pin legal offsets for mixed texel sizes, including the previous RGBA32F-at-offset-8 regression. No renderer channel order or screenshot orientation behavior is redesigned.

`--sdl-mesh-readback` extends the existing hidden indexed draw preflight. It closes rendering, records a full color-image readback through the retained manager, submits the frame, waits on the readback ticket, and compares the triangle centre and an outside clear pixel to distinct expected RGBA values with two-byte-channel tolerance. It then uses the existing blit/presentation path. Readback arenas and mesh buffers remain alive through completion and waited teardown.

This source increment is unvalidated. The next bounded batch should run backend tests, build the tool, and run the pixel preflight once. A successful check will prove two selected pixels from the new indexed backend path, not game scenes, all captures, arbitrary formats, partial-frame arena reuse, or screenshot/UI composition. Game resource/capture routes, startup/window completeness, vendor features and full port acceptance remain open.

The [first bounded validation](validation-p0-readback-2026-09-30-01.md) passed 47 backend cases, a clean build, both selected pixel assertions, and presentation/teardown. This verifies the exercised RGBA8 indexed/readback path; broader capture/game/provider integration remains open.
