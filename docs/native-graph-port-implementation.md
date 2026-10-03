# Native declarations and pooled graph resources

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or game runs.

Native uniform/sampler/texture/pipeline/pass declarations are extracted unchanged from `VulkanDevice.Native.cs` into `Core/NativeRenderDeclarations.cs` and join backend compilation. The excluded device implementation uses the same types, avoiding duplicated declarations while the complete facade's other dependencies are removed. They depend on retained shader interface/layout/state code, not game types.

The retained `TransientAllocator`, its texture backing, and `FeedbackCopyPool` join the compile list. Inclusive pass lifetimes, per-description physical pools, logical rebinding/restoration, discarded contents, slot opt-in, idle release, timeline retirement and within-frame feedback copy reuse remain unchanged. The aliasing override key becomes `VULKANSTORY_VULKAN_ALIAS`, including its staged resource-facade documentation. Source baseline: `386e0d05386d0b228b439d09aeca851428f7bbf3`.

Original CPU `FramePlanningTests` and the feedback-copy completion/retirement case are copied with expected values intact. They cover plan/history shape changes, resource transitions, transient lifetime/description separation and copy reuse after completion. This increment is source-only. Next bounded validation should run backend tests and build the tool once.

This does not compile the full device implementation or exercise physical transient aliasing, feedback snapshot draws, native placement-table execution, game stage/target routing or vendor/world frames. Those remain part of the full facade, resource bridge and feature acceptance. No requested feature is removed or replaced by these declarations.

The [first bounded validation](validation-p0-native-graph-2026-09-30-01.md) passed 67 backend cases and a clean tool build. Planning and controlled retirement have evidence; physical graph/resource draws and complete facade/game/provider integration remain open.
