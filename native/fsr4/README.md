# FSR 4 bridge

`VulkanStoryFsr4.dll`: runs the FidelityFX DX12 upscaler for the Vulkan renderer through shared images and fences.
Migrated from Optimum's `native/optimum-fsr4` (revision `386e0d0`); its provenance and notices apply.

Build (Windows, `g++`, `VULKAN_SDK` set); the SDK root defaults to the `sdk/fidelityfx` submodule when built through `scripts/build-provider-bridges.ps1`:

```powershell
./native/fsr4/build.ps1 -SdkRoot sdk/fidelityfx -Output <dir>/VulkanStoryFsr4.dll
```

At runtime it loads `amd_fidelityfx_upscaler_dx12.dll` from the same directory.
