# Optimum temporal frame contract

This document describes the data produced for native TAA and exposed to mod
renderers through `IOptimumTemporalContext`. The implementation is being refactored;
this document is not a claim that final acceptance tests have passed.

| Thing | Source of truth |
|---|---|
| Input record, reset reasons, jitter sequence | `VintagestoryApi/Client/Render/OptimumTemporalFrame.cs` |
| Entity, standard-model and instance motion histories | `sources/VintagestoryApi/Client/Render/OptimumTemporalMotion.cs` |
| Jitter shear, Halton and phase count | `sources/VintagestoryApi/Client/Render/OptimumTemporalFrame.cs` (`OptimumTemporalMath`) |
| Resource formats, sampler state, history slots | `build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs` |
| Channel semantics and the validity tolerance | `sources/shaders/taa-resolve.fsh`, `taa-skymotion.fsh`, `taa-sharpen.fsh` |
| Per-class motion status | §6 below, and the P3/P4/P5 status tables in `TAA-PLAN.md` |

---

## 1. The per-frame input record

One engine-owned instance, `OptimumTemporal.Frame` (`OptimumTemporalFrame`), exposed read-only as
`OptimumTemporal.Context` (`IOptimumTemporalContext`). It is **mutable and single-instance by
design**: it lives on the render thread only and is read by shader-uniform setters in the hot path,
so it must not allocate per frame. "Immutable" in this contract means *immutable to consumers*:
a consumer reads it, never writes it, and treats every `float[16]` it hands out as read-only.
Values that must outlive the frame have to be copied — the matrix arrays are the live per-frame
arrays, not copies.

`Advance()` is called once per real rendered frame in `ClientMain.MainRenderLoop`, immediately
after `shUniforms.Update()` and before the `Before` render stage.

### 1.1 Members

Types are the declared C# types. "Valid from" names the point in the frame after which the value is
this frame's; before that point it still holds the previous frame's value (or zero).

