---
name: vulkan-parity-debug
description: Debug a rendering difference between the OpenGL path and the Vulkan backend (missing post-processing, wrong filtering, transparency, colours). Baseline capture, trace and dump analysis, GL-vs-device state diff, GPU regression test, in-game verification.
---

# Vulkan rendering parity debugging

## 0. Read the layer's log first (2026-09-11)
`OPTIMUM_VULKAN_VALIDATION=1` now logs to `$TMPDIR/optimum-vulkan-validation.log` (or set the
variable to an absolute path). `OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best` adds synchronization
and best-practices validation through VK_EXT_validation_features. Run the game for ~30 s, then
`grep "\[error\]"` and `sort | uniq -c` the warnings. Before this the messages went nowhere and a
whole class of bugs (undefined writes into unwritten attachments, present-path waits) was "0 errors".
Known external noise: MangoHud's overlay pass (`vkCmdBeginRenderPass`, old-style barrier) reports a
READ_AFTER_WRITE on the swapchain image; the backend uses dynamic rendering, so that one is not ours.

## 0b. What single frames cannot show
Frame-to-frame alternation (SSAO noise, a stale present, a swapped history) looks converged in every
screenshot and identical on both backends per frame. When the user reports "flickers between frames"
and per-frame probes agree, stop probing frames: read the validation log, check what differs in
*undefined* behaviour between the APIs (unwritten fragment outputs, missing waits, image aliasing),
or capture a 60 fps sequence (`ffmpeg -f x11grab`) and diff consecutive frames per region.

The Vulkan backend substitutes the platform: `VulkanClientPlatform` overrides `ClientPlatformWindows`'
graphics members. Every bug so far was a state difference between a member's OpenGL body and its
Vulkan override, not shader maths.

## 0. Temporal artefacts on foliage or thin detail: audit the TAA resolve first
The 2026-09-11 "Vulkan TAA jitters on distant trees / looks disabled" bug was not a parity gap: it was
`sources/shaders/taa-resolve.fsh` rejecting history per sample on sub-pixel foliage and blending with a
fixed weight, on both backends. Before any OpenGL-vs-Vulkan capture for a temporal complaint:
1. Read the resolve's rejection (disocclusion, reset, off-screen, NaN), clip and weighting against known
   practice (Karis 2014, Playdead 2016). The nearest-depth 3x3 disocclusion and the luminance anti-flicker
   weighting must still be there.
2. Quantify from an existing parity dump: `python3 scripts/dev/taa-rejection.py <dump dir>` (history depth
   slots 19/20 give this frame's and last frame's linear depth).
3. Only if the resolve is clean and the numbers are low, continue with the parity procedure below.

## 1. Baseline before touching code
- `RENDERER=vulkan OPTIMUM_VULKAN_VALIDATION=1 OPTIMUM_RENDER_TRACE=/tmp/before.trace scripts/dev/run-client.sh`
- confirm `scripts/dev/client-renderer.sh` says Vulkan; screenshot to `/tmp/vulkan-before.png`
- same scene on `RENDERER=opengl`, screenshot `/tmp/opengl.png`; Read both and write down the differences in words.
- Trace summary (python): map `program N 'name'` lines to ids, count `fullscreen program=` per name,
  list `validation:` lines with `[error]`. Passes that never run are one class; passes that run but
  produce nothing are the other.
- Dump the intermediates from a live frame: `OPTIMUM_DUMP_TEXTURES=<ids from the trace's bind lines>
  OPTIMUM_DUMP_DIR=/abs/dir OPTIMUM_DUMP_AFTER_SECONDS=60`; build a contact sheet with PIL and Read it.
  Texture ids: `bind unit=U texture=T` lines right before a pass's `fullscreen` line.

## 2. Diff the two paths, do not theorise
For the pass that is wrong, open the method in `build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs`
(or the mod renderer) and read the `if (optimumDevice != null) {...}` branch next to the GL branch,
plus the framebuffer setup pair `SetupOptimumFrameBuffers` / `SetupDefaultFrameBuffers`. Check every
item in this list on both sides:
- texture create: format, mip levels, `TexParameter` min/mag filter, mipmap mode, wrap S/T, border colour, compare mode
- samplers: `GenSampler`/`BindSampler` semantics (the "linear" flag changes magnification only; min is NEAREST_MIPMAP_LINEAR)
- blend: `glEnable(BLEND)` vs `SetBlend(enabled, mode)` (the latter rewrites per-attachment factors; use `SetBlendEnabled` to toggle only), `glBlendFunci` per attachment
- draw buffers: `glDrawBuffers` vs `SetDrawBuffers(fbo, mask)`; an enabled-but-unwritten attachment is undefined
- clears per attachment, depth mask/test/func, cull, viewport for sub-resolution targets, scissor
- attachment indices and texture-id bookkeeping (`FrameBufferRef.ColorTextureIds`)
- **format table**: every `PixelInternalFormat` a target uses must exist in `Optimum.Render.Vulkan/Core/GlEnums.cs`;
  a missing entry falls back silently (R32F became RGBA8 and quantised the TAA history depth: near
  stable, distance shimmering). `rg "0x[0-9A-F]{4} =>" GlEnums.cs` against the formats in `SetupDefaultFrameBuffers`.
