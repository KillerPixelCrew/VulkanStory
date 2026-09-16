# feat/vulkan-taa: handoff

Everything needed to continue the Vulkan branch on another machine. Last updated 2026-09-16 at 14f0779.

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

**Scope, stated by the owner 2026-09-16.** Frame structure is Vulkan foundation and IS in scope here: one frame
identity per frame with markers around simulation, render submit and present, and the world frame separated from
UI composition (`SceneNoHud` plus a UI target). The vendor layer on top of them - DLSS, XeSS, FSR, frame generation,
the NV/AMD/XeLL latency backends, NGX - stays on `feat/dlss`, `feat/dlss-g`, `feat/latency`. Standing rendering
direction: physically correct over the vanilla look; AO defaults to GTAO while TAA is active on Vulkan.

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
  - `OPTIMUM_VULKAN_SHADER_CACHE=<path>|0`, `OPTIMUM_VULKAN_SYNC_PIPELINES=1` (blocking pipeline creation; the
    capture scripts default to it)
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

### Plan status, audited 2026-09-16 (scoped to this branch)

**Phase 3b stage 1 completed and verified in game, 2026-09-16 (merge `2c9bc70`).** All nine post/TAA chain
passes draw through the native device API: OIT merge, sky motion, SSAO + bilateral blur + AO composite (both AO
modes), TAA resolve and sharpen (the lib body keeps the temporal contract, only the draw is re-routed), the bloom
chain, god rays, FXAA luma and the final composition (write slot 0 while sampling slot 1, no feedback copy), plus
the stage-1a blit. Suites on the merged state: Optimum.Tests 1243 passed, GPU 1065 passed, 0 failed, patches
157/0 conflict. Headless both-backends run on the RTX 4070, AO pinned to vanilla so the backends compare like for
like: renderer line confirmed per run, 0 client errors, validation 0 errors and 0 `SYNC-`, native chain active in
the real client. Per-frame SSIM Vulkan vs OpenGL 0.9756 / 0.9573 / 0.9697 against this session's OpenGL-vs-OpenGL
noise floor of 0.9597 / 0.9611 / 0.9670 - at or above the floor, i.e. the backends differ no more than two OpenGL
launches of the same save differ from each other.

Every item of `/home/n1ght/.claude/plans/i-never-wanted-this-sequential-kernighan.md` checked against this tree.
The plan predates the PR #69 split, so it also contains DLSS, upscaler, frame-generation, HDR and ray-tracing work:
those are marked `[out]` and are NOT owed on this branch.

**In scope for this branch: 44 done, 15 partial, 19 left, 1 blocked, 2 superseded** (the 17 audited items plus the two foundations below).**
**Out of scope for this branch: 11 items** - PR #69 carries the Vulkan backend and TAA only; DLSS, upscalers,
frame generation, the vendor latency backends, NGX, HDR and ray tracing live on `feat/dlss`, `feat/dlss-g` and
`feat/latency` and are NOT work owed here. They appear in the plan because the plan predates that split.

Audit method: five read-only agents, each required to cite a file, test or commit for anything marked done; a claim
in the plan or in the handoff was not accepted as evidence.

Legend: `[x]` done, `[~]` partly done (what is left follows it), `[ ]` not started, `[!]` blocked externally,
`[-]` superseded by a later decision, `[out]` out of scope for this branch.

**Step 0: branching; Phase 0: foundations; Phase 1A: platform substitution; Phase 1B: synchronisation foundation; constrai

