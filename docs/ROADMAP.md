# VulkanStory Roadmap

Updated 2026-10-04. Authoritative current status; detailed results are in
[development-evidence.md](development-evidence.md).
Workspace: D:/Coding/VulkanStory-Rewrite, master at
f5388f74723a6da5de21379b5f9218e468906796 plus working changes.
Current development package: `artifacts/validation/controller-prompt-polish-20261004-160020/stage`,
installed in the user's Vintage Story directory; hashes verified, manual test pending.

## Immediate goal and next work

Working upscaling/frame generation across supported vendors, and usable original
Options menus from both the main menu and a loaded world. The goal is **not complete**.

1. **SDK-02: resolve DLSS FG output gain.** The last strict hidden check produced
   one reported output per real frame. The prepared visible comparison needs the
   user's launch instruction. Do not substitute more peripheral hardening or an
   unchanged hidden run for this check.
2. **HW-01/HW-02: obtain AMD execution evidence.** Current recorded hardware is
   RTX 4070 Laptop GPU and Intel UHD 770; no AMD adapter. Unsupported fallback is
   verified, actual AMD SR/FG is not. Intel FSR3 SR/FG executes; XeSS-FG requires
   eligible hardware rather than UHD 770.
3. **UI-01: finish ordinary Options use.** Both hosts render all five pages;
   Save/Cancel, persistence and 6x slider callbacks have scoped diagnostic evidence.
   Live adjustment and Cancel/close restoration are now implemented in source;
   Image shows current provider, internal/output resolution and FPS. Release builds
   passed and the package is installed with hashes verified; user reports ordinary
   Options operation works. Broader transitions/resize acceptance remains open.
   Remaining acceptance is real SDL input, scrolling/resize, error visibility,
   leave/rejoin and correct provider resume after closing menus.
   User reports no visible/FPS difference between Quality and Ultra Performance;
   their live log confirms 1707x1067 versus 853x533 DLSS inputs at 2560x1600 output.
   VSync is saved Off and maxFps is 241. Performance/visible output remains unverified.

## Working rules

- Official Vintage Story 1.22.7, Windows x64; use the complete **foggy village story**.
- Routine checks use the isolated hidden harness. It must be silent: all six audio
  levels are zeroed and verified before launch; the copied cache is excluded.
  Offline preparation checks passed; no acoustic measurement has been performed.
- Do not change installed settings/save or deploy/open/focus/close the user's game
  for routine checks. Visible launches/deployment require the user's instruction.
- Keep implementation and validation turns separate. A validation turn has one
  bounded batch, once. New tests stay deferred until integration is finished.
- Preserve working donor algorithms, attribution and architecture boundaries.
  No launcher/transplant pipeline or modified official game assemblies.
- Group necessary changes; maintain this Roadmap and the one evidence summary.
  Secondary cleanup must unlock the primary feature rather than replace it.

## Current feature status

| Feature | Implemented / observed | Open boundary |
| --- | --- | --- |
| Renderer, SDL, official startup and graphics routing | Migrated source, original menu/world rendering, normal shutdown | Full lifecycle/material/input parity |
| DLSS SR | Successful world evaluations/captures | Historical sky issue and moving-scene acceptance |
| DLSS FG | Constants/tags/options/present integration | **Output gain unresolved**; visible check pending |
| XeSS SR | NVIDIA and Intel evaluations | Wider scene/quality acceptance |
| XeSS FG | NVIDIA scoped execution/handoff/cadence | Visible output/pacing; eligible Intel coverage |
| FSR3 SR/FG | NVIDIA SDK path; Intel single-queue direct interpolation, HUD composition and extra submissions | Moving-camera/disocclusion/scanout/pacing |
| FSR4 | Bridge/runtime implemented; unsupported-NVIDIA fallback | Supported AMD execution |
| Options | Original Graphics entry, five shared pages, live adjustment, Save/Cancel and status; user reports it works | Broader transitions/resize |
| Latency/controllers/touch | World/GUI release guards, radial sticks, separate menu bindings and semantic inventory actions implemented; inventory packet path checked | Physical input, gesture/layer/radial extensions and latency/touch acceptance |

