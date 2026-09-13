# Optimum roadmap

What is done, what is being worked on, and what is planned. The detailed design lives in
[`docs/vulkan-native-plan.md`](vulkan-native-plan.md); acceptance numbers live
in `docs/vulkan-acceptance.md` and `docs/taa-acceptance.md`; the temporal rules live in
`docs/temporal-frame-contract.md`.

Status: **done** = merged to `main` and accepted in game · **in progress** = on a branch · **planned** = not started.

## Done

| | What | Evidence |
|---|---|---|
| done | **TAA** with a jitter-stable resolve: 3x3 nearest-depth disocclusion, motion from the nearest-depth tap, luminance anti-flicker weighting | `docs/taa-acceptance.md`, `scripts/dev/taa-rejection.py` (distant-leaf rejection 1.05 %, was 3.7 %) |
| done | **Native Vulkan backend, Milestone 1**: platform substitution (`VulkanClientPlatform : ClientPlatformWindows`), timeline semaphores, asynchronous uploads, split present, usage-derived barriers, streaming frame graph, transient allocator | `docs/vulkan-acceptance.md` "Milestone 1 exit results"; blocking uploads 0, passes == scopes, validation clean |
| done | **Latency reduction**, on by default: the sleep moved before input sampling, NVIDIA Reflex (`VK_NV_low_latency2`), AMD anti-lag (`VK_AMD_anti_lag`) and Optimum's own completion pacing | `docs/vulkan-acceptance.md` "Latency acceptance"; input-to-present 7.67 ms -> 1.85 ms on an RTX 4070, free with Reflex |
| done | **DLSS Super Resolution**: NGX through a native C shim (NGX resolves its caller from the return address, so no P/Invoke stub may call it, and no shim function may tail-call), the upscaler slot with the post chain at display resolution, its own Optimum settings tab, presets and live switching | merged in PR #3 (`5cb8ec5`); judged in game by the user on the RTX 4070 (about 50 FPS between DLAA and Ultra Performance). The shimmer at Performance and below had three causes, all fixed: the raster jitter sign, SSAO composited after the upscale, and the AO dither - the horizon swimming went with them, confirmed by the user |
| done | **A headless render harness**: the real client and renderer with the window never mapped, a chat-command script for the scene, selected in-world frames written as PPM, and a clean self-close from the render thread | merged in PR #3; run end to end on both backends since 2026-09-12 (every capture in `docs/vulkan-acceptance.md` from that date on came through it). No camera path is checked in yet |

## Merged, not yet judged in game

Merged to `main` in PR #3. Each is proven by the GPU suite on a real device and by source coverage; none has
been judged on its own in a session. By this file's legend that is the gap to "done".

| | What | Evidence | What is unproven |
|---|---|---|---|
| landed | **The orchestrator's slot coupling**: the latency backend follows the pair (upscaler vendor, GPU vendor), not the device alone - DLSS on NVIDIA takes Reflex, FSR on AMD anti-lag, XeSS on Intel means XeLL and so Native on every Vulkan path, every cross-vendor pair and the vendor-less passthrough slot take Optimum's own pacing, and no upscaler at all leaves the device auto order untouched | `Optimum.Render.Vulkan/Latency/LatencySlotCoupling.cs`; the full vendor table plus the override precedence in `LatencySlotCouplingTests` (53 cases) | Whether the chosen backend is the right one for frame times in a real session, on anything but this dev box |
| landed | **A passthrough upscaler**: the slot that plans exactly like DLSS - same render size, same jitter, same LOD bias - and reconstructs with a magnifying blit instead. Both the diagnostic that separates our rendering from the vendor's and the fallback upscaler for a GPU with no vendor path | `Optimum.Render.Vulkan/Upscale/PassthroughUpscaler.cs`, `PassthroughUpscalerTests` (the plan matches NGX's own answer size for size at all five presets) | The magnified frame on screen, and the overlays' depth upscale behind it - shared code that has never run behind a magnifying blit |
| landed | **SSAO temporal dither** (the GTAO item's step 2): vanilla's screen-locked Bayer-128 dither advances by the golden ratio per frame under `TAAMOTION`, so successive frames rotate the kernel instead of re-rolling the same screen-fixed one | `sources/shaders/ssao.fsh`, `SsaoTemporalDitherTests` (AO differs per frame with the temporal pipeline on, bit-identical with it off, bit-identical when the frame index repeats) | The pay-off. AO that converges instead of fighting the accumulator is the claim; it has not been judged in game or on a parity dump |

The SSAO dither row is the closest to done: the user saw the AO shimmer disappear in the DLSS sessions of
2026-09-12, but the dither landed together with the placement fix, so the effect cannot be attributed to it
alone.

### The headless render harness, honestly

What it covers. `OPTIMUM_HEADLESS=1` creates the window with `StartVisible=false` and `StartFocused=false`:
a real window with a real surface and a real swapchain, never mapped and never focused, on both backends
(there is no surfaceless GL path in this client, so this is the only offscreen mode that is symmetric).
The frame loop, the swapchain and every rendering path are unchanged - the harness is one call in
`window_RenderFrame`, beside the parity dump, after the post chain and the final blit. Frames come from
`ReadDefaultFramebuffer`, the same polymorphic call the in-game screenshot makes and a device-side copy on
Vulkan, so no OS window capture is involved and no compositor is needed; they are written as
`frame-NNNNNN.ppm` at a chosen frame list or cadence, which `scripts/dev/ssim.py` reads and which pairs
between two captures by name. `OPTIMUM_HEADLESS_COMMANDS` feeds a file of chat lines on an in-world frame,
routed the way the chat HUD routes what a human types, so `/time` and `/weather` fix the scene and `.cam
load` / `.cam play` drive vanilla's own keyframed camera (`SystemCinematicCamera`, which nothing in this
repo used before). `OPTIMUM_HEADLESS_FIXED_DT` pins `ClientMain.DeltaTimeLimiter`, the field vanilla's own
recorder sets, so the simulated step is constant. A permanently unfocused window falls under the existing
30 FPS background cap, so a run does not take the machine. The renderer line is still logged and
`scripts/dev/headless-capture.sh` refuses to report a capture it cannot attribute to the backend it asked
for.

