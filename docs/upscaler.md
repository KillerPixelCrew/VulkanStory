# Native Vulkan upscaler integration

The selectable providers are **DLSS Super Resolution**, **XeSS Super Resolution**,
and **FSR 3.1 Super Resolution**. Reflex and frame generation are separate later
work. The existing FSR 1 render-scale control remains available independently.

## Shared frame contract

`IUpscalerBackend` is the device-side provider boundary. Each installed provider
prepares its Vulkan extension requirements before device creation. The selected
provider supplies a plan for the current display size and quality setting. The
platform allocates Primary at that render size and one output with color and depth
at display size. A settings change retires the provider's feature, rebuilds the
targets, reloads the affected shaders, and resets temporal history. Providers
stay initialized across live switches because Vulkan extensions cannot be added
to an existing device.

The shared input is linear RGBA16F scene color, D32 depth, render-resolution
RGBA16F motion, an unjittered motion-vector convention, and the temporal
context's actual jitter and reset flag. Evaluation happens after world rendering
and ambient occlusion, before bloom and final tone mapping. The output is
RGBA16F with storage and color-attachment usage. Bloom, god rays, luma, final
composition, and late world overlays run at display resolution. The separate
UI image is composed only after the world blit. The HUDless snapshot is taken
after late world overlays and before UI, so its non-UI pixels match the presented
scene for a future frame generator.

DLSS uses the SDK's optimal-size query for each quality mode. It creates a new
feature when size or mode changes, and retires old features after the frame
timeline. NGX owns one lifetime per process; the feature is released and the
timeline drained before NGX shutdown, while the Vulkan device is still alive.
The NVIDIA feature library (`nvngx_dlss.dll` on Windows or its Linux equivalent)
is a separate SDK redistributable placed beside the client or in `dlss/`. On
Windows x64, builds copy it and NVIDIA's license from `_ref/dlss` into the
renderer output; deployment and packages copy both beside the client. Clone
`NVIDIA/DLSS` into `_ref/dlss` first, or set `-p:DlssSdkDir=...`. The local shim
is built with the renderer and reports DLSS unavailable if it cannot be built
or loaded. On Windows it finds the driver's `nvngx.dll` in DriverStore. A
missing SDK feature library leaves DLSS unavailable in the selector.
When the Windows runtime omits the feature extension query entry points, the
device requests the NVIDIA SDK's fallback instance and device extensions before
creation. In particular, `VK_NVX_binary_import` is required for DLSS to load its
Vulkan kernels. NGX diagnostics can be enabled with `OPTIMUM_NGX_LOG=1`.

## XeSS Super Resolution

XeSS-SR implements the same provider interface and live switching path. Before Vulkan
creation it queries Intel's instance/device extensions and features, then patches the
complete feature chain so promoted Vulkan 1.2/1.3 structures are not duplicated.
Unsupported or missing runtimes leave that provider unavailable; a provider failure
only disables that provider for the session, allowing another provider to be selected.

The backend queries `xessGetOptimalInputResolution` for all seven presets, including
Native Anti-Aliasing (the persisted `dlaa` value), Ultra Quality Plus and Ultra Quality.
It publishes the render size and `log2(render/display)` mip bias before world rendering.
DLSS retains its own additional mip bias offset. Shared jitter uses at least
`8 * upscaleRatio²` Halton phases. XeSS receives the actual pixel jitter directly:
an analytical GPU test checks all four sign combinations with our positive-height
viewport. Motion remains unjittered current-to-previous pixel displacement. A same-size
nearest Vulkan blit extracts RG from the RGBA16F motion/metadata attachment into RG16F.
XeSS receives low-resolution motion, D32 forward depth, linear HDR color, and automatic
exposure. History resets on temporal discontinuities and the first dispatch of each
new context. Responsive masks are not enabled without visual evidence they help.

Inputs transition to shader-read-only and output to general with compute read/write
synchronization. SDK execution runs outside rendering scopes; cached dynamic state is
invalidated afterward. Size/quality changes create a fresh context and defer destruction
of the old context and motion texture until GPU work completes. No in-flight context
is reinitialized. The SDK module stays loaded for deferred destruction.

Windows x64 builds copy `_ref/xess/bin/libxess.dll` and Intel's license/third-party
notices beside the renderer. Override the SDK folder with `-p:XessSdkDir=...`;
`OPTIMUM_XESS_LIBRARY` can name an absolute DLL path for development. Without the SDK,
the renderer still builds and the selector explains XeSS's unavailability. Current
Intel binaries require Windows 10/11 and the Visual C++ 14.40.33810+ runtime. Linux
continues to use the other available providers.

Verified runtime: Intel SDK `v3.0.2`, `libxess.dll` SHA-256
`251659dd84a3e84de67c886a4186e01f3eca49b00641906fe38bb6b807e5d5b7`.
The local reference clones are `_ref/dlss` (NVIDIA/DLSS), `_ref/streamline`
(NVIDIA-RTX/Streamline), `_ref/xess` (intel/xess), and `_ref/fsr`
(GPUOpen-LibrariesAndSDKs/FidelityFX-SDK). `_ref` is ignored.

