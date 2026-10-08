# Current development evidence

Updated 2026-10-08. Validated/installed implementation: `ae53b49`, checkout `e4da90d`, on
`codex/review-issue-fixes`, based on `ee2d870839efc58f3cf3f9c2a3513ced15477fb8`.
This is the single current evidence summary. Raw runs remain under artifacts/validation.

Current source `ab85ef8` implements scheduled main/world Options lifecycle and SDL
resize, matching PowerShell preflight/result checks, and Linux `core/` notice paths.
Source diff inspection completed. No builds, tests, probes, packages, deployment
or game runs occurred in this implementation turn; installed identity remains above.

Latest [platform batch](../artifacts/validation/platform-increment-20261008-230846/results.json)
ran once at `e4da90d`: all eight managed Release builds, Linux shell/embedded-Python
syntax, Windows staging and both Windows ZIP archives passed. Both existing isolated
foggy-village scenarios passed all 21 actions and three paired captures each:
XeSS→FSR3→XeSS and latency Off→On→Boost. First-enabled history reset resolves the
observed -12 stop; mandatory XeLL remains active through requested Off. These hidden
stationary runs establish scoped software execution, not visible quality/scanout,
moving-scene cadence or eligible-Intel acceptance. The existing CS8600 warning remains.
Independent completed-handoff inspection found 12/12 assertions passing and 160
SDK-reported presents over each 80-frame XeSS/FSR3/returned-XeSS interval. XeSS SDK
callbacks recorded two resets/two context destructions, with no warning/error
callback or negative result. XeLL's 255 sleep/simulation/input/render marker sets
and PCL's 257 asynchronous Present start/end pairs returned zero. Captures show
an upright world/HUD but use preGenerateReadback, so do not prove generated pixels.
XeLL Present marker return values are not separately logged; overlapping XeLL
Present-marker verification remains open.

Linux RID reference outputs are now present. Linux staging stopped because its
requiredNotices names omit the existing notice tree's `core/` prefix; no Linux ZIP
or runtime/install check ran. `ab85ef8` corrects that manifest contract,
preserving all required notices; its stage/package validation is pending. No production-source repair or batch rerun occurred
in this validation turn. GL registration passed normal world startup; third-party
refusal remains unexercised and specific adapters remain user-deferred.

The validated Windows stage was delivered to the established installation with an
external [backup](../artifacts/validation/platform-increment-20261008-230846/install-backup/deployment.json).
Independent [receipt verification](../artifacts/validation/platform-increment-20261008-230846/install-verification.json)
matched all 400 installed file hashes. The owned loader configuration was preserved;
no normal installed-game launch occurred. Client archive SHA256:
`3E21DB8AC57D6DBA9214FADA460897B43770ED04F418DA7B8434CDD869346D85`.

Options lifecycle now schedules the existing original entry, checkbox/footer,
native world Back/resume/Escape and main leave callbacks. Main scenarios require
no world and report actual mod-loaded state with Game assembly provenance. World
scenarios retain original client/dialog identity. Logical SDL dimensions avoid
pixel/DPI assumptions; preview assertions settle on later ticks. Saved official
ScreenManager source confirms StartMainMenu reuses its constructor-owned mainScreen.
Prepared [main](../artifacts/implementation/options-lifecycle-main.json) and
[world](../artifacts/implementation/options-lifecycle-world.json) inputs cover
Save/Cancel/abrupt-close restoration, all five pages, resize and resumed provider
inputs/present gains. The [next batch](../artifacts/implementation/validate-options-lifecycle.ps1)
builds Game on both targets, stages/packages both, runs both profiles once and
delivers Windows only after both pass. DLSS-FG world mode is visible/focused.
These inputs have not run; callbacks do not prove general physical SDL interaction.

`c5ba14d` corrects all five delegate declaration/creation references in the two
GL source files using System.Func/System.Action. Source diff inspection passed;
no builds or runtime checks ran in this implementation turn. The installed
implementation remains `9a14715` until validation and delivery of the new increment.