What it does not cover. A display server is still required - real, nested or Xvfb - because GLFW asks for
the screen size before any window exists and Vulkan needs a WSI surface; "headless" here means no visible
window, not no display. Reproducibility is frame-for-frame repeatable, not bit-exact: a fixed step does not
pin chunk streaming, particle or mob RNG, which is the same standard `docs/vulkan-acceptance.md` already
sets for GL-vs-GL noise. The per-attachment dump `scripts/dev/taa-rejection.py` reads is still
`OPTIMUM_PARITY_DUMP` (composed in by `--parity-dump`, not replaced). Presenting to a never-mapped window
has been exercised on this project's dev box and in the GPU suite, not on every driver, Wayland or Xvfb
combination. No camera path is checked in yet - one has to be authored per scene with `.cam p` and
`.cam save`.

## In progress

| | What | Where |
|---|---|---|
| in progress | **DLSS Frame Generation groundwork**: two presents per frame (step 0), a per-call present source (1), the HUD-less scene snapshot (2), the HUD in its own target composed before present (3), three frames in flight (4) - all landed and verified; NGX DLSS-FG bring-up (5) is being mapped; the present thread and spacing pacer (6) come last | branch `feat/dlss-g`; status and review findings under "DLSS frame generation: the design" |

## Planned

### Next

- **DLSS Frame Generation (DLSS-G)** - reports available on native Linux; Optimum owns the pacing
  (present the generated frame when evaluate returns, the retained real frame at equal spacing from a
  present thread), `FramesInFlight = 3`, HUD-less colour and UI inputs. Requires Reflex active.
  Design and order of work: the "DLSS frame generation: the design" section below.
- **XeSS and FSR** - the second and third upscaler backends behind the same slot. XeSS ships Windows-only
  libraries; AMD's current SDK has no Vulkan backend, so FSR is a shader port. The coupling above already
  maps their setting tokens, so they need no latency work when they land - only `UpscalerNames` grows.

(The slot coupling that used to sit here has merged; see "Merged, not yet judged in game".)

### Quality and tooling

- **GTAO (XeGTAO) replaces the vanilla SSAO.** Vanilla is hemisphere SSAO with a screen-locked Bayer-128
  dither at half render resolution - a dither fixed to the pixel grid re-rolls every surface point's kernel
  under a jittered camera, which no temporal accumulator can average. Order of work: (1) composite AO
  before the upscaler at render resolution, (2) make the dither temporally varying, (3) only then port
  XeGTAO (MIT, HLSL compute -> GLSL port; 0.56 ms at 1080p on an RTX 2060). Judge it against fixed SSAO,
  not against today's.
- **Acceptance runs driven by the headless harness.** The harness itself is done (above); what is still
  manual is using it - an authored camera path per scene checked in as data, a shimmer number computed
  from a capture rather than judged by eye, and the harness wired into `docs/vulkan-acceptance.md` so a
  backend comparison is a script invocation. See "what it does not cover" under the harness for the gaps.

  *The determinism guard the shimmer number needs.* A shimmer number is a difference between consecutive
  captured frames, so anything that moves for a reason other than the effect under test is measured as
  shimmer. Two captures are comparable only when all of this is pinned and recorded beside the number:
  the world (save file and seed), the camera path (the checked-in `.cam` file, played from a fixed
  in-world frame), the step (`--fixed-dt`, same value), the frame list (same indices, not the same
  count), the graphics settings that change what is drawn (`ssaa`, `fxaa`, `ssaoQuality`, `bloom`,
  `godRays`, `mipMapLevel`, render and display resolution, upscaler and quality preset), the time and
  weather the command script sets, and the backend and GPU/driver the run actually used (from the
  renderer line, not from what was asked for). What is *not* pinned, and therefore may never be read as
  a signal: chunk streaming order and the pop-in it causes, particle and mob RNG, wind phase, and
  anything before the first frame the world has finished loading - so the capture starts well after the
  command script, and mobs and weather are commanded off rather than hoped away.

  A run that drifted is rejected, not reported. The check is mechanical: capture the same scene twice on
  the same backend and settings, and compare the two runs frame by frame (`scripts/dev/ssim.py`). That
  GL-vs-GL (or Vulkan-vs-Vulkan) self-pair is the noise floor, and the shimmer number is only meaningful
  above it. A run whose self-pair falls below the floor agreed in `docs/vulkan-acceptance.md`, or whose
  recorded settings, camera file, frame list or renderer line differ from the reference run's, is thrown
  away and re-run - it is not published with a caveat. Frames that fail to pair by name (a short or
  ragged capture) are the same failure and get the same treatment.

