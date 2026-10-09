# Streamline camera constant and shared NGX release — 2026-10-01

Implementation only. No builds, tests, probes, packages, snapshots or game runs.

The completed headless run logs cameraPinholeOffset left invalid. SDK sl_consts.h
and commonEntry.cpp show the centered-camera optional offset must have valid float
values even when unused. Native frame constants now explicitly set (0,0); jitter
continues through its separate field. No temporal matrix or motion algorithm changed.

The same run logs an FG NGX release failure 0xbad00004 during shutdown. Source order:
RuntimeUpscalers.Dispose -> DlssUpscaler.Shutdown -> NgxLifetime.ShutDown calls
NVSDK_NGX_VULKAN_Shutdown1, while Streamline remains alive until VulkanContext.Dispose.
Thus direct SR NGX shutdown precedes Streamline's remaining FG handle release.
This concrete shared-lifetime ordering is corrected; exact runtime resolution still
requires subsequent evidence.

A new bridge export, VulkanStorySlFreeFrameGenerationResources, calls the SDK's
slFreeResources(DLSS_G, viewport) when previously configured FG is off. Successful
release invalidates its options cache. The managed device first disables FG, drains
presentation through the Streamline DeviceWaitIdle proxy, then frees the feature.
Session shutdown performs this before upscaler shutdown and direct NGX shutdown.
Streamline hooks remain alive for swapchain destruction and normal context teardown.
Uninitialized/unsupported/no-swapchain preparation paths skip the release. Failures
are recorded through existing aggregate cleanup, not hidden.

SDK references: include/sl_consts.h, include/sl_core_api.h (flush pending work before
freeing resources), ProgrammingGuideDLSS_G.md section 6.4 (Off/free lifetimes), and
source/plugins/sl.common/commonEntry.cpp NGX release wrapper. No vendor binaries,
SDK JSON or warning suppression changed. Existing first-window ownership, frame
identity, scene gating and resource tagging remain intact.

The new export requires rebuilding the Streamline bridge and managed backend as
one staged payload before the next isolated harness run. Do not reuse the previous
native bundle unchanged. Installed game remains untouched. SDK camera-warning
removal, release-error removal, FG interpolation, visual parity and remaining
provider/platform/release gates remain unverified.
