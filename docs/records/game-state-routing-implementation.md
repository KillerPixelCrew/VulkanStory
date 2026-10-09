# Fixed graphics state routing source port

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

Nineteen original platform targets now populate retained StatedRenderState:
viewport/scissor/flag/getter, depth enable/write/compare, cull enable/direction,
color mask, line width/wireframe, blend, smooth-line no-op and stencil flags.
The independent dormant graphics-state subset cannot satisfy complete graphics-api
activation. Calls check renderer/session owner and use the shared state read by
generic native draws.

Original scissor positive-quadrant clipping, depth GL-token mapping, enable versus
stored factors and blend algorithms are retained. EnumBlendMode uses an explicit
neutral mapping because its Multiply/Brighten order differs. Global blend changes
reapply the original SSAO slot 2/3 and motion attachment replacement policy using
mod-owned GameGraphicsFrameState. The frame/post-chain owner must populate these
stage flags; its live integration is still pending. Stencil application remains
absent where no stencil attachment exists, as in the retained renderer.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`,
VulkanClientPlatform.State and Frame motion-blend bodies. State/host boundary
changes are adapters; native pipeline math is unchanged. Preserve inherited notices.

A new unrun CPU fixture calls actual patched original methods against an owned
adapter without a native context. It checks viewport, negative scissor clipping,
depth/cull, mask, width/wireframe, blend enable and getter toggles. It does not
check all blend factors/SSAO/motion overrides or pipeline pixels.
Next validation is one bounded game-test/profile-build batch. Target signatures,
installation and successful state behavior await validation. Remaining state/
texture/mipmap/debug/stencil no-op consumers, framebuffer/clear/query/capture,
full startup/menu/world/provider/SDL/release acceptance remain open.

The [first bounded validation](validation-g1-state-routing-2026-09-30-01.md)
passed 34 game cases and a clean profile build. All nineteen state targets
installed; successful original patched CPU-state calls matched viewport/scissor/
depth/cull/mask/width/wireframe/blend enable values. Pipeline pixels and complete
blend/SSAO/motion factor behavior remain unverified.
