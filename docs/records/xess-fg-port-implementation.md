# XeSS FG presenter compile group

Date: 2026-09-30. Implementation turn; no builds, tests, probes, packages or game runs.

The retained `XessFgInteropRequirements`, `XessFgRuntime` and `XessFgPresenter`
join the backend compile list, reusing the now-compiled DX12 runtime interface,
Vulkan shared images and shared fence. These files have no game/Harmony dependency.
The runtime loads `VulkanStoryXessFg.dll` and matching renamed bridge exports.
`native/xess-fg` carries the copied C++ bridge and explicit-SDK build recipe;
vendor names and entry points are retained. Diagnostic keys use
`VULKANSTORY_XESS_*`, including the present-thread switch.

The excluded device's XeSS path now reads its host-owned
`FrameGenerationMultiplier` property instead of injected `OptimumConfig`.
Default 2 and the original `Clamp(multiplier - 1, 1, 5)` request are retained;
the SDK still reports effective/maximum counts. Settings integration must supply
the host value when connected. This removes the last executable game-config
reference found in renderer source; it does not establish full device compilation.

## Retained behavior and provenance

Baseline revision: `386e0d05386d0b228b439d09aeca851428f7bbf3`.
Original paths: `Optimum.Render.Vulkan/Present/XessFg*.cs`,
`VulkanDevice.XessFg.cs`, and `native/optimum-xess-fg`.
Names/diagnostic keys are mechanical changes; multiplier ownership is an adapter
change. Keep inherited file provenance and license scope.

Three shared image sets, flipped input copies, ready/done fence sequence,
render queue gating, asynchronous proxy present, PCL token/frame pairing,
XeLL marker order, outcome handoff and stop/drain/destruction behavior remain
unchanged. The successful path still requires exclusive DXGI ownership of the
actual SDL HWND; the staged device retains Vulkan swapchain restoration.

An unrun x64 ABI fixture checks the original 216-byte presentation frame and
all field offsets, including 16-float matrices at 56/120 and fence values at
200/208. Native size/ready-offset assertions are retained. No fixture instantiates
the present thread or claims successful vendor initialization.

## Next validation and open gates

Plan one bounded batch of Release backend tests and the preflight-tool build.
That proves expanded managed compilation and ABI expectations only. Native
bridge build/exports require declared SDK paths and separate evidence.
No SDK initialization, shared resource/fence execution, actual SDL HWND proxy
present, multiplier switching, resize/focus/fallback, timing/marker validation,
scene/UI inputs, shutdown or new-host hardware coverage occurred this turn.
The complete device and game frame/settings/presentation wiring remain excluded
or unconnected. Full renderer, all SR/FG/latency, SDL and release remain open.

The [first bounded validation](validation-p0-xess-fg-2026-09-30-01.md) passed
70 backend cases and a clean tool build. Expanded managed compilation and
presentation-frame ABI now have evidence; native bridge/SDK, present thread,
shared-resource execution, excluded device and game ownership remain open.
