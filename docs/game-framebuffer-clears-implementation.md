# Original framebuffer clears and draw-buffer selection

Date: 2026-09-30. Source migration was an implementation-only turn without
builds/tests/probes/packages/game/deployment. The subsequent
[first bounded validation](validation-g1-framebuffer-clears-2026-09-30-01.md)
passed 36 game cases and a clean profile-tool build. Official/incoming anchors,
installation/removal and exercised CPU dispatch passed; native clears and game
rendering remain unverified.

## Original method integration

The existing dormant framebuffer group gains four prefixes:

- `GlClearColorRgbaf(float,float,float,float)` records retained clear-color state.
- `ClearFrameBuffer(FrameBufferRef,bool)` uses the original private `clearColor`
  array, accessed through a cached typed binding.
- `ClearFrameBuffer(FrameBufferRef,float[],bool,bool)` binds the owned target
  without changing viewport and clears its color/depth attachments.
- `ClearFrameBuffer(EnumFrameBuffer)` retains default, primary, shadow/liquid
  depth and weighted OIT clear values plus the retained motion-slot clear.

There are now eight prefix targets including the prior four lifecycle/binding
targets. No injected `ClearBoundFrameBuffer`, `ClearFrameBufferPass`, or
draw-buffer helper method is assumed to exist in the official game.

The original `GlClearColorRgbaf` changes GL clear state without updating the
private array used by reference clears. The adapter preserves that separation;
the array's original default is `(0,0,0,1)`, while retained GL clear-color state
starts at zero. Default pass clearing temporarily binds the default target,
selects Back and restores the original primary target while keeping viewport.
Primary clears use the frame owner's SSAO flag and motion attachment, including
temporary motion-write selection and the retained non-motion mask afterward.

Color clears retain the selected-slot/all-false-color-mask gate; depth clears
retain the depth-write gate. Backend pending clears, pass transitions, submission
and resource algorithms are unchanged. This is not a new per-channel masked
clear implementation: the inherited color-mask rule only suppresses all-false
masks. Partial channel-mask parity remains an explicit acceptance gap.

## Checked original call sites

Draw-buffer selection is embedded in existing methods. Transpilers substitute
only the exact typed GL calls below, adding the original platform receiver.
Expected counts come from the original platform source snapshot. The first
validation matched them against the official loaded assembly and incoming
Harmony IL; successful full method execution and native pixels remain open.

| Original method | DrawBuffer | DrawBuffers | Color ClearBuffer |
| --- | ---: | ---: | ---: |
| CreateFramebuffer(FramebufferAttrs) | 0 | 1 | 0 |
| SetupDefaultFrameBuffers() | 13 | 3 | 0 |
| ClearFrameBuffer(EnumFrameBuffer) | 1 | 0 | 7 |
| LoadFrameBuffer(EnumFrameBuffer) | 2 | 1 | 0 |
| UnloadFrameBuffer(EnumFrameBuffer) | 1 | 0 | 0 |
| MergeTransparentRenderPass() | 1 | 0 | 0 |
| RenderFinalComposition() | 0 | 3 | 0 |
| RenderPostprocessingEffects(float[]) | 0 | 0 | 1 |

Eight transpiler bodies contain 34 substituted calls. Two methods also have
prefixes, so the complete subset touches fourteen distinct original methods.
The post-processing substitution covers its white SSAO color clear. Original
and incoming bodies must both match exact counts before routing can be accepted.
Branch labels and exception-region beginnings move to the inserted receiver;
exception-region endings stay with the replaced call. Installation/removal uses
the same framebuffer Harmony owner. Dormant wrappers call the original GL API.

Selection updates the current owned framebuffer's retained draw mask. None,
default Back, color attachment zero and identity multi-attachment masks cover
the targeted calls. A reordered attachment list is rejected before mutation;
the retained mask model cannot represent that output remapping. Counts/arrays,
owner thread, enabled routing, target ownership and released state are checked.
This does not establish support for arbitrary third-party GL selectors.

## Source fixture and remaining acceptance

The expanded fixture passed checks for group install/remove for new prefixes and
call-site bodies, active original clear-color/pass-prefix dispatch, original
private-array independence, None/Back masks, invalid-selector/count rejection,
post-clear wrapper dispatch and foreign/not-ready target rejection. Its borrowed
device has no Vulkan context or active frame. There are no new CPU-to-GPU clear,
positive target, motion, SSAO/OIT pixel, in-flight lifetime or live game results.
The official OpenTK.Graphics test reference is non-copying, like the game refs.

The first Release game-test/profile-tool-build batch passed. Native clear pixels
require a later bounded batch using an initialized device and owned
targets. Default target setup/shared-depth lifetime and load/unload still contain
unported operations, as do other post/state/window calls. These substitutions
do not make those complete methods safe to activate alone. The subset cannot
satisfy the mandatory complete `graphics-api` transaction group. Menus/worlds,
SDL startup, enabled providers and release acceptance remain open.

Provenance: baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`;
`porting/old-platform/VulkanClientPlatform.FrameBuffers.cs` clear helpers and
`VulkanClientPlatform.NativeWorld.cs` clear/write-mask rules. Original flow and
call-site inventory come from the reference-only official platform snapshot at
`D:\Coding\VulkanStory\build\snapshot\VintagestoryLib\Vintagestory.Client.NoObf\ClientPlatformWindows.cs`.
The snapshot is not a build/runtime dependency. Preserve inherited attribution
and license scope.