The [platform-increment batch](../artifacts/validation/platform-increment-20261008-224404/results.json)
ran once at checkout `f92e8ea`: Windows and Linux-target Bootstrap/Mod/Companion
Release builds passed; bash and embedded Python helper syntax passed. Both Game
targets failed because new GL source imports conflicting Func delegate names from
System and Vintagestory.API.Common. Win Game log names ModGlUsage.cs:52 and
ModCompatibilityConsumerPatches.cs:21. Staging, packaging and both runtime scenarios
were skipped. Static Linux source acceptance found no additional actionable defect,
but is not Linux execution evidence. The next implementation qualifies System.Func;
no source repair or repeat batch occurred during this validation turn.

Source-only increment `9767c89`: `cab1cf5` applies mandatory XeLL before XeSS FG
and retains it through requested latency Off/frame-cap changes, publishing the
effective dependency; `c2f33e0` adds loader-bound metadata GL discovery/refusal
and ordinary status output; `9767c89` adds verified Linux startup/profile selection,
tagged original-run.sh activation/install/remove and RID-aware build/stage/package.
Official archive inspection confirmed identical managed game-library hashes, so
the existing startup IL inventory is shared. Shell checkout/staging uses LF.
No builds, tests, packages, installation or runtime checks ran for this increment.
Linux display/runtime/provider, installer lifecycle and direct URI launches remain
unverified; GL01 execution remains unverified. User selected no third-party mods
on 2026-10-08, so concrete GL02/03 adapters/profiles are explicitly deferred.

The first autonomous T01 [handoff/latency batch](../artifacts/validation/provider-handoffs-20261008-220016/results.json)
ran once against the same deployed implementation. Both profiles crashed before
active XeSS FG: SetEnabled(true) returned -15. The pinned XeSS FG SDK defines this
as unmet latency-reduction requirements and requires XeLL enabled before FG;
the current deferred-first-frame path applies the calls in the opposite order.
The next implementation increment must repair that order and mandatory dependency
through LowLatency Off/frame-cap updates. No rerun or source repair occurred in this
validation turn; earlier XeSS execution records do not validate the current source.

All 118 [review implementation findings](completed-review-findings-2026-10-08.md)
are completed. The latest runtime correction addresses the mapped-buffer regression
introduced by `6df3105`: small updates after any GPU use cloned whole capacities,
churned descriptors and changed VRAM placement. `e4de738` uses the existing ordered
GPU range-upload path. Rare raw-pointer access synchronizes queued writes/readers;
this compatibility path was source-reviewed, not separately exercised. Existing
backend stats now time Reflex sleep without changing its protocol.

The old hidden strict test logged [DLSS-G disabled because its window was not focused](../artifacts/validation/streamline-global-20261004-030212/run/stderr.log).
Its 1:1 result did not establish a present-path defect. The first focused run showed
104 real / 208 SDK presents in 31.165 s (3.34 / 6.67 FPS), exposing the base-renderer
slowdown. Two foreground runs passed their action assertions but failed terminal
visibility invariants; those failures remain recorded in
[fg-ready](../artifacts/validation/fg-ready-20261008-204227/focused-world/frames/scenario-result.json)
and [mesh-range](../artifacts/validation/mesh-range-20261008-211002/world/frames/scenario-result.json).
`9a14715` makes terminal checks honor visible/focused mode and removes the 33 ms
hidden-diagnostic throttle from explicitly visible runs.

The [final foreground batch](../artifacts/validation/fg-foreground-20261008-211805/results.json)
passed Game/Mod Release builds, staging and the existing strict DLSS gain scenario:
all six actions executed, captures paired, all assertions and terminal checks passed.
RTX 4070 used DLSS Quality 1707x1019 to 2560x1528, with 2x requested/configured,
query result/status zero, SDK maximum one generated frame and VSync disabled.
Current world camera/motion and liquid motion were ready. A
[2.055 s steady interval](../artifacts/validation/fg-foreground-20261008-211805/steady-metrics.json)
recorded 79 real / 158 SDK presents: 38.4 / 76.9 FPS. The inspected stationary
[world capture](../artifacts/validation/fg-foreground-20261008-211805/world/status/world-rendered-97644.png)
shows intact scene/HUD and a 50 / 101 FPS sample. Backend timing showed no steady
allocation/blocking-upload churn and negligible Reflex waits. These are scoped
SDK-output/capture measurements, not physical scanout or moving-scene quality acceptance.

