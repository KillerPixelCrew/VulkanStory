# VulkanStory rewrite roadmap

Updated: 2026-10-04. Active project: D:/Coding/VulkanStory-Rewrite.
This is the authoritative current roadmap. Historical checkpoints are evidence
for their own payloads; they do not override the statuses below.

## Objective and working rules

Finish the retained custom Vulkan renderer, upscalers, frame generation and SDL3
as a fresh mod using the existing Vintage Story installation and normal shortcut.
The bulk source migration is integrated. Remaining Windows work is defects,
lifecycle completion and feature acceptance; full completion is not yet proved.

- Baseline game: Vintage Story 1.22.7, Windows x64. World: **foggy village story**.
- Routine world checks use the migrated hidden harness and an isolated snapshot
  of that complete world. Do not create a superflat or a new character baseline.
- Leave the user's installed game, settings and original save alone during
  routine development. Visible launches/deployment require the user's instruction.
- Preserve working donor algorithms/layouts and provenance. No Optimum launcher,
  patched game assemblies, binary-delta pipeline or separate game installation.
- Install direction: hostfxr.dll plus VulkanStory payload and ordinary mods beside
  the original executable. Keep the normal shortcut and version.dll name available.
- **Defer writing or expanding tests until integration is finished.** Implementation
  and validation remain separate turns. A validation turn runs one bounded batch;
  fixes happen in a later implementation turn. Do not repeat full suites/packages
  after each small edit or infer broad acceptance from one successful capture.

## Current milestone status

| Milestone | Current status | Remaining completion boundary |
| --- | --- | --- |
| B0: early activation | Implemented; normal-shortcut activation recorded | Current disable/failure behavior and MFG proxy coexistence |
| P0: source migration | Renderer, SDL host, providers and native bridges integrated; current Game/Mod and FSR3/Streamline release repairs compiled | Current source unpackaged; retained feature operation and final audit |
| G0/G1: original game, window, graphics API and menus | Active routing; real menu/world use recorded | Interactive SDL lifecycle, reload/fallback and latest payload coverage |
| G2: scene, temporal and post-processing | Real world renders; motion/post owners integrated | Sky defect, moving-scene/history correctness and option transitions |
| G3: providers and controls | SR/FG/latency and controller systems integrated; scoped SDK execution recorded | Actual generated output/pacing, complete controls, failure paths and hardware coverage |
| R0: Windows delivery | Development ZIP/update/remove tooling assembled | Current source in release candidate; install/update/disable/remove and notices acceptance |
| C0: third-party direct OpenGL | Planned; adaptation module unimplemented | Targeted mod profiles and shared resource/state translation after core parity |
| Linux activation/release | Inherited source paths retained; new activation/package incomplete | Linux entry, package and platform/provider execution |

## What is already ported

| Feature | Source ownership | Recorded evidence / limitation |
| --- | --- | --- |
| Vulkan device, resources, descriptors, pipelines/caches, shaders, frame graph, uploads/readback and queries | [Render.Vulkan](../src/VulkanStory.Render.Vulkan), [graphics adapter](../src/VulkanStory.Game/GameGraphicsAdapter.cs) | Managed production builds and real world images; full resource/material parity open |
| Official startup, graphics API, native scene/content routes, frame loop and shutdown | [startup composition](../src/VulkanStory.Game/StartupProfileComposition.cs), [scene composition](../src/VulkanStory.Game/SceneProfileComposition.cs) | Pinned 1.22.7 routing active; compiled against official assemblies |
| Terrain, shadows, entities/held items, mechanical instances, particles/decals, sky/celestial, clouds/map, liquids and OIT | Game graphics/consumer partials and retained shaders | Loaded world visible; all moving content is not thereby verified |
| Jitter/camera/object/animation/instance motion, TAA, GTAO/SSAO, bloom/god rays/luma, sharpening/final composition and separate UI | [temporal owner](../src/VulkanStory.Game/GameTemporalOwner.cs), [frame state](../src/VulkanStory.Game/TemporalFrameState.cs), graphics post partials | Native-resolution and SR captures; sky writer output repaired and sampled |
| DLSS SR, XeSS SR, FSR3 SR, FSR4, DLSS-G, XeSS-FG, FSR3 FG | [SR host](../src/VulkanStory.Game/RuntimeUpscalers.cs), [FG host](../src/VulkanStory.Game/RuntimeFrameGeneration.cs), [native bridges](../native) | All five bridges built; FSR3-to-XeSS and FSR3-to-DLSS switches recorded; FSR4 hardware execution open |
| Reflex/PCL, XeLL and Anti-Lag frame identity/input/simulation/render/present ordering | Vulkan device latency/presentation modules and session pre-input hooks | Scoped SDK execution; complete pacing/latency acceptance open |
| SDL3 window/events, focus/fullscreen/scaling, cursor, clipboard, IME, file drop, touch, controller navigation/remaps/profiles/gyro/haptics/keyboard | [SDL host](../src/VulkanStory.Platform.Sdl), [game adapter](../src/VulkanStory.Game/GamePlatformAdapter.cs), [input](../src/VulkanStory.Game/Input) | Window/event integration active; interactive controls need acceptance |
| Private PromptFont glyphs, analog movement and optional server companion | [font owner](../src/VulkanStory.Game/Input/ControllerPromptFont.cs), [shared input](../src/VulkanStory.Input), [companion](../src/VulkanStory.Input.Companion) | Font embedded; companion loads on integrated server; actual glyph/input negotiation open |
| Screenshots/timelapse, AVI acquisition, declared mod-pass/motion-writer API | [capture routing](../src/VulkanStory.Game/QueriesCaptureConsumerPatches.cs), [mod API](../src/VulkanStory.Game/ModRendering/VulkanStoryModPasses.cs) | PNG/PPM captures succeed; AVI patch accepted; actual video/custom callback execution open |
| Ordinary mod/settings/status commands, early disablement, package/update/remove tooling and headless harness | [Mod](../src/VulkanStory.Mod), [scripts](../scripts), [player instructions](../packaging/README-client.txt) | Fresh archives built; commands and isolated world capture recorded; removal behavior not executed |
| Bounded autonomous headless scenario core | [scenario parser](../src/VulkanStory.Game/HeadlessScenario.cs), [session scenarios](../src/VulkanStory.Game/GameRenderSession.Scenarios.cs), [launcher](../scripts/dev/headless-capture.ps1), [result verifier](../scripts/dev/verify-headless-result.ps1) | Implemented and accepted by source review on 2026-10-03; strict preflight/result types, finite actions, pre-FG capture versus late completed-frame receipts, timeout/parity handling and deferred terminal success. Managed core compiled on 2026-10-04; unrun. Virtual input, GUI/world actions and provider/D1 scenario integration remain pending. Controller/touch enablement is rejected until an owned input profile exists. |