- [x] DONE — **Step 0**: Branching: fix/taa-sky-direction and feat/vulkan-native from origin/main
- [x] DONE — **Phase 0**: Foundations: patcher capabilities, diagnostics, validation default, parity dump, acceptance doc skeleton
- [x] DONE — **Phase 0 exit criteria**: Phase 0 exit: builds/suites green, both dump paths run, GL-vs-GL noise floor, VK-vs-GL table, pacing baselines recorded
- [x] DONE — **Phase 1A step 1**: VulkanClientPlatform forwarding subclass; SetupOptimumFrameBuffers moved; ClientProgram.Start wiring; csproj donor reference
- [x] DONE — **Phase 1A step 2**: TAA members to the abstract class; 7 casts become virtual calls; 14 Optimum.Tests files re-pointed
- [x] DONE — **Phase 1A step 3**: Program/uniform/UBO virtuals; ShaderProgramBase.cs and UBO.cs revert to vanilla plus virtual calls; per-draw CPU measured
- [x] DONE — **Phase 1A step 4**: Remaining leaf sites moved; IOptimumGraphicsDevice/OptimumRender.Device/OptimumRenderBootstrap.Install deleted; ClientPlatformWindows branch-free
- [x] DONE — **Phase 1A Tests**: Phase 1A test list: GL.-grep source test, no-lambda test, fallback re-assigns ScreenManager.Platform, no device/cast remnants, PlatformSubstitutionTests, identical-pixel GPU tests
- [x] DONE — **Phase 1A Exit**: Phase 1A exit: identical screenshots per backend; forced-install-failure fallback exercised with the exact log line
- [x] DONE — **Phase 1B step 1**: FrameTimeline + RetireQueue; FrameRing on timelines; blocking waits = 1/frame
- [x] DONE — **Phase 1B step 2**: UploadManager + per-slot upload command buffer (backend A); ReadbackManager + SubmitPartial + QueryRing; SubmitAndWait/FlushFrame deleted
- [x] DONE — **Phase 1B step 3**: Readback in a frame: ReadbackManager.CopyToHost + SubmitPartial; only screenshot path waits
- [x] DONE — **Phase 1B step 4**: Swapchain/SwapchainRetirement/IPresentPath split submission; resize/alt-tab/minimise clean under sync,best; acquire wait stage never ALL_COMMANDS
- [x] DONE — **Phase 1B step 5**: VulkanAllocator pool classes + budget; static meshes off ReBAR; allocator policy tests; heap report
- [x] DONE — **Phase 1B step 6**: Per-slot indirect ring; descriptor arena; dirty-masked dynamic state; free GetError; CPU frame time drop measured, draw counters unchanged
- [~] PARTIAL — **Phase 1B Tests**: GPU test list: AsyncTransferTests, PresentDecouplingTests, SwapchainRecreationVisualTests, ConcurrentDeviceAccessTests, ReadbackMidFrameTests, QueryRingTests, AllocatorPolicyTests; unit: IndirectRingWrapTests, TimelineLifetimeTests, PresentWaitStageTests, SwapchainRetirementTests
- [x] DONE — **Phase 1B/1A Exit (combined, Phase 1 exit)**: Phase 1 exit: both renderers start; forced-install-failure fallback; sync,best 0 errors; blocking uploads 0; build/test counts recorded
- [x] DONE — **Constraint: Cecil transplant rules**: No cached lambdas / LINQ predicates / non-capturing lambdas / hidden-helper lowering in transplanted bodies
- [x] DONE — **Constraint: Patcher capabilities (typesToUnseal/methodsToVirtualize/verifier)**: typesToUnseal clears TypeAttributes.Sealed; methodsToVirtualize sets Virtual|NewSlot|HideBySig; call-vs-callvirt verifier fails the patch on a stray call
- [x] DONE — **Constraint: Hardware floor**: Vulkan 1.3 + dynamicRendering, synchronization2, timelineSemaphore, scalarBlockLayout, independentBlend, multiDrawIndirect; optional tiers with fallback + env override

**Phase 2: frame graph -> Milestone 1; Phase 3: native shaders

- [x] DONE — **Phase 2 Step 1**: ResourceStateTracker + BarrierBatcher drive the immediate path
- [x] DONE — **Phase 2 Step 2**: FrameGraph streaming recorder + PassRecorder, coexisting with the non-graph path
- [x] DONE — **Phase 2 Step 3**: Write-mask motion tiers, clear promotion, FramePlan load/store solving; TAA through the graph
- [~] PARTIAL — **Phase 2 Step 4**: Transient aliasing implemented, default off, but not wired into the live per-frame graph path
  - left: Wire FrameGraph/PassRecorder to call BindTransientForFrame per declared transient lifetime so aliasing can actually take effect outside tests.