- **clears vs draw-buffer mask**: on Vulkan `ClearColor(attachment)` is a no-op while that attachment
  is masked out of `SetDrawBuffers`; GL clears it regardless. Enable, clear, restore the mask.
Write the list of mismatches first; then fix them all, not the first one.

Symptom-to-class hints (from the TAA round, 2026-09-10): "no AA, just jitter" = history never
accepted (validity/format); "stable near, unstable far" = precision of a depth-like input;
"per-quad noise in a debug view of a cleared target" = clear not happening (mask or between-frame no-op).

## 2b. Instrument the shader instead of guessing (Codex's method, 2026-09-10)
When a pass "does nothing" or "wobbles" and the inputs are hard to inspect, temporarily rewrite the
pass's fragment shader to OUTPUT ITS INTERNAL SIGNALS AS COLOUR and look at the screen:
- Save the original: `cp sources/shaders/<pass>.fsh /tmp/<pass>-original.fsh`.
- Patch the deployed copy directly (no rebuild needed): edit `sources/shaders/<pass>.fsh` and copy it to
  `.vanilla/win-x64/vintagestory/assets/game/shaders/<pass>.fsh`; the game loads it at start.
  Example for the TAA resolve: `outColor = vec4(alpha, clamp(length(mv)/4.0, 0, 1), resetHistory != 0 ? 1 : 0, 1)`
  shows blend weight, motion magnitude and reset per pixel; early-out branches get a fixed colour
  (`vec4(0,0,1,1)`) so you can see which path ran.
- Replace real inputs with CONTROLLED ones to split the chain: a checkerboard or diagonal pattern as
  "current" proves the resolve+display copy are identical on both backends; a static pattern under the
  live jitter proves accumulation on its own, independent of wind, lighting and foliage.
- Freeze the world for comparisons: `/time set 12:00`, `/weather set clearsky`, `/weather setprecip -1`,
  still camera, screenshot pairs 1 s apart, numeric diff of a crop.
- Test allocator luck explicitly: fill a suspect texture with deliberately non-zero data before the pass
  (cold-start dumps that happen to read zero hide a missing clear).
- Restore the original shader afterwards and re-deploy; never commit the instrumented version.

## 2c. Measure instead of asking "does it still jitter"
Two screenshots 1 s apart, still camera, `/weather setw still` (foliage sway otherwise dominates), noon,
clear sky. Mean absolute luminance diff over the centre 60% crop, repeated for ~7 pairs per backend,
compare medians. TAA at parity: Vulkan 1.84 vs OpenGL 1.87 (medians 1.74/1.72). Above ~3 on one
backend only is a real bug; equal-but-high means the scene (wind, water, temporal storm) is moving.
Hunger damage and temporal storms change the picture mid-run: creative mode or `/player .. gamemode`.

## 3. Fix, test, verify
- Backend changes in `Optimum.Render.Vulkan/` (platform overrides in `Platform/VulkanClientPlatform.*.cs`),
  new platform virtuals on `ClientPlatformAbstract` in `build/` + Cecil list (see patch-workflow skill),
  fork-only device calls in `OptimumForkGraphics` (`VintagestoryApi/Client/optimum-render-device.cs`).
- Add a GPU readback test per fix in `Optimum.Render.Vulkan.Tests` (draw with a translated shader,
  read the pixel, assert; readbacks must happen inside a frame). For temporal state, the test must
  span several frames in flight with Present between them and no readback/wait in the loop
  (`TemporalHistoryAcrossFramesInFlight` in `VulkanDeviceIntegrationTests`): single-frame tests
  passed while both TAA bugs were live.
- `make deploy`, run Vulkan with validation, screenshot after; run OpenGL; compare live. Then
  `dotnet test Optimum.Render.Vulkan.Tests`, `dotnet test Optimum.Tests -c Release`, `bash scripts/check-patches.sh`.
- Keep evidence (before/after PNGs, logs) in the scratchpad and cite it in the report and commit.
