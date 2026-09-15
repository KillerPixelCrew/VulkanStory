# Vulkan-native shaders: the interface contract

Every native program family is written against this document. It turns plan section C ("Shaders",
`docs/vulkan-native-plan.md`) and decision 9 into rules precise enough that seven families can be rewritten in
parallel and still link against one pipeline layout, one manifest and one runtime.

Inputs:
- the plan's set convention, uniform placement and define matrix;
- `docs/research/vulkan-bindless.md` and `docs/research/vulkan-descriptor-model.md`;
- the committed `sources/shaders-vk/include/bindings.glsl` and `Shaders/SetConvention.cs`;
- four read-only maps of the tree taken on 2026-09-15: the program corpus, uniform write frequency, native
  integration seams, and the motion writers. Their findings are restated below where they decide something.

## 1. Files

- **Program sources:** `sources/shaders-vk/<program>.vert` and `.frag`, named after the program's `PassName`
  (`chunkopaque`, `taa-resolve`, ...). Never `.vsh`/`.fsh` (`SetConventionTests` enforces it), so no packager
  glob over `sources/shaders/` can pick them up.
- **Per-program interface:** `sources/shaders-vk/<program>.interface.glsl` declares the program's push block
  and record (section 4). Both stages include it, so the two declarations cannot drift.
- **Shared includes:** `sources/shaders-vk/include/*.glsl`, one per game include, same base name
  (`fogandlight.frag.glsl`, `fogandlight.vert.glsl`, `vertexwarp.glsl`, `shadowcoords.glsl`, `colormap.vert.glsl`,
  `colormap.frag.glsl`, `dither.glsl`, `skycolor.glsl`, `underwatereffects.glsl`, `noise2d.glsl`, `noise3d.glsl`,
  `oit.glsl`, `fxaa.glsl`, `colorutil.glsl`, `normalshading.glsl`, `fogspheres.glsl`, `vertexflagbits.glsl`).
  `default.fsh` and `printvalues.fsh` are included by nothing and are not ported.
- **Generated and fixed includes:**
  - `include/bindings.glsl`: sets and bindings, the source of truth (committed).
  - `include/frame.glsl`: the FrameGlobals block, generated from `Shaders/FrameGlobals.cs`. A test regenerates
    it and fails when the committed file differs.
  - `include/specialization.glsl`: constant ids (section 5), mirrored in C# with an agreement test.
  - `include/motion.glsl`: the only motion writer (section 7).
- **Language:** GLSL 450 with `GL_EXT_scalar_block_layout`, `GL_EXT_nonuniform_qualifier` and
  `GL_GOOGLE_include_directive`, compiled by the same shaderc library the runtime uses
  (`--target-env=vulkan1.3 -O`).

## 2. What must stay identical to the GLSL 330 program

The client's oracle for a program's uniforms is `ShaderProgram.collectUniformNames`
(`build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgram.cs:57-68`). It regexes the GLSL 330 source
**text**, before preprocessing, and assigns texture units in sampler declaration order. `HasUniform` only ever
consults that set. So for every program and every variant:

1. **Names:** frame members the program declares, plus push members, plus record members, plus sampler names,
   must equal the GLSL 330 name set exactly, with the same GLSL type per name (`extraGlow` is `int` in some
   programs and `float` in others; each keeps its own).
2. **Samplers:** the sampler list keeps the GLSL 330 declaration order. It is the key the device uses to turn a
   bound texture unit into a bindless slot.
3. **Vertex inputs:** same locations and types.
4. **Fragment outputs:** same locations and names per variant.
5. **Behaviour:** the same pixels within the tolerance the family's differential test states. The TAA resolve's
   rule-11 invariants (3x3 nearest-depth disocclusion with motion from the nearest-depth tap, luminance
   anti-flicker 0.3x..1.2x) are reproduced verbatim.

A static parity test per program per variant (names, sampler order, inputs, outputs, types) runs through
`ShaderCorpus` on the GLSL 330 side and the manifest on the native side. It needs no GPU and is part of every
family stage.

