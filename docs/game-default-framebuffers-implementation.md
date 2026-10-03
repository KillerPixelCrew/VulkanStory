# Default framebuffer allocation, loading and shared lifetime

Date: 2026-09-30. Source migration ran without builds/tests/probes/packages/game
or deployment. The subsequent [first bounded validation](validation-g1-default-framebuffers-2026-09-30-01.md)
passed 37 game cases and a clean profile-tool build. Signatures/metadata,
installation/removal and exercised minimized/default/placeholder CPU behavior
passed. Positive native allocation, shared depth and real host wiring remain open.

## Retained allocation body

The default allocation body and color/depth/history/owned-target/noise helpers
are copied from the retained platform. Namespace, enum aliases, sampler tokens,
owner registration and settings/callback access are adapted; texture formats,
filters, noise/kernel seed and order, resource slots, transient opt-in and optional
allocation failure behavior remain the baseline's implementation.

| Slots | Retained allocation |
| --- | --- |
| 0 | Primary depth, color/glow, optional SSAO G-buffer and appended motion; motion excluded from ordinary draw mask. |
| 1 | OIT accumulation RGBA16F, revealage R16F, glow RGBA8; shared Primary depth. |
| 2/3, 8/9 | Display/render-scaled half/quarter post-chain color transients. |
| 4/7/10 | Bright, god-ray and luma post targets with original formats. |
| 5 | Quarter render-resolution liquid depth. |
| 11/12 | Shadow targets or nonnull zero-ID placeholders, including handheld dimensions and supported D16 choice. |
| 13/14/15 | SSAO transient result, original RGBA noise/kernel and blur targets. |
| 18 | FSR 1 output when render scale requires it without a selected SR provider. |
| 19/20/21 | TAA histories (color/aux/R32F depth) and independent sharpen target. |
| 22 | Storage-capable display-resolution upscaler scene plus depth. |
| 23/24 | HUD-free snapshot and separate UI with its own depth. |
| 25 | Storage-capable RGBA8 generated-frame output, allocated as in the baseline. |

Reserved/unpopulated slots retain their original indices. This ports allocation;
it does not implement scene, temporal, UI composition or FG evaluation producers.
Failure callbacks preserve the selected provider/TAA owner's disablement and
rebuild responsibilities. Noise pinning is released in finally on upload failure.

## Game state and host boundary

`GameFramebufferHost` must be configured explicitly. It supplies SDL pixel size,
the captured settings, retained provider planning, feature-disable callbacks,
diagnostics, FG reset, AO release, completed target publication and temporal reset.
No missing callback is replaced with a silent production no-op. The process
session still needs to wire these to its real owners before activation. Provider
selection/planning/LOD-bias and settings persistence belong to those owners.

Cached typed accessors bind original OffscreenBuffer, SetupSSAO,
ShadowMapQuality, ssaaLevel, ssaoKernel and screenQuad fields. Motion index,
TAA/UI publication, allocated provider plan and composite readiness remain
adapter-owned. The original caller assigns the returned list to frameBuffers;
the original rebuild body keeps its assignment-before-disposal order. Retiring
the old list does not erase the newly built set's published indices.

Setup finishes with the retained fullscreen mesh upload/replacement, target
selection, history invalidation and reset notification. Its mesh lifecycle
requires the complete mesh patch group. The original public PixelPackBuffer
array retains three objects with zero GL names; upcoming game query/readback
routes must replace all native uses of those names before activation. Native
readback is already retained in the backend but is not yet connected here.

## Loading and disposal

Six new prefixes cover SetupDefaultFrameBuffers, DisposeFrameBuffers and both
reference/enum LoadFrameBuffer and UnloadFrameBuffer overloads. The subset now
contains fourteen prefix targets plus eight checked transpiler bodies, touching
seventeen distinct original methods. Signatures/private metadata and expanded
installation/removal passed the first batch; complete native execution remains open.

Reference loading binds an owned allocated target and replaces color attachment
zero; reference unloading preserves the original delegation to Primary. Enum
loading retains target and depth/cull/blend changes, including OIT slot factors,
default Back selection and the ready upscaled composite for late overlays.
Primary/Transparent and post target viewports use actual target dimensions,
including provider render/display scaling. The default target uses SDL pixels.
Transparent unload restores depth writes and the bound target's viewport.

List disposal validates owners before resource deletion, resets FG, closes UI
redirection, releases AO targets, and deduplicates shared texture/target handles.
Released references protect repeated disposal; zero-ID shadow placeholders own
no native target and cannot delete the default framebuffer. Positive/native
retirement, allocation-failure cleanup and mixed individual/shared disposal
still need lifecycle acceptance; this source does not prove those cases.

## CPU fixture and remaining gates

One CPU fixture passed for actual original patched setup/load/unload/list
disposal: missing-host rejection, minimized-size list/field publication, default
pixel viewport, scaled unload, owned zero-ID shadow placeholders, foreign-list
rejection before cleanup, released-state checks and repeated disposal. It uses
an uninitialized device; no positive native handles are fabricated. It does not
exercise successful texture allocation, shared positive depth, compact formats,
provider plans, noise pixels, TAA/UI/FG inputs or live game behavior.

The first bounded Release game-test/profile-tool-build batch passed.
Native initialized target construction, representative allocation configurations,
resource lifetime and clear/draw pixels need later relevant validation. Full host
wiring, query/capture, remaining graphics/state/post/scene routes, startup/SDL,
provider evaluation and player release remain required. This subset cannot
satisfy the complete mandatory graphics-api group or activate the product alone.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`;
`porting/old-platform/VulkanClientPlatform.FrameBuffers.cs`,
`VulkanClientPlatform.UiSeparation.cs` and `VulkanClientPlatform.FrameGeneration.cs`.
Reference-only original platform source supplies private metadata and load/unload
control flow; the donor's injected helper bodies were inspected only to identify
their required state effects. Neither snapshot/donor nor the old checkout is a
build/runtime dependency. Preserve inherited attribution/license scope.
