# Vulkan, upscaling, and frame generation profile — 2026-09-26

## Capture setup

The packaged Windows client ran the same saved 1920 × 1080 scene on an RTX 4070 Laptop GPU (2580 MHz during samples). VSync was off and `maxFps` was 241. Each capture used an isolated copy of the same save and settings, a fixed 16.667 ms simulation delta, and 3,000 rendered frames. The table reports the final 15 one-second Vulkan statistics windows after pipeline compilation settled. GPU utilization comes from `nvidia-smi`. SR runs used Quality mode (1280 × 720 input); FG runs used 2× and a visible window because the SDK presenters require one. The sample scenes are not pixel-identical across runs as world streaming and animation continue, so small differences are noise. The first native run had streaming hitches; the repeat is the control.

The captured raw statistics, process logs, GPU telemetry, and analysis script are under `.tools/profile-20260926/` (ignored local artifacts). `analyze.py` reconstructs throughput from wall-clock Vulkan stats, not the fixed-delta FPS log. One 12-second native process trace is in `native-cpu-01/cpu.speedscope.json`; that trace overlaps asynchronous sound loading and is suitable for identifying call paths, not precise per-method exclusive CPU cost.

An attempted 1280 × 720 native capture was excluded because the game restored `screenWidth` and `screenHeight` to 1920 × 1080 at startup. It cannot support a resolution-scaling conclusion.

## Measured throughput

| Path | Render FPS | Display FPS reported by SDK | Frame ms | p95 / p99 ms | Frame wait ms | Present CPU ms | GPU use |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Vulkan repeat | 117.8 | — | 8.49 | 9.89 / 11.03 | 4.87 | 0.14 | 100% |
| DLSS SR Quality | 99.0 | — | 10.10 | 11.57 / 13.57 | 6.82 | 0.15 | 100% |
| XeSS SR Quality | 99.8 | — | 10.02 | 11.63 / 13.85 | 6.46 | 0.16 | 100% |
| FSR3 SR Quality repeat | 102.3 | — | 9.78 | 11.07 / 12.87 | 6.41 | 0.15 | 100% |
| DLSS SR + FG 2×, active window | 73.5 | 147.1 | 13.60 | 14.19 / 14.87 | 0.02 | 0.61 | 98% |
| XeSS SR + FG 2× | 74.4 | 148.8 | 13.44 | 19.38 / 21.13 | 0.02 | 10.30 | 96% |
| FSR3 SR + FG 2× | 87.2 | 174.4 | 11.47 | 12.06 / 13.03 | 0.01 | 8.06 | 100% |

`Frame wait` is the engine's frame-slot pacing wait; it is already included in frame time. Display FPS is the SDK's actual presented-frame counter, not an estimate from requested multiplier. Native and SR had 0 blocking uploads in these windows. FSR3 SR's first run was only 79.0 FPS, with an 11.32 ms median and repeated 18–22 ms hitches. The repeat was 102.3 FPS without those hitches, so the first mean is not evidence of a steady FSR3 upscaler cost.

DLSS-G's 2× output began late in both full runs. About the first 1,320–1,440 rendered frames had 1× SDK presentation; thereafter the counter advanced by 240 displayed frames per 120 rendered frames. In the first run, a window resize to 1276 × 1481 was followed by 1× again. The repeat had no resize and sustained 2× through its final 15-second window, which the table reports. The fallback for a Streamline runtime that reports `maxGenerated == 0` now enables the supported 2× path, but the delayed activation and resize recovery need investigation. The first run's mixed window averaged 98.3 rendered and 132.7 displayed FPS and should not be used as steady throughput.

FSR4 could not be benchmarked on this NVIDIA GPU. Its unavailable-provider fallback was exercised; its DX12 interop path remains a code-level cost model until tested on supported AMD hardware.

## Native pipeline and CPU

The native control averaged about 103 passes, 354 image barriers, 122 barrier commands, 165 native draws, and 3 compute passes per frame. GPU utilization was 100% with about 4.9 ms of frame-slot wait and only 0.14 ms in native present. This points to GPU completion/pacing as the principal throughput limit in this scene. A 12-second .NET trace found `BeginNativeDraw`, descriptor binding, pass preparation, and barrier flushing on the render thread; its nested wall times are not additive and cannot isolate driver time. The renderer's own latency counters put render/submit CPU at 3.19 ms per frame in the native control.

The pipeline-bind cache added in this profile skips a graphics bind when the pipeline handle is unchanged within the same command-buffer recording. It uses `FrameSlot.RecordingSerial`, so recycled command buffer handles do not carry stale state. The post-change native run was 116.6 FPS and 2.95 ms render/submit CPU versus 117.8 FPS and 3.19 ms before; scene variation prevents claiming a throughput improvement. It removes redundant driver calls and passed a full scene capture.

