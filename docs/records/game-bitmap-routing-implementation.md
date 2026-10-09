# Retained bitmap and atlas texture routing

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

The original bitmap upload methods from `VulkanClientPlatform.Textures.cs` now
live in `GameGraphicsAdapter.Textures.cs`: `LoadTexture`, `LoadIntoTexture`,
BGRA load/update, deferred BGRA atlas mipmaps and RGBA load/update. The dormant
Harmony texture group expands from three to eight typed targets, with explicit
return types, overload parameter arrays and by-reference `LoadedTexture` binding.
The game integration enables unsafe code for the original external bitmap pointer
path. The backend receives neutral pixel formats; game bitmap types stay here.

Baseline revision: `386e0d05386d0b228b439d09aeca851428f7bbf3`, source
`Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Textures.cs`.
Bodies are transplanted with adapter changes: main-thread identity becomes the
session owner thread, device access uses the checked renderer association,
mipmap settings are callbacks, and injected GL constants become identical
mod-owned token values. Local names and namespace change mechanically. Keep
inherited provenance/license scope.

Managed arrays retain `GCHandle` pin/free in `finally`; external bitmaps retain
`PixelsPtrAndLock`. BGRA/RGBA choices, filtering/wrapping, texture ID update,
size/reallocation decision, subregion uploads and mipmap behavior are unchanged.
The deferred atlas route still requests the full mip chain when enabled while
leaving its later generation to the existing caller. No upload shader, format
layout, temporal math or resource lifetime algorithm was redesigned.

The prior Harmony fixture now validates/installs the expanded target set, but
has not run against it. Its active deletion missing-owner assertion still does
not prove successful bitmap/atlas calls. No session attaches this adapter live,
and the subset still cannot satisfy the mandatory complete graphics-api group.

Next validation: one bounded Release game-test batch and profile-tool build.
Official signatures, unsafe source dependencies and compilation must be checked.
Pixel channel order, real atlas/mipmap updates and native resource lifetime need
new-host execution evidence. Cairo/cubemap/array routes and the remaining full
graphics/startup/menu/world/provider/SDL-control/release scope stay open.

The [first bounded validation](validation-g1-bitmap-routing-2026-09-30-01.md)
passed 29 game cases and a clean profile build. All eight target signatures
resolved and installed through the existing Harmony fixture. Successful bitmap
buffer/upload, atlas/mipmap pixels and native resource lifetime remain unverified.