| Member | Type | Units / space | Valid from |
|---|---|---|---|
| `FrameIndex` | `long` | count, +1 per **real** rendered frame; generated/present ids are a separate counter reserved for frame generation | `Advance` |
| `JitterActive` | `bool` | the temporal window itself: true from `Advance` until `RenderAfterPostProcessing` closes it | `Advance` (set by the caller right after) |
| `JitterPx` | `Vec2f` | render pixels, the offset **actually applied**; `(0,0)` whenever the window is closed | `Advance` |
| `PrevJitterPx` | `Vec2f` | render pixels, the offset the previous frame really rendered with | `Advance` |
| `JitterSequencePx` | `Vec2f` | render pixels, this frame's Halton offset whether or not it is applied | `Advance` |
| `RenderWidth`, `RenderHeight` | `int` | render-resolution pixels (Primary's size, i.e. window size × SSAA/render scale), clamped to ≥ 1 | `Advance` |
| `GetProjection(view)` | `float[16]` | column-major **unjittered** perspective matrix last loaded for that view | that view's `Set3DProjection` |
| `GetPrevProjection(view)` | `float[16]` | the same for the previous frame | `Advance` |
| `IsViewCaptured(view)` | `bool` | whether the view was set up this frame (the hand view is absent in third person) | per `Set3DProjection` |
| `ActiveView` | `EnumTemporalView` | the view the currently loaded projection belongs to; a draw issued now is under this FOV | per `Set3DProjection`; reset to `World` by `Advance` |
| `CameraMatrix` / `PrevCameraMatrix` | `float[16]` | column-major view matrix, **entity view** (camera at the player) | `CaptureCamera`, at `GlLoadMatrix(CameraMatrix)` |
| `CameraMatrixOrigin` / `PrevCameraMatrixOrigin` | `float[16]` | column-major view matrix, **terrain view** (camera at the chunk-relative origin). This is the space the resolve works in | `CaptureCamera` |
| `CameraPosDelta` | `Vec3f` | blocks; `cameraPos(this frame) − cameraPos(previous frame)`, differenced from `EntityPlayer.CameraPos` in **double** precision and then narrowed. Forced to `(0,0,0)` on any reset frame | `CaptureCameraPosition` |
| `Playerpos` / `PrevPlayerpos` | `Vec3f` | blocks, camera relative to the slowly rebased reference position (`DefaultShaderUniforms.PlayerPos`) — the space the warp noise is sampled in | `CaptureCameraPosition` |
| `Warp` / `PrevWarp` | `OptimumWarpState` | every uniform the `vertexwarp.vsh` functions read (see §1.2) | `Advance` (`Warp`), `Advance` (`PrevWarp`, rolled) |
| `Reset` | `bool` | `ResetReason != None` | `Advance`, possibly upgraded by `CaptureCameraPosition` |
| `ResetReason` | `EnumTemporalResetReason` | see §5 | as above |
| `ZNear`, `ZFar` | `float` | blocks (world units), the world camera's near/far planes | `Advance` |
| `Fov` | `float` | **radians**, vertical, the world FOV (`ClientSettings.FieldOfView * π/180`). The hand view's FOV is not in the record — only its projection matrix is | `Advance` |
| `DeltaTimeMs` | `float` | milliseconds since the previous frame | `Advance` |

`OptimumTemporalFrame` additionally exposes, beyond the read-only interface:
`TeleportThresholdBlocks` (`const double`, 8.0), `WasViewCaptured(view)`, `RequestReset(reason)`,
`Advance(...)`, `CaptureCameraPosition(...)`, `RecordProjection(...)`, `CaptureCamera(...)`,
`ApplyJitterCopy(double[])`, `ApplyMotionUniforms(IShaderProgram)`, and a settable `JitterActive`.
Consumers use the interface; only the client owns the mutators.

### 1.2 `OptimumWarpState`

`TimeCounter`, `WindWaveCounter`, `WindWaveCounterHighFreq`, `WaterWaveCounter`, `WindSpeed`,
`GlobalWarpIntensity` (= `DefaultShaderUniforms.GlobalWorldWarp`), `GlitchWaviness`,
`WindWaveIntensity`, `WaterWaveIntensity` (all `float`), `PerceptionEffectId` (`int`),
`PerceptionEffectIntensity` (`float`). Built by `OptimumWarpState.FromUniforms(DefaultShaderUniforms)`.

The counters **wrap** (`DefaultShaderUniforms.Update` takes them modulo 6000), so the previous value
is stored, never derived as current − dt. Some of these are overridden per entity or per pass; the
two that are (`windWaveIntensity`, `waterWaveCounter`) are recorded per draw by
`OptimumEntityMotion` / `OptimumStandardMotion`, not taken from this struct.

### 1.3 Placement in the frame (where each value becomes true)

```
MainRenderLoop
  shUniforms.Update()
  Advance()                      -> FrameIndex, jitter, RenderW/H, ZNear/ZFar/Fov, DeltaTimeMs,
                                    Warp/PrevWarp, rotate cur->prev for every captured value
  JitterActive = EffectiveTaa || TaaJitterDev     (opens the temporal window)
  Before stage                   -> PlayerCamera writes EntityPlayer.CameraPos and shUniforms.PlayerPos
  CaptureCamera(...)             -> CameraMatrix, CameraMatrixOrigin
  CaptureCameraPosition(...)     -> CameraPosDelta, Playerpos, + Teleport / Rebase reset detection
  Set3DProjection(world|hand)    -> RecordProjection: GetProjection(view), ActiveView
  ... jittered scene passes, SSAO ...
  taa-resolve, taa-sharpen
  RenderAfterPostProcessing      -> JitterActive = false     (closes the temporal window)
```

**The rule that produced this layout** (P3 finding (a)): a value is snapshotted against the stage
that *writes* it, not against the top of the loop. Reading `CameraPos` in `Advance` paired a
one-frame-stale translation with a fresh previous rotation, and the difference — the camera's
acceleration — painted motion onto static ground.

---

## 2. Jitter

**Definition.** `JitterPx` is the **raster displacement of a static point**: with the jittered
projection, a point that projects to pixel `p` unjittered projects to `p + JitterPx`. Y is up, in
render pixels, in `[-0.5, 0.5]`, never exactly `(0, 0)`.

**Shear.** Optimum's perspective matrices come from `Mat4d.Perspective` (`clip.w = -z_view`,
column-major `float[16]`). The jitter is one NDC shear on that matrix:

