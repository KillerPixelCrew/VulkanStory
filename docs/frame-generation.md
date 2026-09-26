# Vulkan frame generation

The Vulkan renderer already marks input, simulation, submission and presentation
boundaries, and retains a HUD-free scene plus a separate premultiplied UI image.
Frame generation reads those images after UI composition. Changing
`OptimumConfig.FrameGeneration` releases the previous provider, resets temporal
history, and activates the new provider without recreating the Vulkan device.
The Vulkan frame identity
is shared by latency and frame generation. Each SDK owns its generated presents
and pacing while its swapchain proxy is active.

| Provider | Current path | Availability |
| --- | --- | --- |
| DLSS Frame Generation | Streamline 2.14.1 Vulkan swapchain proxy; depth, motion, HUD-free scene and UI are tagged for the SDK | Requires a supported NVIDIA device and Streamline FG runtime; a hidden headless window cannot verify displayed generated frames |
| FSR 3 Frame Generation | FidelityFX Vulkan swapchain proxy calls the SDK generation callback on present and composes the separate UI resource | Requires `amd_fidelityfx_vk.dll` and `OptimumFsr3.dll`; after world load, 120 rendered frames added 240 SDK presents in a headless run |
| XeSS Frame Generation | XeSS 3.0.2 DX12 proxy replaces Vulkan WSI on the same window; Vulkan blits the rendered color, depth, motion, HUD-free scene and UI into imported D3D12 resources and signals a shared fence | Selectable in the Vulkan options; a headless world run reported 239 SDK presents for 120 rendered frames with generation enabled |

XeSS-FG's SDK 3.0.2 uses a DX12 `IDXGISwapChain4` proxy. A provider switch
destroys Vulkan WSI before creating the Intel proxy; leaving XeSS-FG restores
Vulkan WSI. A temporary loss of XeSS inputs returns to normal Vulkan
presentation, and a failed WSI restore is retried. The matching D3D12 device
and XeLL context use the Vulkan device LUID. D3D12 committed resources are
imported into Vulkan with `D3D12_RESOURCE_BIT`. A shared D3D12 fence orders
Vulkan copies against DX12 SDK reads. The Intel bridge tags depth, motion, HUD-free color, UI and
row-major unjittered matrices, sets the frame's present ID, and calls the proxy
`Present`. A targeted headless probe passes the adapter, shared image, shared
fence and Vulkan queue handoff with Vulkan synchronization validation enabled.
The proxy `Present` has run through 120 world frames in a hidden-window client;
displayed output still needs visible-window verification. The prior `testhost.exe` popup
came from the reversed sharing direction and has not recurred in the headless
probe.

Reflex uses Streamline and `VK_NV_low_latency2` where their respective
presentation paths support them; AMD AntiLag uses `VK_AMD_anti_lag`. Both attach
to the engine's existing frame markers. XeLL receives sleep, frame cap and
markers while the Intel proxy is active; Reflex and AntiLag are suspended for
that path.

Visible-window validation remains necessary for displayed FPS, history resets,
resize/minimize, motion-vector sign and scale, and UI recomposition. The
FidelityFX and XeSS SDK counts confirm generated presents during headless world runs,
but a hidden window cannot establish what a monitor actually displayed.
