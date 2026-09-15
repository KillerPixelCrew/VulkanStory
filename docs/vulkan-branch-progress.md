# feat/vulkan-taa: handoff

Everything needed to continue the Vulkan branch on another machine. Last updated 2026-09-15 at 11195c5.

- Plan of record: `docs/vulkan-native-plan.md` (decisions 1-9, phases, risks).
- Research the designs follow: `docs/research/` (caching, descriptor model, bindless, XeGTAO, validation).
- Acceptance procedures: `docs/vulkan-acceptance.md`, `docs/taa-acceptance.md`, `docs/temporal-frame-contract.md`.
- Older planning documents still in the tree: `VULKAN-BACKEND-PLAN.md`, `TAA-PLAN.md` (history; the plan of
  record supersedes them where they disagree).

---

## 1. Context

**Upstream.** StratumServer/Optimum PR #69 (owner: NightHammer1000) was split: the maintainer wants the
Vulkan backend landed first, so this branch carries Vulkan + TAA only, without the DLSS/frame-generation
work (branches `feat/dlss`, `feat/dlss-g` keep that). The owner handles all upstream communication and the
PR itself; do not push to upstream or touch the PR.

**Base.** Branched from the last TAA-only commit before the DLSS work (9ad0c70 after the identity rewrite).

**History rewrite that already happened.** All 14 fork branches had their author identity rewritten to
NightHammer1000 <nightstorm@kpc.bz> on 2026-09-15. A first attempt also re-created upstream's signed
commits, which broke the common history with StratumServer:main and closed PR #69 irrecoverably; the redo
excluded upstream history. Binding from now on: no rewrite or force-push without the owner's explicit OK,
restricted to the commits that need it (`--not <upstream ref>`), and with `git merge-base` against upstream
compared before and after for every branch.

## 2. Working rules

- **Research first.** Before each major piece, research current best practice online (Khronos spec,
  guide and samples, vendor guidance, shipped engines such as DXVK, Godot, Unreal, Bevy), write the result
  to `docs/research/<topic>.md` with citations, commit it, and state the design with its sources before
  writing code.
- **Commit each verified step, push regularly.** Verified means the relevant tests ran.
- **Identity.** `git config --global user.name NightHammer1000` and
  `git config --global user.email nightstorm@kpc.bz` on every machine before committing. The notebook is the
  machine that previously committed with a wrong identity; check it first.
- **No tooling or assistant attribution** in code, comments, docs, tests, scripts or commit messages, and
  no co-author trailers.
- **Genuine decisions go to the owner** (forks between plan and research, system installs, anything
  outward-facing).
- **Keep the to-do list** in section 5 current and commit it as items change state.

## 3. Repository essentials

- **Game code is decompiled and patched.** `scripts/bootstrap.sh` (or `scripts/bootstrap.ps1 -Refresh` on
  Windows) downloads the client, decompiles it into `build/VintagestoryLib`, clones the forks and applies
  `patches/*.patch`. `scripts/extract-patches.sh` regenerates the patches from the working tree,
  `scripts/check-patches.sh` verifies them.
- **Fork sources live at the repo root** (`VintagestoryApi/`, ...); extraction copies them into `sources/`
  and overwrites anything edited there. Always edit the root fork tree, never `sources/<fork>`.
- **Cecil transplant patcher** (`Optimum.Patcher/Program.cs`): every member injected into the client
  (fields, methods, shader program entries) has to be listed in its members/targets lists, and transplanted
  code must avoid cached lambdas and LINQ predicates (`Optimum.Tests/cecil-transplant-lambda-tests.cs`).
  Client members the Vulkan platform reads also go into `VulkanClientPlatform.ExpectedWindowsMembers`, and
  new shader files into the packaging scripts.
- **Build and deploy:** `make build`, `make deploy` (Cecil-patched DLLs into the vanilla client), `make run`.
  `make check` reports missing tools.
