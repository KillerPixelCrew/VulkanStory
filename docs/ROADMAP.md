# VulkanStory Roadmap

Updated 2026-10-09. Authoritative current status; detailed results are in
[development-evidence.md](development-evidence.md) and the dated implementation and
validation records under [records/](records/).

Last installed development package: the 2026-10-09 code-quality build (Release managed
build, all five provider bridges, win-x64 stage/package/deploy passed). Its runtime
behaviour checks (CQ-01) are outstanding. The REN-06 composition gate and the REN-07
sky-edge jitter fix are implemented but not yet built.

Commit hashes quoted in older records predate the 2026-10-09 history rewrite (which
removed the vendored Streamline SDK and local agent files) and no longer resolve.

## Immediate goal and next work

Working upscaling/frame generation across supported vendors, and usable original
Options menus from both the main menu and a loaded world. The goal is **not complete**.

1. **SDK-01/SDK-04: broaden runtime transition acceptance.** Required XeLL enables
   before FG and stays active through requested Off. First-enabled history reset
   resolved the observed -12 stop. The existing XeSS→FSR3→XeSS and latency
   Off→On→Boost profiles passed all actions/captures on RTX 4070. Ordinary
   Options close/resize/resume passed on DLSS: both measured resume windows
   produced 40 real/80 SDK presents. `6a3fab6` removes the stale full-extent override;
   validate using a freshly built bridge with no sanitize warning. Wider quality/
   pacing and eligible Intel behavior remain open.
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
   shared-sidebar overflow follows the original fixed layout at saved GUI scale1.25.
   Existing logs prove recomposition, so no redundant parent callback is needed;
   this inherited layout limit is not a missing VulkanStory resize implementation.
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
| DLSS FG | Focused gain/lifecycle passed; full-extent override removed in source | Validate with fresh bridge/no sanitize warning; moving quality/scanout/pacing and warning-39 recovery |
| XeSS SR | NVIDIA and Intel evaluations | Wider scene/quality acceptance |
| XeSS FG | XeLL/history corrections validated; XeSS→FSR3→XeSS and Off→On→Boost scenarios passed | Wider transitions, moving-scene/visible quality/pacing and eligible Intel acceptance |
| FSR3 SR/FG | NVIDIA SDK path; Intel single-queue direct interpolation, HUD composition and extra submissions | Moving-camera/disocclusion/scanout/pacing |
| FSR4 | Bridge/runtime implemented; unsupported-NVIDIA fallback | Supported AMD execution |
| Options | Main/world callback lifecycle passed; custom panels fit; inherited sidebar fixed-layout limit confirmed/no missing recompose | Scrolling, broader physical interaction/error/leave-rejoin; native sidebar limit is recorded separately |
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

From the 2026-10-08 review, all **118 findings have completed implementations**;
CLEAN-05 needed no change, and none of its implementation findings is open. The detailed
[completed ledger](records/completed-review-findings-2026-10-08.md) retains every ID,
original finding, source reference, audit coverage and recorded proof limit.

### Pending review verification

- Active XeSS FG, PCL asynchronous Present brackets and XeLL sleep/input/simulation/
  render markers passed the scoped batch. XeLL Present marker returns are not
  separately logged; their overlap and wider latency impact remain open.
- Run the existing partial-output-array checks and a scenario using
  `hostGeneratedPresents`. New/expanded tests remain deferred under the working rules.

The [current evidence](development-evidence.md) separates completed source work
from these checks. Broader feature, platform and hardware work remains below.

### Code-quality review (2026-10-09)

The [code-quality review record](records/code-quality-review-2026-10-09.md) holds every
finding (I, E, R, S, A, C IDs) with its per-row implementation outcome. All rows
except S13 (not attempted: tests deferred) and the partial rows below are
implemented in the working tree, which is **unbuilt and unvalidated**. Every
row below is implemented-unvalidated unless marked open.