```
P[8] -= 2 * jx / renderWidth;
P[9] -= 2 * jy / renderHeight;
```

`OptimumTemporalMath.ApplyProjectionJitter` is the only implementation; `OptimumTemporalFrame.ApplyJitterCopy`
and `ClientMain.CurrentProjectionMatrix` both go through that convention. **One NDC shear per
frame**: auxiliary targets of other sizes (the quarter-resolution LiquidDepth prepass) inherit the
same NDC shift and never get a per-target pixel offset.

**Sequence.** Halton(2, 3), one-indexed:

```
phaseCount = max(1, ceil(8 * upscale^2))      where upscale = 1 / renderScale
phase      = FrameIndex % phaseCount
jx         = Halton(phase + 1, 2) - 0.5
jy         = Halton(phase + 1, 3) - 0.5
if (jx == 0 && jy == 0) jx = 0.25             // a frame must contribute a new sub-pixel sample
```

At native resolution that is 8 phases; at render scale 0.5 it is 32.

**Scope.** The jitter reaches **only** the perspective matrix `Set3DProjection` last loaded, and
only while `JitterActive`. `ClientMain.CurrentProjectionMatrix` compares the top of the projection
stack element-by-element against that matrix and hands back the sheared copy only on an exact match,
so an ortho stack, a shadow ortho matrix or a caller-pushed matrix is returned unchanged. Shadow,
ortho, offscreen and every post-window pass (AfterFinalComposition overlays, the HUD, AfterBlit
rifts, the blit) are never jittered. `CurrentProjectionMatrixUnjittered` is the escape hatch.

**Writers.** A motion-vector writer's current pixel is `gl_FragCoord.xy - taaJitterPx` on **both**
backends: the Vulkan device renders offscreen unflipped and flips only in the present blit, which is
the entire Y-flip story for the backend.

---

## 3. Resources

All at render resolution unless stated. Render resolution = Primary's size = window size × the
effective render scale (`ssaa`). Sampler state is given as (min/mag filter, wrap).

### 3.1 Primary (frame buffer slot 0) — the producer side

| Attachment | Format | Sampler | Contents |
|---|---|---|---|
| depth | `GL_DEPTH_COMPONENT32` (GL) / `D32_SFLOAT` (Vulkan) | NEAREST, CLAMP_TO_EDGE | window depth, `[0,1]`, **0 = near**, not reversed, `GL_LESS` |
| colour 0 | `RGBA8` | NEAREST (LINEAR when ssaa > 1), REPEAT | jittered scene colour |
| colour 1 | `RGBA8` | NEAREST (LINEAR when ssaa > 1), REPEAT | jittered glow |
| colour 2, 3 | `RGBA16F` | LINEAR, CLAMP_TO_BORDER, white border | SSAO G-buffer (position, normal); present only when SSAO is on |
| colour `MotionAttachmentIndex` | `RGBA16F` | NEAREST (GL); device path leaves the default — the resolve reads it with `texelFetch`, so filtering is not load-bearing | **motion**, see §3.2 |

`ClientPlatformWindows.MotionAttachmentIndex` is **2 without the SSAO G-buffer, 4 with it**, and
`-1` when TAA is off or the attachment failed to allocate. It is appended after every existing
attachment so no existing index moves, and it is **never in the default draw-buffer mask**: a pass
that writes it opens a window explicitly (`BeginMotionWrite` / `EndMotionWrite`, or
`BeginMotionOnlyWrite` for the liquid velocity pass). A window is refused unless Primary is bound
and `JitterActive` is true.
Since 2026-09-11 (Vulkan-native plan, Phase 1A step 2) these members are declared virtual on
`ClientPlatformAbstract` with neutral bodies and `ClientPlatformWindows` overrides them with the
bodies described here; the move changes no semantics of v1.

Cleared to `vec4(0)` each frame — which is what makes `a == 0` mean "nothing wrote here".

### 3.2 Motion attachment channel semantics

```
rg = mv         motion vector, RENDER PIXELS
b  = reactive   [0,1]
a  = writerDepth  window depth in [0,1] at write time
```

