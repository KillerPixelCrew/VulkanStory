# Cairo and cubemap routing source port

Date: 2026-09-30. Implementation only; no builds/tests/probes/packages/game runs.

The retained `LoadCairoTexture`, `LoadOrUpdateCairoTexture` and
`Load3DTextureCube` bodies join the game graphics adapter. The dormant texture
group expands from eight to eleven typed targets. `cairo-sharp` is referenced
from the official installation with `Private=false`; game/Cairo types remain
outside the renderer. The renderer still receives raw format tokens and native
pixel pointers.

Baseline revision: `386e0d05386d0b228b439d09aeca851428f7bbf3`,
`Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Textures.cs` and the
conditional `CheckGlError` body in `VulkanClientPlatform.State.cs`.
Names/constants/thread identity and device access change mechanically or through
the established adapter. Preserve inherited source provenance and license scope.

Cairo creation still uses BGRA four-byte pixels and original filtering. Update
still deletes/recreates on extent changes and copies raw bytes when extents match.
The final conditional backend-error check uses an explicit host callback for
the original error-checking setting; session attachment must now supply it.
The cube still uploads all six external bitmap buffers in original order and
uses retained filtering/wrapping. Owner/routing is checked before acquiring cube
pixel pointers. No image arithmetic, swizzle, shader or resource layout changes.

Next validation is one bounded game-test/profile-build batch. The existing
Harmony fixture will resolve/install the expanded signatures, but successful
Cairo surfaces, cubemap faces, pixel order and lifetime remain unexercised.
Texture arrays and broader graphics/shader/mesh/state/startup routing, actual
menu/world calls, vendor features, SDL controls and release remain open.
The eleven-target subset cannot activate the mandatory graphics-api transaction.

The [first bounded validation](validation-g1-cairo-cube-routing-2026-09-30-01.md)
passed 29 game cases and a clean profile build. All eleven texture signatures
resolved and installed through the existing Harmony fixture. Successful Cairo
and cube uploads, error-setting behavior, pixels and lifetimes remain unverified.