The SDL project intentionally compiles only game-neutral window/event/coordinate/
native-loader code. Game-facing touch/key/controller/OS glue lives in Game/Input;
excluded donor reference copies are not missing active integration.

## Current source, package and installation

- Latest candidate: [VulkanStory-win-x64.zip](../artifacts/validation/delivery-refresh-20261001-223159/archives/VulkanStory-win-x64.zip).
  Size 142,832,071 bytes; 375 entries. SHA256:
  4FCD606A690A7C0B8421FEE9E7C084E2089D7B0EE494B36DE248B653F2C50614.
- Latest [delivery build](validation-delivery-refresh-2026-10-01-01.md): Game and
  dependencies passed with zero warnings/errors; removal/stage/archive scripts
  parse; archive payload hashes match. Package acceptance remains unverified.
- **Source is newer than that ZIP:** controller world/damage-listener cleanup,
  the autonomous scenario core and per-callback mod-pass viewport restoration
  plus deferred-retirement owner retention and the first Options-tab integration
  are implemented and [managed compilation passed](validation-options-managed-2026-10-04.md)
  (Game: one harness nullable warning; Mod: none), but remain unpackaged and
  unverified at runtime. See PORT-01, API-01, REN-05 and
  [UI-01 source identities](options-tab-integration-2026-10-04.md). The harness
  source review is [accepted](../.codex/reviews/1738e94bc7e243258362ee74fff5f9b4/H01-R3-review.md);
  [viewport copy evidence](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P02-prep/capture-api/viewport-packet/results/VP-I01-complete.md)
  records the separately approved two-line source fix.
- **Latest source/build:** the [FSR3 swapchain release repair](fsr3-swapchain-release-2026-10-04.md)
  adds checked native exports and managed failure retention. The subsequent
  [session/device release and Streamline cache repair](session-provider-release-2026-10-04.md)
  stops dependent cleanup on provider failure and preserves successor options.
  [Current Game/Mod and matching FSR3/Streamline bridges built](validation-provider-release-2026-10-04.md)
  in one batch; FSR3 checked exports are present in its PE table. Game has one
  harness warning; FSR3 has eight native warnings. Runtime remains unverified;
  no new package or deployment exists.
- **Earlier runtime checkpoint: FAIL, unresolved.** The [isolated world batch](validation-provider-world-2026-10-04.md)
  rendered with providers off, then captured black output after off → FSR3.
  Motion remained invalid; successful SR evaluations and FG prepared frames were
  zero. The assertion stopped before DLSS/XeSS. Child exited normally; no rerun
  or installed deployment occurred. The fresh development stage is not an
  accepted release candidate.
- **Latest focused runtime checkpoint:** [motion transition diagnostics](motion-transition-diagnostics-2026-10-04.md)
  now report exact scene/compiled-mode/producer failure gates;
  [compiled and exercised in one focused batch](validation-motion-gate-2026-10-04.md).
  No numerical or shader-reload fix is yet justified; the black-frame cause
  remains unresolved.
  The focused off → FSR3 run passed with visible world output, motion location 4
  matching, 141 successful upscales and 139 prepared FG frames. It did not
  reproduce the preceding failure; diagnostics alone are not a proved fix.
