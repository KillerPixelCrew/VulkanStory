# NGX source migration boundary

Date: 2026-09-29. **Source-only implementation; not built or run in this turn.** The first Vulkan backend project now includes the retained NGX interop, SR/FG feature objects, capability requirements, process lifetime owner, and one renderer-owned native resolver. A VulkanStory-named copy of the C bridge is under `native/ngx/`.

## Grounded host profile

- The retained renderer is Vulkan. `VulkanContext` creates the instance and device, and `Swapchain` owns acquire/present resources and image indices. `VulkanDevice.FrameGeneration.cs` prepares depth, motion, HUD-free color, and UI images and calls the retained Streamline wrapper. Those device and present files are **still staged, outside the compiled project**.
- DLSS Super Resolution uses the raw NGX path through a small native C bridge. The existing DLSS-G presentation path uses the separate Streamline 2.14.1 Vulkan proxy. Compiling the NGX feature object does not activate the Streamline present path.
- The retained Streamline wrapper calls its proxy instance/device/swapchain functions and tags real-frame resources. A later source turn renamed it to `VulkanStoryStreamline.dll` and added it to the backend project; that later change awaits validation. Frame token, current-frame constants, present identity, SDK state, and runtime fallback still require live checks.
- [The local Streamline SDK guide](../../../VulkanStory/_ref/streamline/docs/ProgrammingGuideDLSS_G.md) and `include/sl_dlss_g.h` require same-frame constants/tags valid through present, explicit `slDLSSGSetOptions`, a presenting-thread options handoff, and a generated-frame count bounded by `DLSSGState::numFramesToGenerateMax`. The final VulkanStory integration has not demonstrated these conditions yet.

## Source and ABI changes

The new `native/ngx/vulkanstory_ngx.c` and `.h` are mechanical copies of the working shim: exported `OptimumNgx_*` names and the library basename became `VulkanStoryNgx_*` and `VulkanStoryNgx`. Result values, structure layouts, SDK calls, and the no-tail-call guard remain the same. The original copy remains under `native/migrated/optimum-ngx/` for provenance. The C build recipe now reports a missing compiler or failed compile as a failure on Windows, so a stale DLL cannot be mistaken for a new build.

The managed `NgxShim` imports the new names. One renderer assembly resolver selects `VulkanStory/native/<rid>/VulkanStoryNgx.dll` or `libVulkanStoryNgx.so` for a deployed package, with an explicit absolute development override through `VULKANSTORY_NGX_SHIM_PATH`. A deployed package does not search the game folder for this bridge. `NgxSession` searches the same private native directory for redistributable feature libraries; development output paths remain available outside the deployed layout. The existing custom-engine NGX project GUID is retained to preserve the tested baseline; its acceptance in the new package is a runtime gate.

`NgxResourceVk` keeps the original 56-byte ABI. Its `VulkanTexture` adapter moved to a second partial source file, excluded until the texture manager joins the backend project. The NGX feature objects, resource ABI, and lifetime owner are included now. Focused tests carry forward native offsets, jitter/reset mapping, FG camera validation, and release/drain/shutdown ordering.

## Remaining acceptance

The next validation turn can run one bounded batch for the managed backend tests and native Windows bridge build, recording outputs and exported symbols. That will establish compilation and ABI names, not SDK functionality. Full NGX and DLSS-G acceptance still requires the Vulkan device, texture adapter, native package staging, real game adapter, tagged real-scene frames, optional-runtime fallback, resize/toggle paths, visible output, and frame pacing evidence. The old renderer's recorded hardware results remain baseline evidence, not proof of the new host path.
