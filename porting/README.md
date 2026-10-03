# Source migration inventory

## Current implementation override — 2026-10-01

The inventory below records chronological import checkpoints. Statements about
excluded/pending backend/device/provider code refer to those earlier increments.
The active backend compile list now includes the retained full device, resource,
frame graph, shader/transfer and provider modules. Platform.Sdl compiles the four
game-neutral window/event/coordinate/native-loader files; game-facing controller,
touch, key-map and OS-service glue is compiled under Game/Input or Game instead
of introducing game references into SDL. The Game project uses the ordinary
default compile set and official game references, with sidecars/Harmony replacing
donor overrides and injected members.

All five named native bridges and the full shader corpus have build evidence.
Current Game integration includes declared passes, GUI/controller glyphs and AVI
readback; source provenance/legacy notices remain retained. Use
[current feature status](../docs/current-port-status.md) to distinguish actual
compiled/running scope from outstanding feature/platform acceptance. This update
does not imply every imported feature has been exercised or accepted.

2026-09-30 source continuation: `src/VulkanStory.Game/TemporalFrameState.cs`
migrates the retained API temporal frame/math source into mod-owned state,
omitting injected static holders. `RuntimeFrameGeneration.cs` migrates the
retained platform FG host; `RuntimeUpscalers.cs` migrates its SR registry.
The session connections and unverified limits are recorded in
[the provider implementation record](../docs/runtime-providers-implementation.md).
Existing provenance/license scope remains applicable; no SDK versions changed.

The retained `sources/shaders/ui-compose.vsh` and `.fsh` are now copied unchanged
to `src/VulkanStory.Game/Shaders/` and embedded for the adapter-owned UI program.
`GameGraphicsAdapter.UiSeparation.cs` migrates the corresponding retained
platform UI bodies. See [UI source status](../docs/ui-separation-implementation.md).

The retained `sources/shaders/taa-resolve.{vsh,fsh}` and
`taa-sharpen.{vsh,fsh}` are also copied unchanged into
`src/VulkanStory.Game/Shaders/`. Their owned programs, history decisions and
native/stated draw seams are recorded in [TAA source status](../docs/taa-implementation.md).

The retained `sources/shaders/scene-ssao.{vsh,fsh}` is copied unchanged into the
same embedded shader directory. GTAO/native SSAO host migration and post entry
are recorded in [AO source status](../docs/ambient-occlusion-implementation.md).

Retained `fsr-easu`, `fsr-rcas` and `taa-debug` vertex/fragment shader sources are
copied unchanged into the embedded shader directory. Final presentation and
post-group source status are recorded in [the blit record](../docs/final-blit-implementation.md).

Imported on 2026-09-29 from `D:\Coding\VulkanStory` at revision `386e0d05386d0b228b439d09aeca851428f7bbf3`. The source checkout was clean during the import. Files were copied without namespace, algorithm, shader, SDK, or resource-layout edits. This is a source migration, not a working renderer integration yet.