- [x] DONE — **Phase 2 invariants pinned by tests**: Invariants pinned by tests
- [ ] LEFT — **Milestone 1 bullet: pacing-gate.sh passes**: M1 bullet 1 - pacing-gate.sh passes against the OpenGL baseline
- [x] DONE — **Milestone 1 bullet: blocking uploads/waits**: M1 bullet 2 - blocking uploads 0, blocking waits 1/frame
- [x] DONE — **Milestone 1 bullet: acquire ordering**: M1 bullet 3 - acquire after render submit, wait stage TRANSFER/COLOR_ATTACHMENT_OUTPUT
- [x] DONE — **Milestone 1 bullet: scopes and passes**: M1 bullet 4 - ScopesOpened==PassCount; no transition inside a scope
- [x] DONE — **Milestone 1 bullet: validation scripted session**: M1 bullet 5 - sync,best validation, zero [error] over the scripted session
- [ ] LEFT — **Milestone 1 bullet: SSIM parity TAA off**: M1 bullet 6 - per-attachment SSIM vs OpenGL, TAA off
- [x] DONE — **Milestone 1 bullet: TAA still-frame stability**: M1 bullet 7 - TAA on: luma-diff median within 0.3 of OpenGL, distant-leaf rejection <=1.5%
- [ ] LEFT — **Milestone 1 bullet: TAA acceptance rows re-pass**: M1 bullet 8 - TAA acceptance rows A11,A13,A14,A15,A17,A18 re-pass
- [x] DONE — **Milestone 1 bullet: in-game judgement**: M1 bullet 9 - user judges it in game on both backends
- [-] SUPERSEDED — **Phase 3 set convention**: Set convention: superseded by decision 9, implemented as a single shared layout
- [x] DONE — **Phase 3 placement table**: Uniform placement table
- [x] DONE — **Phase 3 manifest**: shaders.manifest.json schema and consistency
- [x] DONE — **Phase 3 compiler tool**: Offline shader compiler tool (--build/--verify/--single)
- [x] DONE — **Phase 3 adapter layout**: Mod-shader adapter retargeted to the shared layout in one change
- [x] DONE — **Phase 3 seven worktree stages of native GLSL**: Native GLSL ported in family stages
- [x] DONE — **Phase 3 scanner v2**: Launcher scanner v2 (ShaderAssetOverride, PlatformInternals, schema 2)
- [ ] LEFT — **Phase 3 temporal contract addendum-or-v2 decision**: Temporal contract addendum-or-v2 decision for native shaders
  - left: Add a dated entry to docs/temporal-frame-contract.md (or a version bump) stating whether the native-shader motion-writer port is a v1 addendum or a v2 change.
- [ ] LEFT — **Phase 3 ReloadShaders no longer recompiling on a settings change**: ReloadShaders no longer recompiling on a settings change
- [~] PARTIAL — **Phase 3 Tests list**: Phase 3 Tests list (parity, motion-writer shape, manifest, adapter-layout, differential, launcher fixtures, no legacy extensions)
  - left: The plan specifically asks that 'the eight Taa*Motion*Tests gain native-vs-rewriter differential cases (motion attachment equal within 1 ULP of RGBA16F)'. Searched TaaMotionWriterTests.cs, TaaEntityMotionWriterTests.cs, TaaInstancedMotionWriterTests.cs, TaaStandardMotionWriterTests.cs, TaaLiquidMotionTests.cs, TaaSkyMotionTests.cs and found no native-vs-rewriter comparison in any of them - all sti
- [ ] LEFT — **Phase 3 Exit criteria**: Phase 3 exit criteria (48 native/0 failed logged, SSIM>=0.99, validation clean, in-game settings sweep, vulkan-acceptance.md matrix, contract decision)
  - left: Run and record the actual Phase 3 exit in docs/vulkan-acceptance.md: the native/rewritten/failed count from a real (non-headless-forced) launch, per-attachment SSIM, validation log, the full settings sweep on both backends, and the temporal-contract addendum-or-v2 decision.