Validated on NVIDIA GeForce RTX 4070 Laptop GPU, driver 616.92. XeSS and FSR 3.1
execute real-runtime GPU readback and context-recreation tests. DLSS's Windows
runtime test reports `SuperSampling.Available=1`, valid Quality and Performance
render sizes, and a successful GPU evaluation. A hidden game capture with DLSS
Ultra Performance rendered the world at 427×283 and upscaled to 1280×850. A
second capture with XeSS selected kept DLSS initialized and available for a live
switch. DLSS runtime tests skip when its feature DLL is absent.

GPU acceptance covers HDR output readback, real RGBA-to-RG motion conversion, all
seven preset queries, in-flight context retirement/recreation, and static jitter
registration with Vulkan synchronization validation. In-game visual acceptance on
Intel and AMD hardware remains necessary, especially moving geometry, disocclusions,
exposure changes, resize/minimize and live provider switching.

## FSR 3.1 Super Resolution

`fsr3` uses the signed AMD FidelityFX SDK 1.1.4 Vulkan runtime. It queries render
resolution for Native AA, Quality, Balanced, Performance, and Ultra Performance,
and applies AMD's `log2(render/display) - 1` texture mip bias. The runtime receives
linear HDR color before tone mapping, forward D32 depth, RG16F low-resolution
unjittered current-to-previous motion vectors in pixel units, actual render-pixel
jitter, frame time, camera planes and vertical field of view. Auto exposure is
enabled. A same-size Vulkan blit extracts the RG channels from the shared
RGBA16F motion/metadata attachment. The output is RGBA16F at display resolution.

FSR contexts and motion resources retire after the GPU timeline on size, quality,
or provider switches. The signed runtime stays loaded until retired contexts are
destroyed. At device creation, the provider enables shaderInt16 and shaderFloat16
when supported because AMD's Vulkan backend selects FP16 permutations from
physical-device capabilities. The bridge maps the SDK's unconditional
`vkGetBufferMemoryRequirements2KHR` lookup to the Vulkan 1.1 core alias when the
device exposes only the promoted name.

On Windows x64, builds copy `_ref/fsr-vulkan/PrebuiltSignedDLL/amd_fidelityfx_vk.dll`
and its license and build `OptimumFsr3.dll` against the matching 1.1.4 headers.
This requires the Vulkan SDK and a MinGW C++ compiler. Override the SDK folder
with `-p:Fsr3SdkDir=...`; for development, `OPTIMUM_FSR3_LIBRARY` and
`OPTIMUM_FSR3_BRIDGE` can point to the two DLLs. Without these artifacts the
provider reports unavailable. The local Vulkan-capable SDK reference is a worktree
at `_ref/fsr-vulkan`; `_ref/fsr` tracks the current FidelityFX SDK separately.

The FSR GPU test covers all five preset queries, HDR readback, RGBA-to-RG motion
conversion, in-flight context retirement, size and quality changes, and Vulkan
validation. Visual acceptance on AMD and Intel hardware remains necessary.

The newer FSR 4 ML runtime in AMD FidelityFX SDK 2.3.0 supports DirectX 12 but
does not support Vulkan. Consequently the Vulkan selector uses FSR 3.1 on both
older and newer hardware; no FSR 4 mode is offered until AMD releases Vulkan
support. The AMD signed binary supplied here is Windows only. Frame generation
remains a separate later integration.

## Frame generation preparation

The existing UI separation supplies the two color views a frame generator
needs: a display-resolution HUDless scene and a UI image with alpha. The
HUDless image is captured after all world overlays. The render-resolution depth
and motion inputs come from the same frame as SR. Frame generation will also
need its own presentation scheduling, backbuffer handoff, latency integration,
and explicit validation of HUDless/backbuffer pixel agreement. Those are not
enabled by this SR integration.

## Primary integration guides

- [NVIDIA DLSS Programming Guide](https://raw.githubusercontent.com/NVIDIA/DLSS/main/doc/DLSS_Programming_Guide_Release.pdf), especially sections 3.1, 3.5–3.10, and the Vulkan resource contract.
- [NVIDIA DLSS Frame Generation Programming Guide](https://raw.githubusercontent.com/NVIDIA/DLSS/main/doc/DLSS-FG%20Programming%20Guide.pdf), for HUDless and UI requirements used in the future-facing layout.
- [Intel XeSS-SR Developer Guide](https://github.com/intel/xess/blob/main/doc/xess_sr_developer_guide_english.md), for XeSS Vulkan input/output layouts and temporal data.
- [AMD FSR 3.1 Super Resolution Upscaler guide](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-upscaler/), for the temporal inputs and decoupled frame generation path.
- [AMD FSR 4 Super Resolution ML guide](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/Kits/FidelityFX/docs/techniques/super-resolution-ml.md), for the current Vulkan support status.