| New location | Source location | Files | Current role |
| --- | --- | ---: | --- |
| `src/VulkanStory.Render.Vulkan/` | `Optimum.Render.Vulkan/*.cs` plus `AmbientOcclusion`, `Core`, `Frame`, `Graph`, `Latency`, `Present`, `Shaders`, `Transfer`, `Upscale` | 118 | All staged C# namespaces have been mechanically changed to `VulkanStory.Render.Vulkan`. The backend project now includes context/resources, NGX, FSR 3 ABI, Streamline, SDL surface, descriptors and bindless table, swapchain, frame ring, texture manager, render targets/frame graph, shader translation/native library, program resources, vertex layout, and graphics/compute pipeline caches. The device facade and remaining providers await integration. |
| `src/VulkanStory.Platform.Sdl/` | Six `Optimum.Render.Vulkan/Platform/Sdl*.cs` files and the moved `Core/SdlVulkanWindowHost.cs`; one new private native resolver | 8 C# | Four game-neutral files form the SDL project: window host, event pump, coordinate conversion, and native DLL resolver. The project now declares the pinned SDL3-CS runtime package. Four game-facing input helpers remain staged but excluded from compilation pending their game-adapter port. |
| `src/VulkanStory.Platform.Sdl/assets/` | `sources/controller/gamecontrollerdb.txt`, SDL game controller license | 2 | Input data and its notice. |
| `src/VulkanStory.Contracts/` | New mod-owned provider and renderer boundary derived from legacy graphics and temporal interfaces | 4 C# | Explicit upscaler plan, current-frame provider inputs, mutable session-state interface, and neutral blend meaning. No game types. |
| `shaders/native/` | `sources/shaders-vk/` | 76 | Native shader corpus, GTAO sources and includes. Shader source content has not been changed. |
| `native/migrated/` | `native/optimum-ngx`, `optimum-streamline`, `optimum-xess-fg`, `optimum-fsr3`, `optimum-fsr4` | 12 | Existing native bridge code and build recipes. No vendor SDK binaries copied. |
| `native/ngx/` | `native/optimum-ngx` | 4 | VulkanStory-named C bridge, header, and Windows/Linux build recipes. Export and library names changed mechanically; SDK calls and ABI values preserved. Source-only until native validation. |
| `native/fsr3/` | `native/optimum-fsr3` | 2 | VulkanStory-named FidelityFX C++ bridge and explicit-SDK Windows build recipe. Export names changed mechanically; ABI fields and SDK calls preserved. Source-only until native validation. |
| `native/streamline/` | `native/optimum-streamline` | 2 | VulkanStory-named Streamline C++ proxy bridge and explicit-SDK Windows build recipe. Export names changed mechanically; proxy and frame-tag ABI retained. Source-only until validation. |
| `porting/old-platform/` | `Optimum.Render.Vulkan/Platform/*.cs` | 42 | Reference for moving override method bodies into Harmony-owned game adapters. Not compiled. Six SDL files are also staged in the future SDL project above. |
| `porting/game-adapter/` | Vintage Story enum conversions and render-stage listener from `Optimum.Render.Vulkan` | 4 | Staged game-facing graphics mappings. The compiled `GlEnums.cs` retains neutral GL constant translation; `PipelineState` uses a neutral blend meaning, and shader translation uses a neutral stage tag. Excluded device/platform call sites must move to this adapter when the game bridge is compiled. |
| `porting/renderer-tests/` | `Optimum.Render.Vulkan.Tests/**/*.cs` plus its mod fixture metadata | 84 | Existing test source for later adaptation. Focused SDL event and coordinate cases have now been ported into a separate new test project. |
| `porting/graphics-api-contracts/` | Selected `sources/VintagestoryApi/Client` graphics, temporal, motion, mod-pass and mesh contract files | 7 | Reference for new mod-owned contracts; no game DLL injection planned. |
| `porting/legacy-render-config/` | `sources/VintagestoryApi/Config/OptimumConfig.cs` | 1 | Settings/feature meaning reference; do not copy the mixed Optimum configuration system into the new runtime. |
| `porting/shader-compiler/` | `tools/shader-compiler` program and old project file | 2 | Offline compiler reference; its old `.csproj` is not part of the new solution. |
| `porting/shaderincludes/` | `shaderincludes/vertexwarp.vsh` | 1 | Legacy include reference until game shader adapter scope is settled. |
| `porting/reference-project/` | `Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj` | 1 | Pinned package/SDK reference and build-asset mapping only. It is not loaded by the new solution. |
| `porting/provenance/` | Existing license scope, legacy license and notice | 3 | Original provenance record for copied sources. Preserve notices in source files and shader includes. |

The active `VulkanStory.slnx` contains B0, contracts, SDL, and the Vulkan backend with focused test projects. SDL passed five tests in [the P0 batch](../docs/validation-p0-kernel-2026-09-29-01.md). The core/shader backend slice passed fourteen tests in [its latest batch](../docs/validation-p0-kernel-2026-09-29-02.md), and the provider contracts passed three in [their batch](../docs/validation-p0-provider-contracts-2026-09-29-01.md). The newly included NGX group and four additional test methods await validation. Most renderer files remain excluded by the explicit compile list while game state and native-runtime seams are extracted. The port must not use a patched game library or transplant project as a runtime dependency.