- **`rg` — motion vector.** `mv = previousPixel − currentPixel` (current pixel → where it was),
  in render-resolution pixels, **jitter excluded** (both positions come from unjittered
  projections), **undilated**, **not** normalized. History lookup is
  `historyUV = (pixelCentre + mv) / renderSize` — anchored at the unjittered pixel-centre grid,
  because that is the grid the history lives on (P2 finding (c)).
- **`b` — reactive.** `0` opaque, `1 − revealage` for OIT transparents (added by the merge), `1`
  for cube particles, `0.3` for liquid surfaces, `mix(coverage, taaCloudReactive, coverage)` on
  cloud-covered sky. It lowers the history weight in the resolve (`alpha = max(alpha, reactive)`)
  controls TAA history rejection. **Read whether or not
  the pixel passed the validity test** (P3 finding (h)), so a writer that bails out of its vector
  must still deliver `b` and zero only `rg` and `a` (P4 finding (u)).
- **`a` — writer depth, and the validity rule.** `a` is the **window depth in `[0,1]`** the writer
  put in the depth buffer (`gl_FragCoord.z`, plus any depth offset the draw applies) — the same
  space as the depth attachment, **never NDC depth**. The resolve treats the pixel as validly
  written only when

  ```glsl
  bool written = motion.a > 0.0 && abs(motion.a - depth) <= max(2e-4, 8e-4 * depth);
  ```

  The tolerance is half-float aware: the attachment is `RGBA16F`, whose ULP near 1.0 is already
  ~5e-4, so a fixed absolute epsilon rejects every legitimate distant writer. The relative term
  covers precision; the floor covers depths near the near plane. Where the test fails, the resolve
  falls back to camera reprojection. **This is the contract for unknown writers**: mod geometry,
  uninstrumented renderers and sky all land in the fallback without relying on undefined
  unwritten-output contents. The reference resolve evaluates this rule at the nearest-depth tap of
  its 3x3 (note under §4, 2026-09-11); the rule itself is unchanged.

  Consequence, measured (P4 finding (o)): a draw whose depth offset moves the depth buffer further
  than the tolerance — a decal at one block's distance moves it ~1.3e-3 against a tolerance of
  ~7.2e-4 — must write its **own** depth into `a`, not the depth of the geometry it sits on.

- **Blend state.** The motion attachment is **replace**-blended in a window
  (`SetBlendFuncSeparate(MV_LOCATION, 1, 0, 1, 0)`), except in the OIT merge, which is additive
  `(ONE, ONE)` under `FUNC_ADD` with `rg` and `a` written as zero so the opaque vector underneath
  survives bit-for-bit. Per-attachment blend state is **global pipeline state, not per-framebuffer**,
  on both backends: set the global blend mode first, then the per-attachment override (P4 finding (x)).
- **Write order.** The merge's reactive is written first and overwritten by everything after it
  (AfterOIT terrain, AfterOIT entities, decals, the liquid velocity pass, the sky pass), all with
  replace blending. The merge's value survives only where nothing later claimed the pixel
  (P4 finding (y)).

### 3.3 History slots (frame buffer slots 19 and 20)

Two render-resolution slots, selected by frame parity: `TaaHistory(parity)` returns slot **19** when
`(parity & 1) == 0` and slot **20** otherwise. The resolve writes `TaaHistory(_taaFrameParity)` and
reads `TaaHistory(_taaFrameParity + 1)`, then flips the parity. Both are allocated together and both
are null when TAA is off or allocation failed (`TaaTargetsReady`).

| Attachment | Format | Sampler | Contents |
|---|---|---|---|
| 0 | `RGBA16F` | **LINEAR**, CLAMP_TO_EDGE | resolved colour (rgb) + resolved scene alpha (a) |
| 1 | `RGBA8` | **LINEAR**, CLAMP_TO_EDGE | resolved glow. The plan's `b = ssao` slot is **allocated and reserved, written as zero, read by nobody** — resolving SSAO temporally is deferred (P2) |
| 2 | `R32F` (raw GL token `0x822E`) | **NEAREST**, CLAMP_TO_EDGE | previous **linear view depth**, positive, in blocks: `-(viewMatrix * vec4(world,1)).z` in the terrain (camera-relative-origin) space |

