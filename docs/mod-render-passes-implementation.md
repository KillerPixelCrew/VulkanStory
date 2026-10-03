# Declared mod render passes and motion writers — 2026-10-01

Implementation only. No builds, tests, packages, deployment or game launches.
The previous goal turn made source progress restoring SDL icon startup.

## Missing retained feature

The donor API contract and VulkanClientPlatform.ModPasses host existed only under
porting reference folders. They were absent from the compiled Game integration.
Both are now migrated into mod-owned source. Existing game assemblies remain
unchanged; no extra virtual methods, game API types or static holders are injected.

## API and host

The API lives in `VulkanStory.Game.ModRendering` in VulkanStory.Game.dll. Names
retain the donor shape with VulkanStory replacing Optimum. Registration copies
pass declarations, validates attachments and slots, replaces by name, and keeps
registration order. Motion writers retain identity-based registration.

Mods may use `capi.RegisterVulkanStoryPass(system, declaration, out reason)`,
`RegisterVulkanStoryMotionWriter`, and `UnregisterVulkanStoryPasses`. Standalone
registered renderers call `VulkanStoryModPasses.BeginMotionWriter(writer)` around
their draws and call EndMotionWriter only when Begin returned true, in finally.
They must provide the retained motion shader output/uniform contract; this API
does not create object history automatically. SDK/backend/Harmony access is not
required for the calling mod. Reference the Game integration assembly for these
client-only declarations, alongside the ordinary official game API reference.

The graphics sidecar resolves current attachment handles into target IDs, color
masks and frame-graph reads. Passes use OpenSampling and AllowSplit. Missing write
targets skip the callback with a once-per-declaration report; missing reads drop
out as in the donor. Shadow targets and feedback loops retain contract refusal.
Callback exceptions are logged once and the host closes motion/pass scopes and
restores masks, framebuffer, viewport and outer stated declaration.

The original TriggerRenderStage prefix enters a scoped stage; its finalizer runs
the declared passes after ordinary registered renderers on successful return,
then restores the enclosing stage even on failure. Motion writers are limited
to Opaque/AfterOIT and the existing Primary temporal attachment. All actual GPU
work still uses the existing backend; game types remain inside Game integration.

## Lifecycle

Registry changes invalidate cached plans, dropping references to removed mod
callbacks. Framebuffer publication/rebuild and world disposal clear plan caches.
LeaveWorld still unregisters each mod's entries. Original client disposal also
clears registrations, and graphics detachment removes owned hooks and leftover
entries. The ordinary ModSystem does not become another device or patch owner.

## Status and limits

Source was inspected against the original host, official framebuffer enum names,
existing stated draw declaration path and temporal motion implementation.
Compilation and execution of custom declared passes remain unverified. Existing
successful vanilla-world harness evidence predates these additions and does not
prove this feature. Full renderer/provider/SDL acceptance remains open.

## Lifecycle source follow-up

UnregisterMod now rebuilds cached slot snapshots immediately, so menu-only frames
cannot keep an unloaded mod's callback/API alive. Stage dispatch also drops cached
plans on registry changes even when the resulting slot has no passes. Original
client disposal refuses to clear registrations belonging to a newer attached
client on the shared process platform.

The mod-specific motion scope now saves the caller's target draw-buffer mask and
restores it after closing the existing motion window. Its End hook acts only on
a window opened through this mod API, preserving ownership of built-in windows.
Declared passes use the same paired scope. These are boundary corrections;
motion shaders, attachment layout and temporal algorithms are unchanged.
This follow-up is implementation-only and remains unbuilt/unrun.