The NGX group and its bridge passed [their focused batch](../docs/validation-p0-ngx-2026-09-29-01.md), the FSR 3 ABI and bridge passed [theirs](../docs/validation-p0-fsr3-2026-09-29-01.md), and the context/Streamline bridge passed [their batch](../docs/validation-p0-context-2026-09-29-01.md), including a headless Vulkan device. The SDL surface passed [its focused preflight](../docs/validation-p0-sdl-surface-2026-09-29-01.md). Swapchain creation and 26 backend tests passed [the third swapchain batch](../docs/validation-p0-swapchain-2026-09-29-03.md). The newly included texture/frame/present path and `--sdl-present` preflight are source-only pending validation; see [the implementation record](../docs/present-path-implementation.md). Other upscalers, visible output, and the game frame loop remain staged.

The first swapchain compile exposed its dependency on the original descriptor key types. `DescriptorCache` and `DescriptorArena` joined the project as a coherent game-neutral group, with retained resource-age and recycled-handle tests; see [the failed batch](../docs/validation-p0-swapchain-2026-09-29-01.md).

The second swapchain compile found that the cache's storage-set descriptor-type helper lived in the still-staged shared layout. The one-line decision now has a game-neutral owner shared by cache and layout; the descriptor group stays otherwise unchanged; see [the second batch](../docs/validation-p0-swapchain-2026-09-29-02.md).