- **Current Options source:** [Options callback ownership](options-callback-ownership-2026-10-04.md)
  prevents replaced/closed composers from changing, saving or reopening settings.
  Applied in both Options and standalone hosts;
  [Game/Mod compiled and current DLSS → XeSS runtime passed scoped assertions](validation-dlss-xess-2026-10-04.md).
  Options interaction remains unrun. DLSS reported output cadence did not show
  the requested interpolation gain; actual FG output/pacing remains open.
- **Current protocol/controller source:** [Streamline protocol checks](streamline-protocol-results-2026-10-04.md)
  now propagate token/Reflex failures and publish applied mode only after success;
  [compiled/run](validation-dlss-gain-2026-10-04.md). No protocol error surfaced;
  they did not resolve the DLSS output-cadence issue.
  [Options controller entry](options-controller-entry-2026-10-04.md) now saves the
  draft, acknowledges actual child opening and owns pause-menu focus/return;
  also compiled; actual controller/Options interaction remains unrun.
- **Current visible harness:** [explicit visible isolated harness](visible-isolated-harness-2026-10-04.md)
  now supports visible capture and bounded manual inspection while default runs
  remain hidden. [Built, staged and scripts parsed](validation-visible-ready-2026-10-04.md).
  Exact DLSS-gain and manual Options launchers are prepared; no visible launch
  is authorized or performed.
- **Source newer than that prepared stage:** [SR release retention](sr-release-retention-2026-10-04.md)
  checks FSR3/XeSS context destruction and retains failed registry owners/tail;
  [Game/Mod and matching FSR3 bridge compiled](validation-sr-release-2026-10-04.md);
  unrun. The prepared visible
  launch scripts/stage remain unchanged while approval is pending.
  Subsequent [Options entry viewport placement](options-entry-viewport-2026-10-04.md)
  clamps the original landing entry and owns narrow-sidebar fallback; unbuilt/unrun.
  [Isolated world Options navigation](options-world-diagnostic-2026-10-04.md) now
  drives the real pause-menu entry/composer click path for capture;
  [build failed](validation-options-world-build-2026-10-04.md) on missing
  Vintagestory.API.Common import for EnumMouseButton. No client launched. The
  [import correction](options-world-diagnostic-2026-10-04.md) is now applied;
  [corrected build and real pause Options capture ran](validation-options-world-runtime-2026-10-04.md).
  The panel rendered, but shutdown crashed during late GUI cleanup after Vulkan
  routing/device release. Raw verifier pass is invalid as lifecycle acceptance.
  [Shutdown ordering and crash verification](options-shutdown-order-2026-10-04.md)
  are [compiled and passed a corrected hidden Options checkpoint](validation-options-shutdown-2026-10-04.md).
  Normal shutdown had no crash; the verifier rejected the old crash artifact.
  Header truncation and initially clipped Save/Cancel remain UI defects.
  [Fixed footer and header fit](options-fixed-footer-2026-10-04.md) are now applied
  in source: actions sit outside the scroll viewport; header font is smaller.
  Unbuilt/unrun; actual fit and Save/Cancel interaction remain pending.
- **2026-10-03 stop point:** development/delegation stopped at the user's request;
  this chat is back in DIRECT mode. All 12 applied harness/viewport file hashes
  matched their accepted records at stop. No build, test, preflight/verifier run,
  GPU/game run, package or deployment ran in this session. The installed game,
  settings and original save were not changed. Options, lifetime and other source
  proposals under the [saved run](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4)
  are **unapplied**; interrupted revisions are drafts, not execution-ready work.
- Installed Game DLL inspected during regroup dates from **15:37**, differs from
  the **22:32** candidate DLL and predates later fixes. Restarting the normal
  shortcut does not install the candidate. This roadmap update deploys nothing.
- Native compiler warnings are retained in the native build records. A clean
  managed build is not a claim that all native compilations had zero warnings.

## Remaining work: current Windows port

Types below distinguish **fix/source completion** from **integrated, verify**.
No item is closed until its stated completion condition has matching evidence.

### 1. Current source and renderer correctness

- [ ] **PORT-01 — Finish the pending controller world-ownership increment** (source
  implemented and current Game compiled; delivery/runtime pending). Source: SdlGamepadInput, session controller
  callbacks and world/disposal routes. Confirm unbinding belongs to the departing
  world, delayed disposal preserves a newer loading world's controls, and disable/
  re-enable still closes UI, flushes profiles and releases actions/gyro/rumble.
  Done: current source compiled and included in the final candidate; world rejoin
  behavior recorded. [Source record](controller-world-damage-ownership.md).