The same stage was deployed to `C:/Users/N1GHT/AppData/Roaming/Vintagestory` with
[backups](../artifacts/validation/fg-foreground-20261008-211805/install-backup/).
[All 400 receipt hashes match](../artifacts/validation/fg-foreground-20261008-211805/install-verification.json),
including Renderer/Game DLLs against stage. User settings/save and loader policy
were preserved; no normal installed client was launched after delivery. The only
Game build warning is existing Scenarios.cs CS8600. Renderer's 26 and Game's 17
XML warning causes are compiler-confirmed resolved. Native bridges/bootstrap and
50-program shader corpus reuse the previously recorded successful inputs.
No unit suite or new/expanded tests ran; current XeSS FG/marker and partial-output
array fixture checks remain separate from this DLSS foreground result.

Documentation covers 376 maintained C# files after pruning and adding shared
implementations, plus all nine active native source/header/test files and 22
maintained scripts (19 PowerShell, two Python, one shell). Six migrated native
reference build scripts are excluded from active documentation work. Compiler
XML sidecars were emitted, staged and deployed at `9a14715`. Earlier runtime
evidence below belongs to its recorded payloads and does not validate later source.

| Area | Evidence | Limit |
| --- | --- | --- |
| Intel UHD 770 FSR3 SR/FG | [Actual generated image and same-frame UI](../artifacts/validation/intel-generated-output-20261004-081416/); upright scene, nonduplicate pixels, opaque HUD match | Stationary camera; scanout/pacing unproved |
| Intel XeSS SR | [201 successful evaluations, current motion](../artifacts/validation/intel-motion-20261004-062918/) | Quality/interactive acceptance incomplete |
| Intel XeSS FG | [XeLL initialization rejection](../artifacts/validation/intel-fg-stage-20261004-064938/) | UHD 770 outside supported Arc/Core Ultra families |
| NVIDIA XeSS/FSR3 | [21-action handoff and normal teardown](../artifacts/validation/resource-bindings-20261004-103435/) | SDK counts are not display quality/pacing proof |
| NVIDIA DLSS FG | [Focused strict gain scenario passed](../artifacts/validation/fg-foreground-20261008-211805/); sampled 38.4 real / 76.9 SDK output FPS | Moving-scene/generated-image quality, scanout and sustained pacing remain unaccepted |
| Main/world Options | [Save/Cancel/persistence/Graphics return](../artifacts/validation/options-current-save-cancel-20261004-082438/); [6x slider interaction](../artifacts/validation/multiplier-world-20261004-055122/) | Diagnostic input; physical input, resize and error paths incomplete |
| Status/FPS UI | [Provider limits](../artifacts/validation/suspended-limits-20261004-090105/); [unclipped HUD](../artifacts/validation/fps-hud-sizing-20261004-091348/) | Narrow-window/scale coverage incomplete |
| Silent harness | [Exact settings block checked offline](../artifacts/validation/headless-audio-offline-20261004-105627/) | Six volumes zero, cache excluded; no acoustic measurement or new launch |
| AMD FSR4 | [Unsupported NVIDIA fallback](../artifacts/validation/fsr4-fallback-20261004-050856/) | No AMD execution evidence |

The cleanup removed speculative constructor-retention plumbing, the auxiliary
reflection checker, production shutdown-trace wrappers and one-off SR/generated
capture hooks. Their existing images/logs remain available. Ordinary constructor
cleanup, checked Vulkan results and the normal hidden harness remain.
Game/Mod [cleanup builds passed](../artifacts/validation/cleanup-build-20261004-114649/)
with zero errors: Game has the existing CS8600 warning at Scenarios.cs:504; Mod has
none. No game or runtime suite was launched. Earlier successful runs do not validate
the simplified source's runtime behavior or negative GPU failure paths.