## 3. Descriptor use

- **Set 0 (frame):** `frame.glsl` declares the FrameGlobals UBO at `OPTIMUM_BINDING_FRAME_GLOBALS` (scalar
  layout, dynamic offset). The fixed frame textures (`shadowMapFar`, `shadowMapNear`, `sky`, `glow`,
  `liquidDepth`) come from `bindings.glsl` under their game names.
- **A frame member is used under its own name** only when the program includes the member's owner file, the
  same rule `FrameGlobals.TryPlace` applies today. Otherwise the member is an ordinary record member.
- **Set 1 (textures):** every other sampler is an index into the bindless array of its GLSL type
  (`optimumTextures2D`, `optimumTextures2DArray`, `optimumTexturesCube`, `optimumTextures2DShadow`, ...). The
  index is a `uint` push member carrying the sampler's own name (section 4). Indices are uniform over a draw, so
  no `nonuniformEXT`.
- **Set 2 (storage):**
  - `OPTIMUM_BINDING_FACE_DATA` holds the chunk `FaceData` array.
  - `OPTIMUM_BINDING_ANIMATION` and `OPTIMUM_BINDING_ANIMATION_PREV` hold the bone matrices, read as storage
    buffers.
  - `OPTIMUM_BINDING_PROGRAM_RECORD` (binding 3) is the program record: a dynamic uniform buffer.
- **Push constants:** one block per program, at most `OPTIMUM_PUSH_CONSTANT_BYTES` (128).

## 4. Placement: push block and program record

```glsl
// <program>.interface.glsl
layout(push_constant, scalar) uniform OptimumDraw {
    OPTIMUM_SAMPLER_SLOT(sampler2DArray, terrainTex);  // uint slot, the GLSL 330 sampler's name and type
    vec3 origin;              // DRAW-frequency uniforms that fit
    mat4 modelViewMatrix;
} draw;

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram {
    float alphaTest;          // everything else the program declares
    vec4 rgbaFogIn;
} program;
```

- **Push block:**
  - Every non-frame sampler's slot index first (4 B each), declared with
    `OPTIMUM_SAMPLER_SLOT(<GLSL 330 sampler type>, <name>)` from `bindings.glsl`, which expands to `uint <name>`.
    SPIR-V keeps no trace of which array a `uint` indexes, so the offline compiler reads the type from the macro
    and checks it against the set 1 array the shipped module actually indexes (section 6). A plain `uint` push
    member that indexes a texture array is a build error. (Added 2026-09-15 with the compiler: the first draft
    wrote a bare `uint` with a comment, which reflection cannot read.)
  - Then the uniforms the frequency map classifies as DRAW (they change between draws without a `Use()`), in
    declaration order, while the block stays within 128 B.
- **Record:** every remaining non-frame uniform (PROGRAM-frequency ones, and DRAW ones that did not fit).
  - The device snapshots it into the frame's uniform ring when it changed.
  - It binds the new dynamic offset at the next draw.
- **Classification for the families (from the frequency map):**
  - **Chunk family** (origin, modelViewMatrix or mvpMatrix, forcedTransparency, slot): 76-84 B, all in push.
  - **Decals:** about 80 B, in push.
  - **Entities** (about 280 B of DRAW data): push holds `entityId`, `addRenderFlags`, `taaHistoryValid`,
    `taaReactive`, the two slots and the small flags. The record holds `modelMatrix`, `prevModelMatrix`,
    `viewMatrix`, `rgbaLightIn`, `renderColor` and the remaining scalars.
  - **GUI** (160-240 B): push holds slots, `rgbaIn`, `extraGlow`, `applyColor`, `noTexture`,
    `overlayOpacity`; `modelViewMatrix`, `modelMatrix` and `projectionMatrix` go to the record.
  - **Particles:** no DRAW uniforms; push holds only slots.
  - **Fullscreen and post programs:** one draw per `Use()`, so push holds only slots and everything else is
    record.
  - **Held items (`standard`):** at most two draws a frame; slots and flags in push, the rest in the record.