- [ ] **REN-01 — Resolve the DLSS horizontal sky pattern** (known defect). Source:
  sky motion/frame history, RuntimeUpscalers, NGX evaluation and native sky/dither.
  Retained Slot22 float exports contain the structure with FG off, but the
  [2026-10-03 source investigation](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/O01-sky-research.md)
  shows that final composition overwrites this same texture before the dump.
  These exports do not certify untouched post-NGX output; the earlier
  before-final-composition localization is not established. Historical
  output4/writer-depth1 repair evidence remains, and the pattern persists.
  No cause or numerical fix is proved. A [single-frame boundary diagnostic](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P01/D1/D1-packet.md)
  is specified but neither implemented nor run. Done: a source-backed cause/fix or
  supported limitation decision, and original-resolution world evidence showing
  the accepted result. Do not invent a jitter/sign/scale/dither change from guesses.
- [ ] **REN-02 — Complete moving-scene motion parity** (integrated, verify).
  Sources: temporal owner/frame state, object/instance histories and terrain,
  animated/rigid/dropped/falling/held-item/liquid/sky consumers. Cover camera move/
  rotation, wind/animation, liquids/clouds, hand FOV and both motion locations2/4.
  Done: current-frame vectors/writer depth and previous transforms agree with the
  rendered content; invalid history safely resets; no missing writer certification.
- [ ] **REN-03 — Close history/reset and shader reload transitions** (integrated,
  verify). Cover world leave/rejoin, resize/render scale, provider/quality changes,
  TAA/AO on/off, FOV/dimension/teleport/rebase and handheld shadows on/off. Sources:
  GameTemporalOwner, ShaderModes/Shaders, default framebuffer allocation and session
  settings/terrain reload. Done: no old camera, shader mode, target or specialization
  survives a transition; requested/effective state describes the rendered frame.
  The [October-4 off → FSR3 run](validation-provider-world-2026-10-04.md) reproduced
  invalid motion and black output after switching. Target/shader-mode transition
  and motion certification need a source fix before another runtime batch.
  [Exact-gate diagnostics](motion-transition-diagnostics-2026-10-04.md) are now
  [compiled/run](validation-motion-gate-2026-10-04.md). The focused run passed;
  the earlier failure remains unexplained. Source already reloads shaders on the switch; do not
  duplicate reloads or force motion validity without identifying the failed gate.
- [ ] **REN-04 — Close post-processing and material parity** (integrated, verify).
  Cover TAA resolve/sharpen, GTAO and vanilla SSAO presets/debug, bloom, god rays,
  luma, final blit, OIT, shadows/celestial, GUI/text/clipping and late overlays.
  Done: retained features compose correctly in the baseline world and menu, with
  HUD excluded from SR/FG inputs and intended depth/alpha behavior preserved.
- [ ] **REN-05 — Close graph/resource and fallback boundaries** (integrated;
  cleanup fixes required). Sources: stage/native/post graph declarations, target scopes, barriers,
  descriptor/uniform snapshots and retirement. Cover forced shader rewriter and
  asset overrides, async first-use pipelines, transient/aliased reads, shader
  deletion/reload, shared depth and resize/minimize/restore. Done: no stale handles,
  dropped mandatory draw or invalid read/write/lifetime; timestamps name the actual
  pass. Classify the two inherited donor resize-sync reports separately from any
  reproduced rewrite issue; historical reports alone are not current failures.
  The [parallel source investigation](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P02-prep/render-provider/render-provider-readiness.md)
  identifies failed-release retention/disposal defects in current swapchain,
  session and device owners. These are source findings, not reproduced GPU faults;
  the broader proposed lifetime repair remains unapplied. A bounded direct
  [deferred-retirement fix](deferred-retirement-ownership-2026-10-04.md) now keeps
  failed owners and the unprocessed tail, rejects cleanup retries, and guards
  frame/device teardown against a known deferred failure. Source inspection only;
  that managed increment compiled, unrun. The subsequent
  [FSR3 swapchain/retirement repair](fsr3-swapchain-release-2026-10-04.md) is applied
  and compiled in the [provider-release batch](validation-provider-release-2026-10-04.md),
  but unrun. Other provider and general disposal defects remain.
  See SDK-03.

### 2. Upscalers, frame generation and latency

- [ ] **SDK-01 — Close SR settings and switching behavior** (integrated, verify).
  Sources: RuntimeUpscalers, provider backends/bridges and target planning. Cover
  off/DLSS/XeSS/FSR3, applicable quality/native-AA modes, output/render sizes, LOD
  bias, resize and live switches. Done: valid inputs/output and predictable fallback
  on unsupported hardware/missing runtime/evaluation failure. FSR4 execution is HW-01.
  Current [off → FSR3 evidence](validation-provider-world-2026-10-04.md) failed:
  zero successful SR evaluations and black post-switch output. Earlier initial-
  FSR3 handoffs do not certify this off-to-provider path.
