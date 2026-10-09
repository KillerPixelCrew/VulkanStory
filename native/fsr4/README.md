# FSR 4 bridge

Source migrated from `native/optimum-fsr4` at baseline revision
`386e0d05386d0b228b439d09aeca851428f7bbf3` of the Optimum repository
(StratumServer/Optimum). Own `OptimumFsr4` identifiers/exports become
`VulkanStoryFsr4`; SDK calls, frame layout, adapter selection, image sharing,
queue waits/signals and destruction are retained. Preserve the repository's
inherited provenance and notices.

The explicit-SDK build recipe is retained. From a Windows developer shell with
`g++` and Vulkan SDK headers available:

```powershell
./native/fsr4/build.ps1 -SdkRoot <FidelityFX-SDK-root> -Output <absolute-output-directory>/VulkanStoryFsr4.dll
```

The supplied SDK root must contain `Kits/FidelityFX/api/include/dx12/ffx_api_dx12.h`.
`VULKAN_SDK` supplies Vulkan headers. This recipe neither downloads SDKs nor
copies vendor binaries. Ship the separately authorized signed
`amd_fidelityfx_upscaler_dx12.dll` beside `VulkanStoryFsr4.dll` in the payload's
native RID directory. Players do not run this script.

Consult the [Roadmap](../../docs/ROADMAP.md) for current source and validation status. AMD initialization, shared-resource/fence execution, real-scene quality, switching and shutdown remain acceptance gates.