## Source and delivery

The cleanup preserves feature code, checked Vulkan errors and simple constructor
cleanup. Speculative constructor-retention plumbing and the auxiliary reflection
checker were removed. [Game/Mod cleanup builds passed](../artifacts/validation/cleanup-build-20261004-114649/);
the simplified source has not been runtime-validated.

The latest completed normal source batch is
[resource-bindings](../artifacts/validation/resource-bindings-20261004-103435/):
Game/Mod builds plus NVIDIA handoff and Intel FSR3 checks passed. It predates cleanup.
Other exact payloads and proof limits are listed in the evidence summary.
The October-1 development ZIP is older than current source. The installed development
package is identified above and includes the crash correction/controller status.
Prepared visible launchers still pin their recorded
compiled payloads, not automatically the latest worktree.

## Retained completion coverage

Every item remains open until its stated behavior has evidence. These IDs preserve
the wider Roadmap without turning it into the immediate implementation queue.

| ID | Remaining completion boundary |
| --- | --- |
| PORT-01 | Controller world ownership/damage cleanup acceptance |
| REN-01 | Historical DLSS sky pattern: current nonreproduction is not a fix |
| REN-02 | Moving camera/object/animation/transparency motion parity |
| REN-03 | History reset, resize, option changes and shader reload |
| REN-04 | Post-processing/material parity |
| REN-05 | Graph/resource ownership and bounded fallback |
| SDK-01 | SR settings, unsupported behavior and live switching |
| SDK-02 | Actual FG output, HUD/occlusion correctness and pacing |
| SDK-03 | DLSS-G VRAM warning (39) crashed framebuffer cleanup in user run; checked drain/free correction built and deployed, runtime unvalidated |
| SDK-04 | Latency authority and concrete impact of SDK warnings |
| SDL-01 | Interactive window/focus/fullscreen/input lifecycle |
| SDL-02 | Core controls, radial menu, gestures, modifier layer and context-correct prompts deployed in controller-prompt-polish-20261004-160020. Inventory/radial/UI and compiled gesture/ownership/context lifecycle/prompt checks passed. Live movement/look/hotbar/inventory/radial/modifier timing, focus/reconnect and performance/crash acceptance still require user controller evidence |
| SDL-03 | VRAM accounting and mesh headroom corrected; reserve now uses image working-set plus external driver usage. fg-vram-headroom-20261004-142525 built/deployed and passed 4500 world frames/20 inventory cycles at viewDistance=1536, with DLSS-G still enabled and no VRAM warning/crash. Physical-controller/visible-session acceptance remains open |
| NET-01 | Analog companion negotiation/disconnect/rejoin |
| CAP-01 | Ordinary screenshot/timelapse behavior |
| CAP-02 | Encoded AVI execution/decoding |
| API-01 | Declared mod-pass/motion callbacks and state restoration |
| UI-01 | Both original Options hosts and full ordinary interaction |
| DEL-01 | Current normal-shortcut startup/disable/failure behavior |
| DEL-02 | Install/update/remove/recovery |
| DEL-03 | Actual MFG version.dll coexistence and supported limits |
| DEL-04 | Coherent current candidate, dependencies, licenses and release gates |
| HW-01 | Supported AMD FSR4 execution |
| HW-02 | Vendor FG and handheld visible/pacing/power coverage |
| LINUX-01 | Normal-shortcut Linux activation/package/provider acceptance |
| GL-01 | Subsequent third-party GL discovery/capability policy |
| GL-02 | Subsequent shared-resource/state adapters |
| GL-03 | Subsequent concrete mod profiles |

Older plans, detailed session reports and pre-cleanup source are recoverable from
[the cleanup backup](../.codex/cleanup-backup-20261004/). Raw validation artifacts
remain intact. Historical claims do not supersede this Roadmap.