- **Tests:**
  - `dotnet test Optimum.Render.Vulkan.Tests -c Release` - the GPU suite (real device; the validation layer
    is used when installed).
  - `dotnet test Optimum.Tests -c Release` - source, patch, shader and script coverage.
  - `make test` - Optimum.Tests plus the launcher tests.
- **Headless render harness:** `scripts/dev/headless-capture.sh --renderer vulkan|opengl --world <save>
  --out <dir> [--commands <file>] [--count <n>]` (real client, hidden window, frames written by the client;
  compare with `scripts/dev/ssim.py`). Needs a logged-in game install and a save.
- **Diagnostics environment variables** (Vulkan):
  - `OPTIMUM_VULKAN_VALIDATION=1|<log path>`, `OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best,mobile,gpu,gpu-only`
  - `OPTIMUM_VULKAN_STATS=<file>`, `OPTIMUM_RENDER_TRACE`
  - `OPTIMUM_VULKAN_SHADER_CACHE=<path>|0`
  - `OPTIMUM_VULKAN_FRAMEGRAPH=0`, `OPTIMUM_VULKAN_ALIAS=1`, `OPTIMUM_VULKAN_COLOR_WRITE_TIER=enable|mask|pipeline`
  - `OPTIMUM_VULKAN_NO_MEMORY_BUDGET=1`, `OPTIMUM_VULKAN_NO_REBAR=1`, `OPTIMUM_VULKAN_POISON`, `OPTIMUM_VULKAN_CHECKPOINTS`

### Renderer layout (Optimum.Render.Vulkan)

- `VulkanDevice.cs` (about 3400 lines, to be split in the refactor): the device behind the patched
  platform; program link, uniform writes, descriptor binding, draw, present, teardown.
- `Core/VulkanContext.cs`: instance (validation via `VK_EXT_layer_settings`), device selection, feature
  negotiation, capabilities (incl. vendor, device, driver version, pipeline-cache UUID).
- `Core/PipelineCache.cs` (`GraphicsPipelineCache`), `Core/PipelineCacheFile.cs`, `Core/CacheFileWriter.cs`.
- `Core/DescriptorCache.cs`, `Core/DescriptorArena.cs`, `Core/ShaderProgramResources.cs` (today: set 0 frame,
  1 samplers, 2 storage, 3 program blocks, one layout per program - what decision 9 replaces).
- `Core/TextureManager.cs` (texture ids with a free list and deferred deletion; `Rebind`/`RestoreBindings`
  for transient aliasing), `Core/MeshManager.cs`, `Core/RenderTargetManager.cs`, `Core/FrameRing.cs`
  (timeline semaphores, uniform ring with dynamic offsets), `Core/GlStateTracker.cs` (GL emulation, to be
  removed in Phase 3b).
- `Graph/`: streaming frame graph, transient allocator, feedback copy pool.
- `Shaders/`: runtime translation GLSL 330 -> 450 (`GlslParser` -> `ProgramInterfaceLayout` ->
  `ShaderRewriter` -> `ShaderCompiler`/shaderc), `FrameGlobals.cs` (shared frame block, include-owner rule),
  `ShaderBinaryCache.cs`.
- `Platform/VulkanClientPlatform.cs`: the substituted client platform.

## 4. Knowledge that is not obvious from the code

- **Temporal stability (the whole-frame jitter).** Symptom: with TAA the entire image appeared to shift a
  few pixels per frame in random directions, worst toward the horizon. A 3x3 nearest-depth test only masked
  it. The real fix (41373cf, backported from `feat/dlss`): AO computed from the jittered G-buffer is
  composed into the scene before the TAA resolve (`ApplyOptimumSceneSsao`, `scene-ssao` shaders,
  `optimumSsaoInScene` in `final.fsh`), and the SSAO dither advances per frame when TAA is on. Any new AO
  (XeGTAO) must follow the same placement.
