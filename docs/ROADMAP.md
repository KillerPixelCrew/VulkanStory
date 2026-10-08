# VulkanStory Roadmap

Updated 2026-10-08. Authoritative current status; detailed results are in
[development-evidence.md](development-evidence.md).
Workspace: D:/Coding/VulkanStory-Rewrite, `codex/review-issue-fixes`, based on
ee2d870839efc58f3cf3f9c2a3513ced15477fb8; implementation commit `aaae759`;
latest compiled/staged source `014fef2`.
Current staged candidate: `artifacts/validation/rereview-fixes-20261008-175333/stage`;
world capture passed, but active XeSS FG was not established and liquid-motion loading failed.
Subsequent liquid-include and XML-documentation repairs require compilation.
Installed development package: `artifacts/validation/controller-prompt-polish-20261004-160020/stage`,
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

The latest compiled/staged source is `014fef2`: all managed Release builds,
50 native shader programs and staging passed. Its isolated world capture passed,
but active XeSS FG and the liquid motion path were not established. Source
`aaae759` repairs the liquid include loader and Game XML warnings; recompilation
is pending. Exact payloads and proof limits are in [development-evidence.md](development-evidence.md).

The staged and installed packages are identified above. The October-1 ZIP and
prepared visible launchers retain their older recorded payloads.

## Review findings

All **118 review findings have completed implementations**; CLEAN-05 needed no
change. There are **zero open review implementation findings**. The detailed
[completed ledger](completed-review-findings-2026-10-08.md) retains every ID,
original finding, source reference, audit coverage and recorded proof limit.

### Pending review verification

- Compile/stage the liquid-include and 17 Game XML documentation corrections at
  `aaae759`, then check liquid-motion loading in the isolated foggy-village world.
- Verify active XeSS FG and overlapping XeLL Present markers; the latest capture
  establishes world loading/capture, not active FG execution.
- Run the existing partial-output-array checks and a scenario using
  `hostGeneratedPresents`. New/expanded tests remain deferred under the working rules.

The [current evidence](development-evidence.md) separates completed source work
from these checks. Broader feature, platform and hardware work remains below.

## Remaining feature and verification work

These IDs retain remaining feature/compatibility work and verification boundaries.
Pending acceptance does not reopen completed review implementation findings.
The [completed ledger](completed-review-findings-2026-10-08.md) maps the closed
findings to their broader feature and acceptance areas.

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
| SDK-03 | Verify DLSS-G framebuffer teardown after SDK VRAM warning 39; the checked drain/free correction is implemented |
| SDK-04 | Latency authority and concrete impact of SDK warnings |
| SDL-01 | Interactive window/focus/fullscreen/input lifecycle |
| SDL-02 | Physical-controller acceptance for movement/look, hotbar/inventory/radial, gestures/modifier timing, context prompts, focus/reconnect and performance/crash behavior |
| SDL-03 | Physical-controller and visible-session VRAM/mesh-headroom acceptance; implemented corrections and the 4500-frame regression result are recorded in the evidence summary |
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
