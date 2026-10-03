# VulkanStory porting and development plan

Updated: 2026-10-01. The [ROADMAP](ROADMAP.md) is the authoritative current feature
status and remaining-work list. This file defines development boundaries; it does
not maintain a second chronological progress ledger.

## Scope and reference

Port the retained custom Vulkan renderer, upscalers, frame generation and SDL3
into the fresh VulkanStory mod at D:/Coding/VulkanStory-Rewrite. Retained source
baseline: 386e0d05386d0b228b439d09aeca851428f7bbf3. Existing provenance records and
license notices stay with the migrated implementations.

The current reference is official Vintage Story 1.22.7/Windows x64 and the user's
complete **foggy village story** save. Inherited Linux and third-party direct-GL
compatibility work remain explicit tracks in the roadmap.

## Architecture boundaries

- Keep the normal shortcut and existing installation. Windows activation uses
  hostfxr.dll and a contained payload, leaving version.dll available for the MFG
  enabler. Coexistence remains an acceptance item, not a conclusion from naming.
- Do not import the Optimum launcher, donor/delta pipeline, copied game directory,
  unrelated optimization systems or modified game assemblies.
- Preserve working rendering/SDL/provider algorithms, shader/resource layouts and
  math. Change names, dependencies and explicit host/state interfaces as required
  by the fresh mod; defer optional algorithm/layout refactors until parity.
- Compile Game/Mod against official assemblies; do not ship them. Game internals
  and Harmony belong to Game/Mod/Input integration, not renderer/SDL/providers.
- Use sidecar/session ownership instead of injected fields, unsealed game types
  or added virtual slots. Cache validated bindings outside draw-call paths.
- Native bootstrap must not execute managed or graphics work under the loader lock.
- Providers remain optional with explicit requested/effective state, actual limits
  and safe resource/swapchain lifetime. Separate HUD-less scene and UI for FG.
- Build from this checkout, official game and declared SDK/runtime inputs. Development
  SDK locations may be external; players need neither SDKs nor the old project.

See [architecture](architecture.md), [bootstrap/install contract](bootstrap-and-installation.md)
and [source inventory](../porting/README.md) for the detailed boundaries.

## Workflow

1. Read the roadmap's current source/package status and select a concrete open item.
2. During implementation, inspect/edit only: no build, tests, probes, packaging or run.
3. Accumulate required integration fixes; **defer new/expanded tests until integration
   is finished**, as requested. Do not add a routine check after every source increment.
4. A validation turn plans and runs one bounded relevant batch, records its entire
   result and stops. Diagnose failure from existing logs/source; repair in a later
   implementation turn. Never repeat the same batch to check a speculative fix.
5. Routine runtime work uses the headless harness and an isolated SQLite snapshot
   of foggy village story. Do not deploy/open/focus/close the user game for a check.
   A direct user request to start the game is honored using that world.
6. Update the corresponding roadmap item and link a detailed evidence record. Keep
   source-only, compile-only, scoped runtime and full feature acceptance distinct.
7. Refresh the coherent package when accumulated changes are ready. Audit current
   source, candidate and installed DLL identities independently before any release.
8. Completion requires the retained feature contract and applicable open items to
   be proven; source presence, absent TODOs and one successful capture are insufficient.

## Milestone navigation

B0 activation; P0 migration; G0/G1 original platform/graphics/menu routing; G2 scene/
temporal/post; G3 providers/controls; R0 Windows delivery; C0 targeted mod GL support;
Linux activation/release. Their current statuses and exact work are in [ROADMAP](ROADMAP.md).

## Historical evidence

The full previous plan is preserved in
[porting-plan-history-2026-10-01.md](porting-plan-history-2026-10-01.md).
Detailed dated implementation/validation records remain in this directory.
Historical statements such as no world rendered/no provider evaluated are superseded
by later recorded world/SDK runs. The [current summary](current-port-status.md) links
those runs without treating their limited scope as full acceptance.