The last recorded development build was installed in `C:/Users/N1GHT/AppData/Roaming/Vintagestory`
for the user's manual test. [Installation batch](../artifacts/validation/install-current-20261004-115727/):
Bootstrap, Game, Mod and Input.Companion Release builds passed; all 373 deployed
package files matched staged hashes. Existing loader and client settings were
preserved; replaced files are in the batch's `backup/`. No game was launched.
Mod version remains 0.1.0; this is the current working source, not a new release.
The October-1 ZIP is older than current source.
User test: DLSS Quality and Ultra Performance appeared identical and had no FPS
gain. Installed-client logs recorded successful DLSS evaluation at 1707x1067 and
853x533 respectively, targeting 2560x1600; saved VSync is Off, maxFps is 241.
These logs prove a changed provider input, not visible quality or FPS gain.
Subsequent source changes apply Options edits at the next frame boundary without
persisting them; Save persists, Cancel/closing restores the previous settings.
Image now displays current provider, render/output resolution and FPS. This source
increment's [Release build/deployment batch](../artifacts/validation/live-options-install-20261004-121233/)
passed and installed the updated package in the same game directory. All 373 deployed
files matched staged hashes; loader, client settings and mod settings were preserved.
Game has the existing CS8600 warning at Scenarios.cs:504; other builds have none.
Replaced files are in the batch's `backup/`. No game was launched; manual acceptance
of live adjustment, rollback and quality/FPS behavior remains pending.
User subsequently reports ordinary Options operation works, but Steam Controller
input does nothing. Client log shows SDL opened `Steam Controller` (`28de:1303`).
Detection alone is not input acceptance. Source now exposes live controller axes,
A button and routing/blocking state in Options Status to distinguish SDL input
from host routing; no controller fix is claimed yet.
The same user run crashed at 12:34:41: DLSS-G state query returned 39, followed by
the same result while disabling FG during framebuffer disposal. The bundled SDK
defines 39 as `eWarnOutOfVRAM`. Source now handles Off with that warning through
checked device drain and explicit feature release before framebuffer deletion;
other disable/release failures still stop cleanup. Native bridge and managed source
were rebuilt together in [controller/crash installation](../artifacts/validation/controller-crash-install-20261004-124437/).
Native Streamline bridge and four managed Release builds passed (existing Game
CS8600 warning only); the package was deployed and all 373 installed payload hashes
matched. Backups are in that batch's `backup/`. No game was launched by the agent.
Runtime correction/input acceptance and the origin of memory pressure remain open.
Automatic controller logging is now [built and deployed](../artifacts/validation/controller-logging-install-20261004-125817/).
It records device-count changes, SDL open failures, routing/blocking transitions,
the first raw SDL input per connection (before routing guards), and panel-open
results/failures. It does not log every frame. Game/Mod builds passed with the
existing Game CS8600 warning; all 373 installed hashes matched. No game was launched.
Controller input and panel opening remain unresolved pending the next user's run.
The prepared [visible DLSS launcher](../artifacts/validation/visible-dlss-current-20261004/launch.ps1)
pins an older compiled payload; do not silently treat it as the simplified source.

Controller fixes and performance instrumentation are now
[built and deployed](../artifacts/validation/controller-performance-install-20261004-134736/).
The initial Game build failed on two incorrect diagnostic settings references;
these were corrected to use CaptureFrameSettings and the corrected Release build
passed with the existing Scenarios.cs:504 CS8600 warning. Mod Release passed
without warnings. The fresh stage reuses the prior package's native/shader inputs
and current managed outputs; all 375 installed receipt hashes were verified,
including the Game DLL against the corrected build. Backups are in the batch's
`backup/`; the user's game was not launched. While Controllers are enabled it
automatically logs `[VulkanStory]
Controller performance` summaries every five seconds to the normal game log.
World/menu samples distinguish idle, controller state, physical events, and mixed
input. Each has average/maximum real-frame interval and CPU pump/pacing/events/
vendor latency sleep/SDL gamepad update/controller polling/render times, plus render-thread allocated
bytes per frame and process-wide GC counts. Update is a subset of events; render
includes simulation/UI and GPU waits, not GPU execution timing or FG output FPS.
Vendor latency sleep is a subset of pacing, allowing a separate comparison of
the actual vendor wait against controller processing. Reports include
FG/latency/VSync/frame-cap settings and controller status.
`VULKANSTORY_CONTROLLER_DIAGNOSTICS=0` disables collection. For live comparison,
remain in the same world/view, idle for at least ten seconds, use mouse/keyboard
for ten seconds, then controller for ten seconds; compare reported phases rather
than treating input-state correlation as proof of causation. Hotbar and faster
stick changes are included in this deployment. Live controller acceptance and
the FPS slowdown's cause remain unverified.

