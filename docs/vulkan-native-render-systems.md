# Vulkan native rendering

The Vulkan backend delivers native draws and shaders, TAA and GTAO. UI/game separation
and frame timing belong to the renderer foundation. Vendor upscalers, frame generation
and their interop are outside this PR. Existing game rendering options remain compatibility
requirements; they do not authorize new vendor integrations.

## Ownership

| Owner | Responsibility |
| --- | --- |
| `Platform/VulkanClientPlatform.*` | Game render seams, target selection, client settings and per-system draw data |
| `Platform/StatedRenderState.cs` | State stated through the existing client/mod API and conversion into native draws |
| `VulkanDevice.Native.cs` | Explicit pipeline/pass descriptions, uniform placements, sampler resolution and draw setup |
| `VulkanDevice.Resources.cs`, `Core/MeshManager.cs` | Mesh resources and native mesh recording |
| `Core/RenderTargetManager.cs`, `Graph/` | Rendering scopes, declared image uses, barriers, pending clears and transient lifetimes |
| `Core/FrameRing.cs`, `Frame/`, `Transfer/` | Submission, frame-slot storage, uploads, readbacks and retirement |
| `Present/`, `Latency/FrameTiming.cs` | Presentation lifetime and frame timing reports |

Paths in this table are relative to `Optimum.Render.Vulkan`.

## Integration contracts

The decision numbers are retained for references in source comments.

1. **Client and mod API compatibility.** The runtime GLSL rewriter remains for mod
   programs and overridden game shaders. Existing state, texture-unit and named-uniform
   calls are accepted by the platform adapter and produce native device draws. See
   [mod support](vulkan-mod-support.md) for override and pass-registration rules.
2. **Game seams.** Platform virtuals select Vulkan implementations. Their neutral OpenGL
   bodies preserve the existing behavior. Required transplanted members belong in the
   patcher's explicit member list; calls must dispatch virtually. Injected fields cannot
   depend on initializers that transplantation does not execute. Avoid premature access
   to vanilla static classes during startup.
3. **State ownership.** Dedicated render systems derive fixed state from their caller's
   declared values and the client settings. `StatedRenderState` owns the compatibility
   state for general client/mod calls. The device receives explicit pipeline and target
   descriptions. Per-attachment blend, color masks, depth, culling, topology and line
   width must participate in pipeline identity where applicable.
4. **Native device API.** Programs come from the packaged manifest or the runtime
   rewriter. Uniform and sampler placements are resolved when linking or creating a
   native pipeline. Per-draw data is snapshotted into frame-owned storage; relinking
   invalidates derived metadata. Mesh draws pass the actual mesh identity to resource
   binding, including animation blocks and chunk face data. Fullscreen, indexed,
   instanced and indirect draws share device resource ownership.
5. **Render systems.** Terrain and shadow groups, entities, particles, decals, sky,
   clouds, GUI/text and the post chain use dedicated seams or the general stated-draw
   adapter. Both reach native device recording. A general API caller still needs its
   stated blend, depth, texture-unit and uniform semantics. Related helpers belong
   beside their subsystem rather than in separate files for each small type.
6. **Verification.** Require independently specified pixels or image properties,
   multi-frame temporal checks, clean synchronization validation and actual game
   integration. Differential comparisons can supplement these checks. Historical test
   names and counts are not contracts or proof that the current refactor passes.

## Passes, synchronization and lifetime

Pass declarations identify the target, attachment slots and actual textures read this
frame. Reuse the stage's rendering scope across compatible draws; opening a scope per
entity would add unnecessary work. Read-only depth sampling must be declared explicitly.
Sampling a writable color target uses a feedback snapshot with a lifetime protected by
completion of the relevant GPU work.

The streaming frame graph caches load operations for matching pass prefixes. Clears can
be promoted into attachment loads. Stores preserve contents because future passes are
not yet known when the commands are recorded. Transient image reuse is owned by
`TransientAllocator`: compatible descriptions can share an image only when their
inclusive pass lifetimes do not overlap. New leases arrive in frame order. Barriers
still order earlier uses when the old contents are discarded.

Uploaded data, uniform snapshots, descriptors, indirect commands and readbacks must
remain valid until their submissions finish. Destroying a resource or rebuilding the
swapchain must honor both frame and transfer work that still references it.

## Temporal rendering and UI

Compose AO into the scene before temporal resolve. Bloom and god rays consume the
unsharpened scene; TAA sharpening follows final composition and late scene overlays.
Preserve the existing game's final-blit selection and avoid applying sharpening twice.
Motion attachment writes use explicit per-attachment masks and replace blending.

The scene and UI have separate images. The UI target uses transparent black and actual
coverage in alpha, with its own depth attachment. Compose it once with premultiplied
alpha before the Done stage so screenshots and video capture see the completed frame.
Resize and interrupted rendering must close stale UI scopes. See the
[temporal contract](temporal-frame-contract.md) for motion, jitter and history rules.

Frame identity begins before input processing. CPU phase markers, every submission's
frame association and presentation identity remain connected across partial submits and
swapchain recreation. Present-ID support is queried from the selected device. CPU
reports do not imply measured driver, operating-system or GPU latency, and the timing
foundation does not change the client's frame limiter.

## Maintenance and acceptance

Document each seam's visible output, its corresponding OpenGL entry point, target and
attachments, and non-obvious fixed state. Keep lifecycle rules near the owning code.
The [shader contract](vulkan-native-shaders.md) describes source layout and packaging;
[acceptance](vulkan-acceptance.md) describes runtime checks.

The refactor's final suite and real-client validation are still pending. Older captures
and test totals describe their recorded revisions only. Verify selected GPU identity:
Intel UHD Graphics 770 is Xe-LP integrated graphics, not Arc. Claims about presentation,
pacing or another vendor require runs on that device and the finished build.
