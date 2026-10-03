# Neutral texture capture boundary and dump helper

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or game runs.

The retained texture dump helper joins backend compilation. Its decoded byte/float/depth representation now uses `TextureCaptureData` in VulkanStory contracts instead of the injected game API's `OptimumTextureReadback`. The staged device readback partial imports that contract and no longer imports game API/config types. Format decoding, texel sizing, channel conversion, row order and file-writing algorithms remain unchanged from baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`.

Debug keys become `VULKANSTORY_DUMP_*`, the trace-directory lookup follows the existing `VULKANSTORY_RENDER_TRACE`, and the default temporary folder becomes `vulkanstory-texture-dumps`. These are mechanical product/path adaptations. No old optimization API or launcher is required to decode/write captures.

Three new source fixtures cover RGBA/BGRA colour equivalence with row/alpha preservation, HDR single-channel half-float expansion, one-float-per-texel depth and short-input rejection. The existing pixel preflight now decodes its real GPU readback through the capture contract before asserting selected pixels. No fixture or native check ran this turn.

Next bounded validation should run backend tests, build the tool and run one indexed readback/present scenario. File-writer output, other formats, actual game screenshots/AVI, capture/resource ownership, full excluded facade compilation, startup/menu/world and providers remain open. This increment does not close full capture or port acceptance.

The [first bounded validation](validation-p0-capture-boundary-2026-09-30-01.md) passed 54 backend cases, a clean build and decoded native pixel assertions/presentation. File writing and the listed game/facade/provider boundaries remain open.