Minecraft-style controller mapping is now
[built and staged](../artifacts/validation/controller-minecraft-install-20261004-135210/),
not deployed: the user is continuing their test in the running game. Game/Mod
Release builds passed (existing Game CS8600 warning only). RT attacks/breaks,
LT uses/places, LB/RB wheel directions are swapped, A jumps/accepts, B toggles
sneak in-world and cancels in menus, LS sprints, Y opens inventory, X opens the
same inventory/crafting dialog, and D-pad down drops in-world. Start opens the
menu and View opens controller settings. Old untouched default device bindings
are upgraded on load while tuning values remain intact; user-remapped layouts
are preserved. No installed files/settings were changed during the user's test.
Live mapping acceptance remains pending.

The user's 13:54:07 inventory crash is preserved in
[controller-inventory-install-20261004-135616](../artifacts/validation/controller-inventory-install-20261004-135616/).
The primary failure is ErrorOutOfDeviceMemory allocating a 5,898,240-byte dedicated
DeviceImages block with 168 live allocations; frame-retention cleanup exceptions
followed it. This does not establish a leak, global VRAM exhaustion or a PCL cause.
Recorded world controller polling commonly averaged 0.02-0.05 ms; frame cost was
predominantly in rendering. The controller menu path previously warped the mouse
every frame even without movement; it now warps only when the cursor changes.
Allocation-failure exceptions now include image dimensions/format/mips/layers,
live texture count, memory type and refreshed allocator/driver heap usage/budget.
The existing five-second controller report also logs GPU memory snapshots.
Game/Mod Release builds passed (existing Game CS8600 warning only). These changes
and the prepared Minecraft-style mapping were staged and deployed after the game
had exited; all 375 installed receipt hashes matched. Backups are in `backup/`.
No game was launched. Inventory stability, mapping acceptance and the cause of
the memory pressure remain open; the cursor correction is not a proven OOM fix.

The inventory allocation correction is
[built, checked and deployed](../artifacts/validation/inventory-memory-fix-20261004-135926/).
For ordinary DeviceImages, a typed ErrorOutOfDeviceMemory allocation failure now
falls through once to a host-visible, non-device-local type allowed by the image's
memoryTypeBits. Dedicated-image requirements, resource identity, usage and GPU
lifetime stay intact. Other allocation errors and unsupported alternate types
still fail explicitly. System-memory images can be slower under VRAM pressure;
this is a correction to the fatal image-allocation path, not evidence that the
underlying memory pressure has been eliminated.
Game/Mod Release passed (existing Game CS8600 warning). The first launch attempt
was rejected before process creation because controller harness profiles remain
deferred; controller simulation was omitted. The initial runtime check captured
the world but did not open inventory because the command gateway excluded the
new action. The gateway was corrected and Game rebuilt; the corrected hidden,
silent isolated foggy village story run with DLSS SR/FG logged the original
inventory opening, captured it at frames 240/300/360, and exited normally with
headless/verifier success. Frame 300 was visually inspected and shows the original
Inventory and Crafting dialog. This is scoped inventory-rendering evidence;
the original prolonged VRAM-pressure failure and alternate-heap fallthrough were
not reproduced/exercised, and physical controller acceptance remains open.
The checked `stage-inventory-gateway` payload was deployed with backups; all
375 installed receipt hashes matched. The user's game was not launched.

