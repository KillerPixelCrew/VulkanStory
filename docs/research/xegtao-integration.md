# XeGTAO on the Vulkan path

Research notes for integrating XeGTAO (MIT, GameTechDev) into the Vulkan renderer's TAA path. Collected 2026-09-15. The integration plan at the end is what the roadmap's "XeGTAO" step follows.

**Summary.** XeGTAO ports cleanly to GLSL compute. There are two real porting problems:

- **Prefilter pass:** it depends on HLSL `groupshared` memory and has to be restructured.
- **Coordinate frame:** a GL-style projection with depth remapped to [0,1] needs a small constants and normals mapping, derived below.

With TAA: NoiseIndex = frame % 64 and a single denoise pass. On GL 3.3, keep vanilla SSAO.

Source files are cited by their GitHub URLs: [XeGTAO.h](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.h), [XeGTAO.hlsli](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.hlsli), [vaGTAO.hlsl](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/vaGTAO.hlsl) and [README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md).

## 1. XeGTAO specifics

**Inputs**

- **Depth:** raw depth, turned into view-space depth by `z = DepthUnpackConsts.x / (DepthUnpackConsts.y - d)`, with far values clamped to 65504 for fp16 ([hlsli](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.hlsli)).
- **View space:** positive Z forward. Screen UV origin is top-left; view +Y is up (`NDCToViewMul = (2·tanX, −2·tanY)`, `NDCToViewAdd = (−tanX, tanY)`) ([XeGTAO.h](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.h)).
- **Normals:** optional but recommended, in view space. A separate normals-from-depth pass exists ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)). Shading normals keep more detail than geometry normals ([issue #3](https://github.com/GameTechDev/XeGTAO/issues/3)).
- `GTAOUpdateConstants` reads D3D-style matrix entries, and its handedness fix carries the comment "I think it is [correct]" ([XeGTAO.h](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.h)). Don't reuse it for a GL matrix.

**Passes**

| Pass | What it does | Threads / dispatch |
|---|---|---|
| Prefilter | Writes view-space depth into 5 mips (weighted-average filter) | 8×8 threads, each handling a 2×2 block; dispatch `(W+15)/16` ([vaGTAO.hlsl](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/vaGTAO.hlsl)) |
| Main | GTAO integral; writes the AO term and packed edges | 8×8, dispatch `(W+7)/8` |
| Denoise | Edge-aware 3×3 blur, two horizontal pixels per thread | Dispatch X ≈ `((W+1)/2+7)/8` (read from the code); the last pass multiplies back by 1.5 |

- **Quality levels** (slices × steps per side): Low 1×2, Medium 2×2, High 3×3, Ultra 9×3 ([vaGTAO.hlsl](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/vaGTAO.hlsl)).
- **Default constants:** RadiusMultiplier 1.457, FalloffRange 0.615, SampleDistributionPower 2, ThinOccluderCompensation 0, FinalValuePower 2.2, DepthMIPSamplingOffset 3.30, DenoiseBlurBeta 1.2 (1e4 disables denoise) ([XeGTAO.h](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.h)). The README gives 3.15 for the sampling offset, so docs and code disagree ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)).
- **Noise:**
  - A Hilbert index (64×64 tile) drives an R2 sequence, plus `288·(NoiseIndex%64)` per frame ([vaGTAO.hlsl](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/vaGTAO.hlsl)).
  - NoiseIndex is `frame%64` when denoising with TAA, otherwise 0 ([XeGTAO.h](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.h)).
  - Computing the Hilbert index in the shader costs about 7%; a lookup texture avoids that ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)).
- **Formats:**
  - Working depth: R16F ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)).
  - AO: `R8_UINT`, stored as visibility / 1.5.
  - Edges: `unorm` R8.
  - Bent normals: packed RGBA8, bent normal in xyz and visibility in w, adding about 25% cost ([hlsli](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.hlsli), [README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)).
