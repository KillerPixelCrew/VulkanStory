# Native client-block draw snapshots

Date: 2026-09-30. Implementation only: no builds, tests, probes, packages or game runs.

`--sdl-uniform-snapshots` connects the retained client UBO shadow/snapshot code to the existing native indexed draw, descriptor arena/shared layout, translator, frame ring, readback and SDL presentation path. A GLSL 330 std140 `TintBlock` is rewritten through the retained named-block storage convention. Each immutable descriptor set names the frame-arena offset of the particular block snapshot; all unused storage/record bindings receive the existing-shaped zero placeholder.

The scenario draws red into the triangle's left half, ends the rendering scope and performs a partial frame submission. It checks that the unchanged block reuses its same-frame offset, updates the block to green, requires a distinct offset, and draws the right half. Readback checks red `(48,48)`, green `(80,48)`, and the existing outside clear pixel. This checks the integration risk that all draws would otherwise see the final block contents. It does not introduce an additional wait until the retained readback requests completion. Descriptor pools, placeholders and mesh/readback resources survive through waited teardown.

This is a focused backend integration scenario, not a replacement device facade. The actual game API, shader named-block lookup/configuration, arena-exhaustion fallback, full descriptor state cache, animation blocks, multi-frame reuse, providers and menu/world startup remain to be connected. The new source has not compiled or run. The next bounded validation should run backend tests, build the tool and execute this preflight once.

The [first bounded validation](validation-p0-uniform-draw-2026-09-30-01.md) passed 51 backend cases, clean compilation, native snapshot offset checks, red/green/clear pixel assertions, partial submission and presentation. The listed game/facade/provider boundaries remain open.