- [ ] **SDK-02 — Establish actual FG output and pacing** (integrated, verify).
  Sources: RuntimeFrameGeneration, Streamline/FSR3/XeSS presenters and UI separation.
  Done: visible generated frames have correct motion/occlusion and HUD composition;
  menu/loading/pause/return-to-world suspend/resume without stale tags or hitching;
  SDK counts, supported multiplier clamping and real/display pacing agree. Hidden
  prepared-frame counters and SDK present reports do not prove interpolation quality.
  [October-4 DLSS → XeSS](validation-dlss-xess-2026-10-04.md) passed current input/
  routing assertions and scene captures. DLSS SDK reported 158 presents over roughly
  160 rendered frames; HUD FG output was near real cadence. Requested interpolation
  gain is not established. XeSS reported about twice real cadence, still without
  visible interpolated-frame or display-pacing proof.
  Source tracing found one consuming DLSS state query per application frame;
  SDK documentation describes two presents for one interpolated plus one rendered
  frame. Do not repair the near-one cadence by inflating the reported counter.
  [Current strict DLSS gain assertion failed](validation-dlss-gain-2026-10-04.md):
  180 SDK presents for 180 rendered frames between checkpoints, despite prepared
  inputs, one generated frame configured and SDK status zero. No checked protocol
  error surfaced. Hidden-window gain is absent in this run; visible-window
  behavior and SDK/proxy cause remain unproved.
  An [explicit visible isolated mode](visible-isolated-harness-2026-10-04.md) is
  now [built/staged](validation-visible-ready-2026-10-04.md) for this missing
  window/presentation boundary. It still requires the user's visible-launch instruction.
- [ ] **SDK-03 — Close provider failure/drain ownership** (remaining source defects;
  failure checks unrun). Sources: FSR3/Streamline native bridges, managed FG
  reset/suspend, swapchain recreation and SR retirement. Done: handoff/resize/shutdown
  cannot retire SDK-owned inputs or swapchain state before successful disable/drain;
  failures preserve ownership and give a usable reason. Existing successful switches
  do not establish failed-disable behavior. Earlier lower-level fixes do not cover
  all owners. A direct [FSR3 swapchain release repair](fsr3-swapchain-release-2026-10-04.md)
  now stops native release on failed disable/drain/destroy, propagates checked
  results, retains failed slots/queue tail and prevents cleanup retries. Transitions
  process retained slots even without a current slot; known failure blocks device
  cleanup. This increment and its matching bridge compiled in the
  [provider-release batch](validation-provider-release-2026-10-04.md); unrun.
  The subsequent [session/device and Streamline cache repair](session-provider-release-2026-10-04.md)
  stops dependent GPU cleanup on first failure, checks initial device idle and
  retains failed owners. Streamline old-chain destruction now preserves the
  successor's options cache; current source did not disable FG at that seam.
  This increment also compiled in that batch; unrun. Other lower-level provider destruction
  returns, direct-device provider release ordering and runtime failure checks remain.
  [SR release retention](sr-release-retention-2026-10-04.md) now checks FSR3/XeSS
  destruction and stops registry owner removal/dependent cleanup after release
  errors, including bring-up cleanup and live retirement.
  [Managed/native compilation passed](validation-sr-release-2026-10-04.md), unrun;
  failure-path execution and other provider owners remain open.
  A [frozen lifetime proposal](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P02-prep/render-provider/lifetime-packet/lifetime-packet.md)
  is **not applied or approved**. Its [last completed review](../.codex/reviews/1738e94bc7e243258362ee74fff5f9b4/RP-PR01-review.md)
  requires three corrections: old Streamline retirement must not disable the live
  successor; deferred failed FSR3 destruction must retain managed owners/tail and
  propagate the terminal failure; provider transition must prepare retained slots
  even when there is no current slot. Later revisions were interrupted. Private
  ABI-2 payloads remain proposal artifacts. The fresh October-4 bridges above
  come from the direct source repairs; none were deployed.
- [ ] **SDK-04 — Close latency authority and SDK warnings** (integrated, verify).
  Sources: session pre-input hooks, frame tokens, Reflex/PCL/XeLL/Anti-Lag and vendor
  frame-cap selection. Done: one pacing authority, correct sleep/marker ordering,
  supported off/on/boost selection and fallback. Document or repair the three
  retained unsupported Streamline hook warnings with their actual SDK impact;
  do not silence them merely to produce a clean log. The 2026-10-03 parallel source
  report also identifies discarded managed pacing/token error returns (RP-F3);
  those findings are only partially repaired below. Warning impact remains open.
  [Runtime protocol result checks](streamline-protocol-results-2026-10-04.md) now
  propagate bound Streamline token/Reflex options/sleep/handoff errors and cache
  mode only after success; [compiled/run without protocol error](validation-dlss-gain-2026-10-04.md).
  XeLL/PCL marker error paths remain.
  Fresh October-4 logs name unsupported sl.common Vulkan hooks CmdBindPipeline,
  CmdBindDescriptorSets and BeginCommandBuffer; their impact remains unresolved.

### 3. SDL and controller completion

- [ ] **SDL-01 — Interactive window/input lifecycle** (integrated, verify).
  Sources: SdlWindowHost/EventPump, GamePlatformAdapter, window consumers and GUI
  text/cursor owners. Cover first visible window/icon, focus loss/gain, relative
  mouse/warp, logical-versus-pixel sizes, DPI/display changes, fullscreen/borders,
  minimize/restore, safe-area notification, clipboard, IME composition/text target,
  file drop, close cancellation and shutdown. Done: original game behavior is
  preserved and no successful SDL path needs an OpenGL/GLFW window.