- **Errata:**
  - The repository is discontinued and was archived in April 2024 ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)).
  - Open issue #7: a biased hash in `vaNoise`, which only affects the non-default hash noise path ([#7](https://github.com/GameTechDev/XeGTAO/issues/7)).
  - The README says "5×5 denoise" but the code is a 3×3 kernel; the question was never answered ([#6](https://github.com/GameTechDev/XeGTAO/issues/6)).
  - Code comments admit several weak spots: `RotFromToMatrix` is "not tested… especially 16-bit floats", there is a fudge for over-darkening on slopes, fp16 plus 32-bit depth is an `#error`, and the depth bias is 0.99920 for fp16 vs 0.99999 for fp32 ([hlsli](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.hlsli)).

## 2. Existing ports

- **Bevy** ([PR #7402](https://github.com/bevyengine/bevy/pull/7402), [source](https://github.com/bevyengine/bevy/tree/main/crates/bevy_pbr/src/ssao)):
  - Uses R16F if the adapter supports it for storage, otherwise R32F.
  - Hilbert lookup texture (64×64, u16), and the same `288·(frame%64)` noise when TAA jitter is active.
  - One 3×3 denoise pass of one pixel per thread (XeGTAO does two pixels per thread and two passes).
  - Its `textureGather` component indices match XeGTAO's.
  - It **stores raw NDC depth in the mips** and reconstructs positions through `view_from_clip`, which handles reversed-Z generically. It samples depth with a *linear* sampler, which XeGTAO warns causes interpolation artefacts.
  - The main pass has since become visibility-bitmask AO (VBAO).
  - AO only affects indirect diffuse ([docs.rs](https://docs.rs/bevy/latest/bevy/pbr/struct.ScreenSpaceAmbientOcclusion.html)).
  - Pitfalls from review: packing workarounds and r32float fallbacks ([PR #7402](https://github.com/bevyengine/bevy/pull/7402)).
- **DiligentFX:** based on XeGTAO, uses pixel shaders for depth convolution so it runs on WebGL, supports half resolution with depth-aware upsampling, and uses a ReBLUR-style denoiser because XeGTAO's was not enough for large radii ([DiligentFX](https://github.com/DiligentGraphics/DiligentFX/tree/master/PostProcess/ScreenSpaceAmbientOcclusion)).
- **ReShade BaBa_XeGTAO:** fragment-shader-only port with no depth mips, an à-trous denoiser, its own temporal history and bilateral upsampling ([source](https://github.com/BarbatosBachiko/Reshade-Shaders/blob/main/Shaders/BaBa_XeGTAO.fx)).
- **Unreal (VisionGTAO):** its README says to keep denoise on with TAA/TSR/DLSS and to apply AO before fog and translucency ([VisionGTAO](https://github.com/JustinDarlington/VisionGTAO)).
- **Unity:** the aaaa-rp SRP includes XeGTAO with bent normals ([aaaa-rp](https://github.com/Delt06/aaaa-rp)).
- **Godot 4** uses ASSAO, not GTAO ([godot#101961](https://github.com/godotengine/godot/pull/101961)).
- **three.js** GTAO is a WebGL fragment shader with no XeGTAO lineage ([GTAOShader](https://raw.githubusercontent.com/mrdoob/three.js/dev/examples/jsm/shaders/GTAOShader.js)).

## 3. HLSL to GLSL compute

**Straight mappings**

- `[numthreads]` → `layout(local_size_x=8, local_size_y=8)`.
- `SV_DispatchThreadID` / `SV_GroupThreadID` → `gl_GlobalInvocationID` / `gl_LocalInvocationID`.
- `frac`/`lerp`/`saturate` → `fract`/`mix`/`clamp`; `asfloat`/`asint` (in FastSqrt) → `intBitsToFloat`/`floatBitsToInt`.
- `textureGather` returns texels in the order (i0j1, i1j1, i1j0, i0j0), the same as `GatherRed` ([GLSL built-ins](https://docs.vulkan.org/glsl/latest/chapters/builtinfunctions.html)).

**Porting traps**

- **Matrix order:** HLSL `mtx[r][c]` is row-major and GLSL is column-major, so `RotFromToMatrix` needs a transpose. This only matters for bent normals.
- **`groupshared`:** GLSL has no workgroup-shared memory. SPIR-V allows a Workgroup storage class in Vulkan compute ([SPIR-V environment](https://docs.vulkan.org/spec/latest/appendices/spirvenv.html)), but compiling the HLSL through DXC was historically blocked by `GroupMemoryBarrier` being unimplemented ([DXC #795](https://github.com/Microsoft/DirectXShaderCompiler/issues/795)). Fix: split the prefilter into separate dispatches (see the plan below).
- **Storage formats:**
  - Declare every storage image with a format qualifier matching the Vulkan format, and check `STORAGE_IMAGE_BIT` first ([Vulkan Guide](https://docs.vulkan.org/guide/latest/storage_image_and_texel_buffers.html)).
  - The mandatory storage list includes RGBA8_UNORM, RGBA16F and R32F but **not R8_UNORM** ([Vixen #612](https://github.com/Rikarin/Vixen/issues/612)).
  - The gpuweb capability table marks R8_UNORM and R16F storage on Vulkan as conditional, and R32F, R32UI and RGBA8 as universal ([gpuweb wiki](https://github.com/gpuweb/gpuweb/wiki/Texture-format-capabilities)).
  - `shaderStorageImageExtendedFormats` only guarantees support; enabling it does nothing ([VkPhysicalDeviceFeatures](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceFeatures.html)).
  - Per-format coverage from vulkan.gpuinfo.org could not be obtained (HTTP 403), so query at runtime.
- **Precision:** `min16float` → `mediump` becomes RelaxedPrecision in SPIR-V, which drivers may ignore ([zeux notes](https://gist.github.com/zeux/c83001968e06fe0b789fa4bd513860c6)). Mesa 22.3 fixed RelaxedPrecision bugs ([Mesa notes](https://docs.mesa3d.org/relnotes/22.3.0.html)), and XeGTAO itself saw fp16 slowdowns on some GPUs ([README FAQ](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)). Start with `float`.
- **Constants block:** all members are paired `vec2`s and scalars, 96 bytes. The std140 layout matches the HLSL cbuffer and fits in push constants (Vulkan guarantees at least 128 bytes). Calculated from the struct, not taken from a source.

## 4. TAA coupling

- **Denoise pass count:** since v1.21, one pass "is enough when TAA [is] enabled" ([XeGTAO.h](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/XeGTAO.h)).
- **Noise and history:** XeGTAO relies on TAA plus temporal noise, and temporal variance has to stay low enough that TAA doesn't treat the noise as detail ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)). That matters for our resolve's 3×3 neighbourhood clamp, since noisy AO widens the clamp box. Unverified; measure it.
- **Where AO is applied:**
  - XeGTAO's sample dims probe diffuse and specular light, plus micro-shadowing on direct light ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)).
  - Bevy applies it to indirect diffuse only.
  - Applying before the resolve, as the renderer now does with SSAO (41373cf), lets TAA integrate the noise.
- **Performance and resolution:**
  - High preset: 2.39 ms at 1080p on i7-1195G7 integrated graphics; 0.56 ms on an RTX 2060.
  - Medium costs about 2/3 of High, and Low about 2/3 of Medium.
  - The paper runs at half resolution. XeGTAO defaults to full resolution and suggests half resolution with a bilateral upsample if that's still too slow ([README](https://github.com/GameTechDev/XeGTAO/blob/master/README.md)).
  - No Arc 140V numbers exist; measure.

## 5. Vulkan compute integration

- **Same command buffer is fine:** dispatch, then a barrier from GENERAL/`SHADER_WRITE` to `READ_ONLY_OPTIMAL`/`SHADER_READ` before the fragment pass ([sync examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)).
- **Storage image rules:** layout must be GENERAL, the image needs `STORAGE` usage, and views need identity swizzle ([VkWriteDescriptorSet](https://docs.vulkan.org/refpages/latest/refpages/source/VkWriteDescriptorSet.html)).
- **Mip views:** one view per mip for writes, as XeGTAO does ([vaGTAO.hlsl](https://github.com/GameTechDev/XeGTAO/blob/master/Source/Rendering/Shaders/vaGTAO.hlsl)); Bevy's mip0–4 bindings follow the same pattern.
- **Frame graph:** model GTAO as one node that reads Depth and Normal and writes AO, with transient internal images and no readbacks. Profile with timestamp queries.

## 6. GL 3.3 fallback

- Core GL 3.3 has no compute and no `textureGather` (that arrived with [ARB_texture_gather](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_texture_gather.txt)/GL 4.0).
- Fragment-shader ports exist (ReShade, DiligentFX for WebGL), but they drop or rework the mips and the denoiser.
- Godot's GL renderer went with a trivial SSAO (S4AO, about 0.6 ms on a GTX 1650 Ti) ([Godot PR #109447](https://github.com/godotengine/godot/pull/109447)).
- **Decision:** keep vanilla SSAO on GL.

---

## Integration plan for this renderer

1. **Constants from a GL projection with remapped depth.** Derived here, not taken from a source; verify with step 7.
   - Take `A = P[2][2]` and `B = P[2][3]` (math row/column notation; in a column-major array these are `m[10]` and `m[14]`). With `d = (z_ndc+1)/2`:
     - `DepthUnpackConsts = (−B/2, (1−A)/2) = (n·f/(f−n), f/(f−n))`
     - For an infinite far plane this becomes `(n, 1)`.
     - This is the same form as D3D, so the shader needs no changes.
   - `tanX = 1/P[0][0]` and `tanY = 1/P[1][1]`. Keep XeGTAO's `NDCToViewMul`/`NDCToViewAdd` unchanged.
   - Normals, mapped from GL view space (z pointing backwards):
     - **G-buffer stored upright** (row 0 = top of screen): pass `(n.x, n.y, −n.z)`. This is a mirror; visibility only uses dot products and lengths, so it is unaffected. Bent normals are uncertain here.
     - **G-buffer stored bottom-up:** pass `(n.x, −n.y, −n.z)`, which is a proper rotation.
   - TAA jitter: ignore it at first; the error is sub-pixel.
2. **Formats**
   - **Working depth:** R32F with `XE_GTAO_FP32_DEPTHS` and the half-precision path off (the code requires this combination).
     - R32F storage is universally supported.
     - fp16 steps are 0.5 m between 512 and 1024 m, which is coarse against a 0.5 m radius at voxel view distances.
     - R16F is an option where supported.
   - **AO and edges:** R8_UNORM where `STORAGE_IMAGE_BIT` is set, otherwise RGBA8_UNORM (AO in r, edges in g).
   - **Hilbert lookup:** a sampled R16_UINT texture.
   - **Bent normals:** skip for now.
3. **Passes**
   - **Prefilter:**
     - Pass A writes mip0 and mip1: one gather per thread, dispatch `(W+15)/16`. Guard writes that fall outside the image.
     - Passes B–D build mips 2–4, each from the previous mip with `texelFetch`.
   - **Main:** quality level set by shaderc macros, dispatch `(W+7)/8`.
   - **Denoise:** 1 pass with TAA (final pass multiplies by 1.5). Offer 2–3 passes when TAA is off, ping-ponging between images with pre-built descriptor sets.
   - **Sampler:** point, clamp, NEAREST mipmap mode.
4. **TAA:** `NoiseIndex = frame % 64` when TAA and denoise are on, otherwise 0.
   - Composite before the resolve, and don't apply AO to the glow attachment.
   - Longer term, move AO onto the ambient terms only.
5. **Presets and tuning**
   - Integrated GPUs: Medium at full resolution. Discrete GPUs: High. Ultra only for screenshots.
   - Add half resolution with bilateral upsampling later.
   - EffectRadius (in blocks): start around 0.5–1.0 and tune; this is a starting guess.
6. **GL backend:** keep vanilla SSAO.
7. **Verification**
   - **Frame check:** compare linearised depth and reconstructed XY against the view-space position attachment. Target under 0.1% error away from sky and far pixels.
   - **Normals check:** debug-view the normals and edges.
   - **Reference:** a numpy CPU version of the main pass on depth and normals captured by the headless harness, compared with PSNR and [ꟻLIP](https://github.com/NVlabs/flip).
   - **Convergence:** average 64 NoiseIndex frames with a static camera to get a converged image, and diff the TAA output against it.
   - **Temporal stability:** per-pixel temporal standard deviation with a static camera, and frame-to-frame reprojected differences in motion.
   - **Hardware:** cross-check NVIDIA, AMD, Intel and Mesa (lavapipe as a deterministic CPU reference), with per-pass GPU timestamps on the Arc 140V against vanilla SSAO.
