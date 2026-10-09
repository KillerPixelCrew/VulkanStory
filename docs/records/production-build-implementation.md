# Production build source increment — 2026-10-01

Implementation only. No scripts, compiler invocations, tests or game runs occurred.

`scripts/build-runtime.ps1` now builds the native shader compiler and runs its
full `--build` mode against `shaders/native`. Output is
`artifacts/runtime-shaders/<Configuration>/shaders-vk`, the input expected by
runtime staging. `-SkipShaders` is available for incremental managed work; a
package still needs the full corpus. No shader verification/test command is added.

`scripts/build-provider-bridges.ps1` builds the five retained Windows x64 bridges:

| Bridge | Build input |
| --- | --- |
| VulkanStoryNgx.dll | C compiler; retained driver-discovery shim source |
| VulkanStoryFsr3.dll | FidelityFX 1.1.4 API and Vulkan headers |
| VulkanStoryFsr4.dll | FidelityFX DX12 API and Vulkan headers |
| VulkanStoryXessFg.dll | XeSS FG/XeLL SDK and Vulkan headers |
| VulkanStoryStreamline.dll | Streamline SDK and Vulkan headers |

SDK roots and output directory are explicit. The script requires a fresh output
directory and checks principal SDK headers/compiler availability before building.
No SDK downloads, donor assemblies, game copying, tests, install or launch steps
are included. Built bridges are combined with licensed vendor redistributables,
SDL and shaderc in the native bundle supplied to staging. Bridge output alone is
not a complete runtime bundle.

FSR 4 and XeSS-FG recipes now accept VulkanSdkRoot consistently with the other
provider recipes. Streamline statically links GCC/C++ runtime components, reducing
compiler installation dependencies in delivery; remaining binary dependencies
are unverified. NGX's incremental return no longer exits a calling build script.

All changes are unexecuted source. Build success, binary exports/dependencies,
complete shader variants, licensed distribution and in-game provider execution
remain open; no evidence count or accepted milestone changes.