### Bigger, in dependency order

- **Native Vulkan shaders (Phase 3)** - Vulkan-native GLSL for the vanilla program set, explicit sets and
  bindings, compiled offline to SPIR-V; the rewriter stays for mod shaders.
- **Performance (Phase 4)** - disk pipeline cache, per-pass GPU timestamps, transient aliasing on by
  default. Carries the open Milestone 1 gap: Vulkan costs about 25 % more frame time than OpenGL on the
  fixed scene and is GPU-bound.
- **Mod API (Phase 5)** - the pass and motion-writer API in the contracts, fork ports, launcher scan v2.
- **HDR output** - not a swapchain-format switch: the scene colour gains range, a real tone mapper appears
  at the end of the chain, the UI moves to display-referred space, and the swapchain gains HDR10/scRGB with
  its metadata. The upscaler contract changes with it (DLSS switches to `IsHDR = 1`, exposure stops being
  optional) and DLSS-G forbids FP16/scRGB, so an HDR path that wants frame generation must be RGB10A2.
  After Phase 3, alongside the frame-generation format decision.
- **Auto-PBR materials** - a prerequisite for anything specular, and useful on its own. Minecraft shader
  packs (Complementary's Integrated PBR and friends) generate normals from luminance differences in the
  albedo and guess specular from colour, because a resource pack is only pixels. Vintage Story gives us
  more: every block carries a material class (stone, wood, metal, glass, plant, liquid) and its light
  emission, so roughness, metalness and emissive masks come from a curated table keyed on what the block
  *is*, with generated normals filling in surface detail. Generate it at atlas build time into companion
  atlas layers (normal, roughness/metalness, emissive), never per frame; hand-authored PBR layers from a
  resource pack override the generated ones where present. Wanted by the user, 2026-09-12: "I want to use
  some autopbr like some minecraft shaderpacks do when possible." Sits after native shaders (the material
  set convention lands there) and before ray tracing, which needs roughness and normals to be worth
  anything.
  Read from Iris (cloned 2026-09-12, `net.irisshaders.iris.pbr`): Iris itself generates nothing - it is the
  plumbing, and that plumbing is what we copy. Hand-authored `_n` and `_s` textures are loaded per sprite
  and assembled into companion atlases beside the albedo atlas (`PBRAtlasTexture`, `PBRAtlasHolder`,
  `AtlasPBRLoader`), with LabPBR channel packing and, importantly, **per-channel mipmap generation**
  (`ChannelMipmapGenerator`, `DiscreteBlendFunction`) - averaging a normal or a packed specular channel
  across mips the way colour is averaged is wrong. The auto-generation the user is after lives in the shader
  packs instead (Complementary's Integrated PBR derives normals from albedo luminance differences when a
  pack ships no PBR layers); take the technique, not their code - those packs carry restrictive licences.
- **Ray tracing** - last. Acceleration structures over a world the player edits (BLAS per chunk, TLAS over
  loaded chunks, refit as chunks stream) are the hard part; `VK_KHR_ray_query` in the existing fragment
  shaders is the cheaper entry than a full ray-tracing pipeline. Spend rays in this order: ambient
  occlusion (the honest end of the GTAO item), shadows, then water and glass reflections. Every one needs a
  denoiser - DLSS Ray Reconstruction on NVIDIA (`libnvidia-ngx-dlssd.so`, already bound), a spatiotemporal
  denoiser as the cross-vendor fallback. Depends on native shaders, a stable temporal contract, HDR range
  and the headless harness.

## A HUD-less frame and the HUD as its own resource: a requirement of every frame generator