## Code-level cost candidates, in priority order

1. **FG present/interop scheduling.** XeSS FG spends 10.3 ms per rendered frame in the present path and FSR3 FG spends 8.1 ms. That is the largest measured CPU-side difference. XeSS currently tags four resources with `XEFG_SWAPCHAIN_RV_ONLY_NOW`; [Intel documents that this requests an internal copy for each tagged resource](https://www.intel.com/content/www/us/en/developer/articles/technical/xess-fg-developer-guide.html#resource-tagging). Changing to `UNTIL_NEXT_PRESENT` requires shader-resource state and proof that the rotating shared images remain owned and alive through the SDK's fence timeline. FSR3 leaves `allowAsyncWorkloads` disabled despite an async queue; [AMD describes possible overlap with greater memory use](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-interpolation/). Timestamp copies and queue work before either change.
2. **Image barriers.** About 340–354 image barriers and 119–126 barrier commands occur each rendered frame across these paths. `OpenSampling` transitions all nonattachment framebuffers for arbitrary mod stages. [Khronos recommends narrow stage/access scopes and eliminating unnecessary barriers](https://docs.vulkan.org/samples/latest/samples/performance/pipeline_barriers/README.html); a per-resource barrier trace is required before reducing this conservative behavior.
3. **Upscaler transfers.** XeSS, FSR3, and FSR4 share a motion RGBA16F → RG16F blit every frame. The SR path also blits display-size depth for late overlays. FSR4's DX12 bridge copies color, depth, and motion into shared images and copies output back, with two extra Vulkan submits and DX12 waits/signals. At 1080p Quality the full-image ingress/egress traffic alone is at least roughly 63 MB per frame, excluding conversion and SDK work. Directly writing the expected formats or avoiding depth work when no overlay consumes it may help; GPU timestamps are needed to rank those edits. [AMD's FSR input guidance](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-upscaler/) and [Intel's XeSS input guidance](https://www.intel.com/content/www/us/en/developer/articles/technical/xess-sr-developer-guide.html) constrain any format changes.
4. **Descriptor setup.** `BindProgramSets` and `BindStorageSet` allocate small managed arrays and construct descriptor keys per native draw, even when the cached set is reused. The CPU trace includes these paths, but no allocation-rate measurement was taken. [Khronos' descriptor sample](https://docs.vulkan.org/samples/latest/samples/performance/descriptor_management/README.html) supports caching immutable descriptor sets; changes here must preserve keys and resource lifetime.

No per-frame `vkDeviceWaitIdle` was found in the render paths. GPU utilization and whole-frame timing identify the broad limit, but no GPU timestamp or vendor GPU pass trace was available in this environment, so the exact cost of any pass, blit, or barrier remains unmeasured.

## Correctness fixes encountered during profiling

The map page renderer called a scalar OpenGL uniform setter for a Vulkan sampler, which crashed world rendering before benchmarks could begin. It now makes that call only on OpenGL; Vulkan uses its explicit texture binding. The DLSS-G state check now tolerates the installed Streamline runtime's zero `maxGenerated` field and allows its verified 2× mode. The GUI options patch no longer depends on a compiler-generated lambda donor method, and its patch hunk lengths were corrected. An unavailable FSR4 provider initially triggered an early framebuffer/shader reload during the loading screen; the fallback rebuild now waits until initial shaders are loaded and avoids recompiling them if the fallback happened before that load. These fixes made the profiling matrix runnable; they are not counted as performance gains.

The renderer suite completed with 581 passing, 13 skipped, and 2 failing real-window resize cases. Both failures are synchronization-validation `PRESENT_AFTER_WRITE` reports on the swapchain's final layout transition. The normal present path was not changed in this work, and a [Khronos validation-layer issue documents a similar semaphore/present report](https://github.com/KhronosGroup/Vulkan-ValidationLayers/issues/7479), but that does not prove these two reports are false positives. They remain an unresolved validation finding.

## Correction: GPU timestamps and chunk mesh placement (same day)

The captures above were not at native 1080p. `capture.ps1` wrote `screenWidth`, `screenHeight` and `ssaa` as top-level keys of `clientsettings.json`; the client keeps them under `intSettings` and `floatSettings`, so the template's `ssaa: 0.5` applied to every run. The "native" rows rendered at 960 × 540 and the SR rows at 1280 × 720, so "SR is slower than native" compared different resolutions. The script now writes the nested keys.

The stats log now has a `stats.gpu` line (`Frame/GpuTimestamps.cs`): GPU milliseconds per frame for each render stage, chunk pass, post step, upscaler and FG section, and triangle counts for multi-draws. It is on with `OPTIMUM_VULKAN_STATS`; set `OPTIMUM_VULKAN_GPU_TIMESTAMPS=0` to turn it off. It measures only the graphics queue. SDK queues and the DX12 interop side are excluded.

At true 1080p the GPU frame was 10.3 ms (97 FPS), and 7.3 ms of that was chunk passes: `chunk_opaque` drew 488 k triangles in 2.9 ms (~170 M tri/s), and the depth-only shadow passes ran at ~95 M tri/s. The chunk cost did not change between 540p and 1080p. Persistent (game-mapped) meshes, including every chunk pool, were allocated in host-visible system memory, so each vertex fetch crossed PCIe. With resizable BAR (a device-local host-visible heap of at least 1 GiB) they now live in mapped VRAM (`MeshManager.PersistentMeshesInVram`; set `OPTIMUM_VK_PERSISTENT_MESH_VRAM=0` to opt out):

| Path, true 1080p | Before | After |
| --- | ---: | ---: |
| Native GPU frame | 10.30 ms (97 FPS) | 5.09 ms (196 FPS) |
| `chunk_opaque` | 2.92 ms | 0.62 ms |
| All chunk shadow passes | 1.75 ms | 0.86 ms |
| DLSS-SR Quality GPU frame | 10.07 ms (99 FPS) | 4.74 ms (210 FPS) |

The "Present CPU ms" column under FG is blocking in the SDK present, not CPU work. Frame-slot waiting fell to ~0 ms and present time rose by the same amount. FG cost should be read from frame-time deltas and GPU sections, not from that column.

### Follow-up: GTAO prefilter, foliage alpha test, and FG at true 1080p

`gtao_prefilter` took 0.20 ms because one invocation built a whole 16 × 16 block (256 serial fetches, per-invocation arrays in local memory; ~130 waves at 1080p). It now uses XeGTAO's layout: one 8 × 8 group per block, 2 × 2 quads per invocation, and levels 2–4 reduced in shared memory between `barrier()` calls. It takes 0.04 ms. `TheWorkingDepthLevelsFollowTheXeGtaoMipFilter` checks every level against a CPU reference of the filter.

`chunkopaque` ran its alpha test after 18 shadow-map taps, fog, and sky colour, none of which change alpha. The test now runs right after the texture fetch and uses `demote` (Vulkan 1.3 `shaderDemoteToHelperInvocation`, now a device requirement), so later implicit-LOD lookups keep defined derivatives. `chunk_blendnocull` fell from 1.03 to 0.69 ms. At equal frame rate, frames differ from the old shader by less than the old shader's run-to-run noise.

Native true 1080p after these changes: 4.55 ms GPU frame (~220 FPS), down from 10.30 ms.

DLSS-G generated no frames in unattended visible captures because Streamline logs `DLSS-G disabled: window not focused` (verbose log, `OPTIMUM_STREAMLINE_VERBOSE=1`). That also explains the earlier late activation and the return to 1× after resize. `capture.ps1` now focuses the game window when the world is ready. FG runs at true 1080p, SR Quality, 2×, focused window:

| Path | Render FPS | Display FPS | p50 / p99 ms |
| --- | ---: | ---: | ---: |
| FSR3 SR + FG | 167 | 334 | 6.0 / 7.8 |
| DLSS SR + DLSS-G | 129 | 258 | 7.7 / 8.9 |
| XeSS SR + FG | 114 | 229 | 8.0 / 13.9 |

XeSS FG pacing is bimodal (p95 13.1 ms against p50 8.0 ms) and remains open.

### Follow-up: XeSS FG pacing, shadow alpha test, and remaining sections

**Benchmark hazard.** Between about 18:33 and 18:50 the machine sometimes ran at half rate, with ~500 ms/s blocked in `vkQueuePresentKHR`. It affected old and new shaders equally and cleared on its own; a later run of the same build gave 202–220 FPS. When a stats window shows `present_ms` in the hundreds without FG, discard the capture. GPU-timestamp sections are not affected.

**XeSS FG pacing (open).** Under `OPTIMUM_XESS_FG_TIMING=1` the bridge prints per-phase present timing to stderr. All the time is inside the proxy's DXGI `Present`: mean 5.5 ms, max 12.8 ms. The allocator ring, recording, submit, retire and XeLL sleep phases each take under 0.1 ms. GPU utilisation is ~68%, against 85% for FSR3 FG and 93% for DLSS-G, so the SDK's non-Intel pacer leaves the GPU idle. Neither setting `frameRenderTime` nor turning XeLL off changed p50/p95 (~8 / 13 ms). The next step needs a GPU timeline across the Vulkan and DX12 queues (Nsight Systems or GPUView). Clean FG reruns matched the table above.

**Shadow alpha test.** `chunkshadowmap` samples and may discard for every pass, which disables early depth. Without the discard, `chunk_shadow_opaque` fell from 0.45 to 0.25 ms, but frame span did not measurably change. Opaque-pass content can have cut-out texels, so that experiment was reverted. A content audit (which opaque-pass textures have alpha below 0.15) would decide whether an opaque shadow variant without discard is safe.

**Remaining sections at native 1080p (4.49 ms span).** A native pass opened under a stage-level section is now labelled `np_<pass>`: GUI quads 0.22 ms, sky 0.18 ms, sun 0.07 ms. Chunk passes are ~2.9 ms of the frame including shadows. Every other section is under 0.45 ms.

**Stray OpenGL call (fixed).** Every Vulkan session logged `Mod exception during event LevelFinalize` from `GL.DeleteTexture` in `FluffyClouds.CloudRendererMap.FreeGlResources`, and clouds never rendered on Vulkan. `mod-patcher.cs` transplants the cloud renderer methods from the runtime donor, which is built from decompiled game code plus `patches/runtime`. Only the source-tree patch (`patches/VSEssentials/.../Newclouds`) had the device routing, so the transplant copied vanilla GL bodies. `patches/runtime/VSEssentials/FluffyClouds/` now carries the same routing against the runtime code, and clouds render (`np_cloudvolumetric` ≈ 0.11 ms). `EveryTransplantedMethodHasARuntimePatch` fails when a transplanted method's type has no runtime patch.

## XeSS FG pacing (fixed)

Measured on an Optimus laptop (RTX 4070 Laptop renders, Intel iGPU scans out), with PresentMon 2.6 and DX12 timestamps from the bridge (`OPTIMUM_XESS_FG_TIMING=1`; its stderr dump every 120 presents itself causes a hitch, so use it only for diagnosis).

- The proxy `Present` blocked in a strict 2 ms / 10 ms alternation, the GPU was ~68% busy, and on-screen real frames were bimodal (1–2 ms vs 6–7 ms displayed).
- The DX12 queue carried ~5.4 ms of SDK work per frame. Run alone, with the Vulkan queue idle (`OPTIMUM_XESS_FG_SERIALIZE=1` with `OPTIMUM_XESS_PRESENT_THREAD=0`), the same work takes 2.0 ms. The Vulkan and D3D12 contexts time-slice the GPU, so overlapping them cost ~3.4 ms per frame. Our copies, including the SDK's `ONLY_NOW` tag copies, are 0.13 ms, so the resource-validity change suggested above would not help.
- XeLL cannot pace this path: it sees only the DX12 device's work, never the Vulkan rendering, and its sleep stays at ~0 ms. `frameRenderTime` and XeLL on/off made no difference.

Fix:

1. **GPU gate.** Each frame's first Vulkan submission waits on the GPU for the previous frame's DX12 "done" value (`FrameRing.GateNextFrameSubmit`), so the two contexts no longer overlap. The bridge signals "done" on every call, failed ones included, after a queue wait on "ready" so the shared timeline stays monotonic. The presenter host-signals the last gated value on dispose, so `WaitDeviceIdle` cannot stall. Opt out with `OPTIMUM_XESS_GPU_GATE=0`.
2. **Present thread.** The proxy `Present` runs on a worker (`XessFgPresenter`), one frame in flight, so the render thread records the next frame while the SDK paces. XeLL present markers come from the worker. Opt out with `OPTIMUM_XESS_PRESENT_THREAD=0`.

| XeSS FG, 1080p SR Quality 2× | Before | After |
| --- | ---: | ---: |
| Render / display FPS | 114 / 229 | 143 / 286 |
| Render frame p50 / p95 / p99 | 7.2 / 13.3 / 13.9 ms | 7.0 / 7.35 / 7.5 ms |
| Displayed time p95 / p99 (PresentMon) | 7.6 / 10.0 ms | 4.9 / 5.5 ms |
| Real-frame displayed time, sd | 3.8 ms | 0.96 ms |
| Display latency mean / p99 | 17.2 / 23.2 ms | 14.3 / 17.5 ms |

The present thread alone gave ~118 / 236 FPS; the gate is the main win.

## Input latency markers (open)

`LatencySleep` runs inside the render callback, after OpenTK has already pumped window input, so Reflex, XeLL and Anti-Lag sleep between input sampling and the simulation that consumes it. `InputSample` is marked together with `SimulationStart` and measures nothing. Streamline PCL's `statsWindowMessage` ping, which is PCL's input-latency measurement, is not handled. Planned fix: pump GLFW events again after the vendor sleep and mark input there; subclass the window procedure in the native bridge to answer the PCL ping with `ePCLatencyPing`.
