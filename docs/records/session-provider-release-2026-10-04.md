# Session/provider release and Streamline cache ownership — 2026-10-04

DIRECT implementation turn in `D:/Coding/VulkanStory-Rewrite` (no Git repository).
SDK-03 remains open. No builds, tests, probes, native/GPU/game runs, packaging or
deployment occurred; installed game/settings/save and existing binaries are unchanged.

## Applied changes

Session StopAndDrain previously collected errors and continued destroying dependent
GPU owners after FG disable/drain or Streamline resource release failed. It now
marks the session stopping before cleanup and releases GPU owners sequentially:
FG host, Streamline FG resources, graphics auxiliary owners, SR host, device and
graphics association. An exception stops that chain immediately. Fields clear
only after their release succeeds; the first GPU failure is retained and later
cleanup attempts reject it. A failed provider/device release retains the device
and SDL host through the existing ReleaseSessionResources boundary. Controller
cleanup remains independent: its failure is reported but does not block a safe
GPU drain. Resources still present after a partial failure remain retained until
process exit, rather than being retried as though destruction were atomic.

VulkanDevice now checks the device-idle result before its dependent cleanup,
records a disposal exception, rejects retries and marks disposal complete only
after the full release succeeds. The existing deferred-resource and swapchain
lifetime guards still run before teardown. This does not fix arbitrary lower-level
owners that discard a native destruction result or standalone direct-device
provider shutdown ordering.

Current Streamline native destruction did **not** globally disable FG: it reset
the shared fgConfigured cache on every retired chain. The bridge now tracks its
successfully created current swapchain. Successful successor creation invalidates
options once; old-chain destruction leaves the successor's cache intact. Current-
chain destruction and shutdown clear that identity/cache. Failed creation preserves
the previous identity/cache. No feature mode, tags, frame constants, queue mode,
ABI signature, multiplier or rendering algorithm changed.

## Source basis and proof boundary

The Streamline DLSS-FG skill/checklist and local release SDK 2.14.1 were used.
Relevant contracts: `include/sl_dlss_g.h` options and Vulkan queue mode,
`include/sl_core_api.h` resource-release contract, and the DLSS-G guide's
presentation/resize rules. The active engine profile is Vulkan proxy dispatch via
StreamlineRuntime/bridge, one viewport and process device, Swapchain/Slot owners,
RuntimeFrameGeneration tagging depth/motion/HUD-less color/UI with current temporal
constants, ordinary renderer settings and deferred menu/world readiness, private
package-native DLLs, and the isolated foggy-village headless capture harness.
Motion/visual/pacing correctness remains unverified by this increment.

Source inspection followed session stop/dispose and SDL detachment, device
disposal/guards, and native cache creation/destruction/shutdown paths. No regression
tests were added. Managed/native compilation, actual patch/runtime execution,
failure-path evidence, all-vendor SR/FG output/pacing and Options acceptance remain
open. The earlier managed build predates both this and the FSR3 release increment.

## Current source SHA256

| File | SHA256 |
| --- | --- |
| `native/streamline/bridge.cpp` | `DEEAF6C536EDA9615A87BAD163D9B4207E0DBBD4069260D591942964430B3155` |
| `src/VulkanStory.Game/GameRenderSession.cs` | `5B1B10528F3B6C582470FED638F82204A4CA71F49D9AFBB716FA286140A27A85` |
| `src/VulkanStory.Render.Vulkan/VulkanDevice.cs` | `04302A7CB3CC8F68794338CC6E6A8D75052769C0AFDFF0D25E7F6F49959C6ACB` |

The VulkanDevice hash supersedes that file's hash in the earlier FSR3 source
record; its previously applied swapchain lifetime guard remains present.
