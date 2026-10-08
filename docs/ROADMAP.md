# VulkanStory Roadmap

Updated 2026-10-08. Authoritative current status; detailed results are in
[development-evidence.md](development-evidence.md).
Execution queue and acceptance criteria: [autonomous task list](../.codex/TASKS.md).
Persistent goal is active in the current chat; T01 is in progress.
Workspace: D:/Coding/VulkanStory-Rewrite, `codex/review-issue-fixes`, based on
ee2d870839efc58f3cf3f9c2a3513ced15477fb8.
Current validated/installed implementation: `bc85817`, checkout `80158f3`
(Options lifecycle/SDL resize, namespace correction and Linux notice paths).
Both Game builds, both platform stage/packages and main/focused DLSS world lifecycle
scenarios passed. Scoped software acceptance does not close the defects/gaps below.
Current development package: `artifacts/validation/options-lifecycle-20261008-232950/stage-win`,
installed in the user's Vintage Story directory with backup; all 400 receipt hashes verified.
The focused DLSS-G gain scenario passed. A 2.055 s steady interval measured 38.4 real /
76.9 SDK output FPS after the mapped-mesh regression correction.

## Immediate goal and next work

Working upscaling/frame generation across supported vendors, and usable original
Options menus from both the main menu and a loaded world. The goal is **not complete**.

1. **SDK-01/SDK-04: broaden runtime transition acceptance.** Required XeLL enables
   before FG and stays active through requested Off. First-enabled history reset
   resolved the observed -12 stop. The existing XeSS→FSR3→XeSS and latency
   Off→On→Boost profiles passed all actions/captures on RTX 4070. Ordinary
   Options close/resize/resume passed on DLSS: both measured resume windows
   produced 40 real/80 SDK presents. Fix the stale Streamline extent exposed during
   resize next; wider quality/pacing and eligible Intel behavior remain open.
2. **SDK-02: moving-scene quality and pacing acceptance.** Focused DLSS-G gain is
   now verified on RTX 4070. The SDK suppressed the old hidden run because its window
   was unfocused. The 8 FPS regression was full-buffer cloning on small mapped mesh
   updates; ordered range uploads now preserve buffer identity and placement.
   Wider camera/object/transparency, loading/leave-rejoin and cadence checks remain.
3. **HW-01/HW-02: obtain AMD execution evidence.** Current recorded hardware is
   RTX 4070 Laptop GPU and Intel UHD 770; no AMD adapter. Unsupported fallback is
   verified, actual AMD SR/FG is not. Intel FSR3 SR/FG executes; XeSS-FG requires
   eligible hardware rather than UHD 770.
4. **UI-01: finish ordinary Options use.** Both hosts render all five pages;
   Save/Cancel, persistence and 6x slider callbacks have scoped diagnostic evidence.
   Live adjustment and Cancel/close restoration are now implemented in source;
   Image shows current provider, internal/output resolution and FPS. Release builds
   passed and the package is installed with hashes verified; user reports ordinary
   Options operation works. Broader transitions/resize acceptance remains open.
   Main/world lifecycle now passed preview/Save/Cancel/abrupt-close restoration,
   all five pages and logical 1280×720 resize. Custom panels are contained, but
   the shared main sidebar overlaps/clips while Options is active at that size;
   diagnose its original layout/resize behavior before choosing a correction.
   Remaining acceptance is real SDL input, scrolling, error visibility,
   leave/rejoin and wider provider coverage after menus.
   User reports no visible/FPS difference between Quality and Ultra Performance;
   their live log confirms 1707x1067 versus 853x533 DLSS inputs at 2560x1600 output.
   VSync is saved Off and maxFps is 241. Performance/visible output remains unverified.

## Working rules

- Official Vintage Story 1.22.7, Windows x64; use the complete **foggy village story**.
- Routine checks use the isolated harness. DLSS-FG checks run visible and focused,
  as requested on 2026-10-08; hidden windows cannot establish its output gain.
  Other routine checks stay hidden. The harness must be silent: all six audio
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
| DLSS FG | Focused gain and Options/resume/resize scenarios passed on RTX 4070; both resume windows show 40 real/80 SDK presents | Correct stale backbuffer extent at deferred resize; moving quality/scanout/pacing and warning-39 recovery |
| XeSS SR | NVIDIA and Intel evaluations | Wider scene/quality acceptance |
| XeSS FG | XeLL/history corrections validated; XeSS→FSR3→XeSS and Off→On→Boost scenarios passed | Wider transitions, moving-scene/visible quality/pacing and eligible Intel acceptance |
| FSR3 SR/FG | NVIDIA SDK path; Intel single-queue direct interpolation, HUD composition and extra submissions | Moving-camera/disocclusion/scanout/pacing |
| FSR4 | Bridge/runtime implemented; unsupported-NVIDIA fallback | Supported AMD execution |
| Options | Main/world callback lifecycle passed settled preview/Save/Cancel/abrupt-close, all pages, logical resize and provider resume | Main sidebar clipping at small size needs diagnosis; scrolling, broader physical interaction/error/leave-rejoin |
| Latency/controllers/touch | World/GUI release guards, radial sticks, separate menu bindings and semantic inventory actions implemented; inventory packet path checked | Physical input, gesture/layer/radial extensions and latency/touch acceptance |

## Source and delivery

The installed implementation is `bc85817`, checkout `80158f3`. Both Game builds,
both platform stage/packages and main/focused DLSS world profiles passed;
all 400 installed hashes match. Linux ZIP exists; Linux runtime/install/providers remain unverified.
The prior focused DLSS world/gain scenario passed at `9a14715`. Liquid motion is ready and
Game XML warnings are resolved; the existing CS8600 warning remains. Used mesh
writes stage only changed ranges, while rare raw pointer access synchronizes
queued updates. Foreground scenarios require focus and skip the diagnostic throttle.
Exact payloads and proof limits are in [development-evidence.md](development-evidence.md).

The staged and installed packages are identified above. The October-1 ZIP and
prepared visible launchers retain their older recorded payloads.

## Review findings

All **118 review findings have completed implementations**; CLEAN-05 needed no
change. There are **zero open review implementation findings**. The detailed
[completed ledger](completed-review-findings-2026-10-08.md) retains every ID,
original finding, source reference, audit coverage and recorded proof limit.

### Pending review verification

- Active XeSS FG, PCL asynchronous Present brackets and XeLL sleep/input/simulation/
  render markers passed the scoped batch. XeLL Present marker returns are not
  separately logged; their overlap and wider latency impact remain open.
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
| SDK-02 | Broader FG HUD/occlusion quality, moving-scene and pacing coverage; RTX 4070 DLSS-G functional output gain passed |
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
| LINUX-01 | Managed cross-builds, RID/notice corrections, staging and ZIP passed; actual Linux runtime/install/providers/URI proof open |
| GL-01 | Loader-bound discovery/refusal compiles on both targets and normal startup registration passes; actual third-party refusal unexercised |
| GL-02 | Concrete shared-resource/state adapters deferred by user until a target mod is selected |
| GL-03 | Concrete mod profiles deferred by user: "none right now" on 2026-10-08 |

Older plans, detailed session reports and pre-cleanup source are recoverable from
[the cleanup backup](../.codex/cleanup-backup-20261004/). Raw validation artifacts
remain intact. Historical claims do not supersede this Roadmap.