The focused VRAM/lifetime investigation and corrections are
[built, exercised and deployed](../artifacts/validation/vram-investigation-20261004-141424/).
Source inspection found two allocation-policy defects: empty-block pressure used
only allocator-owned heap bytes rather than driver/provider usage, and persistent
mesh buffers took mapped VRAM until allocation failure without reserving image
headroom. Pressure now includes driver usage plus renderer allocations since the
last budget refresh; before physical allocations, empty blocks on a pressured
heap are released. Budget refresh is at most once per allocator frame. New
persistent mesh buffers choose compatible system memory when another mesh block
would consume the headroom reserved for one 128 MiB DeviceImages block.
This preserves existing buffer mappings and graphics lifetimes.

The first corrected pressure-accounting payload completed 20 original inventory
open/close cycles: all 40 transitions succeeded, texture bytes stayed exactly
1,085,108,736 (499 live textures), and pending deletions returned to zero after
every close. Mesh capacity grew from 670,908,592 to 2,424,614,872 bytes while the
world was loading. A later payload with mesh headroom and within-frame heap
accounting completed another 20 cycles after 3,600 world frames and captured
frame 4,500, exiting normally. Mesh capacity reached 7,646,603,480 bytes (7.12 GiB),
651 headroom decisions directed new mesh buffers to system memory, and final
pending deletions were zero. GPU allocator bytes were 6,264,596,480 versus
6,652,399,616 driver-reported usage and a 6,899,773,440 budget. System-heap owner
bytes were 2,902,458,368. Live texture bytes were stable during these late cycles
at 1,032,557,056 (495 textures); the decrease from the earlier run is consistent
with feature resources being released, not an inventory texture leak.

The copied settings have viewDistance=1536. Both runs used silent isolated foggy
village story copies, with DLSS SR/FG requested. The long run logged Streamline
out-of-VRAM at 14:19:18 and disabled FG; it continued without a fatal allocation
or cleanup error. This is evidence of mesh/resource pressure and working guarded
spill/retirement, not sustained FG acceptance or definitive reproduction of the
original fatal allocation. Per-resource last-use fencing for direct persistent
mesh writes is not proven by these tests. Terrain already uses pooled meshes and
CmdDrawIndexedIndirect multi-draw; wholesale batching changes are not justified
as the inventory crash correction. No input/controller hardware simulation ran.

Game/Mod Release builds passed (existing Game CS8600 warning only). The long-run
`stage-headroom` payload was deployed, all 375 installed receipt hashes matched,
and backups are retained. No user settings/save were changed or normal game
launched. The exact original crash cause, vendor FG pressure and full fencing/
streaming/performance acceptance remain open.

The remaining DLSS-G pressure failure from the preceding check is now addressed
by [fg-vram-headroom-20261004-142525](../artifacts/validation/fg-vram-headroom-20261004-142525/).
The prior one-block 128 MiB mesh reserve was inadequate for the observed image
and provider working sets. Persistent mesh allocation now reserves the measured
image/transient physical-block footprint on that heap plus driver usage outside
the allocator, with one image block as its minimum. Existing mesh mappings stay
intact; new mesh buffers spill before consuming this working-set headroom.
No native/Streamline ABI or tagging/present protocol changes were made.

Game/Mod Release passed (existing Game CS8600 warning). The checked payload then
ran 4,500 world frames in the silent isolated foggy village story copy with the
user's viewDistance=1536 and DLSS SR/FG requested. All 40 inventory transitions
(20 open/close cycles starting at frame 3,600) succeeded, capture/result/verifier
passed, and the process exited normally. No out-of-VRAM warning, Vulkan image
allocation failure or critical crash occurred. Near frame 4,451, DLSS-G remained
the effective configured feature with state-query result/status both zero.
The settled sample had 7,646,727,184 mesh bytes, 1,104 headroom spills and a
2,132,840,448-byte reserve. GPU owner bytes were 4,370,933,760 versus driver usage
5,270,179,840; system-heap owner bytes were 4,848,615,424. Texture bytes stayed
1,085,110,784 across the late cycles. The final two pending retirements were
within two in-flight Frame values/one Transfer value, not a growing backlog.
This supersedes the previous run's unresolved VRAM-warning result for this scoped
workload; it does not close the separate DLSS-G output-gain or physical-controller
acceptance gates. System-memory mesh spill may trade bandwidth for preserving
image/provider capacity and avoiding the recorded allocation failure.

