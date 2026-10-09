# FSR 4 interop compile group

Date: 2026-09-30. Implementation only; no build, test, probe, package or game run.

## Source changes

- `IUpscalerDevice` now includes the retained shared-frame creation and FSR 4
  evaluation operations. `Fsr4Backend` keeps that interface instead of casting
  to the excluded full device. Staged device forwarders call the original methods.
- The compile list includes `IDx12SharedRuntime`, `VulkanSharedImage`,
  `XessSharedFence`, `Fsr4Runtime`, `Fsr4SharedFrames` and `Fsr4Backend`.
  An explicit Vulkan semaphore alias resolves the SDK type against implicit
  framework imports. These support files retain their original algorithms.
- `native/fsr4/bridge.cpp` and its explicit-SDK `build.ps1` are copied from the
  migrated reference. Own bridge identifiers/exports and the managed runtime
  filename become `VulkanStoryFsr4`. Vendor DLL name and FidelityFX entry points
  are unchanged; the signed provider remains a separate runtime dependency.
- A managed ABI fixture checks all frame-field offsets and the native x64
  size of 96 bytes, including ready/done values at 80/88. The native bridge's
  original size/ready-offset static assertion is retained. The fixture has not run.

## Preserved behavior and provenance

Baseline: `386e0d05386d0b228b439d09aeca851428f7bbf3`,
`Optimum.Render.Vulkan/Upscale/Fsr4*`, `Present/IDx12SharedRuntime.cs`,
`Present/VulkanSharedImage.cs`, `Present/XessSharedFence.cs`,
`VulkanDevice.Fsr4.cs`, and `native/optimum-fsr4`.
Managed changes are mechanical/adapter changes; native changes are mechanical.
Keep inherited attribution and license scope from `porting/provenance`.

The three image sets, RGBA16F color/output, D32 depth, RG16 motion, dedicated
Win32 imports, external queue-family barriers, timeline ready/done values,
partial submissions and drain-before-release behavior are preserved. The
staged evaluation method still copies input images, submits Vulkan readiness,
dispatches DX12, and waits for DX12 completion before copying output back.
No shader, SDK version, temporal convention or resource layout changed.

## Acceptance and next validation

Plan one bounded batch of Release backend tests and the preflight-tool build.
This checks the expanded compile group and managed ABI; it does not invoke an
FSR 4 SDK feature. Native bridge compilation/export checks need declared SDK
paths and remain a separate recorded gate. The full device/forwarders and
`VulkanDevice.Fsr4.cs` remain excluded; their compilation and execution are open.

Real supported AMD hardware, signed runtime loading, shared images/fences,
actual temporal producers/scene reconstruction, resize/switch/fallback,
presentation and shutdown remain unverified in the new host. No runtime
extent logs, SR pixel comparisons or game/provider commands were produced.
All renderer, SR/FG/latency, SDL, game-routing and release milestones stay open.

The [first bounded validation](validation-p0-fsr4-interop-2026-09-30-01.md)
passed 69 backend cases and a clean tool build. Managed frame layout and expanded
component compilation now have evidence; native build/exports, complete device
forwarding and actual SDK/shared-resource execution remain open.
