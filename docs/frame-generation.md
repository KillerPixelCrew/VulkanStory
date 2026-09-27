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
| DLSS Frame Generation | Streamline 2.14.1 Vulkan swapchain proxy; depth, motion, HUD-free scene and UI are tagged for the SDK | On a focused visible Windows SDL world, the SDK reported 239 actual presents for 120 rendered frames at 2×; direct inspection of generated display frames remains open |
| FSR 3 Frame Generation | FidelityFX Vulkan swapchain proxy calls the SDK generation callback on present and composes the separate UI resource | Requires `amd_fidelityfx_vk.dll` and `OptimumFsr3.dll`; after world load, 120 rendered frames added 240 SDK presents in a headless run |
| XeSS Frame Generation | XeSS 3.0.2 DX12 proxy replaces Vulkan WSI on the same window; Vulkan blits the rendered color, depth, motion, HUD-free scene and UI into imported D3D12 resources and signals a shared fence | Selectable in the Vulkan options; a headless world run reported 239 SDK presents for 120 rendered frames with generation enabled |

The graphics options expose a requested 2× through 6× frame generation
multiplier. DLSS and XeSS clamp the requested generated-frame count to the
maximum reported by their active SDK and GPU. AMD FSR 3 Frame Generation uses
its SDK swapchain's supported single interpolated frame, so its effective
multiplier stays at 2×. FSR 4 is offered separately for Super Resolution through
the DX12 interop path; it does not add another frame generation mode.

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

Reflex uses Streamline or `VK_NV_low_latency2` on the NVIDIA presentation path;
AMD Anti-Lag uses `VK_AMD_anti_lag`. Each rendered frame starts after vendor
sleep, marks simulation before SDL input collection, and stamps the completed
SDL pump before dispatch. AMD's INPUT stage is immediately before that pump and
its PRESENT stage uses the same frame ID at the actual Vulkan present. SDL refreshes
gamepad state before the shared input-sample marker and before dispatching events.
XeLL receives sleep, input, simulation, render and present markers while the Intel
proxy is active. Its present markers and Streamline PCL's markers bracket the
real DXGI Present on the present thread, using the frame's saved token. If
Streamline Reflex is loaded, its sleep call still runs in Off mode before XeLL;
XeLL owns active pacing on that path.

Streamline PCL loads independently of the NVIDIA plugins. On non-NVIDIA adapters
the Reflex and DLSS-G plugins are skipped so their NVIDIA Vulkan extensions do
not prevent device creation. A signed hidden-window test on Intel UHD Graphics
770 delivered all six PCL phase markers across three frames; a separate NVIDIA
test delivered those markers and one Reflex sleep per frame. XeLL and AMD
Anti-Lag still need physical-device gameplay timing trials.

Further visible-window validation remains necessary for displayed FPS, history
resets, motion-vector sign and scale, and UI recomposition after transitions. The
FidelityFX and XeSS SDK counts confirm generated presents during headless world runs,
but a hidden window cannot establish what a monitor actually displayed.

The packaged Windows SDL client now uses native runtimes beside the executable even
when the managed renderer loads from the launcher's patched cache. A focused visible
DLSS-G world continued generating at 2× after resize, fullscreen/windowed switches,
and minimize/restore. A physical F12 hotkey saved an intact rendered gameplay image
after those transitions. The SDK present count verifies generation; that screenshot
captures the rendered frame rather than an interpolated display frame.