**Phase 3b (docs/vulkan-native-render-systems.md decisions 1-7, stage 1 nine-pass scope, parallel world-system stages, rem

- [x] DONE — **Phase 3b decision 1**: Runtime rewriter stays permanently as mod-shader adapter
- [~] PARTIAL — **Phase 3b decision 2**: Seams are existing virtuals, overridden without calling base
  - left: Post/TAA chain done: the nine passes are native (TAA resolve/sharpen keep the lib body for the temporal contract and re-route only the draw). No world-system transplanted seams exist yet (ChunkRenderer, entities, particles, GUI unchanged).
- [~] PARTIAL — **Phase 3b decision 3**: A native system reads client state, never GL state
  - left: Rule only exercised by the blit; unverified for any world-render system since none has been ported.
- [x] DONE — **Phase 3b decision 4**: Device API for native systems (NativePasses)
- [~] PARTIAL — **Phase 3b decision 5: order and parallelism**: Stage 1 (device API + post/TAA chain) then parallel world systems then removal
  - left: Stage 1 COMPLETE (2026-09-16, merge 2c9bc70): all nine chain passes native. Stage 2 (chunks, entities, particles/decals/sky/clouds, GUI/text) not started; stage 3 removal not started.
- [~] PARTIAL — **Phase 3b decision 6**: Behavioural identity is the acceptance rule (old-route vs native-route GPU tests)
  - left: Chain passes have differential old-route-vs-native tests. World systems still need theirs once each goes native.
- [x] DONE — **Phase 3b decision 7**: FSR input identity preserved (BlitPrimaryToDefault keeps reading Primary colour 0)
- [x] DONE — **Phase 3b stage 1 scope: 9 chain passes**: Which of the nine post/TAA chain passes are native today
- [ ] LEFT — **Phase 3b: world render systems still on the emulation layer**: Every world render system still on the GL-emulation layer
  - left: All world render systems (chunks, entities, particles, decals, sky/clouds, GUI/text) - stage 2 of decision 5 - are entirely unstarted.
- [ ] LEFT — **Phase 3b: GlStateTracker / texture-unit tables / uniform-by-location reachability**: GlStateTracker, texture-unit tables and uniform-by-location still reachable from the Vulkan path
  - left: Not reachable only from the native blit's own pipeline creation; reachable and load-bearing for every other pass and every world system.
- [x] DONE — **Phase 4: disk pipeline cache**: Disk pipeline cache with FAIL_ON_PIPELINE_COMPILE_REQUIRED, background compile worker
- [x] DONE — **Phase 4: used-key manifest**: Used-pipeline-key manifest for pre-warming
- [x] DONE — **Phase 4: warm-up**: Background warm-up from the manifest
- [ ] LEFT — **Phase 4: push-constant placement from a measured profile**: Push-constant placement frozen from OPTIMUM_VULKAN_UNIFORM_PROFILE measurement
  - left: Entire item: the env-driven measurement tool, the manifest field, and the placement logic reading it are all absent.
- [x] DONE — **Phase 4: animation SSBO ring**: Bone/animation data on a storage-buffer ring with dynamic offsets
- [ ] LEFT — **Phase 4: Use() include-block early-out**: Skipping ShaderProgramBase.Use()'s frame-global include-block writes when unchanged
  - left: Entire item unimplemented; Use() still writes all ~50 frame-global uniforms unconditionally every call.
- [ ] LEFT — **Phase 4: per-pass GPU timestamps**: Per-pass GPU time table from timestamp queries
  - left: No timestamp-query infrastructure exists; no per-pass ms table has been produced.
- [ ] LEFT — **Phase 4: transient aliasing default on**: Transient aliasing switched on by default after clean validation on all targets
  - left: Default flag flip to on, plus the required clean-validation-on-all-targets gate, have not happened.
- [-] SUPERSEDED — **Phase 4: bindless decision**: Bindless set (originally set 2, later set 1) adopted based on measured descriptor-miss rate
- [ ] LEFT — **Phase 4: DirectToSwapchain**: DirectToSwapchain present policy measured and kept only if it wins
  - left: Entire item unimplemented and unmeasured.
- [ ] LEFT — **Phase 4: transfer backend B**: Dedicated-transfer-queue backend (B) measured against backend A
  - left: No ITransferBackend abstraction, no backend B implementation, no measurement exists.
- [ ] LEFT — **Phase 4: exit criteria**: Phase 4 exit - Vulkan mean FPS >= OpenGL, p99 <= OpenGL, pipeline cache hit rate >= 95%, per-pass ms table within 10% of GPU frame time
  - left: Every numeric exit criterion is unmeasured, or where a related number exists (Milestone 1 pacing) it fails the bar; the required 30-minute session and doubled perf-capture.sh runs have not been executed.

**Phase 5: mod API and fork ports; Phase 6: upscaler and frame-generation seams; Latency seams section (L0 types, S1-S8, b

- [~] PARTIAL — **Phase 5**: Mod API and fork ports
  - left: No evidence the exit criterion 'VSEssentials/VSSurvivalMod/VSCreativeMod renderers checked against declared passes' was done: grep for OptimumPass/RegisterOptimumPass/MotionWriter across VSEssentials, VSSurvivalMod, VSCreativeMod (working trees) and their patches/ directories returns nothing; none of the 40+ existing fork renderer patches (CloudRendererVolumetric, MechNetworkRenderer, EntityShapeR
- [out] Vendor orchestrator decision: Optimum builds its own multi-vendor orchestrator (not Streamline) — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] Vendor orchestrator decision: Slot coupling: vendor latency backend only when upscaler vendor matches GPU — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] Vendor orchestrator decision: NVIDIA goes direct: Reflex via VK_NV_low_latency2, DLSS/DLSS-G via NGX P/Invoke (no Streamline) — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] Vendor orchestrator decision: Intel on Windows: D3D12 bridge present path with XeFG and XeLL — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] Latency seams: L0 types, S1-S8 seams, backends None/Native/NvLowLatency2/AmdAntiLag — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] Latency seams: Acceptance numbers (section L) — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] NGX on native Linux: Spike result: NGX comes up through a native shim — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] DLSS SR evaluation: DLSS Super Resolution evaluates on the device — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] Phase 6: Upscaler and frame-generation seams — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency

**Roadmap items (HDR output, ray tracing, headless render harness, GTAO/XeGTAO 3 sub-steps), plan's Documentation-to-updat

- [~] PARTIAL — **Roadmap: headless render harness**: Headless render harness that does not take the machine
  - left: The doc's own admission (docs/vulkan-acceptance.md, headless section, 'What it does not cover'): 'No camera path is checked in yet - one has to be authored per scene with .cam p and .cam save.' The roadmap's acceptance bar - 'the shimmer class of bug (jitter, disocclusion, AO noise) shows up as a number from that sequence' via a checked-in deterministic camera path - has not been demonstrated; the
- [x] DONE — **GTAO order-of-work step 1**: GTAO step 1: composite AO into the scene at render resolution, before the resolve
- [x] DONE — **GTAO order-of-work step 2**: GTAO step 2: make the dither temporally varying
- [~] PARTIAL — **GTAO order-of-work step 3**: GTAO step 3: port XeGTAO and judge it against the fixed SSAO
  - left: All of section D's measurement plan: converged numerical reference, thin-foliage/halo numbers, temporal-stability numbers vs vanilla SSAO+TAA, per-pass GPU cost on Arc-class and RTX hardware, and the resulting handheld-preset decision. Until then GTAO has landed as a code path but has not been 'judged' by the plan's own definition.
- [~] PARTIAL — **Documentation to update**: Documentation-to-update list (VULKAN-BACKEND-PLAN.md v2, acceptance/allowlist docs, contract addendum, CLAUDE.md, skills)
  - left: Rewrite VULKAN-BACKEND-PLAN.md in place to v2 (or formally mark it superseded/archived and delete stale sections instead of leaving contradictory content live); record the Phase-3 temporal-contract addendum-or-v2 decision somewhere durable; add the shaders-vk source-of-truth row, check-shaders-vk build step and the missing env vars to CLAUDE.md; update the three named skills with the manifest/paci
- [~] PARTIAL — **Risks (ranked) mitigations**: Risks section: are the 10 ranked mitigations actually in place
  - left: Fill in docs/vulkan-acceptance.md section 6's vendor matrix with the numbers that already exist elsewhere (risk 6); record the Phase-3 temporal-contract decision (risk 8, shared with the Documentation item above).
- [!] BLOCKED — **Handoff item 1**: Fix the present-after-write hazard
  - left: Waiting on: the complete first-message text of one of the five failures, captured on the Windows GTX 1060 (or another Pascal/580-branch device) by running `dotnet test Optimum.Render.Vulkan.Tests --filter <the 5 test names> --logger "console;verbosity=detailed"` with the implicit Vulkan layers disabled, plus that machine's driver version and `vulkaninfo --summary` (present modes, image counts) rec
- [~] PARTIAL — **Handoff item 7**: Caching follow-ups
  - left: Two items explicitly still open, confirmed absent from the code: VK_KHR_pipeline_binary (grep for 'PipelineBinary'/'pipeline_binary' across Optimum.Render.Vulkan: zero hits) and a real-client warm-start check driven through the headless harness (no 'warm-start' or 'WarmStart' hit anywhere outside the two progress-doc lines that call it open).