The first retained-present compile failed before tests because `TextureManager` needed `GlEnums`. Its two game-enum methods were staged under `porting/game-adapter/`, and the neutral remainder joined the backend compile list. The nullable preflight dereference was also fixed; see [the failed present batch](../docs/validation-p0-present-2026-09-29-01.md) and [the implementation record](../docs/present-path-implementation.md#compile-boundary-repair).

That repair passed [the second present batch](../docs/validation-p0-present-2026-09-29-02.md): 26 tests and a hidden SDL3 Vulkan frame through acquire, blit, and queue present. The following source group added render-target dynamic rendering, frame-graph recording, and neutral pipeline state, and changed the preflight to clear through an attachment; see [its implementation record](../docs/render-target-port-implementation.md).

The render-target group passed [its first batch](../docs/validation-p0-render-target-2026-09-29-01.md): 28 tests and a hidden dynamic-rendering target presented through the retained path. The shader translation, cache, native shader library, and repository-owned compiler tool are now in source and await validation; see [the shader implementation record](../docs/shader-port-implementation.md).

The first shader compile stopped on `TextureKind`; [the failed batch](../docs/validation-p0-shaders-2026-09-29-01.md) ran no tests or shader tool. The complete bindless table and shared layout then joined the backend, with two retained slot-lifetime cases and preflight construction; see [the bindless implementation record](../docs/bindless-port-implementation.md). The repair passed [the second shader batch](../docs/validation-p0-shaders-2026-09-29-02.md): 32 tests, a real native `ui-compose` build, and hidden SDL presentation.

The program-module, vertex-layout, graphics/compute pipeline cache, and cache-persistence group is now source-only in the backend, with a new `--sdl-draw` path; see [the pipeline implementation record](../docs/pipeline-port-implementation.md). It awaits a bounded validation turn.

The SDL window host is an **adapter** migration: its window/clipboard/IME/cursor/input and SDL Vulkan surface calls remain in place, but the VulkanContext/Silk type and Streamline branch moved to the backend-side `SdlVulkanWindowSurface`. The event decoder's physical key field is now a raw SDL scancode; the game adapter will apply the original `SdlKeyMap` when delivering game key events. Coordinate conversion uses `System.Numerics.Vector2` instead of OpenTK's vector type. These are boundary changes, not algorithm rewrites. The original versions remain in the source repository and the reference `porting/old-platform/` copy where applicable.

The Vulkan backend namespace change is **mechanical** across the 106 transplanted files. The sixteen included files preserve their resource-state, compute/frame-planning, device-requirement, latency, generated-frame pacing, GLSL parsing, SPIR-V reflection, shader manifest, and native Reflex/Anti-Lag function-table behavior. The native-runtime directory helper now prefers the private package RID directory and refuses a game-folder fallback in a deployed payload. The shader include constants point at the new source tree; no shader content changed. The color-write override environment key is now `VULKANSTORY_VULKAN_COLOR_WRITE_TIER`, a product configuration-name change. The test project carries expected behavior from the source renderer's planning, device-validation, upscaler-requirement, frame-generation, and shader tests without a GPU dependency. This does not claim that the rest of the backend compiles or that providers are attached.

Provider boundary **adapter** changes are now in staged source. `UpscalerPlan` and the old LOD-bias formula live in `VulkanStory.Contracts`; the selected provider receives the offset explicitly. XeSS retains its provider-specific bias. `TemporalProviderFrame` carries only the values the existing DLSS/XeSS/FSR evaluation bodies use, including a game-supplied inverse origin-view matrix for FSR 3 FG. The game adapter must compute that inverse with the original matrix convention after camera capture and keep its storage alive until evaluation returns. The backend's staged upscaler and FG call sites no longer name `IOptimumTemporalContext`, and DLSS session state is injected through `IUpscalerRuntimeState` instead of read from `OptimumConfig`. These edited provider files are still excluded from the current backend project and have not been built. Remaining device/statistics/XeSS-FG reads of `OptimumConfig`, plus the game-side adapter implementation and provider runtime loading, remain to be ported.

## Next integration seams

The [full-device compile inclusion](../docs/full-device-port-implementation.md)
adds all retained facade partials and remaining support dependencies, with
unchanged GTAO shader embedding and mechanically adapted timestamp fixtures.
No game/Harmony reference is added to the backend. This group is unvalidated;
remaining compile errors, game routes and native/provider acceptance stay open.

The [XeSS FG source increment](../docs/xess-fg-port-implementation.md) adds the
retained requirements/runtime/presenter compile group, copied renamed native
bridge in `native/xess-fg`, and managed frame ABI fixture. Multiplier ownership
moves from injected game configuration to the staged device's host property;
all SDK/presentation algorithms remain retained. This increment is unvalidated.

The [FSR 4 interop increment](../docs/fsr4-interop-port-implementation.md)
adds the retained runtime/shared-frame/image/fence group to the backend compile
list through `IUpscalerDevice`. `native/fsr4` contains the copied bridge/build
recipe with own identifiers/exports renamed to `VulkanStoryFsr4`; the migrated
reference is preserved. This mechanical/adapter increment and x64 ABI fixture
are source-only. Full device forwarding, native build and actual SDK/GPU
evaluation remain open.

1. Resolve the B0 bootstrap and normal-shortcut startup acceptance gate. The 2026-09-29 third batch proved native and managed early activation but Harmony binding failed before the game's own `Lib` resolver was registered. The source fix is in `BootstrapDependencies` and awaits validation; see [the third batch](../docs/validation-b0-2026-09-29-03.md).
2. Add a new backend project around the staged files, with game-neutral configuration, logging, window surface and temporal contracts. Keep method bodies and shader data stable.
3. Move the staged `VulkanClientPlatform` behavior into a game adapter that patches existing vanilla methods. Do not load it as a subclass of the sealed original platform.
4. Migrate test fixtures by feature group and reuse their existing expected values. The original renderer evidence remains the baseline; validation of the new integration must target changed boundaries.

Any later edit to an imported file should be classified as mechanical, adapter, or behavior-changing in the port review. This inventory does not claim that the staged sources compile or run in the new host.

The game-facing SDL input extraction now lives in `src/VulkanStory.Game/Input`: physical scancode mapping, touch tap/drag/hold, key/button source arbitration, and focus-loss release behavior. Cached original field/callback bindings replace injected game members. This is an unvalidated adapter increment; the static profile tool includes new binding metadata checks. See [the implementation record](../docs/sdl-input-port-implementation.md). The SDL sidecar, active startup routing, GUI IME coordination, and controller mapper remain open.

`GamePlatformAdapter` now adapts the retained SDL event/frame loop around the original platform, with transaction gating, DPI/input/IME filtering, resize/close/focus callbacks, and graphics-before-window teardown. This increment is source-only and has no live startup caller. See [sidecar implementation](../docs/sdl-sidecar-implementation.md).

The retained `Core/MeshManager.cs` now joins the backend compile list via neutral custom-layout/draw/conversion contracts. Allocation, SSBO layout, mapped/staged writes, deletion and indexed/indirect methods remain retained; `GameMeshLayout` converts official metadata. Three original range cases, four boundary cases and `--mesh-buffers` are source-only pending validation. See [mesh implementation](../docs/mesh-port-implementation.md). Game mesh routing and actual draws remain open.
