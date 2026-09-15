# feat/vulkan-taa: where the work stands

Handover note so the work can continue on another machine. Plan of record: `docs/vulkan-native-plan.md`.
Research the designs follow: `docs/research/`. Last updated 2026-09-15.

## Goal and roadmap (in order)

1. Fully Vulkan-native (no GL mimicry; render systems record pipelines and descriptors directly; mods get
   documentation for native support, no compatibility layer).
2. XeGTAO (native compute, composed before the TAA resolve).
3. General refactor.
4. Optimisation, streamlining, simplification.
5. Validation against Vulkan best practice.
6. Cleanup of history and documentation for the upstream review (last).

Review blockers land alongside.

## Working rules

- Research current best practice online before each major piece; every research result goes into
  `docs/research/<topic>.md` (committed), and the design cites it.
- Commit each verified step and push regularly.
- Commits as NightHammer1000 <nightstorm@kpc.bz>. Set `git config user.name/user.email` on a new machine
  before committing.
- No history rewrites or force-pushes without explicit OK and a merge-base check against upstream.
  Upstream PR communication is handled by the owner.
- No tooling or assistant attribution in code, docs or commits.

## Done on this branch

| Commit | What |
|---|---|
| 41373cf | AO composed into the scene before the TAA resolve; SSAO dither advances per frame (temporal stability fix) |
| 766aada | Headless render harness backport |
| f83bda2 | Plan of record with the full-native roadmap |
| 19d101e | Review blockers: shaderc placement, prime-run, numpy probe, Windows bootstrap |
| a41efce | Shared frame block (set 0 `FrameGlobals`) |
| 9b52768 | Research notes: caching, descriptor model, XeGTAO, validation |
| 618a0b1 | Persisted SPIR-V cache and driver pipeline cache (`GamePaths.Cache/optimum-vulkan`, `OPTIMUM_VULKAN_SHADER_CACHE`) |
| 5bc337b | Plan decision 9: one pipeline layout, bindless textures from the start |
| 31684e3 | Validation extra checks via `VK_EXT_layer_settings` (`OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best,mobile,gpu,gpu-only`) |

Test baselines (Windows machine): Vulkan GPU suite 652/656 before the validation layer was
installed (1 host failure `PacingStatsTests` - WSL path translation; 3 skips for the missing layer).
Optimum.Tests: 5 host-environment failures (pacing gate x2, numpy self-tests x2, `_ref/` not
materialised).

## In progress when the machine changed

- **Bindless implementation research** (for decision 9) was running and did not finish. Redo it and write
  `docs/research/vulkan-bindless.md`. Questions: separate `texture2D[]` + `sampler[]` vs combined arrays
  and handling of shadow/array/cube/integer samplers; layout and pool flags (partially bound,
  update-after-bind, variable count) and required features/limits per vendor incl. Intel iGPU; slot
  lifetime with frames in flight (texture ids already recycle through a free list with deferred
  deletion, and transient aliasing rebinds ids per frame, so slots must follow `Rebind`/`RestoreBindings`);
  when `nonuniformEXT` is needed; driver quirks 2024-2026.
- **Full GPU suite with the validation layer active** (Vulkan SDK 1.4.357.0, installed with
  `winget install KhronosGroup.VulkanSDK`; previously every validation test skipped) finished: 661 tests,
  6 failures. One is the known host failure `PacingStatsTests`. The other five are new and all the same
  sync-validation hazard, which the missing layer had hidden: `SYNC-HAZARD-PRESENT-AFTER-WRITE` ("no
  sufficient synchronization is present to ensure that a swapchain present operation does not conflict
  with a prior layout transition") in `PresentDecouplingTests.RecordingTimeDoesNotGrowWithTheInjectedAcquireDelay`,
  `SwapchainRecreationTests.AHiddenWindowResizeLoopRecreatesWithoutWaitingAndStaysClean` and
  `SwapchainTests.TogglingVsyncRebuildsTheChainCleanly` / `ADeviceComesUpAgainstARealWindowAndPresentsFrames`
  / `ResizingRebuildsTheChainAndKeepsPresenting`. First fix on the list: the present path's semaphore wait
  must cover the command buffer that transitions the swapchain image to PRESENT_SRC, with one render-finished
  semaphore per swapchain image indexed by the acquired image (`docs/research/vulkan-validation.md` §4,
  Swapchain Semaphore Reuse). The notebook needs the SDK (or distro validation layers) too.

## Next steps

1. Bindless research note (above), then Phase 3 groundwork on decision 9: one global pipeline layout
   (set 0 frame UBO + frame textures, set 1 bindless textures + shared samplers, set 2 storage, push
   constants <= 128 B), descriptor-indexing feature and limit check at startup (device without it stays
   on OpenGL), `bindings.glsl` + `SetConvention.cs` with an agreement test.
2. Rewriter retargeted to that layout (samplers -> bindless indices in push constants, loose uniforms ->
   per-frame record addressed from push constants); then native GLSL 450 per program family, offline
   compiler tool and manifest (plan Phase 3), then native render systems and removal of the GL emulation
   (Phase 3b).
3. Caching follow-ups (`docs/research/vulkan-caching.md`): compile-required path with background builds,
   growth-triggered saves, pipeline-key log for pre-warming, optional `VK_KHR_pipeline_binary`.
   Real-client warm-start check with the headless harness still open (no game data on the Windows
   machine).
4. XeGTAO per `docs/research/xegtao-integration.md` (compute pass kind in the frame graph first).
5. Validation milestones 2-7 per `docs/research/vulkan-validation.md` (per-area runs, suppression list,
   headless sessions on NVIDIA/AMD/RADV/ANV, lavapipe lane, Khronos checklist incl. one present semaphore
   per swapchain image, debug names and device fault reports).
6. Refactor (split `VulkanDevice.cs`), optimisation (plan Phase 4), final cleanup.

Untracked `shaderincludes/` at the repo root is a bootstrap artefact; leave it alone.
