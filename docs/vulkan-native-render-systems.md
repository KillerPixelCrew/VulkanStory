# Vulkan-native render systems (Phase 3b)

Design for handoff item 5. Every vanilla render system on the Vulkan path draws through a renderer that owns its
pipelines, its descriptor use on the set convention and its per-draw data, instead of reaching the device through
the GL-shaped platform virtuals. OpenGL keeps `ClientPlatformWindows` unchanged ("OFF is vanilla").

Inputs:
- plan decision 7 and Phase 3b (`docs/vulkan-native-plan.md`);
- `docs/research/vulkan-descriptor-model.md`, "Native architecture for this renderer";
- the shader contract (`docs/vulkan-native-shaders.md`): all 49 vanilla programs are native, and the runtime seam
  links them from the manifest;
- three read-only maps of the tree (2026-09-16): the post and TAA chain, the world render systems, and the
  GL-emulation layer. Their findings are restated below where they decide something.

## 1. What the maps established

- **Post and TAA are not native yet.** Every post and TAA override in `Platform/VulkanClientPlatform.Graph.cs` calls
  `base.<Method>()`. The OpenGL body in `ClientPlatformWindows` runs on Vulkan, and each call reaches the device
  through a GL-shaped virtual:
  - state toggles land in `GlStateTracker`, which the device resolves into a pipeline key on every draw;
  - textures bind by unit;
  - uniforms are set by name through a location;
  - draw-buffer masks select attachments.

  The graph declarations around those calls take the union of every texture a pass might read (both TAA history
  parities, raw, resolved and sharpened scene), because the platform cannot see which one the OpenGL body picked.
- **What already has the target shape:**
  - the frame graph (`FrameGraph`, `PassRecorder`, `BarrierBatcher`, plans, promoted clears);
  - handle-based textures, meshes and targets;
  - the bindless resolve (`BindlessTextureTable.Resolve` with kind checks, depth-read-only layouts and feedback
    copies);
  - the upload shadows (frame block, record, push) with dirty snapshots into the uniform ring;
  - the native programs and their placement tables.

  None of that is OpenGL emulation, and it all stays.
- **What is emulation and leaves the Vulkan path system by system:**
  - `GlStateTracker`'s setters and the per-draw key resolve;
  - the texture-unit tables (`_boundTextures`, `_unitSamplerOverrides`, `SamplerUnits`);
  - uniform dispatch by name and location;
  - `RenderTargetManager`'s draw-buffer-mask and single-bound-target model as the way a pass says what it
    writes;
  - the GL-enum translators;
  - the platform overrides that only forward those calls (`.State.cs`, and the GL-shaped parts of
    `.Shaders.cs`, `.FrameBuffers.cs` and `.Textures.cs`).

## 2. Decisions

1. **The runtime rewriter stays, permanently, as the mod-shader adapter.** Plan decision 2 promises that mods
   rendering through the game API keep working. Those mods set state, bind units and set uniforms by name. The
   GL-shaped adapter therefore stays reachable for mod renderers and for any vanilla system not yet moved, retargeted
   to the shared layout as it already is. Phase 3b's exit is that no *vanilla* render system uses it. The adapter
   shrinks to exactly what mod programs need.
2. **Seams are the existing virtuals, overridden without calling base.**
   - Post and TAA: `RenderPostprocessingEffects`, `RenderOptimumTaaResolve`, `RenderOptimumTaaSharpen`,
     `RenderOptimumSkyMotion`, `MergeTransparentRenderPass`, `RenderFinalComposition` and
     `BlitPrimaryToDefault` are virtual on `ClientPlatformAbstract`.
   - World systems: they get transplanted seams in the library where no virtual exists. For example
     `ChunkRenderer`'s pass methods call a platform virtual whose OpenGL body is today's body.
   - Every new seam follows the decision-5 rules: Cecil listing, `callvirt`, no injected field initializers, and no
     early touch of vanilla static classes.
3. **A native system reads client state, never GL state.**
   - It takes the values the OpenGL body computes: settings, ambient manager, temporal frame, framebuffer list, SSAO
     kernel. Where a value is private to `ClientPlatformWindows`, the seam passes it as an argument or a
     transplanted accessor exposes it; fields are never widened.
   - It computes its uniform values exactly as the OpenGL body does, so both backends produce the same image.
4. **Device API for native systems (`NativePasses`, device side).**
   - **Pipelines.** A native renderer asks for a pipeline by (program, variant defines, fixed state: blend per
     attachment, depth test/write/compare, cull, topology, colour write masks, target formats).
     - The pipeline comes from the manifest program and the pipeline cache, and is created at load or on first
       use, never re-derived from tracked GL state.
     - The key is `GlStateTracker.PipelineKey`'s shape without the tracker.
   - **Passes.** A pass is declared with explicit writes (target handle, colour slots, depth) and explicit reads
     (texture handles, the one texture the system chose this frame).
     - Load/store and transient hints follow the existing plan machinery.
     - Passes still render into the existing framebuffer objects created by `SetupDefaultFrameBuffers`, so target
       ownership and resize handling do not move in this phase.
     - The draw-buffer mask is not consulted for a native pass.
   - **Draws.** A native draw writes the program's push block and record through typed setters by placement: the
     placement table resolved once at pipeline creation, not by name per draw. It resolves sampled textures to
     bindless slots directly from the texture handle and sampler state, then records the draw: fullscreen
     triangle, mesh, multi-draw or instanced.
   - Motion windows are write masks of the pass or pipeline (the colour-write tiers), not `SetDrawBuffers` calls.
