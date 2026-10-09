# Full retained device compile inclusion

Date: 2026-09-30. Implementation only; no build, test, probe, package or game run.

## Source changes

The backend project now explicitly includes every retained `VulkanDevice` partial:
main lifetime/frame/present owner, resource/mesh, program/uniform, binding,
native draw, readback/query, shadow capability, DLSS/FSR 3/FSR 4/XeSS evaluation,
frame-generation, XeSS proxy ownership and upscaler host forwarding.
Support dependencies join with them: GTAO renderer/settings, GPU timestamps,
XeLL native wrapper, FSR 3 frame-generation owner and Streamline texture tag
adapter. No game assembly or Harmony reference was introduced.

The four retained GTAO shader files are embedded from this checkout's
`shaders/native/gtao` with their existing `shaders-vk/gtao/` resource names.
GPU timestamp gating uses `VULKANSTORY_VULKAN_GPU_TIMESTAMPS`, and the original
label/gating fixtures are copied mechanically into the backend tests.
No draw, shader, barrier, descriptor, provider or presentation algorithm changed.

Baseline revision: `386e0d05386d0b228b439d09aeca851428f7bbf3`, original
`Optimum.Render.Vulkan` device/support files and
`Optimum.Render.Vulkan.Tests/GpuTimestampsTests.cs`. Source transplantation,
earlier boundary adaptations and inherited provenance remain as recorded in
`porting/README.md`. The project change is compile/resource inclusion; timestamp
names and fixture namespaces are mechanical changes.

This inclusion has not been compiled. Any remaining extraction/signature/type
errors must be diagnosed from the next batch and repaired in a later implementation
turn; inclusion is not evidence of successful facade integration.

## Retained DLSS-G engine profile

The `integrate-dlss-fg` skill, its integration/validation checklists and local
Streamline 2.14.1 DLSS-G guide/header and Vulkan helper declarations were read.
This change exposes retained code to compilation; it does not change SDK setup.

| Surface | Current source and acceptance limit |
| --- | --- |
| Backend/queues | Vulkan context/frame ring; device present owns acquire/blit/present. The retained optional SDK queue/proxy path is unchanged. |
| Streamline setup | `InitializeWindow` creates Streamline before context creation, then binds Reflex/FG/PCL and queries support after the device exists. No new-host live setup acceptance. |
| Identifier | Runtime forwards existing `NgxSession.ProjectId`; its authorization/production registration is not established by this port or compile inclusion. |
| Swapchain | Retained `Swapchain`, proxy dispatch and image-index ownership. Actual device/frame routing and resize/toggle acceptance remain pending. |
| Frame constants/tags | `TagStreamlineFrame` uses original camera matrices, flipped upright depth/MV/HUDless/UI textures, motion/jitter conventions, barriers and extent values. No real scene has emitted these through the new game host. |
| Frame identity | Existing BeginFrame/token bookkeeping and present markers retained. Same-frame tagged/present evidence remains missing. |
| MV producers | Terrain/entities/animation/particles and temporal camera producers still need game/world attachment and acceptance. |
| Non-game frames | Existing readiness checks retained; menu/loading/pause deferral requires orchestration and actual frames before acceptance. |
| Settings | Latency selection and frame multiplier are host-owned. Standard mod UI/settings, requested/effective/actually presented wiring remain pending. |
| Runtime path | Private native RID directory through `NativeRuntimePaths`, existing Streamline bridge. Signed dependencies and shipping production/development selection require release evidence. |
| Scenes/capture | No new-host playable scene or FG launch command; capture helper has isolated evidence, actual game screenshots/video remain pending. |

## Validation plan and DLSS-G evidence report

Plan one bounded batch of Release backend tests followed by the preflight-tool
build, stopping after a failed step. This should compile the complete facade
and existing providers/support plus exercise retained CPU fixtures. No SDK/game
run belongs in that initial compile batch.

| Required evidence | Status |
| --- | --- |
| Full device build/static | NOT RUN; explicit compile inclusion only. |
| Runtime optional fallback and Streamline setup | NOT RUN in the new device/game host. |
| Functional DLSS-G activation and actual `DLSSGState` | NOT RUN. No max/generated/presented or VSync/dynamic capability result from this turn. |
| Real-scene tagging, frame identity and motion producers | NOT RUN. |
| Visual capture/artifacts | NOT RUN. |
| Performance/pacing | NOT RUN. |
| Overall new-host DLSS-G quality | Incomplete; MVP acceptance is not established. |

Files changed: backend compile/resource list, GPU timestamp environment name and
copied timestamp fixtures, plus progress records. No runtime option, UI, command,
SDK version or production/development switch was added. FG disabled/enabled and
real-scene commands do not yet exist for the routed new host. Native runtimes,
signature/dependency checks, authorized project ID, requested versus effective
versus actual FG, proxy ownership, required depth/MV/HUDless and optional UI tags,
token continuity, fallback matrix, production overlays and runtime shutdown all
remain acceptance gates. The repository's separate-turn rule defers the skill's
build/run requirements to validation turns; no runtime claim follows from this
source-only increment. Full renderer/SR/FG/latency/SDL/game/release stay open.

The [first bounded full-device validation](validation-p0-full-device-2026-09-30-01.md)
compiled the entire group and passed 75 backend cases. Three nullable warnings
remain in context initialization, shader source dumping and swapchain restore;
source diagnosis is recorded for a later implementation turn. No real device,
GTAO compute, provider or game path ran. Build/static evidence is now PARTIAL;
all runtime/real-scene/visual/performance gates remain NOT RUN.