- **Jitter convention:** `P[8] -= 2*jx/W`, content moves by +JitterPx; resolve in `taa-resolve.fsh`.
- **Shared frame block** (a41efce): uniforms written by `ShaderProgramBase.Use()` live once in set 0
  (`FrameGlobals`), placed only for programs that include the owning include file; frame locations start at
  `1 << 28`.
- **Caches** (618a0b1): SPIR-V key = format version + compiler options + SHA-256 of the loaded shaderc
  binary + stage + rewritten source; one pipeline cache file per GPU, checked against vendor, device, driver
  version, pointer size, UUID and the blob's own header; empty cache on any mismatch or driver rejection;
  saved at device dispose; stored under `GamePaths.Cache/optimum-vulkan`.
- **Headless capture pixel order:** the client-side default framebuffer is BGRA
  (`OptimumDefaultFramebufferIsBgra`); `Leaf.ReadDefaultFramebuffer` swaps unless the device colour format is BGRA.
- **Windows bootstrap pitfalls** (fixed in 19d101e, keep in mind): CRLF in fork refs, a PowerShell module
  shadowing `Expand-Archive`, innounp overwrite prompts, `Tee-Object` masking exit codes.

## 5. Status and to-do

### Done

| Commit | What |
|---|---|
| 41373cf | AO into the scene before the TAA resolve; per-frame SSAO dither (temporal stability) |
| 766aada | Headless render harness backport |
| f83bda2 | Plan of record with the full-native roadmap |
| 19d101e | Review blockers: shaderc in the app root, prime-run guard, numpy probe, Windows bootstrap |
| a41efce | Shared frame block (set 0 `FrameGlobals`), sets reordered |
| 84f4893 | Docs follow the history rewrite |
| 9b52768 | Research notes: caching, descriptor model, XeGTAO, validation |
| 618a0b1 | Persisted SPIR-V cache and driver pipeline cache |
| 5bc337b | Plan decision 9 (one pipeline layout, bindless now); set-convention table and mod adapter rewritten |
| 31684e3 | Validation extra checks via `VK_EXT_layer_settings`, layer version and applied settings logged |
| a9d0218, 11195c5 | Handover note (this file) |

Upstream review blockers: shaderc placement, prime-run, numpy, Windows bootstrap - fixed. Swapchain resize
tests passed on Windows without the layer (the reviewer saw failures on Linux/MX150; recheck there). Donor
drift `TaaRuntimeDonorCoverageTests` 25/25 at this base (recheck whenever patches change). `vkDeviceWaitIdle`
before window release was already correct.

### Test state (Windows machine, 2026-09-15)