- **The placement is authored in the source,** not computed at runtime. The manifest records it by reflection
  (section 6), and Phase 4's measured uniform profile may move members later.
- **Dynamic UBO limit:** set 0 and the record are two dynamic uniform buffers in a layout that also holds an
  update-after-bind set. The device floor therefore requires `maxDescriptorSetUpdateAfterBindUniformBuffersDynamic`
  of at least 2 (`Core/DescriptorIndexingFloor.cs`).

## 5. Defines: variant axes and specialization constants

The prefix is `ShaderRegistry.registerDefaultShaderCodePrefixes`, `ShaderRegistry.cs:462-540`.

- **A define stays a variant axis** (`#if`, compiled offline per value) when it changes a declaration: an input,
  output, uniform, buffer or anything the other stage sees. The axes are:
  - `TAAMOTION` together with `GBUFFER` (= `SSAOLEVEL > 0`). They fix the output set and the motion location
    (2 or 4), exactly as `TAAMOTIONLOCATION` does today.
  - `USEOIT` (entityanimated only).
  - `USESSBO` (0 only for `Chunkshadowmap_NoSSBOs`).
  - `GREEDYMESH` (the programs with two attribute layouts).
  - `ALLOWDEPTHOFFSET`, `GLOWSUB`, `VEC3SCALE` (per-registration defines).
- **Every other define is a specialization constant** declared in `specialization.glsl`, used as
  `if (OPTIMUM_BLOOM != 0)`: `FXAA`, `SSAOLEVEL` (its value), `NORMALVIEW`, `BLOOM`, `GODRAYS`, `FOAMEFFECT`,
  `SHINYEFFECT`, `SHADOWQUALITY`, `WAVINGSTUFF`, `MINBRIGHT` (float), `GREEDYMESH_GRAD`.
  - `DYNLIGHTS` becomes the loop bound `pointLightQuantity` over arrays fixed at `FrameGlobals.MaxDynamicLights`.
  - `MAXANIMATEDELEMENTS` is fixed.
- **Consequences:**
  - Declarations a spec-constant branch uses are declared unconditionally, so the name set of section 2 is
    unaffected (the oracle already sees names inside inactive `#if`s).
  - A settings change becomes a pipeline-key change, not a recompile.
- **Variant key** in the manifest: the sorted `NAME=value` list of the axis symbols the program branches on,
  joined by commas (`GBUFFER=1,TAAMOTION=0`; empty when the program has none).
- **Testing an axis:** only by value (`#if TAAMOTION == 1`, `#if GBUFFER`). Every variant defines every axis it
  branches on as 0 or 1, so `#ifdef`, `#ifndef` and `defined()` on an axis are build errors. A program branches on
  an axis when any `#if`/`#elif` in either stage, includes expanded, names it.

## 6. Offline compile, manifest, reflection

- **Tool:** `tools/shader-compiler/Optimum.Shaders.Compiler.csproj`, a thin front end. The work is
  `Shaders/NativeShaderBuilder.cs` in the renderer, so the runtime `OPTIMUM_VK_SHADER_SOURCE` loop (section 8)
  compiles the same way; the command line is `NativeShaderTool.Run`, which the tests drive. Output always lands in
  `<output dir>/shaders-vk/`. Exit codes: 0 success, 1 build or verify failure (a failed build writes nothing),
  2 usage.
  - `--build <source dir> <output dir>`: compiles every `<program>.vert`/`.frag` pair (a lone stage is an error)
    for every combination of the program's axes. It writes `<program>[.<AXIS><value>...].<vert|frag>.spv`, axes
    sorted (`chunkopaque.GBUFFER1.TAAMOTION0.frag.spv`), plus `shaders.manifest.json`. Only files whose bytes
    changed are rewritten, and SPIR-V no program produces any more is deleted. A tree with only `include/`
    yields a valid manifest with no programs.
  - `--verify <source dir> <output dir>`: recompiles in memory and fails on any difference: manifest bytes,
    SPIR-V bytes (reported with both SHA-256s), a missing or an unexpected `.spv`. `make check-shaders-vk` runs
    it against `bin/<Configuration>/net10.0`.
  - `--single <program> <source dir> <output dir>`: rebuilds one program into an existing manifest written by
    the same toolchain.
