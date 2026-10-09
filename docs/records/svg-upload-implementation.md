# SVG upload and initial Streamline frame tags — 2026-10-01

Implementation only: no builds, tests, probes, packages, deployment or game runs.

## SVG route

The first real-world trace identifies SvgLoader.LoadSvg's direct GL.GenTexture
while BlockClutter/BlockShapeFromAttributes initialize assets. The graphics-textures
subgroup now validates the official 1.22.7 LoadSvg/rasterizeSvg signatures and capi
field before patching LoadSvg with an active-routing prefix. Removal remains in
the same subgroup rollback owner. Dormant routing retains the official body.

The prefix calls the original rasterizeSvg with unchanged asset, texture/logical
sizes and optional color, pins exactly its returned bytes, and uploads via a
transplanted VulkanClientPlatform.Leaf.cs LoadTextureFromRgbaPointer helper from
baseline 386e0d05386d0b228b439d09aeca851428f7bbf3. The helper uses RGBA8 and linear
min/mag filtering without mipmap creation, as the prior working SVG patch did.
No BGRA conversion or rasterization changes. LoadedTexture retains its original
capi and logical width/height, distinct from allocation dimensions. Existing
texture disposal routing handles its Vulkan ID.

This patches the unmodified official game rather than importing modified game
assemblies or requiring the donor's injected platform method.

## Initial non-scene presentation tags

The first world batch still warned about absent/zero optional backbuffer extent.
Source inspection shows provider-specific suspension only calls the invalidation
route when activeProvider is dlss. Initial menu frames can therefore present with
no extent tag even though the Streamline interposer is loaded.

BeginFrame now clears previous scene inputs and tags current swapchain dimensions
for each valid Streamline frame token before original game draws. A real scene's
later TagStreamlineFrame supplies current inputs; this initialization does not
request or enable generated frames. Failed tag initialization enters the existing
backend diagnostic queue. SDK hook-map warnings remain unchanged and unresolved.

## Delivery status

New Game/backend source is unbuilt/undeployed. The installed candidate and native
bridge remain from runtime-world-20261001-152358, whose new WithExtent export is
already built. A subsequent Game/backend build can use that matching native bundle.
World checks continue using the backed-up foggy village story save. Playable world,
actual SR/FG, pixels, inputs and shutdown remain open; no acceptance claimed.