The filters are load-bearing and identical on both backends: colour and glow are read at a
fractional reprojected offset (Catmull-Rom over a bilinear sampler for colour, a plain `texture()`
for glow), while an interpolated linear depth across a silhouette belongs to neither surface and
would defeat the disocclusion test. A freshly allocated slot holds **undefined** contents, so the
resolve treats a NaN/Inf history sample as a reset.

### 3.4 Sharpen target (frame buffer slot 21)

One `RGBA16F` render-resolution colour attachment, LINEAR + CLAMP_TO_EDGE, allocated and released
with the two history slots. Holds a sharpened copy of the final Primary composition, after bloom,
god rays and `AfterFinalComposition` overlays. Those effects therefore consume the unsharpened TAA
resolve. Null when TAA is off or when its allocation failed; presentation then reads Primary.
`TaaSharpness <= 0` is a true bypass and the pass does not run; the pass also skips itself
entirely when `OptimumFsrBlitActive()` says FSR 1's RCAS will finish the frame at native resolution.
Known cost (P5 finding (ae)): the target is allocated whenever TAA is on, including at render scales
where the pass can never run.

### 3.5 Other slots the contract touches

`18` = FSR 1, `19`/`20` = history, `21` = TAA sharpen. Primary is `0`, Transparent is `1`,
LiquidDepth is quarter resolution with its own depth-only target.

---

## 4. The resolve's own inputs (the reference consumer)

`taa-resolve.fsh` is the first consumer and the permanent OpenGL fallback. It consumes exactly the
contract and nothing else:

| Uniform | Source |
|---|---|
| `sceneTex`, `glowTex` | Primary colour 0 and 1 |
| `motionTex` | Primary colour `MotionAttachmentIndex` |
| `depthTex` | Primary depth |
| `historyColor`, `historyGlow`, `historyDepth` | the read history slot's attachments 0, 1, 2 |
| `renderSize` | `RenderWidth`, `RenderHeight` |
| `jitterPx` | `JitterPx` |
| `invViewProjJittered` | `inverse(jitter(GetProjection(World)) * CameraMatrixOrigin)` |
| `prevViewProj` | `GetPrevProjection(World) * PrevCameraMatrixOrigin` |
| `viewMatrix` | `CameraMatrixOrigin` |
| `cameraDelta` | `CameraPosDelta` |
| `resetHistory` | `Reset \|\| !historyValid \|\| !WasViewCaptured(World) \|\| the inverse failed` |
| `blendAlpha`, `varianceGamma` | 0.1, 1.25 |

MRT outputs: `outColor` (colour), `outGlow` (glow), `outDepth` (linear view depth) — the three
history attachments. The camera fallback reprojects a finite surface as `world + cameraDelta` and
sky (`depth >= 0.999999`) as a **direction** with `w = 0`, so camera translation cannot move it.

The resolve widens its YCoCg variance clip by at most `0.25 * sigma` where at least three
axial neighbours match the centre depth and show fine luminance contrast. It still clamps the
box to the observed 3x3 colour range. Flat areas and depth edges keep the original clip width.
This is a local resolve rule in both shader twins; it adds no uniform or history resource and
does not change the temporal frame contract.

**Note (2026-09-11): anti-flicker weighting and nearest-depth disocclusion.** A change inside the
reference consumer only: the contract stays **v1** (motion-vector semantics, the §3.2 validity rule,
history formats and slot layout are unchanged). Root cause of the distant-foliage jitter, measured on
parity dumps of both backends: the resolve's single-sample disocclusion test (this pixel's linear
depth against the one history depth under `historyUv`) rejected history on **~3.7%** of distant leaf
pixels per frame, because a sub-pixel leaf hits the leaf in one jitter phase and the far background in
the next; and a fixed current weight let the neighbourhood clip box, moved every frame by that leaf,
drag the history with it. What the resolve does since:

- **Nearest-depth tap.** The 3x3 loop keeps the tap with the smallest window depth (`closestPixel`,
  `closestDepth`). The motion vector comes from that tap: the validity rule is evaluated there as
  `bool written = motion.a > 0.0 && abs(motion.a - closestDepth) <= max(2e-4, 8e-4 * closestDepth);`
  (`motion` and `closestDepth` are read at the same tap, so it is still a writer against its own
  pixel), and the camera fallback, the sky test and the far-minus-near sky direction use that tap's
  reconstructed point. `historyUv` stays anchored at this pixel's centre, reactive stays this pixel's
  own `motion.b`, and the history still stores this pixel's own linear depth.