- GPU suite with the Vulkan SDK 1.4.357.0 validation layer: 661 tests, 6 failures.
  - `PacingStatsTests.PacingGateReadsTheLinesThisBackendWrites`: host issue (WSL path translation), not a
    renderer defect.
  - Five new, all `SYNC-HAZARD-PRESENT-AFTER-WRITE` ("no sufficient synchronization is present to ensure
    that a swapchain present operation does not conflict with a prior layout transition"):
    `PresentDecouplingTests.RecordingTimeDoesNotGrowWithTheInjectedAcquireDelay`,
    `SwapchainRecreationTests.AHiddenWindowResizeLoopRecreatesWithoutWaitingAndStaysClean`,
    `SwapchainTests.TogglingVsyncRebuildsTheChainCleanly`,
    `SwapchainTests.ADeviceComesUpAgainstARealWindowAndPresentsFrames`,
    `SwapchainTests.ResizingRebuildsTheChainAndKeepsPresenting`.
    They were hidden before because no layer was installed.
- Optimum.Tests: 5 host-environment failures (pacing gate x2, numpy self-tests x2, `_ref/` not materialised).

### Test state (Linux notebook, 2026-09-15, at 99b836d)

RTX 4070 Laptop, driver 615.71.09, X11 (XWayland), Vulkan SDK layers 1.4.357.0 - the same layer version as the
Windows run above.

- **Implicit layers switched off for every run**, confirmed with `VK_LOADER_DEBUG=layer`: MangoHud is enabled
  globally on this machine and `VK_LAYER_LS_frame_generation` has no enable variable, so both would otherwise hook
  the test host and draw or present on the swapchain. Only `VK_LAYER_MESA_device_select` stays (it orders
  devices). Environment:
  `MANGOHUD=0 DISABLE_MANGOHUD=1 DISABLE_LSFG=1 DISABLE_VK_LAYER_VALVE_steam_overlay_1=1 DISABLE_VK_LAYER_VALVE_steam_fossilize_1=1 DISABLE_GAMESCOPE_WSI=1 DISABLE_VULKAN_RENDERDOC_CAPTURE_1_45=1 DISABLE_LAYER_MESA_ANTI_LAG=1`.
- **Validation is live in the suite**, not assumed: the loader inserts `VK_LAYER_KHRONOS_validation` into the test
  process, `ValidationFeaturesTests` asserts the layer settings were applied, and
  `SyncValidationControlTests.AnUnsynchronisedWriteAfterWriteIsReportedUnderASyncId` provokes a hazard and passes
  only because sync validation reports it.
- GPU suite: **661 passed, 0 failed, 0 skipped, no `SYNC-` messages.** The five present-after-write tests listed
  above pass here, and they also pass with MangoHud and the frame-generation layer switched back on.
- Optimum.Tests: 1177 passed, 34 skipped, 0 failed. The one failure before `99b836d` was
  `SsaoTemporalDitherCoverageTests.WithoutATemporalConsumerTheOverrideIsTheVanillaShader` reading the deployed,
  override-carrying copy of `ssao.fsh`; it now reads the client archive.

**Item 1 status:** the present-after-write hazard does not reproduce on this machine with the identical layer
version, so it is specific to the Windows run: its driver, its present path, or an implicit layer installed there
(overlays such as the NVIDIA, Steam or RTSS ones hook the same way). Recheck on the Windows machine with every
implicit layer disabled before changing the present path; the present semaphores already follow the Vulkan Guide's
per-image pattern (`SwapchainSlot.PresentSemaphoreFor(imageIndex)`, signalled by the submission that moves the image
to `PRESENT_SRC`).

### Next, in order

1. **Fix the present-after-write hazard.** The present has to wait on a semaphore signalled by the submit
   that transitions the swapchain image to PRESENT_SRC, with one render-finished semaphore per swapchain
   image indexed by the acquired image (`docs/research/vulkan-validation.md` §4; Vulkan Guide "Swapchain
   Semaphore Reuse"). Exit: the five tests pass with the layer; the rest of the suite is unchanged.
2. **Bindless implementation research: done** (`docs/research/vulkan-bindless.md`). Design outcome: combined-image-sampler
   arrays in set 1, one binding per GLSL sampled type (2D, 2DArray, Cube, 3D, usampler2D, isampler2D, the shadow
   variants), `PARTIALLY_BOUND | UPDATE_AFTER_BIND`, slot 0 a placeholder per type; per-draw indices in push
   constants (no `nonuniformEXT`); slot writes batched once per frame; frees deferred until the timeline says
   the frames that could sample them finished; set 0 stays a normal set (dynamic UBOs cannot be
   update-after-bind); limit checks on the update-after-bind sampled-image/sampler counts, dynamic UBOs and
   push-constant size; transient aliasing must resolve to the physical texture's slot.
3. **Phase 3 groundwork on decision 9:** one global pipeline layout (set 0 frame UBO + frame textures, set 1
   bindless textures + shared samplers, set 2 storage, push constants <= 128 B); descriptor-indexing feature
   and limit check at startup (without it the session stays on OpenGL); `bindings.glsl` +
   `Shaders/SetConvention.cs` with an agreement test; uniform placement table (push | frame | per-frame
   record | storage | texture slot).
4. **Rewriter retargeted** to the shared layout (samplers -> bindless indices, loose uniforms -> per-frame
   record addressed from push constants), then native GLSL 450 per program family (includes; post programs;
   GUI/lines; chunks; entities; particles/decals/sky/clouds; SSAO/godrays/bloom/colorgrade/OIT; Optimum
   programs), offline compiler tool + `shaders.manifest.json` + MSBuild target + packaging, runtime manifest
   load with the "N native, M rewritten, K failed" log line, specialization constants for quality defines,
   parity tests against the GLSL 330 sources.
5. **Phase 3b:** native render systems (post chain and TAA first, then chunks, entities,
   particles/decals/sky, GUI/text); remove `GlStateTracker`, GL id tables, texture units and
   uniform-by-location from the Vulkan path; decide the runtime rewriter's fate for mod shaders.
6. **Phase 5:** mod pass and motion-writer API, fork renderers on native systems, scanner v2, mod
   documentation for Vulkan-native support plus a fixture mod.
7. **Caching follow-ups** (`docs/research/vulkan-caching.md`): `FAIL_ON_PIPELINE_COMPILE_REQUIRED` with
   background compiles, growth-triggered saves, pipeline-key log for pre-warming, optional
   `VK_KHR_pipeline_binary`; real-client warm-start check with the headless harness (needs game data).
8. **XeGTAO** (`docs/research/xegtao-integration.md`): compute pass kind in the frame graph, GLSL compute
   port (prefilter split into dispatches, main pass, one denoise pass with TAA), NoiseIndex = frame % 64,
   composition before the resolve, settings; OpenGL keeps vanilla SSAO; tests and a headless comparison.
9. **General refactor:** split `VulkanDevice.cs`, restructure the project layout, remove GL-emulation leftovers.
10. **Optimisation** (plan Phase 4): per-pass GPU timestamps, push-constant placement from the measured
    profile, transient aliasing on by default, DirectToSwapchain / transfer backend measured. Exit: Vulkan
    mean FPS >= OpenGL and p99 <= OpenGL on the fixed scene.
11. **Validation milestones 2-7** (`docs/research/vulkan-validation.md`): per-area runs (core / sync /
    best + vendors / GPU-AV nightly), versioned `message_id_filter` suppression list, headless sessions
    clean on NVIDIA/AMD/RADV/ANV, lavapipe CI lane, Khronos checklist review, debug names and device fault
    reports.
12. **Cleanup for review (last).** Inventory so far - tracked files with tooling/workflow references:
    `TAA-PLAN.md`, `VULKAN-BACKEND-PLAN.md` (read it fully), `docs/vulkan-acceptance.md` (rule references),
    `docs/taa-acceptance.md`, `scripts/dev/worktree-bootstrap.sh`, `scripts/tests/bootstrap-git-repository.sh`,
    `scripts/dev/parity-capture.sh`, `scripts/dev/luma-diff.py`, `Optimum.Render.Vulkan/Core/RenderTargetManager.cs`,
    `Optimum.Render.Vulkan.Tests/PlatformLeafRoutingTests.cs`, `patches/.../ClientProgram.cs.patch`. About 27
    commit subjects since 553afdb carry worktree/"merge wave" artefacts; changing them needs a history
    rewrite (owner's OK and merge-base check) or a squash for the upstream PR. No co-author trailers exist.
    Optional: fix the host-environment test failures (numpy self-tests, pacing gate path translation on Windows).

## 6. Setting up the notebook

1. `git fetch origin && git checkout feat/vulkan-taa && git pull`
2. Set the git identity (section 2) and verify with `git config user.email`.
3. Bootstrap if the tree is not materialised (`make bootstrap`, or `scripts/bootstrap.ps1 -Refresh` on Windows).
4. Install the Vulkan validation layers (`winget install KhronosGroup.VulkanSDK` on Windows; the distro's
   `vulkan-validation-layers` package on Linux), otherwise the validation tests skip.
5. Run both test suites and compare with section 5, then start with item 1.

Untracked `shaderincludes/` at the repo root is a bootstrap artefact; leave it alone.