- [ ] **SDL-02 — Controller glyphs, UI and profiles** (integrated, verify).
  Sources: ControllerPromptFont/Glyphs/Hints, settings/keyboard/navigation and
  profile store. Cover Xbox/Sony/Nintendo face/shoulder/trigger/axis prompts,
  unknown-font/text fallback, wide glyph fit, remap capture, save/reload, keyboard
  target and enable/disable/world cleanup. Done: displayed prompts match active
  bindings/layout and owned UI/resources do not outlive their world/session.
  [Official Options metadata](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P01-prep/UI/ui-profile.md)
  and [pinned SDL ABI/cursor/grab/focus contracts](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P01-prep/SDL/sdl-profile.md)
  are prepared; virtual-device and UI automation adapters are not implemented.
- [ ] **SDL-03 — Physical/controller/touch arbitration and devices** (integrated,
  verify). Cover overlapping key/button owners, analog sticks/deadzones/movement,
  sneak toggles, gyro, damage/trigger rumble, hot-plug/device switch, focus loss,
  touch tap/drag/hold/cancel and disabling mid-action. Done: exactly the intended
  input reaches the game, with no stuck action, stale rumble or focus stealing.
- [ ] **NET-01 — Analog companion negotiation/rejoin** (integrated, verify).
  Sources: AnalogClient/Direction consumers, shared AnalogMovement and Companion.
  Done: integrated singleplayer and compatible remote server acknowledge analog
  input correctly; unacknowledged/absent companion retains digital movement;
  disconnect/rejoin clears state. Recorded server mod loading proves only loading.
  The [ordinary isolated-world leave/rejoin route](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P01/V01-prep/P01-world-route.md)
  is pinned by static official-assembly inspection, but no scenario executed it.
  Its unbounded ReloadWorld wait must not be substituted for the bounded route.

### 4. Capture and mod extension features

- [ ] **CAP-01 — Screenshot/timelapse options** (integrated, verify). Sources:
  QueriesCaptureConsumerPatches and graphics capture association/readback. Done:
  normal screenshots, applicable size/HDR/timelapse options, row/channel orientation
  and world/session disposal behave correctly. Harness PNG/PPM already works.
  [Ordinary consumer/fixture preparation](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P02-prep/capture-api/capture-api-readiness.md)
  is saved; normal screenshot/timelapse callbacks were not run in this session.
- [ ] **CAP-02 — Encoded AVI recording** (readback port integrated; execution open).
  Sources: SdlXPlatformInterface.GetAviWriter, owned writer associations and patched
  AddFrame readback. Done: real start/frame/stop produces a decodable recording
  without any OpenGL binding dependency or stale capture owner. Startup patch
  acceptance alone does not prove recording. Static metadata now pins the real
  MJPEG writer and asynchronous Close behavior; neither recording nor independent
  video decoding ran. The proposed driver remains unimplemented.
- [ ] **API-01 — Execute declared mod passes/motion writers** (API/host integrated,
  callback execution open). Sources: ModRendering/VulkanStoryModPasses and graphics
  ModPasses/stage context. Done: a registered consumer actually renders using
  declared resources, obeys read/write restrictions and motion masks, restores
  caller state after exceptions, and unregisters cleanly on reload/leave. Existing
  vanilla stage dispatch has no registered custom consumer evidence. The approved
  per-callback viewport snapshot/restore is **applied** in
  [GameGraphicsAdapter.ModPasses](../src/VulkanStory.Game/GameGraphicsAdapter.ModPasses.cs);
  [source evidence](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P02-prep/capture-api/viewport-packet/results/VP-I01-complete.md)
  verifies exactly two insertions and unchanged guards. It isolates the viewport
  on normal return and caught callback errors, not every graphics cleanup failure
  or all render state. Current Game compiled on 2026-10-04; unrun. The real declared-consumer fixture,
  exception/sentinel checks and API-01 completion are still pending.

### 5. Windows player delivery

