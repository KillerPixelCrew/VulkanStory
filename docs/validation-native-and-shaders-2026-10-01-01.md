# Native bridges and full shaders — 2026-10-01

One bounded validation/build batch, run once. No implementation source changed,
tests/probes ran, package was staged, game files were installed or game launched.

| Operation | Exit | Result |
| --- | --- | --- |
| build-provider-bridges.ps1 | 0 | All five Windows x64 bridges produced |
| Shader compiler production build | 0 | 0 errors, 0 warnings |
| Full native shader --build | 0 | 50 programs, 143 variants, 286 SPIR-V files |

SDK header inputs came from the existing `_ref/fsr-vulkan`, `_ref/fsr`,
`_ref/xess` and `_ref/streamline` SDK directories in the old development checkout.
Vulkan headers: `C:\VulkanSDK\1.4.357.0`. These are development SDK inputs;
no donor or game assemblies were copied or used by the bridge builds.

Native outputs in
`artifacts/validation/native-and-shaders-20261001-031956/bridges/`:

| Binary | Bytes |
| --- | ---: |
| VulkanStoryNgx.dll | 56,715 |
| VulkanStoryFsr3.dll | 304,394 |
| VulkanStoryFsr4.dll | 696,624 |
| VulkanStoryXessFg.dll | 738,829 |
| VulkanStoryStreamline.dll | 694,968 |

Full native shader output:
`artifacts/validation/native-and-shaders-20261001-031956/shader-output/shaders-vk/`.
This replaces the single-program artifact as the candidate staging shader input.

Complete provider/compiler/shader logs and results.json are retained in the batch
directory. Native compiler emitted **41 warnings**, retained without suppression:

- FSR 3: 7 function-pointer cast warnings and 1 Wextra warning.
- FSR 4: 6 function-pointer cast warnings and 8 missing-field-initializer warnings.
- XeSS FG: 19 function-pointer cast warnings.

The build establishes native compilation and offline shader generation, including
the compiler's existing reflection/layout checks. It does not establish exported
ABI loading, DLL dependency resolution, vendor SDK execution, real rendering,
presentation, or licensed runtime bundle completeness. Vendor redistributables,
SDL/shaderc binaries and notices still need assembling with these outputs.
No acceptance milestone or earlier test count is marked complete.