| Finding | Kind | Status | Validation or remaining work |
| --- | --- | --- | --- |
| I2/C5 PCL ping hook | Implementation gap (migration dropped the install call) | Implemented, unvalidated | PCL ping markers reach PC Latency stats on Streamline hardware; hook removed before device disposal (SDK-04) |
| I1 VSync mode 2 | Known defect (ran VSync off + sleep) | Implemented, unvalidated; behaviour change | Modes 0/1/2 give swap interval off/on/on and limiter on/off/on; vanilla `SetVSync` calls now ignored |
| C1 startup window border | Known defect (Hidden start not maximizable) | Implemented, unvalidated; behaviour change | Hidden + Maximized startup fills the screen; border kept after leaving fullscreen (visible check, needs user instruction; SDL-01) |
| C2 mouse delta scaling | Known defect (relative deltas scaled by DPI, truncated) | Implemented, unvalidated; behaviour change | Look speed at >100% scaling matches vanilla; unchanged at scale 1; SDL3 relative units per platform |
| C14/C27 window size units | Known defect (logical vs pixel sizes) | Implemented, unvalidated; behaviour change | Saved size round-trips across launches at 150%; maximized size not saved; scenario resize, minimum size and `ScreenSize` are pixels (options-lifecycle scenario now asserts `displayWidth/Height`); possible creation flash |
| C4 DLSS-G reset tagging | Known defect (reset frames never reached providers) | Implemented, unvalidated; behaviour change | Teleport/rebase frame is reset-tagged and does not ghost; diagnostics now show `CanGenerate=true` on reset frames (SDK-02) |
| C3 XeSS FG transient pause | Known defect (presenter torn down on every wait) | Implemented, unvalidated; behaviour change | Pause/rebase/FOV change keeps the DX12 presenter with pass-through presents; first re-enabled frame resets history (I6); XeSS-FG-eligible hardware |
| C8 settings reset scope | Known defect (cosmetic settings caused full reset) | Implemented, unvalidated; behaviour change | TAA sharpness drag no longer resets FG/SR/targets/history; restart-only fields saved without reset |
| C9 minimized/parked throttle | Known defect (full load while minimized) | Implemented, unvalidated; behaviour change | Minimized window or renderer-reported stall (`VulkanDevice.PresentationStalled`: parked swapchain or skipped acquire) waits up to ~33 ms per frame and wakes on events; restore and resize resume normally |
| C26 event-tracked gamepad buttons | Efficiency | Implemented, unvalidated | Button mask maintained from forwarded `GAMEPAD_BUTTON_DOWN/UP` events with full resync on open/remap/focus/2 s rescan; no stuck or missed buttons, pressed edges unchanged |
| E1, A3 follow-ups | Efficiency / simplification | Implemented, unvalidated | Descriptor cache hits allocate nothing (span alternate lookup); single-source XeSS latency selection with unchanged call order |
| C10 precise sleep | Known defect (15.6 ms sleep granularity) | Implemented, unvalidated; behaviour change | maxFps 60 cadence and FSR3 direct-FG pacing with `PreciseSleep` (high-resolution timer + spin) |
| I4, C15, C17, C18 native bridges | Known defects (stale params, timing race, re-init, TOCTOU) | Implemented, unvalidated | Rebuild and ship `native/streamline` and `native/xess-fg` with the managed assembly (renamed `VulkanStorySlInvalidateFrameTags`); no sanitize warning; Streamline re-init; verified interposer load |
| I5, C16, C21, C22, C23, E11, I9 | Known defects / correctness risks | Implemented, unvalidated | Streamline OUT_OF_DATE during waits rebuilds the swapchain; teardown after a failed wait; depth blit formats; latency follows effective FG; default vertex binding persists across vendor passes; vanilla SSAO guard |
| S13 test hooks; `porting/renderer-tests` | Deferred test work | **Open**, deferred by test policy | Decide on `*ForTests` hooks; fixture must map GL stencil ops locally (`StencilOpFrom` removed) and fix the stale `DlssUpscaler` constructor at migration |

## Remaining feature and verification work

These IDs retain remaining feature/compatibility work and verification boundaries.
Pending acceptance does not reopen completed review implementation findings.
The [completed ledger](records/completed-review-findings-2026-10-08.md) maps the closed
findings to their broader feature and acceptance areas.

| ID | Remaining completion boundary |
| --- | --- |
| CQ-01 | Validate the 2026-10-09 code-quality implementation: Game builds, rebuilt Streamline/XeSS-FG bridges, package, harness on the foggy village snapshot, and the behaviour changes in the code-quality table (including the C9 renderer stall throttle, C26 event-tracked controller buttons, C14 pixel units and the E1 descriptor lookup). Progress 2026-10-09 (`artifacts/validation/code-quality-20261009-212939`): Release managed build, shaders and all five Windows bridges built (6 compiler warnings: CS1573 at `SdlWindowHost.cs:434` and `VulkanDevice.cs:177`, CS8600 at `GameRenderSession.Scenarios.cs:525`); win-x64 staged, packaged and deployed to the user game (366 payload files verified, owned `loader.ini` preserved). Harness run and runtime behaviour checks still open |
| CQ-02 | Open code-quality remainders: S13 `*ForTests` hooks and `porting/renderer-tests` fixture fixes (`StencilOpFrom`, `DlssUpscaler` constructor), deferred to test migration |
| PORT-01 | Controller world ownership/damage cleanup acceptance |
| REN-01 | Historical DLSS sky pattern: current nonreproduction is not a fix |
| REN-02 | Moving camera/object/animation/transparency motion parity |
| REN-03 | History reset, resize, option changes and shader reload |
| REN-04 | Post-processing/material parity |
| REN-05 | Graph/resource ownership and bounded fallback |
| REN-06 | First world load showed the renderer assembling itself (missing draws, raw jitter, then TAA/SR/FG). Implemented 2026-10-09, **unbuilt and unvalidated**: a composition gate holds the last loading image until no draw waits on a pipeline, motion is valid, TAA/SR composed a full jitter cycle and FG is ready to enable (15 s timeout logs the unmet condition); temporal/FSR-blit pipelines are requested when targets are built; a cold FSR-blit pipeline no longer disables FSR for the session. Validate on the foggy village snapshot with `VULKANSTORY_HEADLESS_COMPOSITION_HOLD=1` (the harness bypasses the gate otherwise) and visibly per provider. Mid-game first-use pipeline drops (new entity/mod variants) are a separate defect |
| REN-07 | Geometry edges against sky/fog/clouds jittered under native TAA and FSR3 (user report 2026-10-09; foliage against terrain stable). Cause: the sky reactive value (cloud/fog coverage) was read at the sky-side edge pixel while motion came from the nearest-depth tap, so edge pixels dropped history on sky jitter phases. Implemented 2026-10-09, **unbuilt and unvalidated**: `taa-resolve.fsh` reads reactive from `closestPixel`; the FSR3 reactive mask takes the 3x3 minimum. FSR4 passes no reactive mask; an unavailable FSR4 falls back to native TAA. Needs a visible check on the foggy village save with TAA, FSR3 and FSR4 |
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
a local, unpublished cleanup backup. Raw validation artifacts
remain intact. Historical claims do not supersede this Roadmap.