- [ ] **UI-01 — Move VulkanStory settings into an Options-menu tab** (tab and
  controller-entry source compiled; GUI acceptance pending).
  Latest [hidden world Options run](validation-options-world-runtime-2026-10-04.md)
  clicked the original pause Options entry and rendered the Image page. Overall
  FAIL: shutdown cleanup fell through to uninitialized OpenGL. Cleanup ordering
  and crash-log verification need source fixes; header/footer fit also needs work.
  [The cleanup/verifier fix](options-shutdown-order-2026-10-04.md) is now applied,
  [validated for normal hidden Options shutdown](validation-options-shutdown-2026-10-04.md).
  The prior failure remains historical evidence; corrected child closed without
  a crash. Header/footer fixes and full interactive/main-menu acceptance remain.
  Match the Optimum placement with a dedicated VulkanStory tab
  inside the game's existing Options menu. The prior standalone placement was not
  reachable through Options from a loaded world, as reported on 2026-10-01.
  Sources: MenuSettingsConsumerPatches, RendererSettingsScreen, the Mod settings
  dialog and shared RendererSettingsPanel; use the donor Options-tab integration
  as the layout reference while binding official 1.22.7 UI through the new mod.
  Cover both the main-menu Options screen and the loaded-world pause-menu Options
  dialog. Reuse the same settings state/save callbacks and retain all renderer,
  upscaler, frame-generation, effects, device/controller and status controls,
  requested/effective values and restart-required labels.
  Done: the tab is discoverable and usable through ordinary Options navigation
  from either context; settings persist/apply correctly, closing returns to the
  parent Options menu and leave/rejoin causes no stale UI owner. A loaded-world
  user must be able to reach every setting without leaving the world.
  The [2026-10-04 direct source increment](options-tab-integration-2026-10-04.md)
  adds a VulkanStory entry on the original Graphics landing tab in both Options
  hosts, embeds all five existing settings pages in the same host, and returns
  Save/Cancel to Graphics. Content uses measured dynamic text and a clipped
  scrollbar; draft/scroll survive recomposition. Fresh bounds/cache names preserve
  the prior composer on preparation failure. Host close/disposal and standalone
  world-exit cleanup release owned UI. [Game/Mod compilation passed](validation-options-managed-2026-10-04.md);
  no GUI execution has run.
  The subsequent [callback ownership repair](options-callback-ownership-2026-10-04.md)
  guards editing lifetime and exact displayed composer across replacements and
  close; it [compiled in Game/Mod](validation-dlss-xess-2026-10-04.md), but GUI interaction remains unrun.
  [Controller entry source](options-controller-entry-2026-10-04.md) now supplies
  full-draft save, deferred actual-open acknowledgement, pending/cancellation
  behavior and parent focus/return ownership. [Compiled](validation-dlss-gain-2026-10-04.md),
  GUI unrun; controller-device,
  input/focus and leave/rejoin acceptance remain open. Broader vanilla-tab/navigation
  behavior at narrow windows/high GUI scales also remains.
  [Landing entry placement](options-entry-viewport-2026-10-04.md) now adjusts
  composer-root placement and narrow-sidebar visibility without mutating shared
  vanilla header bounds; unbuilt/unrun. The older [nine-file UI/receipt proposal](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P01/UI-first-recovery/P01-UI-first.md)
  and interrupted corrections remain unapplied drafts; the direct increment does
  not apply or certify them. Preserve the previously applied harness/viewport work.


- [ ] **DEL-01 — Current startup, disablement and failure behavior** (implemented,
  verify). Sources: bootstrap/profile, RuntimeModDisablement, loader.ini and Mod.
  Done: normal shortcut starts the current SDL/Vulkan payload; mod-manager and
  loader bypass work after restart; unsupported/missing payload/profile and
  preparation failure follow the advertised bounded failure/fallback behavior;
  server/crash reporter do not acquire a renderer or second CLR/game entry.
- [ ] **DEL-02 — Install/update/remove and recovery** (tools integrated, verify).
  Sources: deploy-runtime/remove-runtime, package inventory/receipt and README.
  Done: fresh extraction, adoption of an extracted install, update, modified-file
  preservation, external backup/rollback and removal preview/execution preserve
  unrelated loaders, user data/settings and normal game launch. Removal script
  syntax/hash delivery has evidence; no removal/rollback execution has been recorded.
- [ ] **DEL-03 — MFG version.dll coexistence** (name preserved; behavior open).
  Sources: native bootstrap/hostfxr proxy and Streamline actual feature-state query.
  Inspect the user's actual MFG enabler before determining support. Done: unchanged
  proxy co-loads with this package through the normal shortcut, actual generated
  limits are reported, and no bootstrap collision occurs. The recorded DLSS run
  reported maximum one generated frame and no dynamic MFG; requested sliders do
  not certify higher multipliers.
- [ ] **DEL-04 — Final candidate/dependency/license audit** (remaining delivery work).
  Sources: production build, bridge/shader build, native bundle inventory, stage/
  archive scripts and notices. Done: one coherent candidate contains the final
  source fixes, matching bridges/full shaders, ordinary client and optional server
  packages, supported-version profile and complete redistribution/provenance
  notices. Build from this checkout plus official game and declared SDK inputs;
  no Optimum/donor binary or old checkout is a runtime dependency. Publish the
  supported hardware/game matrix and meaningful remaining limitations. No new
  candidate or native bridge was built on 2026-10-03. The
  [baseline identity record](../.codex/baselines/1738e94bc7e243258362ee74fff5f9b4/baseline-report.md)
  found FSR3/Streamline DLL-versus-inventory mismatches in the historical
  provider-handoff development bundle; do not reuse that bundle as matching fresh
  validation input. This does not reclassify the separate October-1 ZIP as a newly
  tested or failed artifact.

## Hardware and platform gates

- [ ] **HW-01 — FSR4 on supported AMD hardware.** Runtime/shared-image code is
  ported and compiled. Successful execution, fallback and resize/lifetime parity
  require the supported adapter/driver; RTX4070 evidence cannot close this item.
