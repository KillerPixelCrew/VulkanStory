# FSR3 Vulkan SDK corrections

`luma-history-format.json` records the exact correction for pinned SDK commit
`c6efa6bf7f2027b3ec94f28578bb5965eabb9e55` and [upstream issue 161](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/issues/161).
Both luma history images are RGBA16F, while their GLSL storage declaration says
`rgba8`. Vulkan requires matching storage image and view formats. The correction
changes only that declaration to `rgba16f`; resource precision and the algorithm
remain unchanged. Original SDK copyright and license notices are preserved.

Build from the repository root:

```powershell
& ./native/fsr3/build-sdk.ps1 -VulkanSdkRoot C:/VulkanSDK/1.4.357.0
```

The script requires Windows x64, CMake, Visual Studio 2022/2026 x64 C++ tools,
Vulkan SDK headers/import library, and the pinned SDK's own shader compiler tools.
It copies tracked vendor sources into a fresh directory under `artifacts/`,
guards the original shader declaration and RGBA16F resource descriptions, and
rebuilds the SDK API target and its shader permutations. MSBuild concurrency is
limited to four jobs and each shader compiler to one worker by default. Only
staged build scheduling is adjusted; shader flags/permutations remain unchanged.
It does not edit the
vendor submodule or launch the game. A failed build produces no runtime receipt.

The default output is `artifacts/native-fsr3-sdk/Release/win-x64/runtime`.
`scripts/prepare-native-bundle.ps1` requires that corrected runtime by default.
For another fresh build directory, pass its `runtime/` subdirectory explicitly
with `-Fsr3RuntimeDirectory`. Every selected runtime requires the build
receipt, pinned source identity, correction identity and matching runtime hash;
it cannot silently use the original prebuilt runtime. A missing corrected build
or stale receipt fails packaging rather than restoring known undefined behavior.