- [ ] LEFT — **Handoff item 9**: General refactor: split VulkanDevice.cs, restructure the project, remove GL-emulation leftovers
  - left: Everything: splitting VulkanDevice.cs into smaller units, any project-layout restructuring, and removing GlStateTracker.cs plus its call sites. This is item 9 of 12 on the to-do list and item 5 (Phase 3b native render systems, a prerequisite for retiring GlStateTracker per the handoff's own text) is itself only one stage in (device API + native blit merged; chunks/entities/particles/GUI on native 
- [~] PARTIAL — **Handoff item 11**: Validation milestones 2-7
  - left: A real per-area CI split including a scheduled GPU-AV run; AMD/RADV coverage; a lavapipe CI lane; a written, evidenced sign-off against the Khronos checklist; debug object naming and command-buffer labels; Aftermath and a GFXReconstruct reference capture.
- [ ] LEFT — **Handoff item 12**: Cleanup for review (last)
  - left: The entire item: read VULKAN-BACKEND-PLAN.md fully and reconcile/retire it, review the named scripts and Core/RenderTargetManager.cs for tooling/workflow references, and (with the owner's OK per the branch's binding rule on history rewrites) squash or rewrite the ~23-27 worktree/merge-wave commit subjects before the upstream PR, plus the optional host-environment test fixes (numpy self-tests, Wind
- [out] Roadmap: HDR output: HDR output — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency
- [out] Roadmap: ray tracing: Ray tracing — not this branch; tracked on feat/dlss / feat/dlss-g / feat/latency

#### Vulkan foundation, in scope for this branch (owner's call, 2026-09-16)

Frame structure is part of the Vulkan backend, not vendor work. A backend with an explicit frame graph needs one
identity per frame, with markers around simulation, submit and present, and it needs the world frame separated from
UI composition; both stand on their own whether or not an upscaler ever exists, and both are what make pacing
measurable and keep the HUD out of the scene image. They are IN SCOPE here. What stays off this branch is the vendor
layer that later sits on top of them: DLSS, XeSS, FSR, frame generation, the NV/AMD/XeLL latency backends and NGX.

- [ ] Foundation A, frame marking (source `feat/latency`): L0 latency types, the pre-input `LatencySleep` lib seam,
  `IDeviceRequirementContributor` and the pNext chain builder in `CreateDevice`, one frame id per frame,
  `VkPresentIdKHR` chaining, markers around simulation / render submit / present, the `stats.latency` line.
  Explicitly excluded: the NV, AMD and XeLL backends.
- [ ] Foundation B, GUI separation (source `feat/dlss-g`, not `feat/dlss`): the `SceneNoHud` snapshot (slot 23) at
  the end of `RenderFinalComposition`, and the UI target (slot 24) with a `ui-compose` pass composed back before the
  `Done` stage. Explicitly excluded: the upscaler subsystem its gate reads there, and two-presents-per-frame.
  Conflict: the `feat/dlss-g` hooks live in the lib GL body of `BlitPrimaryToDefault`, which is overridden here and
  dispatches to `RenderNativeBlit()` - ported verbatim they are dead code on Vulkan and must be re-implemented at
  that method's three exit points, the lib patch kept for the OpenGL path only.

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

GeForce GTX 1060 (Pascal) on Windows. Pascal stays on NVIDIA's 580 driver branch; 590 and later dropped it.

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

**Item 1 status: does not reproduce here; the failing hardware is Pascal on the 580 driver branch.**

- Reproduction matrix on this notebook, the five tests only, implicit layers off, layers 1.4.357.0: RTX 4070 on
  Wayland and on X11 (GLFW platform chosen by unsetting `DISPLAY` or `WAYLAND_DISPLAY`; setting them to an empty
  string makes GLFW fail and every test skips), and the Intel UHD iGPU (Mesa ANV, selected with
  `VK_LOADER_DRIVERS_SELECT=*intel*`; `MESA_VK_DEVICE_SELECT` alone still hands the tests the NVIDIA device) on
  Wayland and on X11. **5/5 passed in all four, zero `SYNC-` messages.** The present path has not changed since
  the Windows run (no commits under `Present/`, `FrameRing.cs` or `VulkanDevice.cs` after `11195c5`).
- The two machines that failed share an architecture: the Windows GTX 1060 above, and the upstream reviewer's
  MX150 on Linux (driver 580.173.02), whose resize-loop tests also failed (reported as "swapchain fence signaling
  races" in the PR 69 review). Both are Pascal, and Pascal is frozen on the 580 branch, so the driver's
  acquire/present behaviour (image index order, SUBOPTIMAL/OUT_OF_DATE results, image counts, present modes) is
  the variable this notebook cannot vary.
- What the layer needs to report it (`layers/sync/sync_submit.cpp`, `QueueBatchContext::ResolvePresentSemaphoreWait`
  and `DoQueuePresentValidate`): a present on the same queue imports the batch that signalled its wait semaphore
  through a barrier, and everything else from the queue's last batch without one. `SYNC-HAZARD-PRESENT-AFTER-WRITE`
  therefore means the image's last layout transition reached the present by the unbarriered route: the wait
  resolved against a different batch than the one that transitioned the image, or the layer found no signal for the
  semaphore. Our signal stage is not the cause: `VkSubmitInfo` signal semaphores are converted to `ALL_COMMANDS`
  (`layers/utils/convert_utils.cpp`), which covers layout transitions since KhronosGroup/Vulkan-ValidationLayers#7479.
  Two layer facts to check against the full message: `PreCallRecordDestroySemaphore` erases pending timeline signals
  but not pending binary ones, and an acquire records its semaphore's signal with `emplace`, which ignores an entry
  already present for the same handle.
- **Needed from the Windows machine before any code change:** the complete text of one failure. The assertion prints
  the first full layer message per hazard id (`ValidationAssert.NoSyncHazards`, "first message"), which names the
  prior access: command buffer, submit index, batch tag and command. Run the five tests alone
  (`dotnet test Optimum.Render.Vulkan.Tests --filter <names> --logger "console;verbosity=detailed"`) with the
  Windows implicit layers disabled, and record the driver version and `vulkaninfo --summary` (present modes,
  image counts) next to it.

### Test state (Linux notebook, 2026-09-16, at 14f0779): the native shaders in the real client

Headless captures of `serene cave world` (`scripts/dev/headless-capture.sh`, 5 frames from in-world frame 300,
window never mapped), implicit layers off, `sync,best` validation to a file. The dev client was deployed with
`make deploy INSTALL_DIR=/nonexistent-...` so the user's own install was not touched.

- **Vulkan, native shaders forced** (`OPTIMUM_VK_NATIVE_SHADERS=force`): `[Optimum] shaders: 63 native, 4 rewritten,
  0 failed` (the 4 have no manifest entry: the inline `MinimalGui`, the mod-registered `optimum-map` and two
  registration variants; nothing fell back with a reason). **0 validation errors, 0 `SYNC-` messages.** GTAO ran
  (up to 177 compute passes per stats sample).
- **Vulkan, default**: the dev client has no launcher scan, so the conservative rule put every program on the
  rewriter (`0 native, 50 rewritten`) - as designed. Same run: 0 validation errors, 0 `SYNC-`, GTAO active.
- **OpenGL**: ran unchanged, twice.
- **Pixels (`scripts/dev/ssim.py`)**: two OpenGL launches of the same save differ by **SSIM 0.9757** (mean abs 1.73)
  - the same-session noise floor, since a launch differs in world time, weather and entities. Native vs rewriter on
  Vulkan is **0.9770** (1.58), *inside* that floor: the 49 native programs introduce no measurable pixel difference.
  Vulkan vs OpenGL is 0.969 and native vs OpenGL 0.964, both around the floor and not a per-pass comparison.
- Not yet measured: a deterministic comparison with the scene stilled (`--commands`, `--fixed-dt`), the AO
  measurement plan of `docs/research/ambient-occlusion.md` section D, and pacing numbers.

### Next, in order

1. **Fix the present-after-write hazard.** The present has to wait on a semaphore signalled by the submit
   that transitions the swapchain image to PRESENT_SRC, with one render-finished semaphore per swapchain
   image indexed by the acquired image (`docs/research/vulkan-validation.md` §4; Vulkan Guide "Swapchain
   Semaphore Reuse"). That pattern is already in place; the hazard is Pascal/580-specific and waits on the full
   message from the GTX 1060 (see "Item 1 status"). Exit: the five tests pass with the layer on Pascal; the rest
   of the suite is unchanged.
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
   **Status (2026-09-15):** steps 1 and 2 of the decision-9 sequence are in. Capability negotiation:
   `Core/DescriptorIndexingFloor.cs` judges runtimeDescriptorArray, partially bound, sampled-image update-after-bind,
   dynamic sampled-image indexing and the update-after-bind sampled-image/sampler, dynamic-UBO and push-constant limits
   against the set-1 table; `IsUsable` rejects a device below it (OpenGL fallback with the named reason), `CreateDevice`
   enables the features, `VulkanCapabilities.DescriptorIndexing` carries them and the device-up line logs them. Set
   convention: `sources/shaders-vk/include/bindings.glsl` (source of truth) and `Shaders/SetConvention.cs`, pinned by
   `SetConventionTests` (defines, declarations, uniqueness, floor agreement, the include compiles). Tests:
   `BindlessCapabilityTests` (floor logic, researched vendor limits, a decision-9 layout created and allocated with no
   validation message on the selected device). GPU suite 673/673, no `SYNC-`; both local devices (RTX 4070, UHD ADL-S)
   meet the floor. Open for the layout step: best practices' AMD check `KeepLayoutSmall` warns on that layout's
   128-byte push-constant range; size the real push block from the uniform placement map, not the maximum.
   **Status (2026-09-15, evening): done.** The bindless texture table (slot allocator keyed on the physical texture,
   deferred free on the Frame timeline, per-kind placeholders: opaque black, magenta only under poison mode) and
   `SharedPipelineLayout` are merged, and every program now links against the one layout: set 0 frame block and
   frame textures, set 1 bindless arrays, set 2 FaceData, named uniform blocks as std140 storage buffers
   (Animation 1, AnimationPrev 2, others 4-7) and the program record (dynamic UBO, binding 3), samplers as bindless
   slots in push constants. Per-program layouts are gone. GPU suite 878/878 at the retarget merge.
4. **Rewriter retargeted** to the shared layout (samplers -> bindless indices, loose uniforms -> per-frame
   record addressed from push constants), then native GLSL 450 per program family (includes; post programs;
   GUI/lines; chunks; entities; particles/decals/sky/clouds; SSAO/godrays/bloom/colorgrade/OIT; Optimum
   programs), offline compiler tool + `shaders.manifest.json` + MSBuild target + packaging, runtime manifest
   load with the "N native, M rewritten, K failed" log line, specialization constants for quality defines,
   parity tests against the GLSL 330 sources.
   **Status (2026-09-15, evening):** the rewriter half is done (item 3). Native GLSL 450: contract
   `docs/vulkan-native-shaders.md`; shared includes, `frame.glsl`/`specialization.glsl` generators and
   `motion.glsl`; the offline compiler (`tools/shader-compiler`, SPIR-V reflection, `shaders.manifest.json`,
   MSBuild target, deploy and packaging beside the renderer DLL); the static parity harness
   (`NativeShaderParityTests`, GLSL 330 oracle vs manifest); all 49 registered programs ported (MinimalGui and the
   mod-registered optimum-map stay on the rewriter). GPU suite 968/968 at the family merges. In progress: the
   runtime seam (manifest load in `LinkProgram`, per-program rewriter fallback, placement-table locations,
   initializer seeding, the `Array` alias for sampler2DArray, native-vs-rewriter pixel tests). Open after it: the
   settings-change reload through specialization constants, launcher scanner v2 (in progress), in-game check on
   both backends.
5. **Phase 3b:** native render systems (post chain and TAA first, then chunks, entities,
   particles/decals/sky, GUI/text); remove `GlStateTracker`, GL id tables, texture units and
   uniform-by-location from the Vulkan path; decide the runtime rewriter's fate for mod shaders.
6. **Phase 5:** mod pass and motion-writer API, fork renderers on native systems, scanner v2, mod
   documentation for Vulkan-native support plus a fixture mod.
7. **Caching follow-ups** (`docs/research/vulkan-caching.md`): `FAIL_ON_PIPELINE_COMPILE_REQUIRED` with
   background compiles, growth-triggered saves, pipeline-key log for pre-warming, optional
   `VK_KHR_pipeline_binary`; real-client warm-start check with the headless harness (needs game data).
   - Landed (design items 2, 4, 5): render-thread creation with
     FAIL_ON, skipped draws while a bounded worker compiles against its own cache and merges under a
     lock, publication at frame start; growth-triggered saves from a worker (8 MiB, sampled every 10 s)
     next to the shutdown save; the versioned, LRU-capped `pipeline/<gpu>.keys` log with prewarm when a
     matching program links; `stats.pipelines`. GPU tests default to blocking creation (`GpuTest`).
   - Still open: `VK_KHR_pipeline_binary` (deferred, research item 6) and the real-client warm-start
     check with the headless harness.
8. **GTAO with visibility bitmasks** (XeGTAO-derived; XeGTAO itself is archived since 2024-04-22, see `docs/research/xegtao-integration.md` section 0; the combined design is `docs/research/ambient-occlusion.md` section C; physically correct, default AO on Vulkan while TAA is active): compute pass kind in the frame graph, GLSL compute
   port (prefilter split into dispatches, main pass, one denoise pass with TAA), NoiseIndex = frame % 64,
   composition before the resolve, settings; OpenGL keeps vanilla SSAO; tests and a headless comparison.
   **Status (2026-09-16):** landed on the branch - the frame-graph compute pass kind, the GTAO passes, the
   class channel, the composition before the resolve and the settings, with the native port carrying
   OPTIMUMAO as specialization constant 12. The Optimum options tab now also carries an ambient occlusion
   master switch (`OptimumConfig.AmbientOcclusionEnabled`, default on, `optAo`): it gates `RenderSSAO` only,
   so both AO paths stop together, the SSAO G-buffer and the stamped shader defines stay untouched, and it
   flips live with no shader reload, no frame buffer rebuild and no temporal reset - the in-game A/B for
   judging what AO contributes. Switching it off also sets `optimumSsaoInScene`: the scene shaders stay
   compiled with `SSAOLEVEL > 0`, so `final.fsh` would otherwise multiply by an SSAO target nothing wrote
   that frame and darken the whole image (found in game, 2026-09-16). Beside it, `AmbientOcclusionDebugView`
   (`optAoDebug`) writes the AO term alone as greyscale in the final composition, sourced from the GTAO
   output when it ran and the vanilla blurred target otherwise - the same branch in both shader twins
   (`final.fsh`, `final.frag`), before colour grading. Still open: the section D measurements and the
   deterministic stilled-scene comparison.
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