- **Includes:** `#include "x"` resolves beside the including file first, then in `include/`. Expansion is
  textual and recursive; include guards are the files' own. The includes of both stages together are the
  program's include set for the frame-member rule (section 3).
- **Build wiring:** the MSBuild target sits on the tool project (after `Build`), not on
  `Optimum.Render.Vulkan.csproj`, because the tool references the renderer and a renderer target that ran it would
  be a build cycle. It is incremental:
  - inputs: `sources/shaders-vk/**`, a source list written only when it changes (so deleting a file reruns the
    target), the tool and the renderer DLL;
  - output: a stamp in `obj/`, deleted first when the manifest is missing. The manifest itself cannot be an
    output: write-if-changed leaves an unchanged manifest with an old timestamp, which would rerun the target on
    every build;
  - `-p:OptimumSkipNativeShaders=true` skips it.

  The content-hash cache is the tool's write-if-changed. Output goes to `bin/<Configuration>/net10.0/shaders-vk/`,
  the directory `make deploy` and the packagers read.
- **Deploy and packaging:** `make deploy` replaces `<client dir>/shaders-vk/` beside `Optimum.Render.Vulkan.dll`
  (vanilla dir and install dir) and compares every file with `cmp`. `scripts/package-linux.sh`,
  `scripts/package-macos.sh` and `scripts/package.ps1` stage it the same way and fail when the manifest is missing.
  The GLSL "void main" corruption scan never reads it. Never under `assets/`. The plan's
  `<game>/Optimum/shaders-vk/` is superseded: the renderer DLL sits in the client root, so beside it is
  `<game>/shaders-vk/`.
- **Reflection** is a small SPIR-V reader in the renderer (`Shaders/SpirvReflection.cs`), not a new package.
  - It reads `OpEntryPoint` interfaces, `OpName`/`OpMemberName`, and `OpDecorate`/`OpMemberDecorate` (`Location`,
    `DescriptorSet`, `Binding`, `Offset`, `SpecId`) plus the type graph for sizes.
  - Set and binding numbers are fixed by `bindings.glsl`, so reflection only confirms them. No SPIR-V
    reflection dependency exists in the tree (integration map, section 5), and this avoids adding one to
    packaging.
  - **Two modules per stage.** An optimised module has no `OpName`s and has dropped every declaration nothing
    uses (unused inputs, specialization constants and descriptors; unused outputs survive). Every stage is
    therefore compiled twice: the shipped `-O` module, and an unoptimised twin
    (`ShaderCompiler.CompileForReflection`, never shipped) that keeps names and every declaration.
    - From the twin: names, block layouts, stage interfaces and specialization constants.
    - From the shipped module: `writtenOutputs`, which descriptors are used, and which push member indexes which
      array.
  - glslang sizes a bindless array that is declared `[]` but never indexed as a one-element array rather than a
    runtime array; the convention check accepts either.
- **Checks the tool enforces** (build errors, not warnings):
  - the push block is at most 128 B;
  - the push block and the record agree between the stages;
  - every descriptor sits at a binding `bindings.glsl` defines, with its type: set 0 textures, set 1 arrays by
    sampler type, set 2 storage buffers and the record as a uniform block; there is no set above 2;
  - sampler slots use `OPTIMUM_SAMPLER_SLOT`, are `uint`, come before every other push member, and index the
    set 1 array of their declared type;
  - no plain `uint` push member indexes a texture array;
  - specialization constants agree between the stages.