The verified stage was deployed with backups, and all 375 installed receipt
hashes matched. No user settings/save were changed and the normal game was not
launched. Current exact summaries and hashes are in `pressure-summary.json` and
`deployment-result.json` in this batch.

The first Control Flex implementation increment is
[built and deployed](../artifacts/validation/controlflex-controller-20261004-145551/).
It is a fresh C# implementation informed by the published artifact inventory at
`D:/Coding/VulkanStory/refs/control-flex-analysis/INVENTORY.md`; no reconstructed
Java was transplanted. Controller samples now cache supported buttons/axes.
Context transitions, focus/device changes and world exit release owned input,
drop old GUI owners and suppress held controls until neutral/release. A context
change inside key dispatch stops that sample before another screen receives it.
Movement/look use vector radial inner/outer deadzones and independent response
curves. Profile v2 migrates the former hidden look exponent multiplier and keeps
legacy accept/back remaps; gameplay and inventory bindings are independently
editable. Inventory defaults are A select, X take-half, Y quick transfer, B close;
left stick moves the menu cursor, the other stick scrolls, shoulders tab through
UI focus, and D-pad uses directional slot scoring with row/column wrap and
500/100 ms repeat timing. The hotbar HUD participates in slot navigation when
the foreground is an inventory, while higher-priority modal dialogs exclude it.
Inventory operations call the official grid SlotClick method, respecting its
CanClickSlot check and packet handler rather than mutating stacks directly.

The first build found four dialog-enumeration typing errors; those were corrected.
Game/Mod Release then passed (existing Game CS8600 warning). The initial isolated
inventory check failed its fixture precondition: the copied save had no stack
with at least two items. A preparation retry used the incorrect `dirt` block code
and the server rejected it. Existing server logs identified `game:soil-low-none`;
the corrected server command gave eight blocks only in the isolated copy. The
completed check found 25 slots and exercised take-half, select, place into another
inventory and quick-transfer, restoring the original nine-item stack after
server processing between steps. It logged PASS, captured three frames at
360/390/420 and exited normally with headless/verifier success, DLSS SR/FG requested.

Supported-button caching and linear wheel axes were finalized after that run;
the final Game build passed and `stage-final` was deployed. All 375 installed
receipt hashes matched; backups are retained. The inventory helper exercised
in the run is unchanged by those final sampling edits. No physical gamepad
simulation or normal game launch occurred; the actual controller path and new
tuning-panel layout need user-run acceptance. The user's inventory/settings/save
were not edited by the agent. Advanced gesture/modifier layers, radial action
menus and narrowly justified mod/overlay adapters remain separate increments;
this is not a claim that every Control Flex feature has been reproduced.

The configurable controller radial action menu is
[built, rendered and deployed](../artifacts/validation/controller-radial-20261004-152654/).
Default entry is right-stick click. Hold mode selects with the right stick and
executes on release; press mode opens and confirms with A or another opener press;
B/Start cancels. The eight slots can be cycled among inventory, pause, controller
settings, drop, previous/next hotbar slot, first hotbar slot, screenshot and empty.
Actions use existing game hotkeys/wheel/active-slot APIs. View remains the settings
entry. Focus loss, device changes, world exit and shutdown cancel/dispose the
owned dialog without selecting an action. Profile v3 disables the new radial
default when an older custom gameplay mapping already owns its opener. The
controller panel exposes enable/hold mode and all eight slot actions.

Game/Mod Release passed (existing Game CS8600 warning). The initial isolated
world check selected/redrew all eight sectors and captured three frames, exiting
normally. Visual inspection found small labels and a clipped wheel: dynamic
custom draw uses a local surface and did not set up its Cairo font. Local-center
coordinates and an explicitly configured, larger white font corrected both.
The polished Game build passed; the corrected isolated DLSS/FG check again
selected all eight sectors, captured frame 300 and exited with verifier success.
That capture was visually inspected: the full circle and labels are readable.
It checks rendering/sector selection/resource teardown, not physical hold/release
or execution of every wheel action. The polished stage was deployed with backups;
all 375 installed receipt hashes matched. No normal game was launched or user
settings/save edited. Gesture/modifier layers and physical-input acceptance are
still part of the active controller goal.