- **3x3 nearest-depth disocclusion.** The nearest finite history depth in the 3x3 around `historyUv`
  against the nearest tap's linear depth, tolerance `0.5 + 0.08 * closestLinearDepth`.
- **Anti-flicker current weight** (Playdead INSIDE TAA). For pixels not rejected (reset, off-screen,
  NaN history, disocclusion): `alpha = mix(blendAlpha * 1.2, blendAlpha * 0.3, w * w)` with
  `w = 1 - |lumCur - lumHist| / max(lumCur, max(lumHist, 0.2))` on the rectified YCoCg luminance,
  then `alpha = max(alpha, reactive)`. Rejected pixels keep `alpha = 1`.

Measured: leaf-far rejection **~3.7% -> ~1.1%** per frame; the user confirmed on Vulkan that the
distant-foliage flicker is gone. **Never revert** to a single-sample depth test or a fixed blend
weight. Pinned by `TaaResolveTests.AntiFlickerWeightsFollowTheLuminanceDifference`,
`FlippingSubPixelLeafKeepsItsHistory`, `DisocclusionLargerThanTheNeighbourhoodStillResets` and
`MotionComesFromTheNearestDepthTapAtAnEdge` (GPU), `Optimum.Tests/Temporal/ResolveTests.cs`
(source), and gated in the game by `python3 scripts/dev/taa-rejection.py <parity dump dir>` (3x3
leaf-far rejection <= 1.5 percent; `docs/taa-acceptance.md` row A19).

---

## 5. Reset

`EnumTemporalResetReason`, in declaration order: `None`, `WorldLoad`, `Dimension`, `Teleport`,
`Rebase`, `Resize`, `ShaderReload`, `FovChange`, `RenderScale`, `Toggle`, `Screenshot`,
`CameraHistoryLost`.

| Reason | Trigger |
|---|---|
| `WorldLoad` | a world is loaded |
| `Dimension` | dimension change |
| `Teleport` | `\|CameraPosDelta\| > TeleportThresholdBlocks` (8.0 blocks in one frame), detected in `CaptureCameraPosition` |
| `Rebase` | `DefaultShaderUniforms.playerReferencePos` changed — the reference the warp noise and `playerpos` are relative to moved under the world |
| `Resize` | the render size changed, or a framebuffer rebuild invalidated the history (raised by `RebuildFrameBuffers`); also detected inside `Advance` by comparing the previous render size |
| `ShaderReload` | SSAO or shader reload |
| `FovChange` | FOV change |
| `RenderScale` | render-scale change |
| `Toggle` | the TAA setting was toggled (rebuilds the frame buffers and reloads the shaders) |
| `Screenshot` | mega-screenshot capture |
| `CameraHistoryLost` | a frame advanced without reaching `CaptureCameraPosition`, so the next capture would difference across two frames while the history was rendered with a zero delta |

Rules a consumer can rely on:

- `RequestReset` is safe to call several times before the next `Advance`; **the first non-`None`
  reason wins**, so the earliest cause is the one reported.
- A reset frame has `CameraPosDelta == (0,0,0)`.
- A reset clears **both** history sets; the reason exists because the remedies differ (a resize
  reallocates targets, a teleport only clears colour).
- Beyond the enum, the resolve treats two more conditions as a reset **per pixel**: the reprojected
  history sample is off screen, or the history sample is NaN/Inf (freshly allocated slot). NaN
  survives any weighted blend, so it would poison a pixel forever.

---

## 6. Per-class motion status

`exact` = the writer computes the true previous position for that surface. `fallback` = nothing
wrote a valid vector, so the resolve's camera reprojection owns the pixel (exact for static
geometry, wrong-but-bounded for anything that moves on its own). `reactive` = no usable vector; the
reactive value is what prevents the smear. Consolidated from the P3, P4 and P5 status tables in
`TAA-PLAN.md`.