- **Manifest per program and variant** (schema version 1; written with `Utf8JsonWriter`, `\n` line endings,
  byte-deterministic because `--verify` compares bytes). The program lists its `name` and sorted `axes`; each
  variant has:
  - `key`: the variant key (section 5);
  - `stages`: stage, source file, SPIR-V file, SHA-256;
  - `push` and `record`: type name, size and members (name, type, offset, size, `arrayLength`: 0 not an array,
    -1 runtime), or `null`;
  - `frameMembers`: the FrameGlobals members, in block order, whose owner include the program includes.
    `fogandlight.vsh` stands for `fogandlight.vert.glsl` or `fogandlight.glsl`; `.fsh` likewise with `.frag`;
  - `samplers`: name, GLSL type, `bindlessArray`, `arrayBinding`, `pushOffset`, `order`. The order is push-block
    order, which is the GLSL 330 declaration order by authoring (section 4);
  - `frameTextures`: the set 0 textures the shipped modules sample (name, GLSL type, binding). These are taken
    from use, not declaration, because `bindings.glsl` declares all five in every program; the family parity
    tests apply the owner rule to them;
  - `storageBindings`: set 2 buffers other than the record (name, set, binding, descriptor type, array length,
    runtime array, `used` by a shipped module);
  - `vertexInputs` and `fragmentOutputs`: location, name, type and array length, as declared;
  - `writtenOutputs`: bit n set when the shipped fragment module stores to location n;
  - `specializationConstants`: id, name, type and default (a bool is a JSON bool).

  Schema version and toolchain identity (`ShaderCompiler.Identity`) sit at the top. A reader refuses any other
  schema version.

## 7. Motion: `include/motion.glsl`

```glsl
vec2 optimumMotionVector(vec4 prevClip, vec2 renderSize, vec2 jitterPx);   // prev - current, current unjittered
vec4 optimumWriteMotion(vec4 prevClip, vec2 renderSize, vec2 jitterPx, float reactive, float writerDepth);
vec4 optimumWriteReactiveOnly(float reactive);                             // rg = 0, a = 0
```

- **Behind the previous camera** (`prevClip.w <= 1e-6`), `optimumWriteMotion` returns `vec4(0, 0, reactive, 0)`.
  The frozen contract requires this: "a writer that bails out of its vector must still deliver b and zero only
  rg and a" (`docs/temporal-frame-contract.md` section 3.2).
  - Today `chunkliquidmotion`, `particlescube` and `taa-skymotion` do so.
  - `entityanimated`, `standard` and `instanced` drop `reactive` on that branch. That contradicts the contract
    and is fixed in the GLSL 330 writers first, with GPU tests, so native-vs-330 differential tests compare
    like with like.
  - `chunkopaque`, `chunktopsoil` and `decals` pass a literal 0, so nothing observable changes for them.
- **One exception:** `particlescube` keeps its writer depth on that branch (`a = gl_FragCoord.z`) and its
  reactive of 1. It calls `optimumMotionVector` directly and states why.
- **What stays in each program:** the previous-position reconstruction (warp replay, skinning, instance
  transforms, liquid waves, z-offset replay). The include starts where a previous clip position exists.
- **Enforcement:** a source test fails any assignment to `outMotion` outside the include's return values.

## 8. Runtime

- **Seam:** `VulkanDevice.LinkProgram`, before `ShaderTranslator.Translate`.
- **Lookup:** the manifest is looked up by (`PassName`, variant key built from the program's prefix defines).
  - On a hit the program links from SPIR-V, and its placement table answers `GetUniformLocation`: frame, push
    or record offset, or sampler index, keeping today's three location ranges as the outward shape.
  - On a miss it falls back per program to the rewriter.
- **Log line:** one line reports `[Optimum] shaders: N native, M rewritten, K failed`.
- **Environment overrides:**
  - `OPTIMUM_VK_NATIVE_SHADERS=0` forces the rewriter for A/B runs.
  - `OPTIMUM_VK_SHADER_SOURCE=<dir>` compiles the source tree at runtime for the development loop.
- **Mod shaders:** they stay on the rewriter, retargeted to the same shared layout (handoff item 4, first half).