5. **Order and parallelism.**
   1. The device API plus the post and TAA chain, one stage: Optimum owns the chain end to end, and the chain proves
      the API.
   2. Then, in parallel on the stable API: chunks (all passes including shadow and liquid motion); entities (animated,
      shadow, held items through `standard`, `instanced`); particles, decals, sky, night sky, celestial, aurora and
      clouds; GUI and text (`gui`, `guigear`, `guitopsoil`, `helditem`, `lines`, `texture2texture`, block
      highlights, wireframe, autocamera).
   3. Last, removal: the emulation with no vanilla caller left leaves the Vulkan path, and the adapter that mods need
      stays, under tests that pin the mod path.
6. **Behavioural identity is the acceptance rule.**
   - Each moved system gets GPU readback tests that render the same inputs through the old route (the OpenGL body on
     the Vulkan device) and the native route, and compare pixels. They are bitwise where both paths are, within 1/255
     where filtering allows.
   - The TAA resolve keeps its rule-11 invariants and their tests.
   - The motion attachment stays identical, per the temporal contract.
   - Validation stays clean with `sync,best`.
   - Temporal claims need multi-frame tests, and the in-game check runs on both backends with the headless harness.
7. **FSR input (open item from the post and TAA map).** `BlitPrimaryToDefault` upsamples `Primary` colour 0. The
   native chain keeps exactly that input and does not reinterpret "the scene" per stage. Any change is a separate,
   measured decision.

## 3. Stage 1 scope: device API plus post and TAA chain

- **Device:**
  - native pipeline creation from manifest programs with explicit fixed state;
  - native pass declaration with explicit reads and writes;
  - typed push and record writes by placement;
  - direct bindless resolution by texture handle;
  - native draw recording;
  - motion write masks per pass;
  - stats: native passes, native draws, pipelines.
- **Platform:** a `NativePostChain` on the Vulkan platform implementing the seven post and TAA virtuals plus
  `ApplyOptimumSceneSsao`'s composite, with the frame order and the conditions of the OpenGL body.
  1. OIT merge.
  2. Sky motion.
  3. SSAO and blur, then the AO composite into the scene.
  4. TAA resolve and sharpen.
  5. The bloom chain.
  6. God rays.
  7. FXAA luma or blit.
  8. Final composition.
  9. Debug view, FSR (EASU and RCAS) or blit.

  Each pass reads the one physical texture the chain chose. The motion-writer hosting for mod passes and the AO
  compute pass keep working at their slots.
- **Tests:**
  - old-route versus native-route pixel comparison for every pass under the settings sweep (SSAO 0/1/2, bloom, god
    rays, FXAA, TAA on/off, render scale below 1 with FSR, debug view);
  - a multi-frame TAA accumulation test through the native chain;
  - declared reads that no longer union candidates;
  - validation clean;
  - the full suites.
- **Not in stage 1:** world systems, GUI, and removal of the emulation layer.

## 4. Documentation that makes map stages unnecessary

Every workflow so far has opened with a read-only map stage that rediscovers where things are, at five
figures of tokens each, and thrown the result away when the run ended. The fix is not a map document -
that is a second source of truth and it rots. The fix is that the code answers the question at the
declaration, so an implementer greps and reads instead of mapping.

**The convention.** Every render seam - a platform virtual a system draws through, a native pass, a
device API entry point - carries a doc comment that answers, in this order:

1. **What it draws**, in one sentence, in the game's vocabulary ("the far shadow map for chunk meshes",
   not "a draw call").
2. **Where the other side is**: the OpenGL body's type and method, so the two paths can be diffed
   without searching. For a native pass, also the pass it replaced.
3. **Target and slots**: which framebuffer and which colour slots it writes, whether it writes depth,
   and any colour-write mask that matters (motion windows are masks, never draw-buffer toggles).
4. **State that is not obvious**: blend mode per attachment, depth compare, cull, and anything the pass
   deliberately does differently from the tracked GL state, with the reason.
5. **What pins it**: the test that fails if this changes - the differential test name for a native pass,
   the coverage test for a lib seam.

**Greppability is the point.** A system's name appears in the comment of every member that serves it, so
`rg -n "shadow map"` finds the seam, the native pass, the pipeline and the test in one search. When a
system moves to a native pass, its old body keeps its comment and gains the pointer to the new one.

**Applies to:** `Optimum.Render.Vulkan/Platform/VulkanClientPlatform.*.cs`, `VulkanDevice.Native.cs` and
the transplanted seams in `build/VintagestoryLib/**`. An implementation stage documents the seams it
touches as part of the change, not afterwards; a stage that adds a seam without this comment is
incomplete, and review should send it back.

**Map stages** are then only for questions the code genuinely cannot answer - measured behaviour, vendor
documentation, or a tree the repository does not contain. `scripts/dev/harvest-maps.py` recovers the map
output of past runs from the workflow journals when one of those is needed again.