Gesture modes and a modifier layer are now
[built and deployed](../artifacts/validation/controller-gestures-20261004-153854/).
The per-action monotonic-time state supports Hold, Press, Release, Tap, LongPress,
Toggle and DoublePress/Tap/Hold/Toggle. Thresholds are configurable; defaults
leave the prior mappings/legacy sneak toggle intact. Inventory actions consume
one activation instead of repeatedly clicking for held/latched output, while
default generic GUI mouse handling retains hold-to-drag. Gesture/latch owners
are discarded on context/focus/device release. A disabled-by-default modifier
layer consumes its opener when enabled, overrides selected gameplay buttons and
falls back to main bindings for unspecified actions. Layer changes release old
owned input and suppress previously held buttons until release, while simultaneous
modifier-plus-new-button presses can activate the shifted mapping.

The controller panel has independent gameplay/inventory gesture pages and a
modifier editor, including reset-to-default mode/inherit-main choices. Game/Mod
Release builds passed (existing Game CS8600 warning only). Both editor pages were
opened/rendered in separate silent isolated-world checks with DLSS SR/FG requested,
captured at frame 240 and exited normally with verifier success. Both captures
were visually inspected and fit on screen. These are UI/lifecycle checks, not
evidence of physical gesture timing, latch cancellation or modifier combinations.
The unchanged staged payload was deployed with backups; all 375 installed receipt
hashes matched. No normal game or physical controller input was operated. Timing/
combination execution and live acceptance remain part of the active goal.

The gesture/modifier integration corrections are
[built and deployed](../artifacts/validation/controller-action-polish-20261004-155417/).
Explicit shifted bindings now consume their physical buttons, suppressing inherited
main actions on those buttons; assigning a shifted button removes its prior shifted
owner and cannot bind the modifier itself. This corrects the case where shifted
Drop on A could also activate inherited Jump. Inventory and crafting gesture
states are sampled independently before merging their outputs, avoiding stale
state behind short-circuit evaluation. Game/Mod Release passed (existing Game
CS8600 warning only). A bounded reflection check of the actual compiled production
classes passed all ten gesture traces, including long-press one-shot, double-hold
release and repeated double-toggle latch changes, plus three shifted ownership/
assignment checks. No test project or new test-source suite was added. Exact
binary hash/results are in `logic-check.json`; these are software-state checks,
not physical SDL/input-pipeline or hardware proof. The stage was deployed with
backups and all 375 installed receipt hashes matched. No game was launched in
this increment. The active goal still requires live controller acceptance.

Context-specific controller prompts are
[built and deployed](../artifacts/validation/controller-prompt-polish-20261004-160020/).
Gameplay retains RT/LT prompts; GUI shows A select, X take-half, Y quick-transfer
and B close. Modifier prompts resolve actual shifted ownership, include the
modifier glyph, and omit inherited actions whose buttons are consumed. Hint
publication follows context/modifier changes, not every sample. The initial
patch placed the new block in the wrong method and failed compilation; the block
was moved into Build and corrected/final Game Release builds passed (existing
CS8600 warning). Mod Release passed. Compiled production-class checks passed
world/GUI/modifier prompt maps, held-control suppression on GUI entry, neutral/
release-to-resume, and lifecycle reset suppression. Final GUI/pause-modifier maps
were rechecked after adding the effective pause prompt. Exact results/hashes are
retained in the batch. These checks verify software behavior, not SDL hardware
events or visible-controller play. The stage was deployed with backups; all 375
installed receipt hashes matched. No game was launched. The pending live user
question covers movement/look, hotbar, inventory, radial, gesture/modifier behavior,
stuck/repeated input, FPS slowdown and crashes; absence of a reply is not acceptance.

Superseded session reports and the overgrown Roadmap are recoverable in
[the cleanup backup](../.codex/cleanup-backup-20261004/). They are historical evidence,
not another active plan. Earlier tracked architecture, installation and porting
documents remain in place.
