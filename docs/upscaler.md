# Native Vulkan upscaler integration

The provider order is **DLSS Super Resolution**, **XeSS Super Resolution**, then a
temporal **FSR** provider. Reflex and frame generation are separate later work.
The existing FSR 1 render-scale control remains the spatial fallback while the
temporal providers are added.

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
is a separate SDK redistributable placed beside the client or in `dlss/`. The
local shim is built with the renderer and reports DLSS unavailable if it cannot
be built or loaded. On Windows it finds the driver's `nvngx.dll` in DriverStore;
the NVIDIA feature DLL remains a separate installation input.

## Provider additions

XeSS is next: implement `IUpscalerBackend`, register it before device creation,
translate the shared color/depth/motion/jitter/reset contract to the XeSS Vulkan
API, and validate its required image layouts and synchronization. The FSR
provider follows the same contract. Its temporal reconstruction is separate from
the current FSR 1 spatial blit, and its frame generation remains later work.

The current settings list contains only `off` and `dlss`. The registry chooses
the provider by id, so XeSS and FSR can be added without changing the native
post chain. Provider capability checks determine whether an entry is available
on the active device.

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
- [Intel XeSS-SR Developer Guide](https://github.com/intel/xess/blob/main/doc/xess_sr_developer_guide_english.md), for the next provider's Vulkan input/output layouts and temporal data.
- [AMD FSR 3.1 Super Resolution Upscaler guide](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-upscaler/), for the later temporal FSR provider and its decoupled frame generation path.