- [ ] **HW-02 — Vendor FG and handheld coverage.** Record supported Intel/AMD/
  NVIDIA paths and requested-versus-effective limits. Retain the Claw's visible
  image, pacing, latency and power acceptance as hardware work; do not infer it
  from desktop builds or hidden captures. No new performance optimization is
  authorized by a pending hardware check alone.
- [ ] **LINUX-01 — Normal-shortcut activation and package** (implementation missing).
  The new native bootstrap/build currently targets Windows x64. Provide an explicit
  Linux loading/version/dependency/package route, then accept SDL first-window,
  native rendering and available provider behavior. Preserve platform availability
  differences: Windows D3D12 interop evidence does not establish Linux FG support.

## Subsequent implementation: third-party OpenGL compatibility

After native-game parity, implement the previously requested compatibility direction.
Early loading supplies timing, not an automatic OpenGL-to-Vulkan translator.

- [ ] **GL-01 — Discovery and bounded capability policy.** Identify concrete mod
  assemblies/direct GL consumers before they allocate graphics resources. Define
  supported calls, version profiles and actionable unsupported-operation diagnostics.
- [ ] **GL-02 — Shared resource/state adapters.** Translate supported creation,
  binding, uniform/buffer/draw/deletion operations through the same registry/state
  used by the game; prevent conflicting ownership or double deletion. Keep third-
  party game/window reads and shader asset overrides within explicit profiles.
- [ ] **GL-03 — Integrate the first concrete mod profiles.** Execute their render
  and reload/unload behavior; document exact supported versions/operations and
  unsupported functionality. No blanket all-mod OpenGL compatibility claim.

## Completion and order of work

1. Finish accumulated source/lifecycle changes (PORT-01), implement the Options
   tab accessible from a loaded world (UI-01), and address renderer defects
   (REN-01..05), preserving the donor baseline algorithms.
2. Close the integrated provider/SDL/capture/mod API boundaries (SDK/SDL/NET/CAP/API).
   Record bounded checks only to resolve a concrete gap, using foggy village story.
3. Assemble a coherent candidate and close Windows delivery items (DEL-01..04).
4. Keep hardware/platform and third-party implementation tracks explicit. Complete
   their applicable scope without silently treating unavailable hardware as success.
5. Write targeted regression tests after integration is finished, as requested.
   New test writing is deferred; historical suites remain evidence for their scope.

Full completion requires an item-by-item source/runtime/artifact review against the
retained feature contract. Do not mark the goal done from compilation, source
presence, an absence of TODOs or one successful world capture.

## Evidence and historical material

- [2026-10-03 accepted harness source](../.codex/results/1738e94bc7e243258362ee74fff5f9b4/H01-R2-result.md),
  [launcher correction](../.codex/results/1738e94bc7e243258362ee74fff5f9b4/H01-R3-result.md)
  and [source acceptance](../.codex/reviews/1738e94bc7e243258362ee74fff5f9b4/H01-R3-review.md): no build/runtime checks.
- [2026-10-03 applied viewport fix](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P02-prep/capture-api/viewport-packet/results/VP-I01-complete.md): source-only evidence.
- [Prepared finite validation packet](../.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P01/V01-prep/V01-prep.md): 17 preflight fixtures, 22 result-checker fixtures and six reserved GPU children; none executed.
- [Stopped run ledger and preserved drafts](../.codex/TASKS.md): source versus proposal state and approval history; no remaining work was declared complete.
- [Native bridges/full shader corpus](validation-native-and-shaders-2026-10-01-01.md):
  five bridges; 50 programs, 143 variants, 286 SPIR-V files.
- [Native-resolution world](validation-graph-native-world-2026-10-01-01.md).
- [FSR3 to XeSS integration](validation-integration-harness-2026-10-01-01.md).
- [FSR3 to DLSS handoff](validation-provider-handoff-2026-10-01-01.md).
- [Sky motion attachment repair](validation-motion-variant-2026-10-01-01.md).
- [Sky SR-input/output analysis](sky-pattern-offline-analysis.md).
- [Latest build/package](validation-delivery-refresh-2026-10-01-01.md).
- [Pending controller world ownership](controller-world-damage-ownership.md).
- [Donor capture excluded as a world reference](validation-donor-existing-frame-2026-10-01-01.md).
- [Detailed old port chronology](porting-plan-history-2026-10-01.md) and
  [legacy roadmap/history](../../VulkanStory/docs/ROADMAP-history-2026-10-01.md).

The legacy optimization/launcher backlog is historical. Culling/streaming/vertex
compression/dynamic resolution, HDR/PBR/ray tracing/Ray Reconstruction and VR/audio
extensions are future work, not newly missing portions of this tested-renderer
migration. The donor's unimplemented GTAO WIDTH thickness model likewise is not a
rewrite regression or a port-completion requirement.

Maintain this roadmap by updating relevant item statuses/evidence and package
identity. Put detailed batch chronology in linked records instead of appending
another overlapping current-status section here.