| Class | Vector | Reactive | Note |
|---|---|---|---|
| Chunk opaque (passes 0, 1, 2, 8), chunk topsoil | exact | 0 | `prevRel = truePos + cameraPosDelta`, warp replayed from `PrevWarp`, z-offset applied to both clips |
| Chunk opaque pass 7 (AfterOIT overlay) | exact | 0 | own window in `RenderAfterOIT` |
| LiquidDepth prepass | none, by design | – | own quarter-res target; jittered by the shared NDC shear, never in the motion mask |
| Liquid surfaces | exact | 0.3, compile-time constant | dedicated `chunkliquidmotion` velocity pass into Primary, depth test on **and depth write on**, so `a` matches the buffer |
| Skinned entities, batched opaque | exact | 0 | previous model matrix + `AnimationPrev` bones, hooked on the one bone upload every entity draw makes |
| Skinned entities, OIT | none | `1 − revealage` | six OIT outputs already fill Transparent |
| Skinned entities, AfterOIT (`DoRender3DAfterOIT`) | fallback | 0 | arbitrary per-renderer shaders; stays outside a window |
| First-person hands, echo chamber | exact | 0 | own programs; hands reproject through `GetPrevProjection(Hand)` |
| Held items, dropped items, block-entity models, quern top | exact | 0 | `OptimumStandardMotion.Apply` + a narrow per-draw window |
| Movers (helve hammer, resonator disc, fruitpress mash, pot lid, bloomery/forge/firepit contents, falling blocks) | exact | 0 | keyed on the **drawn thing**, not the renderer (P4 findings (q), (r)) |
| Static standard-shader users (anvil parts, molds, signs, chest labels, knapping, clay forming, ground storage, crucible, support-beam preview) | fallback | 0 | static in the world, so camera reprojection is the right answer; each on a scanned exemption list with a reason |
| Forge / anvil work items | fallback | 0 | drawn on the mod's own `smithingWorkItemShader`, which declares no motion output |
| Instanced mechanical power | exact | 0 | per-instance previous transform in the instance stream, history keyed on the device object |
| ClothManager (shares the instanced program) | fallback | 0 | 20-float instance mesh, draws outside the window; missing attributes read `(0,0,0,1)` = no history |
| Cube particles | fallback, camera-only | 1 | the instance stream carries position and scale only, so there is no previous per-particle position. **No per-particle object motion** |
| Quad particles, OIT entities, liquid shading, aurora | none | `1 − revealage`, additive | the merge adds `anet` into `b` alone; `rg`/`a` written as zero |
| Sky colour, night sky | none, by design | 0 | depth test off for the whole pass, so depth stays 1 and the resolve's infinite-direction fallback is the **exact** answer |
| Sun, moon, celestial objects | fallback, bounded | 0 | depth tested, never written; the fallback ignores only the celestial rotation, ~0.004° per frame |
| Volumetric clouds, aurora | camera-rotation-only | `mix(coverage, taaCloudReactive, coverage)` on sky pixels | `taa-skymotion` claims depth-1 pixels under `GL_LEQUAL`. **The cloud's own scrolling is not in the vector** (P4 finding (n)) — handled through reactive rejection rather than complete object motion |
| Clear sky (no cloud coverage) | exact | 0 | coverage 0, so the dithered gradient keeps full history weight |
| Decals | exact | 0 | own writer: chunk previous path + `PrevWarp` + both z-offsets, `a = gl_FragCoord.z` |
| AfterFinalComposition overlays (work-item guides, selection boxes, wireframes) | none | none | outside the temporal window; the motion window is refused on `JitterActive` |
| Rifts (AfterBlit) | none | none | default framebuffer, outside the window. Recorded as the frame-generation gap |
| Mod geometry via `IRenderAPI` | fallback | 0 | writer-depth mismatch, by design. An opt-in writer API is future work |

Two classes are known-wrong data for a **vendor** consumer even though they are right for the
in-house resolve: cube particles (camera-only vector at reactive 1) and volumetric clouds
(camera-rotation-only vector). Both are listed here so an upscaler or frame generator adapter does
not discover them by looking at smeared output.

### Motion limitations

Cube particles have camera-only fallback motion. Cloud and aurora motion captures
camera rotation, not the content's own scrolling. Their reactive values are part of
TAA history rejection; they do not establish accurate object motion.

Vendor upscalers and frame generation are outside this PR. No vendor adapter is
implemented or specified by this contract.
