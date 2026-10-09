# Streamline presentation extent correction — 2026-10-01

Implementation turn only. No build, tests, probes, package, deployment or game run.

## Grounded integration profile

Vulkan backend with engine-owned queues/swapchain; Streamline starts before Vulkan
instance/device creation. Native bridge obtains creation/acquire/present functions
from the production 2.14.1 interposer. Existing provider coordinator owns settings,
real-scene gating and per-frame constants/tags; Game and SDL remain outside the
provider implementation. Four upright input images are reused and therefore tagged
eOnlyValidNow on the current command buffer. No shader/resource-layout rewrite.
Current frame constants and token flow are in VulkanDevice.FrameGeneration.cs and
StreamlineRuntime.cs. Packaging keeps production vendor DLLs in the private native
folder. Screenshot routes exist, but no pixel capture or generated-frame evidence
has been accepted. Real-world validation must use the user's foggy village story
save, never the development superflat.

## Changes

The native TagFrame entry previously ignored its two supplied backbuffer dimensions.
It now rejects zero dimensions and adds a full-size kBufferTypeBackbuffer tag with
no resource pointer, as permitted by SDK ProgrammingGuideDLSS_G.md (backbuffer
subrect section). The four copied input lifetimes stay unchanged.

Non-game-frame invalidation now receives the current swapchain extent and tags it
while clearing the four scene inputs. Without a usable extent, only input tags are
cleared; no zero-sized explicit backbuffer extent is submitted. This does not
activate FG for menus/loading. The new export is named
VulkanStorySlInvalidateFrameTagsWithExtent, so a stale native bridge is rejected
by export binding rather than called with an incompatible C ABI.

VK_EXT_debug_utils is enabled for Streamline when advertised, even with validation
disabled. Validation-layer setup remains optional. This supplies SDK Vulkan naming
and profiling capability; it does not turn production validation on.

## SDK hook warning diagnosis

SDK source sl.common/common.json requests CmdBindPipeline, CmdBindDescriptorSets
and BeginCommandBuffer. pluginManager.cpp's FunctionHookID map omits all three;
processPluginHooks warns and skips them. The Vulkan interposer wrapper source also
forwards these three calls directly, without invoking those common hooks. That is
consistent with the production log and not repaired by routing additional game
calls through the same supplied wrappers. No runtime vendor binaries, plugin JSON,
or warning filtering were changed. Actual DLSS-G execution/state and compute
state restoration still need evidence; initialization is insufficient.

## Delivery and remaining work

Source requires a matching new native Streamline bridge and managed backend in
one future validation batch. Do not stage the previous native bundle unchanged:
its bridge lacks the new export. Installed candidate remains the successful
bounded startup payload from batch runtime-startup-20261001-151957. Archives
remain stale. Extent-warning removal, valid real-world rendering, provider
execution/visuals, resizing and shutdown are not verified by this source edit.