Not a DLSS-G detail - an architectural requirement for the frame-generation slot as a whole (user,
2026-09-12: "This Hudless frame and the Hud as a seperate ressource is a requirement for all FG solutions,
As Framgenning the UI is Ugly af"). Every vendor asks for exactly these two inputs and says so:

- **DLSS-G** lists HUD-less colour and UI as critical, with the UI premultiplied
  (DLSS-FG Programming Guide §5.1).
- **XeFG** specifies the composition itself: `UIonly.RGB + (1 - UIonly.A) * HUDless.RGB`.
- **FSR frame interpolation** exposes UI composition as an explicit option for the same reason.

The failure mode is worse than it sounds and is why no vendor treats this as optional: a HUD is static
while the world moves, so an interpolator given a composited image smears the crosshair and hotbar against
world motion - and the eye is tracking exactly those static elements, so it is the most visible artefact a
generated frame can have.

So the renderer renders the world genuinely HUD-less and composes the HUD afterwards (see step 3 of the
design below for the shape). That structure is vendor-neutral: it is built once, in our own frame, and
every frame-generation backend - DLSS-G now, XeFG and FSR later - reads the same two handles. It also
belongs in the temporal contract, which already reserves the rows for `SceneNoHud` and a UI target
(`docs/temporal-frame-contract.md` §8) rather than leaving each backend to invent them.

## DLSS frame generation: the design

Written from a read-only map of the present path, the frame ring and the render-stage call order against
the DLSS-FG Programming Guide v310.7.0 (2026-09-12). Steps 0 to 4 are implemented since; the status subsections below say what landed and what it cost. The point of this section
is that the next session starts from the order of work and the three hazards, not from the guide.

### What the present path can and cannot do today

`VulkanDevice.Present()` is single-shot: one submit, one acquire, one present-command submit, one
`vkQueuePresentKHR`, return - called once per game-loop tick from the lib's `EndFrame()`. Frame generation
needs that call site to present **N times per rendered frame at even wall-clock spacing**, generated frame
first, then the retained real one.

Three of the four pieces underneath it are already shaped for that, which is the good news:

- `Swapchain`'s `PresentIdCounter` and `PresentIdMap` were built many-present-ids-to-one-frame-id on
  purpose; the class comment says outright that frame generation will present a frame more than once.
- `VulkanContext.QueueLock` exists because `vkQueueSubmit`/`vkQueuePresentKHR` from two threads is
  undefined behaviour, and the client already submits from two threads for uploads. A present thread can
  share it.
- `FrameTimeline.ReserveFrame`/`NoteFrameSubmitted` are already `Interlocked`, so the clock itself is
  thread-safe even though its doc comment still says "render thread".
- `LatencyMarker` already reserves `OutOfBandRenderSubmitStart/End` and `OutOfBandPresentStart/End`
  explicitly for async present paths. Nothing stamps them yet; they are the seam.

Two are not:

- `BlitPresentPath`'s blit source is a constructor-captured delegate hardwired to `_defaultColor`. There is
  no way to present a *different* image - the generated frame, or NGX's `OutputReal` copy - without giving
  `IPresentPath.Record` a per-call source.
- `FrameSlot.BeginPresentCommands`/`SubmitPresent` record into the current render frame's own command
  pool, which the render thread resets in the next `BeginFrame`. A real present thread cannot borrow it;
  it needs its own pools.

### HUD-less colour and the UI, today

The real stage order is `RenderFinalComposition` -> `RenderAfterFinalComposition` -> `BlitPrimaryToDefault`
-> `RenderAfterBlit` -> Ortho (the 2D GUI). `RenderFinalComposition` writes `OptimumCompositeFrameBuffer`,
which *is* the guide's `pHudless` state - but nothing snapshots it, and the very next stage draws
world-space overlays (selection boxes, work-item guides) straight onto the same image. The composited
backbuffer the guide wants exists continuously as `_defaultColor` once Ortho finishes, so that half is
free. `pUI` - premultiplied UI colour plus alpha - has no analogue at all: the GUI is alpha-blended onto
the same buffer as the world and is never isolated. `docs/temporal-frame-contract.md` §8 reached the same
conclusion independently and reserves the rows.

### Order of work

Smallest independently verifiable step first, rising risk. Do not reorder: each step exists to isolate one
unknown from the next.

0. **Two presents per frame, no NGX anywhere.** Present `_defaultColor` a second time inside
   `VulkanDevice.Present()`, tagged Generated against the same latency frame id and stamped through the
   already-reserved out-of-band markers. This isolates the one open mechanical question - whether the
   acquire-semaphore free list and the timeline bookkeeping survive 2x present pressure - from every
   vendor and threading question. Verifiable with a GPU test under `sync,best` (zero new hazards) plus a
   check that both presents carry the identical image. If this breaks, it breaks here, before any vendor
   code exists.
1. **A per-call source on `IPresentPath.Record`**, replacing the captured delegate. Mechanical; behaviour
   is unchanged while every caller still passes `_defaultColor`.
2. **The `SceneNoHud` snapshot**: copy `OptimumCompositeFrameBuffer`'s colour right after
   `RenderFinalComposition` and before `RenderAfterFinalComposition`, published the way
   `MotionAttachmentIndex` is. Verifiable alone - it equals the composited image on a frame with no
   overlays and no GUI, and provably differs once either draws.
3. **The HUD renders into its own target and is composed afterwards** (decided by the user, 2026-09-12:
   "All of these are Ductape Jobs, We geniuenly need to Render the Frame Hudless and recompose the hud
   afterwards. This is the cleanest solution and best practice."). Neither a second GUI pass nor
   differencing the composite: the frame is genuinely rendered HUD-less, and the HUD is composed onto it
   at the end.
   The structure is already most of the way there - the GUI draws last, in the Ortho stage, after
   `BlitPrimaryToDefault`. What is wrong is only that it draws *onto* the presented image. So: give the
   Ortho stage an owned RGBA target cleared to transparent, let the existing GUI draws go there unchanged
   (one pass, not two), and compose it onto the display image with a single fullscreen blend before
   present. That yields both vendor inputs without inventing either - `pHudless` is the display image
   before the compose, `pUI` *is* the target, premultiplied by construction - and it is the structure every
   engine with an upscaler ends up with, because the UI must never enter the image the reconstruction
   consumes.
   Consequences to settle while implementing: the GUI's blend state has to produce premultiplied alpha in
   the target rather than blending against the world behind it; anything that samples the framebuffer
   *during* the Ortho stage (dialog blur-behind, if any) now reads the pre-compose image and must be
   pointed at it explicitly; and the screenshot paths must capture after the compose, not before. With no
   upscaler and no frame generation the compose is the only added cost and can be skipped entirely by
   drawing the GUI straight onto the display image as today, so "off is vanilla" still holds.
4. **`FramesInFlight` 2 -> 3, on its own.** Every arena already sizes off `_frames.FramesInFlight`, so this
   is close to a constant flip - except `AcquireSemaphoreFreeList`'s `imageCount + 1`, which is derived
   from "at most FramesInFlight - 1 presents outstanding", an assumption frame generation breaks. Re-derive
   it, do not just recompile it. Land it before any FG code so `pacing-gate.sh` separates the cost of
   three-deep buffering from the cost of generation.
5. **NGX DLSS-FG bring-up**, mirroring the proven `DlssUpscaler`/`NgxDlssFeature`/`NgxSession`/`NgxLifetime`
   shim pattern for `NVSDK_NGX_Feature_FrameGeneration`. Feed it the composited image as a stand-in for
   `pHudless`/`pBackbuffer` until step 3 lands `pUI` for real.
6. **The present thread and its spacing pacer, last.** It has the least existing scaffolding and is the
   hardest thing here to verify.

### Requirement: frame generation ships paced, with Reflex, or not at all (user, 2026-09-13)

"DLSSFG without pacing (Reflex) is useless and unplayable." Steps 5 and 6 are therefore one deliverable: no build
exposes frame generation to a player before the present thread and its pacer exist and Reflex is told about the
generated presents correctly. A synchronous step 5 - both presents issued from the render thread inside
`VulkanDevice.Present()` - was planned and started, and was stopped for this reason before anything landed.

What NVIDIA's guide actually requires (DLSS-FG Programming Guide v310.7.0, section 7): "The DLSS-FG feature itself
does not handle timing or presentation; this is the responsibility of your application." Present the generated frame
as soon as the evaluate completes; present the retained real frame (`NVSDK_NGX_Parameter_OutputReal`, the
recommended way to retain it) "at the correct time to achieve equal time intervals between frames. This typically
requires you to present asynchronously from the main render thread." And the reason the guide gives for Reflex: the
real frame is held back by the generated one, and Reflex is what keeps that added latency in check.

Shape this implies, to be confirmed by the map before any code: the render thread keeps the Reflex sleep, input,
simulation, rendering, the SR evaluate, the UI compose and the FG evaluate, then hands an (interpolated, real) pair and
the timeline value that completes them to a present thread. The present thread owns the swapchain, presents on its own
queue from the graphics family - marked out-of-band with `vkQueueNotifyOutOfBandNV` so Reflex's in-band accounting
stays the render thread's - presents the generated frame immediately and the real one after a measured half interval,
and stamps the out-of-band present markers. Spacing is measured present to present. Where the graphics family has
only one queue, frame generation stands down with a log line rather than presenting unpaced.

### The paced present: the design (2026-09-13, from the present-thread map)

Settled by the session owner from a read-only map of queues, swapchain ownership, command recording, retirement,
Reflex and shutdown. It replaces steps 5 and 6 with one deliverable.

**Two owners, no shared swapchain state.**
- *Render thread*: Reflex sleep, input, simulation, render, DLSS SR, the UI compose, and the DLSS-G evaluate into
  an (interpolated, real) output pair taken from a pool of `FramesInFlight` pairs (`OutputReal` retains the real
  frame, as the guide recommends). It stamps `PresentStart`/`PresentEnd` around the handoff and reserves the real
  frame's present id at the handoff, in render order - which keeps Reflex's present-id prediction true once
  presentation is asynchronous.
- *Present thread*: owns the swapchain outright - acquire, present, rebuild, slot retirement - on its own queue
  from the graphics family, marked `vkQueueNotifyOutOfBandNV(PRESENT)`, with its own command pool, and stamps the
  out-of-band present markers. Every acquire-record-submit-present cycle is sequential on that one thread, so the
  step-0 hazard of a rebuild between two acquires cannot recur. The render thread asks for a resize or a vsync change
  through a locked request record, never by writing swapchain fields.

**Lifetime without a second retire model.** The present thread's submissions signal a present timeline of their
own. A pair's present submission waits on the Frame-timeline value that completes its evaluate; a pair is reused
only after the present thread has submitted its read, and the next evaluate into it waits on that present-timeline
value inside its submit - a GPU dependency, not a CPU wait. When no pair is free the render thread blocks for one,
which is the back-pressure; the present thread never waits on the render thread's CPU progress, so the two cannot
wait on each other. Rebuilding the pool (a resize) is a handshake: quiesce the present thread, drop stale pairs, wait
its timeline, rebuild, resume. Shutdown stops and drains the present thread before `NgxLifetime.ShutDown` and device
teardown; the headless self-exit takes the same path.

**The pacer.** A deterministic class with an injected clock. The generated frame is presented as soon as its pair
arrives; the real frame at +1/2 of the smoothed rendered-frame interval, or immediately when the next pair is already
due. Present-to-present intervals get their own stats ring, which is what `pacing-gate.sh` judges. Present modes:
never MAILBOX with frame generation (it replaces the queued generated frame with the real one); FIFO with vsync on,
with Reflex's rendered-frame cap at twice the refresh interval so two presents fit each refresh pair; IMMEDIATE with
vsync off, where the pacer owns the spacing.

**Refusals, not degradation.** A graphics family with one queue, a latency backend other than `NvLowLatency2`, or
NGX refusing the feature: frame generation stands down with one log line. `OPTIMUM_DLSSG_DOUBLE_PRESENT` stays as
the test switch and now drives the present thread with duplicate images, so the pacer can be measured on any GPU.

**What a capture means.** `ReadDefaultFramebuffer`, the screenshot and the headless harness keep capturing the real
composed frame; generated frames are not captured.

### Status: steps 0, 1, 2 and 4 landed (2026-09-12, `feat/dlss-g` at 54a685e)

Three parallel streams, one integration, one adversarial review. Build 0 errors; `Optimum.Tests` 1341
passed / 34 pre-existing skips; GPU suite 895 passed, 0 skips, zero `SYNC-` lines; patches regenerate
byte-exact (158 applied), Cecil 205/205 with the dispatch verifier clean.

- **Step 0** (`OPTIMUM_DLSSG_DOUBLE_PRESENT=1`, off by default): a second acquire and a second
  `vkQueuePresentKHR` per rendered frame, same image, same latency frame id, stamped through the reserved
  out-of-band markers. Both blits deliberately ride **one** present command buffer and one submit: the
  first blit is what transitions the frame image to `TRANSFER_SRC`, and submission order is not a
  dependency, so a second command buffer would read it in that layout with nothing guaranteeing the
  transition happened. `FrameSlot.Submit` gained a second binary wait and signal semaphore instead. This
  is precisely what step 6's present thread must solve for real - it cannot inherit that shared buffer.
- **Step 1**: `IPresentPath.Record` takes a per-call `VulkanTexture?` source; the captured delegate is gone.
- **Step 2**: the `SceneNoHud` snapshot, gated on `OptimumSceneNoHudRequested`, created once in the
  framebuffer setup (not per frame slot, not a transient), 512/1024 pixels differ once an overlay draws.
- **Step 4**: `FramesInFlight` 3 by default. It was **not** the constant flip the text above guessed: the
  uniform ring divided a fixed 32 MiB by the slot count (three slots would have silently cut per-frame
  uniform capacity by a third) and three GPU tests encoded "two" structurally. Cost measured at
  1024x1024 with 12 heavy draws, 120 measured frames: +48.0 MiB heap, p50 unchanged (3.959 vs 3.958 ms);
  the tail direction is run-to-run noise across repeats, so three-deep buffering is neutral on throughput
  here and the real number must come from `pacing-gate.sh` in game.
- The acquire bound is `max(imageCount, FramesInFlight x presentsPerFrame) + 1`, **sized unconditionally**
  rather than gated on the double-present switch: the switch is a runtime property while the semaphores are
  created once per swapchain, so sizing for "off" is one flip away from a mid-frame throw. At the shipped
  F=3, P=2, 3 images that is 7, with the doubled run holding at most 6.

### What the review found (all fixed except where noted)

Two of the three hazards below were live; the third is sound by construction.

1. **Lifetime, at the window between the frame's two acquires (HIGH).** The second `TryAcquire` could
   rebuild the swapchain while the frame already held an image and a semaphore from the old slot and
   before `NotePresentSubmitted` ran - so `Build` retired that slot against the *previous* frame's present
   value, and the next `Collect` destroyed its semaphores, images and `VkSwapchainKHR` while this frame's
   present was still pending. Deterministic on every `SUBOPTIMAL` first acquire, and invisible to the
   tests because none of them resized. Fixed: `TryAcquire(out target, bool allowRebuild = true)`, the
   second acquire passes `false`. A frame with a rebuild pending presents once, as before step 0.
2. **A second acquire can exceed what the WSI guarantees (HIGH).** An application may hold
   `imageCount - caps.min + 1` acquired images before `vkAcquireNextImageKHR` may block indefinitely; a
   surface with `min == max` (real on some drivers and compositors) yields a limit of 1, and step 0 asked
   for a second image with an infinite timeout that nothing in flight could release - a hang, not a slow
   frame. Fixed with `SwapchainPolicy.SimultaneousAcquireLimit`; the image count is deliberately **not**
   grown for the feature, because that would cost a display-resolution image on every ordinary run.
3. **`vkQueueNotifyOutOfBandNV` on the shared graphics queue (MEDIUM).** The extension marks the *queue*,
   not a submission, and nothing marks it back - so declaring the device's own graphics queue an
   out-of-band present queue takes every later in-band render submission out of Reflex's accounting.
   Fixed: the backend refuses `_context.GraphicsQueue` and counts the refusal; the call site stays for
   step 6, whose present thread has a queue of its own.
4. **Poison mode was not a reliable diagnostic (MEDIUM).** The poison clear and the first upload of a
   fresh texture are both `TRANSFER` writes into the same image in the same batch, and the batcher emits
   nothing when the usage does not change (`TransferDst` to `TransferDst`) - so a driver may run the clear
   last and leave poison where the uploaded texels belong. This was the "5 sync hazards per poison run"
   that had been written off as a syncval false positive; it was our missing barrier. Fixed at the poison
   site: 5 and 5 reports become 0 and 0.
5. `SetOptimumSceneNoHudIndex(-1)` sat inside the framebuffer fill loop (LOW, fixed).
6. `PresentIdMap` is a fixed 64 entries, so two presents per frame halve its history to 32 frames
   (LOW, **not fixed**): harmless for the latency reports it serves, but it should be sized from
   presents-per-frame when step 6's pacer lands.

Sound, checked by construction rather than by the tests passing: the acquire-semaphore bound (peak
outstanding at the p-th Take is `(F-1)P + (P-1) + 1 = F x P`); command-pool threading (nothing records
from another context yet); "off is vanilla" (one acquire, one present, no second semaphore, no snapshot
target, unchanged image count - the only always-on costs are three binary semaphores and the third frame
slot); and the frame-id to present-id mapping with two presents.

### Carried into step 5 and step 6

- The snapshot's gate is `OptimumConfig.UpscalerReplacesTaa` alone. **Step 5 must add frame generation to
  that disjunction** or FG finds the slot unallocated and `OptimumSceneNoHudCaptured` false.
- Under FIFO the second present halves the presented frame rate, because step 0 presents a duplicate with
  no pacer. Expected, behind the switch. Hazard 3 below is still untouched: nothing measures
  present-to-present spacing, and the missed-vsync detector's interval now also contains the generated
  present's `vkQueuePresentKHR`.
- **The snapshot has now been seen on a real frame** (2026-09-12, headless, Vulkan, DLSS Balanced
  742x493 -> 1280x850): every differing pixel between it and the presented frame lies inside the HUD's
  bounding box, and outside that box the two are bit-identical - 0 differing bytes over 571 532 pixels.
  Numbers and method in `docs/vulkan-acceptance.md`. Taking it also exposed and fixed a harness defect:
  a headless run used to end in a crash report every time, because an unmapped window can only be stopped
  with SIGTERM and its handler closed the window from a signal thread mid-frame. The client now closes
  itself from the render thread (`OPTIMUM_HEADLESS_EXIT_WHEN_DONE`).
- Still missing: the in-game half of the two-versus-three measurement (`pacing-gate.sh` on the fixed scene
  at both frame-ring depths). It cannot come from a headless run - an unfocused window falls under the
  client's 30 FPS background cap, so pacing numbers from one are meaningless. It comes before step 5.

### Status: step 3 landed (2026-09-13, `feat/dlss-g` at c97551e)

The HUD renders into its own RGBA8 target (slot 24, with depth for the GUI's own depth sort) and is composed back
with one premultiplied fullscreen pass inside `ClientMain.RenderToDefaultFramebuffer`, after the Ortho stage and
before `Done` - the position that keeps the with-HUD screenshot, the AVI writer, the parity dump and the headless
capture reading the final frame. While that target is bound, `EnumBlendMode.Standard` resolves to separate alpha
factors (alpha ONE, ONE_MINUS_SRC_ALPHA); everywhere else it is byte-for-byte vanilla. Gated by
`OPTIMUM_UI_TARGET=1` until frame generation ORs itself in. Verified in game on both backends, numbers in
`docs/vulkan-acceptance.md`: opaque UI bit-identical to the HUD drawn directly, translucent UI with zero systematic
offset.

What getting there cost, because each one generalises:
- **The first in-game frame crashed**: `optimumUiTargetClearColor = new float[4]` was null in the patched DLL. The
  Cecil transplant copies no constructor IL, so an injected field's initializer never runs. It was the eighth
  instance of that bug in this repo, past seven hand-written per-instance guards; the rule is now a general test
  (`NoInjectedFieldAnywhereCarriesAnInitializer`), which found five more, all fixed (11a10a8) - including a
  collect-stride throttle that had never once run.
- **OpenGL with an upscaler configured had not reached a world since a8f09ae**, found only because the UI target had
  to be tried on OpenGL. Details in the acceptance doc. The lesson is procedural: a "both backends" check has to
  include OpenGL with the user's real `optimum.json`, and the headless harness makes that cheap.
- The review's three fixes (b56c3e4..1d78b17): the blend scope healed at the frame's first clear so a throwing GUI
  renderer cannot leave it open into the next frame's world pass; the factors restored on every give-up return of
  the compose; and the compose declared as its own frame-graph pass so it stops costing a mid-pass split.

### The three things most likely to go wrong

1. **Resource lifetime past Present.** The generated frame and the retained real frame must survive until
   an out-of-band present thread actually presents them, but every transient and every frame-slot resource
   is retired against the timeline value of the *render* frame that produced it - which assumes it was
   consumed by the time that value completes. A present thread that lags the render thread by one
   `BeginFrame` lets the retire queue recycle an image before it is blitted. That is flicker or garbage
   that no single-frame readback can see; it needs a multi-frame GPU test, and poison mode.
2. **Command-pool threading, not GPU synchronisation.** A present thread needs its own command pools,
   synchronised with the render thread only through `QueueLock` and timeline waits. Getting this wrong is a
   CPU data race on command-buffer state, which `sync,best` validation does *not* reliably catch under
   light interleaving - unlike the GPU hazards it is good at.
3. **Spacing measured at the wrong place.** DLSS-FG wants the generated and real presents of one interval
   evenly spaced in wall-clock time, but nothing today measures present-to-present spacing - the pacing
   model and the frame-interval tracker both measure `BeginFrame` to `BeginFrame`. This is exactly the
   "jitter invisible to screenshots" failure class: it needs `pacing-gate.sh` stddev and percentile numbers
   against the OpenGL baseline of the same scene, never a screenshot.

### Open questions to settle first

- Whether `native/optimum-ngx` already forwards the DLSS-FG entry points (`NGX_VK_CREATE_DLSSG` /
  `NGX_VK_EVALUATE_DLSSG`) or only the super-resolution ones. The plan names them as the target, not as
  implemented.
- Whether `NvLowLatency2Backend`'s current sleep and marker pattern mis-times Reflex once a generated
  present is interleaved. DLSS-G requires Reflex active, so this is not optional.
- The temporal contract needs a v2 for the presentation-lifetime and HUD-less rows (§8 already reserves
  them). Motion vectors and depth (§7.1, §7.3) need no new row - FG consumes the same per-pixel semantics
  super resolution already does.
- HDR interacts: DLSS-G forbids FP16/scRGB, so an HDR path that wants frame generation must be RGB10A2.
  That decision belongs with the HDR item, not this one.

## Last of all: documentation and comment cleanup

Deliberately the final item (user, 2026-09-12: "We do Code documentation and comment cleanup at the very
end"). Until then comments stay as they are and are only ever moved, never trimmed - most of them record
what a defect cost and several carry measured numbers (the 1.05 % leaf rejection, the 0.37 -> 0.02 px
jitter residual, why the NGX shutdown gate exists, why the acquire wait stage may never be ALL_COMMANDS).
Tidying those away before the work is finished would delete the reasoning while the code that needs it is
still moving. When the renderer settles, do one pass: prune what has gone stale, keep every "why", and make
the entry points readable for someone arriving new.

## Known debt

- `TransientAllocator` is implemented but not driven by the frame graph (aliasing is off by default).
- `ClearDepth` ignores the depth write mask (predates the frame graph).
- Vulkan `BuildMipMaps` keeps the texture LOD bias where OpenGL resets it to 0.
- Shaders are whole-file overrides, not patches; a shader patch system against the vanilla archive is
  planned (`CLAUDE.md`, Known debt).
- The item atlas is not LOD-biased under an upscaler, because the GUI draws inventory icons from it at
  display resolution; fixing it properly needs a per-draw or per-unit bias.
- `BarrierBatcher` inserts no dependency for a write-after-write of the *same* usage (`TransferDst` to
  `TransferDst`). The poison/upload case that bites today is fixed at its own site, not in the batcher, so
  any future path recording two transfer writes into one image in one batch has the same gap. Whoever owns
  the batcher should decide whether same-usage write-after-write ought to barrier by default.
- The injected-field-initializer test reads the patcher manifests, so it cannot see fields the patcher adds on its
  own when it transplants a method that uses them (`InjectMissingFieldsForMethod` in `ILPatcher.cs`); those get no
  constructor either. Harmless today (`optimumAccumulatedDelta` starts at 0 either way), but an initializer on such a
  field would pass the test.
- A vanilla static class has a type initializer that may not be inert: `ShaderRegistry`'s publishes uncompiled
  programs into `ShaderPrograms.*`. Optimum code that runs before vanilla's first use of such a type changes
  initialisation order; the OpenGL loading-screen crash of 2026-09-13 was exactly that.
