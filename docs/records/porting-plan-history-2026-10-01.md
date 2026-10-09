# Porting and development plan

Design date: 2026-09-28. Updated 2026-10-01 through scene/motion and session/provider source integration; the latest validation remains the default framebuffer/load/unload checkpoint. Latest recorded suites passed 75 backend tests, 37 game tests, 10 contract tests and 17 bootstrap tests in separate batches. All retained device partials/provider forwarders compile; subsequent native validation passed a clean build and four full-device clear/readback/present frames. Game fixtures verify patched UBO CPU bytes, fixed-state dispatch, framebuffer null binding and exercised clear/selector dispatch. Focused native batches also verified indexed pixels/capture, mesh offset updates, queries and uniform snapshots with presentation/teardown. The official startup guard matched all 418 member operands across seven targets. Actual SDL startup/game routing, enabled vendor execution, visible game rendering and release acceptance remain open. See migration inventory and [the project roadmap](../../../VulkanStory/docs/ROADMAP.md).

## Runtime packaging source checkpoint — 2026-10-01

The [packaging increment](runtime-packaging-implementation.md) adds a production
build entry independent of test projects and a fresh-directory runtime staging
script, private early dependency resolution, packaged controller mappings and
separate optional server delivery. Source only: neither script has run, no full
package was generated, and native/provider/shader delivery acceptance remains
open. New tests stay deferred until integration is finished.
## Native runtime loading source checkpoint — 2026-10-01

The [native loading increment](native-runtime-loading-implementation.md) selects
packaged shaderc explicitly, registers packaged XeLL resolution, adds local DLL
dependency search for Windows SDL/provider loads and removes XeSS's Optimum
override name. Source only; compilation, package loading and enabled provider
execution remain unverified. No new tests or runs occurred.
## Production build source checkpoint — 2026-10-01

The [production build increment](production-build-implementation.md) connects full
native shader compilation to the runtime build entry and adds one explicit-SDK
entry for all five Windows provider bridges. Vulkan SDK arguments are consistent
across bridge recipes. Source only; no scripts, builds, tests or game runs occurred.
Provider binaries, full shaders and redistributable bundle acceptance remain open.
## Current production compile result — 2026-10-01

One [production-only compile batch](validation-production-compile-2026-10-01-01.md)
passed Bootstrap and Input.Companion but failed Game in its SDL dependency
(invalid managed DLL search flag names) and Mod (ambiguous framework/API Func).
The same flag error exists downstream in the renderer helper. No source fix,
rerun, tests, native/shader builds, package or game launch occurred. Game/renderer
compilation and runtime acceptance remain open. Fix these issues next in source.
## Production compile source corrections — 2026-10-01

Both native loading helpers now use the documented managed DLL search flags.
Game/Mod files importing the Common API qualify framework delegates as System.Func,
including the client bridge and menu screen. No build or tests ran after these
fixes. The [failed compile record](validation-production-compile-2026-10-01-01.md)
remains authoritative; downstream compilation and all runtime gates remain open.
## Current production compile result — second batch, 2026-10-01

The [second production compile batch](validation-production-compile-2026-10-01-02.md)
compiled SDL, the full Vulkan backend and the ordinary client mod. Game reached
integration compilation and failed with 26 errors: 21 renderer namespace/type
references, four missing Common API imports and one internal sky-class reference.
No fixes or reruns occurred. Tests, native/shader builds and game runs were omitted.
These source issues are next; complete Game/runtime/provider acceptance stays open.
## Game integration compile source corrections — 2026-10-01

The second batch's renderer namespace/import issues are corrected in the adapter
partials. Common API imports now supply the capture/controller types. Sky uses
an exact reflected internal type and a guarded public ClientSystem boundary,
preserving the official game's visibility. These changes are unbuilt; no tests
or game runs occurred. See the [compile record and source correction](validation-production-compile-2026-10-01-02.md).
## Current Game compile result — third batch, 2026-10-01

One [Game compile batch](validation-production-compile-2026-10-01-03.md) reached
method bodies and reported 98 errors: 81 remaining renderer import/cascade errors,
12 ClientSettings namespace references, two internal game types, two query fallback
signatures and one internal shader filename access. Dependencies compiled. No
fixes/reruns/tests/native or shader builds/game launches occurred. Correct these
boundary adaptations next; full Game/runtime acceptance remains open.
## Remaining Game boundary source corrections — 2026-10-01

Added renderer imports to the remaining ten adapter partials, corrected official
ClientSettings namespace references, reflected the two internal render systems,
and guarded internal shader filename access. Query fallback calls use the bound
OpenTK out parameters while preserving CLR by-reference patch anchors. No build,
test or game ran; the [third compile record](validation-production-compile-2026-10-01-03.md)
still records the last failed batch. Downstream compilation and runtime remain open.
## Current complete Game compile checkpoint — 2026-10-01

The [fourth production compile batch](validation-production-compile-2026-10-01-04.md)
passed Game and its Contracts/Input/SDL/full Vulkan dependencies with zero errors
and zero warnings. This supersedes the preceding Game compile failures. Bootstrap,
Mod and Input.Companion have preceding successful production builds; Mod retains
one legacy command API warning. No tests, native/shader builds, packaging or game
runs occurred. Native payload production and profile/menu/world/provider execution
remain open; compilation alone does not prove live routing.
## Native bridge and full shader build checkpoint — 2026-10-01

One [native/shader production batch](validation-native-and-shaders-2026-10-01-01.md)
built all five Windows bridges and the full shader corpus: 50 programs, 143 variants,
286 SPIR-V files. Native builds retain 41 warnings; shader tool compiled cleanly.
No tests, probes, packages, installs or game launches ran. Vendor runtime/notices
assembly and actual profile/menu/world/provider execution remain open.
## Full native bundle source checkpoint — 2026-10-01

The [packaging source record](runtime-packaging-implementation.md) now includes an
explicit native component inventory and SDK-to-bundle preparation entry. Staging
requires all listed provider runtimes, not just SDL/shaderc, and selects named files.
SDK notices are bundled with the selected bridge/vendor/core inputs. Source only:
preparation/staging did not run. Matching release Streamline plugins, dependency
completeness and live SDK/plugin discovery remain open.
## Streamline delivery source correction — 2026-10-01

NGX already supplies the private payload feature path. Native delivery inventory
now includes the retained PCL plugin and DLSS-G NGX runtime, plus its release notice.
No SDK calls, builds, tests or package ran. Matching Streamline 2.14.1 release SDK
location has been requested; legacy test/game output was not substituted. See the
[packaging source record](runtime-packaging-implementation.md).
## Client command lifetime source checkpoint — 2026-10-01

The [ordinary mod increment](ordinary-mod-control-implementation.md) now uses the
current ChatCommands API, explicit results and a weak owner handler. Registry
entries cannot retain disposed owners; disposal leaves newly attached handlers
intact. Source only, no builds/tests/game ran. Streamline release SDK input remains
pending; renderer/provider runtime acceptance is still open.
## Client/server archive source checkpoint — 2026-10-01

The [delivery source increment](runtime-packaging-implementation.md) adds inventory-
based separate client and server ZIP creation. Client files extract into the existing
game directory; companion files extract into server Mods. Each gets its own hashes
and source notices, preserving acceptance status. Source only; no package, builds,
tests or game ran. Matching Streamline release SDK input remains pending.
## Client mod and delivery syntax checkpoint — 2026-10-01

The [client mod/delivery batch](validation-mod-delivery-2026-10-01-01.md) compiled
Mod with zero warnings/errors and parsed all five delivery scripts without syntax
errors. No tests or packaging scripts ran. Actual commands, complete bundle
assembly and game/provider execution remain open; matching Streamline release
SDK input is pending.
## Streamline SDK input resolved — 2026-10-01

Located the matching release archive in Downloads and unpacked SDK resources to
sdk/streamline-2.14.1, resolving the pending location request. Preparation source
now selects production bin/x64 and guards version/runtime hashes against that SDK.
No downloaded code, builds, tests or packaging scripts ran. Bundle/notices assembly
and full live operation remain open. See the [SDK/source record](runtime-packaging-implementation.md).
## Dependency notice input checkpoint — 2026-10-01

Collected core/managed dependency notice inputs with pinned upstream/package
sources and hashes, and connected their source record to staging. No builds,
tests or packaging ran. The preparation inputs are now available for the next
bounded native bundle/staging batch. Runtime and final redistribution inventory
acceptance remain open. See the [packaging record](runtime-packaging-implementation.md).
## Full runtime delivery checkpoint — 2026-10-01

The [runtime delivery batch](validation-runtime-delivery-2026-10-01-01.md) passed
native input preparation, complete runtime staging and separate client/server ZIP
creation. Stage inventory has 362 files; client archive 357 entries, companion 7.
Manifests retain unverified acceptance. No tests, SDK execution, installation or game
ran. Next is actual startup integration; live rendering/providers remain open.
## Owned payload deployment source checkpoint — 2026-10-01

Added a non-deleting backed-up update path from the verified existing B0 receipt,
with official/package hashes, owned code collision checks, retained configuration
and version.dll preservation. No deployment/build/tests/game ran. The earlier
cleanup rejection was not retried. See the [delivery source record](runtime-packaging-implementation.md).
## Candidate deployment/live startup result — 2026-10-01

The [owned deployment and startup batch](validation-runtime-startup-2026-10-01-01.md)
installed the full candidate with backups and launched the original executable.
It opened a vanilla OpenGL window and exited 0 after the planned close. Vulkan
activation failed because the mechanical profile names AngledGearBlockRenderer;
the original declaration is AngledGearsBlockRenderer. Fix that identity next.
No builds/tests/source fixes/reruns occurred. Candidate remains installed with
activation bypassed; complete renderer/provider acceptance remains open.
## Mechanical profile identity source correction — 2026-10-01

Corrected the plural original CLR type name; other six mechanical declarations
match source. Retained transforms/counts are unchanged. Target lookup and bootstrap
exception diagnostics identify later failures more precisely. No builds/tests,
deployment or game ran; installed payload still reflects the failed prior batch.
See the [startup record and correction](validation-runtime-startup-2026-10-01-01.md).
## Prepared profile/live lifecycle failure — 2026-10-01

The [second startup batch](validation-runtime-startup-2026-10-01-02.md) passes full
profile preparation and installed build/staging gates. Session shader setup and
animation cleanup fail on an active-routing-only device accessor before commit.
No tests/fixes/reruns occurred. Fix lifecycle ownership access while keeping game
routing dormant until successful creation; installed payload still crashes here.
## Lifecycle source correction — 2026-10-01

Shader setup and owned teardown now use lifecycle device association rather than
active game-routing access. Native motion resources are released directly; cleanup
continues across subsystem errors and retains SDL if GPU disposal fails. Source
only, no builds/tests/deployment/game ran. Installed payload still has the prior
crash; see the [correction record](validation-runtime-startup-2026-10-01-02.md).
## SDL/Vulkan activation and first-frame failure — 2026-10-01

The [third startup batch](validation-runtime-startup-2026-10-01-03.md) commits the
first SDL/Vulkan session and records device-before-window shutdown. First menu
frame fails on stage-gated owned shader asset access. No tests/fixes/reruns
occurred; next is base-asset lookup for early owned shaders. Presented frame and
world/provider acceptance remain open; installed payload still has this failure.
## Early shader asset source correction — 2026-10-01

Owned shader/include lookup now uses the base-asset map; selected overrides and
embedded fallback remain. Override metadata scan no longer eagerly loads assets.
No build/tests/deployment/game ran; installed payload still has the prior failure.
See the [source correction](validation-runtime-startup-2026-10-01-03.md).
## Asset-corrected menu frame failure — 2026-10-01

The [fourth startup batch](validation-runtime-startup-2026-10-01-04.md) passes build,
stage/update and session activation. First menu frame now reaches an uninitialized
OpenGL binding; exact nested operation is not identified by the flattened stack.
No tests/fixes/reruns occurred. Preserve error/call-boundary diagnostics next and
route the escaped operation; no presented-frame acceptance is claimed.
## Bounded first-frame diagnostic source — 2026-10-01

Menu calls are tracked for four active frames, with first-error and separate cleanup
logging. Rendering remains intact. No builds/tests/deploy/game ran; escaped call
is still unproven until the updated payload runs. See the [diagnostic record](validation-runtime-startup-2026-10-01-04.md).
## Loading GUI shader path identified — 2026-10-01

The [fifth startup batch](validation-runtime-startup-2026-10-01-05.md) preserves the
GL failure in ShaderProgramBase.Use called by loading GUI Render2DTexture. Shader
leaf routes currently install after selection callers; adjust order to prevent
caller compilation against unpatched setters. This remains a source hypothesis,
not identification of the exact GL function. No tests/fixes/reruns occurred.
## Shader route installation source correction — 2026-10-01

Leaf uniform/UBO/sampler routes install before final Use/Stop wrappers; original
checks/state remain. No builds/tests/deployment/game ran. The inlining hypothesis
and surviving paths require the next live batch; see the [correction record](validation-runtime-startup-2026-10-01-05.md).
## Shader order live checkpoint and blit failure — 2026-10-01

The [sixth startup batch](validation-runtime-startup-2026-10-01-06.md) passes the
loading GUI GL failure and reaches frame 51; final scene blit then refuses to draw.
Capture exact readiness/target/pass refusal next. OpenAL music source errors are
also unresolved. No tests/fixes/reruns occurred; pixels/world/providers remain open.
## Final presentation pipeline source correction — 2026-10-01

Source inspection found that pending asynchronous pipeline compilation can return
false to a required final scene draw. The final native blit now obtains its
pipeline through the blocking cache path; ordinary draws remain asynchronous.
Shader readiness, native pass/draw and stated fallback failures carry details.
The precise frame-51 refusal is not proven by the earlier trace. Implementation
only, unbuilt and undeployed; menu/world/provider acceptance remains open. See
[the source correction record](validation-runtime-startup-2026-10-01-06.md).
## Mandatory final blit live checkpoint — 2026-10-01

The [seventh startup batch](validation-runtime-startup-2026-10-01-07.md) built
cleanly, staged/deployed and kept the SDL/Vulkan game running for the bounded
20-second observation. The final-blit crash did not recur; requested window close
completed with device drain and normal exit. GUI initialization and local-save
loading began, but world rendering/presented pixels and provider execution are
unverified. Streamline Vulkan hook/optional extent warnings need investigation.
No tests, source repairs, repackages or second launch occurred.
## Streamline extent source correction — 2026-10-01

Native FG tags now carry explicit backbuffer dimensions; menu/loading input
invalidation preserves the current swapchain extent. Streamline also requests
advertised debug-utils independently of validation. The new native export and
managed caller must be built/deployed together. The three hook warnings match
missing hook-map entries in the supplied SDK and remain unresolved; no warnings
are hidden. Implementation only, unbuilt/undeployed. See the
[source record](streamline-presentation-extent-implementation.md).
World checks use the user's foggy village story save, not development superflat.
## User world loading checkpoint — 2026-10-01

The [first world batch](validation-runtime-world-2026-10-01-01.md) built matching
Streamline bridge/backend, bundled, staged and deployed successfully. It loaded
the user's foggy village story save (backed up first), then crashed at frame 1062
on SvgLoader.LoadSvg's direct GL.GenTexture during block asset initialization.
The debug-utils warning disappeared; the initial backbuffer extent warning and
SDK hook warnings remain. World/SR/FG/pixels/shutdown acceptance stays open.
No tests, source repairs, archive regeneration or second launch occurred.
## SVG upload and initial frame-tag source correction — 2026-10-01

Texture routing now intercepts official SvgLoader.LoadSvg, keeps original SVG
rasterization and logical dimensions, and uses the transplanted RGBA pointer
upload with linear filtering. BeginFrame also initializes Streamline's extent
and clears scene inputs before menus can present without a selected FG provider.
Implementation only, unbuilt/undeployed; see the [source record](svg-upload-implementation.md).
Installed native WithExtent bridge is already available from the first world
batch. No tests or launches ran; real-world/SR/FG/pixel acceptance remains open.
## SVG route world checkpoint — 2026-10-01

The [second world batch](validation-runtime-world-2026-10-01-02.md) built/staged/
deployed successfully and passed the SVG allocation failure. The user's save
reached level finalization, then crashed on a direct GL.GetString(Renderer) used
for the Intel Arc advisory. Route this query through actual Vulkan device info
next. Initial extent/debug-utils warnings are absent; SDK hook warnings remain.
Device drain was captured. No tests, source fixes or rerun; playable world and
SR/FG/pixel acceptance stay open.
## World finalization renderer source correction — 2026-10-01

A guarded transpiler replaces only HandleLevelFinalize's GL renderer query with
the selected Vulkan device name, preserving original finalization and Intel Arc
advisory logic. No additional direct GL calls were found in ClientSystem source.
Implementation only, unbuilt/undeployed; see the
[source record](world-finalization-renderer-implementation.md). No tests or game
runs. User-world/pixels/SR/FG and SDK hook warnings remain open.
## Finalization validation precondition — 2026-10-01

The [third world batch](validation-runtime-world-2026-10-01-03.md) stopped before
building/deploying because the preceding crash reporter was still open. Its
confirmed owned process was closed cleanly. No retry, tests or game launch;
finalization query correction remains unbuilt/undeployed. Next batch must include
owned crash-reporter cleanup after collecting failure evidence.
## User world finalization and shutdown live checkpoint — 2026-10-01

The [fourth world batch](validation-runtime-world-2026-10-01-04.md) built cleanly,
staged/deployed and ran foggy village story through level finalization and world
simulation for the full bounded 90 seconds. Requested close saved the world and
completed device drain/SDL teardown, normal exit. No current render exception.
No tests, repairs or rerun. Presented pixels and actual SR/FG execution remain
unverified; SDK hook warnings and remaining feature/release gates stay open.
## Runtime frame evidence source increment — 2026-10-01

An opt-in absolute VULKANSTORY_RUNTIME_DIAGNOSTICS directory collects periodic
SR-success/FG-preparation/SDK-present and camera-motion status records, plus one
composed source-frame screenshot after world readiness. FG presentation text
shows concrete waiting/unavailable reasons. No provider algorithms or selection
changed. Implementation only, unbuilt/undeployed; see the
[source record](runtime-frame-diagnostics-implementation.md). No tests/launches;
actual pixels/provider execution still await the user's world run.
## DLSS execution and image checkpoint — 2026-10-01

The [first DLSS batch](validation-runtime-dlss-2026-10-01-01.md) ran the user's
world for 90 seconds and closed cleanly. Final sample: 1712 successful SR frames,
1712 FG-prepared frames and 3168 SDK-reported presents. Temporary settings were
restored. The one captured source image shows black scene/HUD and predates first
successful SR; visual correctness remains unresolved. Gate the next capture on
complete current scene/provider output. SDK hook warnings, full parity and other
feature/release gates stay open. No tests, source repairs or second run.
## Headless harness source port — 2026-10-01

User requested the existing headless harness so renderer checks do not interrupt
normal play. Frame planning/commands/PPM and parity/AO writers are migrated; SDL
has permanent hidden-window guards, render-thread auto-close and timeout. A
PowerShell runner uses a staged startup hook, isolated settings/data and a SQLite
snapshot of foggy village story, with no deployment or user-game process control.
SSIM tooling is copied with provenance. Implementation only, unbuilt/unrun; see
[the harness guide](../headless-harness.md). Concurrent SDK/GPU behavior and complete
capture acceptance remain open; the black early image is still unresolved.
## Headless compile checkpoint — 2026-10-01

The [first headless batch](validation-headless-2026-10-01-01.md) passed Bootstrap
build, then Game compilation failed on two internal official screen fields accessed
directly, plus 15 nullable warnings. Use validated field refs in the next source
repair. No stage, snapshot, launch, tests, deployment or rerun occurred. User game
and settings untouched; headless/capture acceptance remains open.
## Headless screen access source correction — 2026-10-01

Cached, type-validated Harmony field refs replace the two direct internal screen/
client field accesses. Nullable annotations cover optional migrated harness values;
frame planning, commands and writers remain intact. Implementation only, unbuilt;
no tests/probes/launches or installed changes. See the
[correction record](validation-headless-2026-10-01-01.md). Headless acceptance and
black-scene diagnosis remain open.
## Headless command API compile checkpoint — 2026-10-01

The [second headless batch](validation-headless-2026-10-01-02.md) clears the two
screen-field errors and nullable warnings. One compile error remains: internal
ClientCoreAPI.chatcommandapi must use the public ChatCommands boundary. No stage,
snapshot, launch, tests, installed changes or rerun. Keep command capability;
headless/live capture acceptance remains open.
## Headless command boundary source correction — 2026-10-01

Command scripts now use the public ChatCommands property to reach the official
client dispatcher, preserving its Execute overload, player/group/argument context
and notifications. Implementation only, unbuilt; no tests, probes or launches.
See the [correction record](validation-headless-2026-10-01-02.md). Installed game
and settings remain untouched; headless/visual acceptance remains open.
## Isolated headless capture live checkpoint — 2026-10-01

The [third headless batch](validation-headless-2026-10-01-03.md) builds cleanly,
loads the snapshot without deployment, captures 3 scheduled frames, 55 attachment
files and AO outputs, then drains/exits automatically. Inspected image shows the
user world and HUD; the earlier black image did not recur. SR execution observed;
FG interpolation above real presents remains unproven in the hidden run. Fix
SDK cameraPinholeOffset warning and investigate NGX release 0xbad00004 next.
No tests, installed changes, source repair or second run. Full parity stays open.
## Streamline constant and NGX release source correction — 2026-10-01

Frame constants set centered cameraPinholeOffset explicitly. Shutdown now disables/
drains/frees Streamline FG before direct SR NGX shutdown; presentation hooks remain
until swapchain teardown. New native export/backend must rebuild together for the
next isolated harness batch. Implementation only, unbuilt/unrun; see the
[source record](streamline-shutdown-implementation.md). No installed changes.
Warning/error removal and complete provider/visual parity remain open.
## Streamline lifetime headless checkpoint — 2026-10-01

The [fourth headless batch](validation-headless-2026-10-01-04.md) rebuilt matching
bridge/backend cleanly and captured the snapshot automatically. Camera-pinhole
warning and NGX release error are absent; direct NGX shutdown succeeds and device
drain completes. 233 successful SR frames observed. Hidden FG interpolation and
SDK hook warnings remain open. No tests, installed deployment, fixes or rerun.
Use this batch's matching native bundle for later staged harness runs.
## DLSS-G capability/source guard increment — 2026-10-01

One extended SDK query now carries status/presents/max count/minimum dimensions/
VSync/dynamic support. Coordinator waits for unsupported current configurations;
status records preserve actual capabilities. Headless completion also records
actual SDL visibility/focus and rejects an intrusive window. New bridge/backend
must rebuild together. Implementation only, unbuilt; no launches or installed
changes. See the [source record](dlss-g-capability-state-implementation.md).
Hidden interpolation and remaining full-feature/release gates stay open.
## SDK capability and invisibility headless checkpoint — 2026-10-01

The [fifth headless batch](validation-headless-2026-10-01-05.md) passes matching
build/capture/shutdown. Actual SDL flags confirm hidden/unfocused. SDK: status 0,
max generated 1, minimum dimension 100, VSync/dynamic MFG unsupported. SR executes;
hidden interpolation remains unproven. Known hook warnings persist. No tests,
installed deployment, repairs or second run. Remaining full-feature/release gates
stay open; use this batch's matching native bundle for staged runs.
## XeSS input-layout and provider fallback source correction — 2026-10-01

XeSS-FG presenter reuse now checks all five input dimensions/formats, so an SR
quality change recreates the shared resources even at unchanged display size.
Old submission gate is cleared before presenter disposal. Registry retains exact
FSR/XeSS/provider failures for settings/fallback reporting. Implementation only,
unbuilt; no tests or launches. See the [source record](xess-source-layout-implementation.md).
Use the latest matching native bundle; installed game untouched. Full provider/
visual/feature/release acceptance remains open.
## XeSS SR/FG world live checkpoint — 2026-10-01

The [XeSS batch](validation-headless-xess-2026-10-01-01.md) builds/stages and captures
the isolated user world with hidden/unfocused window and device drain. 233 SR frames,
231 FG-prepared frames and 459 SDK-reported presents observed; SDK output is roughly
2x real frame cadence. Source image shows world/HUD, but differing terrain/streaming
prevents a parity claim. Layout-change branch and remaining feature/release gates
stay open. No tests, deployment, source fixes or rerun.
## FSR 3 SR/FG user-world checkpoint — 2026-10-01

The [FSR 3 batch](validation-headless-fsr3-2026-10-01-01.md) reuses the compiled
stage and captures/exits from the hidden snapshot. 233 SR frames, 231 FG-prepared
frames, 725 real/954 SDK presents observed (229 additional outputs); world source
pixels captured. A redundant DLSS-G options warning during unused-provider cleanup
needs source inspection. No rebuilds/tests/deployment/fixes/rerun. Remaining
transition/visual/other-hardware/release gates stay open.
## Unused DLSS-G cleanup source correction — 2026-10-01

Bridge resource-lifetime state is now separate from its swapchain-invalidated
options cache. Shutdown skips configuring an unused/already-Off DLSS-G feature,
but still drains/frees resources from earlier activation before NGX shutdown,
including when XeSS owns presentation. New bridge/backend export must rebuild
together. Implementation only, unbuilt; see the
[source record](streamline-unused-cleanup-implementation.md). No installed changes;
warning removal, switches and remaining full-feature/release gates stay open.
## Unused-provider cleanup live checkpoint — 2026-10-01

The [cleanup batch](validation-headless-cleanup-2026-10-01-01.md) rebuilds matching
bridge/backend cleanly and captures/exits from the isolated FSR snapshot. Redundant
DLSS-G shutdown warning is absent; device drain completes, with 225 additional
SDK outputs counted. Known SDK hooks and remaining activation/transition/scene/
release gates stay open. No tests, deployment, fixes or second run. Use this
batch's new matching native bundle for future staged runs.
## Required composition first-use source correction — 2026-10-01

Final/luma/HUD-less/UI draws now wait for their required pipelines; stated fallbacks
carry the same policy, while ordinary scene draws stay asynchronous. Diagnostic
capture waits for current composed world output. -AsyncPipelines lets the isolated
harness exercise normal compilation behavior. Implementation only, unbuilt; see
[the source record](required-composition-readiness-implementation.md). No installed
changes; first-use/visual and remaining full-feature/release acceptance stay open.
## Asynchronous composition live checkpoint — 2026-10-01

The [asynchronous batch](validation-headless-async-2026-10-01-01.md) builds/stages
and captures the hidden user-world snapshot with explicit async policy. No required
composition refusal; current world image and 230 additional SDK outputs captured,
with device drain. Driver-cache coldness is unproven. No tests/deployment/fixes/
rerun. Broad visual/transition/input/platform/coexistence/release gates stay open.
## Headless capture viewing and mod isolation source increment — 2026-10-01

Scheduled frames now include PNG sidecars from the same PPM readback, enabling
later-frame inspection. Headless-only collection excludes the standard installed
VulkanStory directory and loads the staged ModSystem from isolated data, removing
the installed-mod hash-match requirement for development. Implementation only,
unbuilt; see [the source record](headless-capture-mod-isolation-implementation.md).
No installed changes or launches. Visual/mod and remaining full-feature/release
acceptance stay open.
## Scheduled PNG checkpoint and isolated-mod failure — 2026-10-01

The [isolation batch](validation-headless-isolation-2026-10-01-01.md) builds cleanly,
captures upright scheduled PNGs and drains/exits; later image shows full mountain
geometry missing from early captures. Staged ModSystem was not discovered because
copied ModPaths still point to original data. Add explicit isolated --addModPath
and require world-ready callback evidence next. No fixes/rerun or installed changes;
mod integration and remaining full-feature/release gates stay open.
## Isolated mod-path/lifecycle source correction — 2026-10-01

Launcher explicitly adds isolated Mods to discovery. Headless completion now
requires the staged module's actual assembly location and world-ready callback,
recorded in result JSON. Artifact counts alone cannot pass missing mod integration.
Implementation only, unbuilt; see the
[correction record](validation-headless-isolation-2026-10-01-01.md). No launches or
installed changes; mod and remaining full-feature/release acceptance stay open.
## Isolated staged module live checkpoint — 2026-10-01

The [mod-path batch](validation-headless-modpath-2026-10-01-01.md) passes build,
capture and device drain, with actual staged DLL location and world-ready callback
confirmed. 241 SR frames and 237 additional SDK outputs observed. Headless mod
isolation no longer requires replacing installed files. No tests/deployment/fixes/
rerun; remaining transitions/input/platform/visual/coexistence/release gates stay open.
## Renderer command/control source increment — 2026-10-01

API-only `.vulkanstory set` adds validated setting edits through the existing JSON
bridge; status reports actual presentation. Latest requested settings are published
before queued application, avoiding lost sequential edits in one frame. Build Mod
and Game together for the next isolated stage. Implementation only, unbuilt;
[source record](renderer-setting-command-implementation.md). No tests/launches or
installed changes; transitions and remaining full-feature/release gates stay open.
## Renderer command compile checkpoint — 2026-10-01

The [command batch](validation-headless-commands-2026-10-01-01.md) passes Bootstrap/
Game builds; Mod fails on ambiguous Func namespace in its new delegate binding.
Use explicit System.Func next. No stage/snapshot/launch/tests or installed changes;
sequential control/transition and full port acceptance stay open. No fixes/rerun.
## Renderer command delegate source correction — 2026-10-01

New mod command bindings explicitly use System.Func, matching the existing bridge
and avoiding the game's Func type collision. Implementation only, unbuilt;
[correction record](validation-headless-commands-2026-10-01-01.md). No builds/tests/
launches or installed changes. Sequential controls and full port acceptance remain open.
## Headless harness port verified — 2026-10-01

Routine renderer checks now use a permanently hidden SDL/Vulkan window and an
isolated SQLite snapshot of foggy village story. Staged ModSystem discovery,
world-ready lifecycle, scheduled PNG/PPM capture, automatic shutdown and scripted
settings are verified. The latest run switched FSR3 SR/FG to XeSS balanced SR/FG
and preserved all sequential setting edits. No installed deployment occurred.
See [validation-headless-commands-2026-10-01-02.md](validation-headless-commands-2026-10-01-02.md).
A repeated Streamline options warning during the switch remains unresolved;
full visual parity and concurrent-session performance remain open.
## Streamline provider-switch source correction — 2026-10-01

The native options bridge now skips Off for an unused/already-disabled DLSS-G
feature even after swapchain cache invalidation, addressing the warning seen
when switching FSR3 to XeSS. On reconfiguration and resource-release ownership
are retained. Shutdown clears enabled state. Implementation only; no new build
or runtime evidence. [Source record](streamline-provider-switch-implementation.md).
## SDL visible-window startup source completion — 2026-10-01

Donor comparison found the original game icon startup call missing from the new
session. Ported its asset decode/RGBA upload directly into GameRenderSession and
wired it immediately after visible SDL window creation; hidden harness skips it.
Provenance recorded. No builds/tests/runs; visible behavior remains unverified.
[Source record](sdl-window-startup-implementation.md).
## Declared mod-pass and motion-writer source port — 2026-10-01

Migrated the reference-only declared render-pass contract/registry and frame-graph
host into mod-owned Game integration. Original stage callbacks now dispatch
registered passes after ordinary renderers, with existing temporal motion windows,
attachment validation, target restoration and world/registration cleanup.
No game API injection or backend game references. Provenance recorded.
Implementation only; compile and custom-pass execution remain unverified.
[Source record](mod-render-passes-implementation.md).
## Mod-pass lifecycle boundary completion — 2026-10-01

Unregistration now drops cached slot callbacks immediately; stage dispatch drops
stale plans even with an empty slot. Mod motion scopes preserve the caller's
mask and close only their own windows. Older-client disposal cannot clear a newer
world's registry. Source-only, no builds/tests/runs.
[Updated implementation record](mod-render-passes-implementation.md).
## Integrated controller delivery and glyph assets — 2026-10-01

Client staging/package ownership now includes the server-only input companion
for integrated single-player analog negotiation. The separate remote-server ZIP
remains optional. Headless discovery uses isolated copies of both mods. Source
only; packaging and negotiation unverified, existing archives stale.
[Delivery record](input-companion-implementation.md).

Controller glyph/font research identified PromptFont (SIL OFL, recommended for
scalable hints) and Kenney Input Prompts (CC0 image alternative). Current hints
still use ordinary text badges; asset import/private loading and physical-control
mapping remain to implement. [Sources and integration plan](controller-glyph-assets.md).
## Private controller glyph font implementation — 2026-10-01

Imported unmodified PromptFont 1.15.0 TTF/metadata with license, attribution and
source hashes. Embedded private Skia/Cairo rendering now feeds world hints and
the controller badge helper with text fallback and bounded disposable cache.
Nintendo/Sony shoulder/menu labels corrected. No OS font install required.
Implementation only; compile and in-game appearance unverified.
[Implementation record](controller-glyph-assets.md).
## Accumulated integration compile checkpoint — 2026-10-01

One compile batch passed Game/SDL/full Vulkan dependencies, ordinary Mod and
Input.Companion with zero warnings/errors, plus the fresh Streamline bridge.
Includes declared passes/lifecycle, SDL icon, embedded controller glyph rendering
and disabled-FG options correction. No tests/packages/deployment/game run.
Runtime behavior and new companion delivery remain unverified; archives stale.
[Recorded result](validation-integration-compile-2026-10-01-01.md).
## Capture ownership source correction — 2026-10-01

Graphics disposal now detaches the original screenshot service's sidecar owner.
Reviewed screenshot entry/readback/SDL size routes and cinematic codec forwarding.
Concrete OS AVI frame acquisition is absent from the current source snapshot and
still needs inspection; donor comments do not prove it uses Screenshot. No
builds/tests/probes/runs. Interactive capture/recording remains unaccepted.
[Source record](capture-lifecycle-implementation.md).
## AVI frame acquisition source port — 2026-10-01

Official OS writer inspection found a separate GL.ReadPixels call in AddFrame,
which screenshot routing did not cover. The mandatory capture group now guards
and redirects that byte-array call through the owning Vulkan session. Original
MJPEG/pooling/encoder thread remain. Added writer association/cleanup and official
OS assembly hash. Source only; compile, patch install and encoded output open.
[Implementation record](capture-lifecycle-implementation.md).
## Fresh integrated hidden runtime checkpoint — 2026-10-01

One build/stage/hidden capture batch passed. AVI call-site startup anchors accepted,
scene stage integration functional, and integrated server loaded the input
companion. FSR3 to XeSS balanced SR/FG switch completed with three world frames;
repeated Streamline options warning absent. Normal shutdown, hidden/unfocused.
No tests/deployment. Glyph appearance, custom-pass draws, AVI encoding and analog
negotiation remain unexercised; full port/release acceptance stays open.
[Recorded result](validation-integration-harness-2026-10-01-01.md).
## Client install/disable/update source wiring — 2026-10-01

Early selection now honors the game's bare-ID and exact id@version disabled-mod
keys before renderer settings/window creation. Identity embeds shipped modinfo.
Client staging adds README/updater. Updater can adopt a ZIP-extracted installation
only after full owned inventory hash checks; loader edits preserved.
Source only; disabled startup/update/removal remain unverified. No builds/tests/runs.
[Implementation record](client-delivery-implementation.md).
## Headless disable/failure isolation source follow-up — 2026-10-01

Isolated disabledMods filtering enables only this product's staged mods, leaving
other/user choices intact. Unexpected headless disablement and preparation errors
now refuse the normal visible OpenGL fallback. Source-only; no builds/tests/runs.
[Implementation record](headless-disablement-implementation.md).
## Active runtime identity completion — 2026-10-01

Remaining managed latency/validation/cache/pipeline/AO/entity/XeSS environment
switches now use VULKANSTORY_*; removed donor capture aliases and renamed log
prefixes/default validation output. Shader ABI symbols and provenance preserved
with the matching shader corpus. Source-only; no builds/tests/runs.
[Implementation record](runtime-identity-implementation.md).
## Current Windows candidate archive — 2026-10-01

Clean production builds and delivery-script syntax passed; fresh staging and
client/server ZIP creation completed. Current client is 374 entries/142,821,486
bytes with both mods, README/updater and glyph notices. Disabled/headless/identity
source changes compile. Acceptance remains unverified; no tests/deployment/game
launch. Older archives superseded, installed payload unchanged.
[Recorded result](validation-client-candidate-2026-10-01-01.md).
## Controller remapping glyph source completion — 2026-10-01

Axis rows now show the same physical stick/trigger glyphs as world hints alongside
names/direction controls. Action rows were already connected. Wide font symbols
fit their raster bounds before drawing. No builds/tests/previews/game run.
[Updated source record](controller-glyph-assets.md).
## Controller panel command integration — 2026-10-01

Added .vulkanstory controller through the existing framework/owner-thread bridge.
Keeps chord toggle, explicit unavailable/disconnected feedback, focus-independent
panel refresh and old-world/device dialog disposal. README updated. Source-only;
no builds/tests/runs, current ZIP predates this change.
[Implementation record](controller-panel-entry-implementation.md).
## Retained render-stage graph context source completion — 2026-10-01

Restored stage GPU begin/after labels, idempotent latency entry, backend stage
closure and donor late-stage OpenSampling flags for default stated draws.
Explicit mod/post declarations retain precedence. Source only; no builds/tests/runs.
[Implementation record](render-stage-context-implementation.md).
## Stated post fallback graph source completion — 2026-10-01

Migrated donor conservative Post reads and transient output masks into stated
fullscreen fallback declarations. Existing sampler reads merge as before;
allocation/native routes/shaders unchanged. Source only; no builds/tests/runs.
[Implementation record](post-fallback-graph-implementation.md).
## Graph/controller compile checkpoint — 2026-10-01

One Game/full dependencies and Mod compile batch passed with zero warnings/errors.
Axis glyph fit, controller panel command/lifecycle, stage graph labels/flags and
stated post declarations now compile. No tests, packages, deployment or game run.
Existing ZIP/runtime evidence predates these changes; behavior remains open.
[Recorded result](validation-graph-controller-compile-2026-10-01-01.md).
## Controller world/shutdown GUI ownership completion — 2026-10-01

Paired settings dialog close/unregister/dispose now runs on device/world replacement,
world exit and controller shutdown. Departure clears owned keyboard/binding/action
state before device teardown, retaining newer-world guards. Source-only; no builds/tests/runs.
[Implementation record](controller-world-cleanup-implementation.md).
## Native-resolution stage world checkpoint — 2026-10-01

One clean Game/Mod build, fresh stage and isolated hidden world batch passed with
SR/FG off. Three frames, current stage plumbing and normal shutdown; integrated
input companion loaded. Known SDK hook warnings/temporary async ordinary draw
skips retained. No tests/deployment. Timing/alias/fallback/controller execution
and full acceptance remain open; current ZIP predates this stage.
[Recorded result](validation-graph-native-world-2026-10-01-01.md).
## Current feature/evidence reconciliation — 2026-10-01

Replaced stale skeleton README status and clarified historical migration inventory.
A current ledger maps every retained feature group to active owners, actual
artifacts and remaining acceptance gaps. Design documents point to this ledger;
no narrow capture is promoted to full parity. Documentation/source inspection
only, no builds/tests/runs or goal completion.
[Current status](current-port-status.md).
## Provider disable/drain failure source correction — 2026-10-01

FSR3 now preserves native/managed effect ownership when disable/drain/destroy
fails; reset refuses input retirement. Streamline suspension/rebuild checks SDK
results; unused optional FG Off remains a no-op. XeSS drained presenter retained.
Source only; fresh FSR3/Streamline bridge builds needed, prior payloads older.
[Implementation record](provider-drain-failure-implementation.md).
## Provider lifetime compilation checkpoint — 2026-10-01

Game and fresh FSR3/Streamline bridges compiled once. Game has one unused warning
callback diagnostic; FSR3 retains eight native warnings. No tests/probes/staging
or runtime run. New bridges available, earlier payloads older. Correct callback
in a later implementation turn; error branches/full acceptance remain open.
[Recorded result](validation-provider-lifetime-compile-2026-10-01-01.md).
## Provider reset diagnostics source correction — 2026-10-01

Removed the unused warning delegate reported by the previous compile. FG
unavailability now retains its precise reason; reset clears stale DLSS-G query
state alongside counters. Successful selection/retry policy unchanged. Source-only,
no builds/tests/runs; existing compiler/runtime evidence remains scoped.
[Updated record](validation-provider-lifetime-compile-2026-10-01-01.md).
## Fresh FSR3-to-DLSS handoff checkpoint — 2026-10-01

Clean Game build/fresh corrected bridges/isolated run passed. Provider disable,
swapchain handoff, DLSS SR/current FG inputs, three captures and normal shutdown
completed. SDK DLSS counts remain about one output per hidden rendered frame;
interpolation unproved. Faint sky pattern recorded for investigation. No tests or
deployment; failure branches/controller/AVI/full parity remain open.
[Recorded result](validation-provider-handoff-2026-10-01-01.md).
## Sky pattern source localization — 2026-10-01

Original-resolution evidence confirms the pattern; native sky/dither sources match
the donor and no migration mismatch was found in the inspected upload path.
Added dimensions/jitter/CPU dither fields to existing diagnostic sampling for later
localization. Rendering algorithms unchanged. Source-only; no builds/tests/runs.
[Investigation](sky-pattern-source-investigation.md).
## DLSS-only sky input capture checkpoint — 2026-10-01

Clean build/fresh isolated run captured one frame and 51 attachments with DLSS SR,
FG off. Pattern remains, excluding active FG as a necessary cause. New diagnostic
sizes/jitter/CPU dither values recorded. Preserve raw Primary/SR output for analysis;
no further run yet. No tests/deployment; source/post/SR localization/full parity open.
[Recorded result](validation-dlss-input-capture-2026-10-01-01.md).
## Sky artifact offline localization — 2026-10-01

Analyzed existing Primary/SR-output float crops once, with residual plots/results
retained. Horizontal structures are present in raw SR output before final/FG;
Primary crop is mainly fine-grained dither. Focus next on reconstruction/temporal
inputs, not final capture/display. No code repair/build/test/device/game run.
[Analysis](sky-pattern-offline-analysis.md).
## NGX rendered-frame delta input source completion — 2026-10-01

SDK contract review found temporal DeltaTimeMs omitted from direct NGX evaluation.
Added the existing frame value and exact SDK parameter, with invalid/unused zero
fallback and matching diagnostics. Jitter/MV conventions unchanged. Source-only;
no builds/tests/runs, sky cause/fix remains unproved.
[Implementation record](dlss-rendered-delta-implementation.md).
## Existing sky motion/depth analysis — 2026-10-01

Offline crop reads found all-finite far depth=1 and RG motion=0, with reactive B
nonzero and writer-depth=0. No NaN/large-vector evidence or justified global sign
change. Static crop does not certify animated-cloud/history handling. No new
capture/build/test/device/game run; source/history investigation remains open.
[Analysis](sky-motion-depth-offline-analysis.md).
## Exact headless attachment inputs — 2026-10-01

Added capture-boundary frame metadata and opt-in sky producer matrices/uniform
placements beside the headless PFM dump. This closes the adjacent-frame ambiguity
in the one-second diagnostics without changing rendering mathematics. Source-only;
compile/runtime validation remains pending along with the NGX rendered-delta input.
The DLSS sky artifact remains unresolved. See Rewrite docs/attachment-frame-inputs-implementation.md.

## Exact-frame DLSS capture passed — 2026-10-01

One bounded Game build/fresh stage/hidden foggy village story snapshot capture
passed. Zero build warnings/errors; NGX delta and exact-frame metadata compiled.
Device, temporal and sky producer frame IDs agree at767; sky draw submitted with
previous history available, DLSS quality SR active and FG off. World closed normally.
The horizontal sky pattern remains visible. Preserve the new attachments for
source diagnosis; no tests or installed deployment. See Rewrite
docs/validation-dlss-exact-inputs-2026-10-01-01.md. Full acceptance remains open.

## Owned sky/liquid motion variant repair — 2026-10-01

Fixed missing SSAOLEVEL in both owned motion shader prefixes. The native manifest
uses that define to select attachment2 versus4, so an attachment4 pass previously
selected a shader writing excluded output2. Added reflected sky-output coverage
guard and exact-capture output metadata. No shader mathematics changed. Source
only; compile/pixel coverage and removal of the sky artifact remain unverified.
See Rewrite docs/owned-motion-variant-repair.md. No tests or game run this turn.

## Sky motion attachment repair verified — 2026-10-01

One bounded build/stage/hidden snapshot capture passed with zero build warnings
or errors. Exact sky metadata selects output4; sampled far-depth sky now has
writer alpha1 and finite near-zero static-camera motion. This verifies the missing
sky-output repair in that region. Liquid prefix compiled; liquid pixel coverage
and the still-visible horizontal DLSS sky pattern remain open. No tests, installed
changes or repeated batch. See Rewrite docs/validation-motion-variant-2026-10-01-01.md.

## Linked motion-output certification — 2026-10-01

Required scene motion writers and owned liquid motion now check actual linked
fragment outputs before certifying producer readiness. This closes requested-mode
versus selected-output ambiguity exposed by the sky variant bug. Remaining owned
SSAO prefix already includes its layout define. Source only; no builds/tests/runs.
See Rewrite docs/linked-motion-output-certification.md. Full acceptance stays open.

## Controller disable lifecycle integration — 2026-10-01

Added explicit settings transitions: disabling closes controller UI, releases
controller actions/gyro/rumble, clears damage listener and flushes profiles.
Disabled frames retry profile writes without preparing/polling gamepads;
re-enable forces hot-plug rescan. Window pump/provider markers retain ordering.
Source only; no builds, tests or game runs. See Rewrite docs/controller-disable-transition.md.

## Fresh current client ZIP — 2026-10-01

Bootstrap, Game/dependencies, Mod and Input.Companion built with zero warnings/errors.
Fresh stage and client/companion archives passed. Current client is
artifacts/validation/client-refresh-20261001-221545/archives/VulkanStory-win-x64.zip
(142829441bytes,374entries). Includes latest motion-output and controller-disable
integration. Acceptance remains unverified; no tests/deployment/game launch.
See Rewrite docs/validation-client-refresh-2026-10-01-01.md.

## Handheld shadow prefix lifecycle — 2026-10-01

Owned shader compilation now removes its inserted handheld shadow define when the
live option is disabled on a reused stage, preserving pre-existing custom defines.
No shader algorithm/target layout change. Source only; no build/tests/run. Latest
ZIP predates this increment. See Rewrite docs/handheld-shadow-prefix-lifecycle.md.

## Existing donor DLSS reference located — 2026-10-01

Located donor tmp/dlss-headless-frames/frame-000100.ppm for possible existing-file
comparison; its effective settings/camera metadata are missing, so no inherited
artifact/parity claim is made. NGX source contract inspection found retained
creation/resource/jitter conventions plus the new rendered-delta input.
No shader change or build/test/game run. See Rewrite docs/donor-dlss-reference-inspection.md.

## Ownership-aware removal source — 2026-10-01

Added remove-runtime.ps1 and packaged instructions/staging entry. It previews with
-WhatIf, moves unchanged owned files to an external backup, preserves modified
payloads/version.dll/saves/settings and attempts dependency-first rollback.
No actual removal, tests, build or package run. Latest ZIP predates this increment.
See Rewrite docs/runtime-removal-implementation.md. Acceptance stays open.

## Current delivery ZIP refreshed — 2026-10-01

Clean Game/dependency build, removal/stage/archive syntax and fresh packaging
passed once. Current ZIP: artifacts/validation/delivery-refresh-20261001-223159/
archives/VulkanStory-win-x64.zip (142832071bytes,375entries), including shadow prefix
lifecycle and removal tool with source-matched archive hash. No tests, deployment,
removal or game run. Acceptance stays unverified. See Rewrite
docs/validation-delivery-refresh-2026-10-01-01.md.

## Controller world damage ownership — 2026-10-01

World unload now explicitly detaches its player damage listener and pending rumble
through recorded world ownership. Delayed cleanup preserves a newer GUI world's
controller actions even before temporal camera attachment. Suspension/disposal
share listener unbinding. Source only; no tests/build/run. Latest ZIP predates this
increment. See Rewrite docs/controller-world-damage-ownership.md.

## Donor frame excluded as world parity reference — 2026-10-01

One existing-file offline review decoded donor frame000100 for viewing, with no
new run/tests/build. It shows character creation without loaded terrain/minimap;
effective SR settings/history are unverified. It cannot establish inherited sky
artifact or loaded-world parity. Preserve rewrite attachments as current evidence.
See Rewrite docs/validation-donor-existing-frame-2026-10-01-01.md.

## Current progress

Source checkpoint updated 2026-10-01 through session-owned SR/FG, temporal/post/UI,
terrain/shadow/OIT, sky/celestial, particle/decal, cloud/cloud-map and entity
routing, plus object/animation/held-item/instance motion histories. All additions
after the recorded validation checkpoint remain unbuilt and unrun. The verified
counts above still refer to the earlier recorded batches.

User priority updated 2026-09-30: defer new test work until integration is complete.
Continue implementation and runtime wiring; existing evidence remains scoped to
its recorded batches. New source is not claimed as validated.

The [query/capture increment](game-query-capture-implementation.md) now routes
the official sun-query calls and screenshot/window-size call sites in source,
including a full-texel BGRA8 capture boundary for HDR targets. Compilation,
runtime wiring and actual pixels/results remain unverified; draft fixtures are
deferred and no new checks ran.

The [process runtime increment](process-runtime-implementation.md) connects the
bootstrap to an entry-thread coordinator and adds real SDL/device/sidecar session
ownership, resize/drain and original frame-handler dispatch through a checked
frame-loop group. Source-only; window/startup/post/scene/service factories remain
incomplete. No complete profile is registered and graphics stay inactive. Tests
remain deferred while those runtime paths are finished.

The [startup-window increment](startup-window-routing-implementation.md) adds
original platform association, native window request/state/size substitutions,
platform Start/diagnostics/resize/exit and ordered shutdown, with complete-group
composition. Source-only; concrete scene/post and service factories still prevent
registration of an active profile. No checks or game ran.

The [settings/upscaler increment](settings-upscalers-implementation.md) adds the
new JSON settings/state boundary, the retained four-provider SR registry and a
mandatory post-device-creation hook for provider bring-up. The lightweight
standard mod entry reports the shared early-runtime status. Concrete settings,
framebuffer, temporal and FG owners and late mod attachment remain unfinished.
These additions are source-only; no new builds, tests or game runs were made.

The [session-provider increment](runtime-providers-implementation.md) now gives
the session concrete SR and FG ownership, registry preparation/bring-up,
framebuffer callbacks, settings-triggered rebuild/reset handling and FG
preparation before Present. Pre-input identity/sleep/markers use the retained
device path. The retained temporal frame state/math is copied into the game
project with an explicit rendered-frame snapshot boundary; original temporal
producers, motion hooks and scene/post/UI call sites still need integration.
All of this is unvalidated source and does not activate the incomplete profile.

The [temporal-producer increment](temporal-producers-implementation.md) connects
session-owned history to original world-loop, projection, post-jitter and disposal
methods. A required graphics-temporal group guards the original/incoming camera
and stage anchors. Fullscreen post-pass cache helpers are also migrated.
Dense motion producers and scene/post/UI routes remain incomplete, so motion
validity and complete-profile activation remain unavailable. No checks ran.

The [UI separation increment](ui-separation-implementation.md) migrates the
scene capture, transparent UI redirection and premultiplied composition, with
adapter-owned retained shader sources and reload handling. A required graphics-ui
group connects original blit, menu rendering, pre-Done composition and raw screen
depth calls. The native blit/post replacements and scene/motion producers are
still required before complete-profile activation. All new work is source-only.

The [native post-stage increment](native-post-stages-implementation.md) migrates
bloom, god rays and luma/blit plus their concrete stated fallback bodies and
post-tail sequencing. Session-owned settings and per-frame TAA result reset are
attached. AO/TAA selection must still feed this tail, and final composition/blit
and the original post call-site replacement remain open. No checks ran.

The [TAA increment](taa-implementation.md) now migrates resolve/sharpen decisions,
native and stated draws, history publication/parity and the retained GLSL sources.
Owned shader reload invalidates TAA history. A session seam selects SR/TAA before
the post tail; AO and the original post replacement must still invoke it, and
sharpen's native final-blit caller remains open. No checks ran.

The [AO/post-entry increment](ambient-occlusion-implementation.md) migrates the
retained GTAO host and native SSAO/blur/composite, with owned settings/temporal
state, targets, debug outputs and scene-composite shader. The original post
method now has a subset prefix sequencing AO → SR/TAA → bloom/god rays/luma.
Scene shader mode publication and native final composition/blit are still
required before composing the complete post group. No checks ran.

The [final-composition increment](final-composition-implementation.md) migrates
native/stated composition, scene/glow/AO selection, retained uniforms, attachment
masks, display viewport and successful composite publication. The subset now
routes original post and final composition methods. Native blit/FSR/debug/sharpen
must still join it before the complete post group is available. No checks ran.

The [final-blit increment](final-blit-implementation.md) migrates plain/FSR/debug
presentation and late TAA sharpening with owned shaders and stated fallbacks.
Original post, final composition and blit now form the graphics-post group in
source. Scene/motion, shader-mode publication and remaining host factories still
prevent complete startup registration. No checks ran.

The [transparency increment](transparency-implementation.md) migrates owned OIT
targets, before/after callbacks, lifecycle and native/stated transparent merge.
It replaces injected static IDs with adapter state. This is a scene subset;
other scene/motion producers and host factories still prevent complete startup
registration. No checks ran.

The [particle increment](particles-implementation.md) migrates cube/quad native
instanced draws and checked original pool call sites, retaining depth and OIT
blend contracts plus stated fallback. Other scene/motion and host integration
remain open. No checks ran.

The [decal increment](decals-implementation.md) migrates the retained pool scope
and native multi-draw with a checked original pool-call substitution. Original
culling/range selection and stated fallback remain. Other scene/motion and host
integration remain open. No checks ran.

The [terrain increment](chunks-implementation.md) migrates scoped native pool
draws at the original depth/shadow/opaque/OIT/after-OIT call sites, retaining
culling/origin work and stated fallback. Raw sampler clearing is routed. Dense
motion and remaining scene/host integration remain open. No checks ran.

The [volumetric-cloud increment](cloud-volumetric-implementation.md) migrates
shared-depth/OIT native drawing and raw state/draw substitutions in the original
built-in renderer. Cloud-map lifecycle, entities and dense motion plus remaining
host integration remain open. No checks ran.

The [instance-history increment](instance-history-implementation.md) migrates
retained per-buffer/device history and expanded layout into session ownership,
with pass-level camera uniforms at instanced draws. Buffer construction/fill and
identity call sites plus remaining dense/host work remain open. No checks ran.

The [entity increment](entities-implementation.md) migrates registered animated
entity/hand program drawing and a checked original multi-texture loop substitution.
Animation uploads and original bindings remain. Remaining hand/item routes, dense
motion and host integration remain open. No checks ran.

The [motion-window/sky increment](motion-window-sky-implementation.md) migrates
primary motion selection and the retained far-depth sky/cloud reactive pass,
with original scene-loop placement and owned unchanged shaders. Dense model,
bone, particle and liquid histories plus shader/host integration remain open.
No checks ran.

The [object-motion increment](object-motion-implementation.md) migrates retained
entity/rigid-object histories into the temporal owner and adds guarded model/warp
uniform capture. Previous-bone buffers, draw identities and other dense producers
still need their call sites; compiled mode publication remains required. No checks ran.

The [animation-history upload increment](animation-history-upload-implementation.md)
connects retained entity history to owned Animation payload uploads and declared
AnimationPrev buffers, including shader/session cleanup. Other dense producer
call sites and compiled mode publication remain open. No checks ran.

The [held-item increment](held-item-motion-implementation.md) connects retained
rigid history at the original draw call using per-renderer/attachment identities
and guarded motion scope ownership. Other dense producers, compiled modes and
host integration remain open. No checks ran.

The [cloud-map increment](cloud-map-implementation.md) routes resource lifecycle,
normalized-short uploads, framebuffer/viewport and point-light operations plus
retained native map drawing. Original tile/weather/shader logic remains. Entities,
dense motion and remaining scene/host integration remain open. No checks ran.

The [native sky increment](native-sky-implementation.md) migrates the retained
mesh pipeline cache and sky-dome draw, with a checked substitution at the original
renderer call site and guarded internal texture bindings. Other scene/motion
producers and service factories remain open. No checks ran.

The [celestial increment](celestial-implementation.md) migrates night-sky, sun and
moon mesh draws, cached sampler names and checked original call-site substitutions.
Other scene/motion producers and host services still prevent complete startup
registration. No checks ran.

The [liquid motion increment](liquid-motion-implementation.md) migrates the
retained liquid pool redraw before sky motion, with previous camera/warp uniforms,
motion-only attachment selection and owned shader/exception cleanup. The terrain
scope now preserves that exact mask. Instance producers, compiled shader-mode
publication and complete scene/profile integration remain open. No checks ran.

The [mechanical instance producer increment](instance-history-implementation.md)
connects original Survival renderer allocation, counts, writer offsets and device
identities to the retained 40-float history layout. Original transforms remain;
instanced motion writes and exceptional identity cleanup are scoped. This source
subset remains dormant until complete scene/profile registration. Compiled shader
modes, remaining producers and host factories remain open. No checks ran.

The [shader source/mode increment](shader-source-modes-implementation.md)
embeds retained stages and connects them to original registry include/prefix/
compile handling. A successful load plus matching retained writer links publishes
session-owned compiled motion/AO modes. Failures disable them; per-frame motion
coverage remains false. Override compatibility, native shader delivery and complete
scene/profile wiring remain open. No checks ran.

The [scene motion scope/rigid content increment](scene-motion-scopes-implementation.md)
connects terrain/decal/cube-particle/entity attachment writes and previous camera/
warp uniforms. Six Survival content renderer families now feed retained rigid
histories at their original final draws, with temporal-window and shader guards.
Dropped/falling producers, remaining content, complete scene/profile composition
and per-frame coverage remain open. No checks ran.

The [dropped/falling motion increment](dropped-falling-motion-implementation.md)
connects original Essentials final draws to retained standard histories, using
dropped-renderer and falling-entity identities. Checked operand/matrix bindings,
shadow/temporal guards and attachment cleanup preserve original draw behavior.
Remaining content/motion routes, complete scene/profile registration and per-frame
coverage remain open. No checks ran.

The [direct animated increment](direct-animated-motion-implementation.md)
connects Echo Chamber's three original mesh draws to native entity drawing and
motion scopes. Source inspection confirms first-person hands already use the
routed animated multi-texture path. Complete renderer coverage, scene/profile
composition and per-frame motion validity remain open. No checks ran.

The [scene/UI raw-state increment](scene-raw-state-implementation.md) routes
original client projection depth ranges, boat water-mask attachment changes and
framebuffer debug texture comparison through owned Vulkan state. Typed targets,
call counts and water failure cleanup are in source. Complete scene/profile
composition and remaining GL coverage stay open. No checks ran.

The [concrete framebuffer host increment](session-framebuffer-host-implementation.md)
removes the external factory and binds allocation to actual SDL pixels, original
game quality settings and owned SR/FG/AO/temporal callbacks. Remaining platform/
controller/service construction and complete scene/profile registration stay
open. No checks ran.

The [concrete terrain service increment](session-terrain-services-implementation.md)
removes external readiness/reload/LOD callbacks. Owned settings and active SR plans
feed synchronized atlas/custom-sampler bias, and original registry/client listeners
handle reload. Failed reloads disable compiled temporal/AO modes. Other service
construction and full scene/profile registration remain open. No checks ran.

The [controller source increment](controller-source-implementation.md) migrates
the retained mapper, profiles/remapping, navigation, keyboard/settings overlays,
hints and gyro/haptics behind a mod-owned host interface. Native update and
post-pump mapping phases preserve sole queue ownership. Session attachment,
GUI/IME targets and negotiated analog movement remain unfinished. No checks ran.

The [concrete controller/platform host increment](session-controller-host-implementation.md)
replaces the external callback factory with owned mapper/host/IME and real pacing,
resize/input/shutdown callbacks. Mapping follows the sole SDL queue drain and
controller imports share the packaged SDL resolver. Analog/hint consumers,
remaining services and full profile registration stay open. No checks ran.

The [concrete session services increment](concrete-session-services-implementation.md)
reduces startup services to settings/data path and replaces remaining external
device/shader/frame/UI/teardown callbacks with owned behavior. Concrete profile
assembly/registration, motion coverage, analog/hint consumers and native runtime
delivery remain open. No checks ran.

The [version profile composition increment](version-profile-composition-implementation.md)
combines 15 scene/motion leaves and constructs the API/startup group topology.
A mandatory remaining-platform GL group still prevents registration/activation;
legacy texture/mip, line/stencil and remaining API routing are the next work.
Motion coverage, analog/hint consumers and packaging remain open. No checks ran.

The [remaining legacy state increment](remaining-platform-state-implementation.md)
supplies eight original public methods for legacy texture/cube binding, mip
generation and retained stencil behavior. The version factory constructs this
required group. Source coverage reconciliation and early registration remain
unfinished; no startup/game acceptance is claimed. No checks ran.

The [early profile registration increment](early-profile-registration-implementation.md)
prepares the assembled original profile before Main and defers settings/service
selection until the first window request, honoring the actual game data path.
Disablement removes prepared routing. This source has not compiled or activated;
the installed payload remains unchanged. Motion/analog/hints, overrides and
runtime packaging remain unfinished. No checks ran.

The [frame motion publication increment](frame-motion-coverage-implementation.md)
connects current scene completion to SR/FG validity with stage/camera/frame,
compiled-target and liquid/sky success guards. Draw failures and resets invalidate
coverage, and FG rejects reset frames. Transparent-pass-disabled completion and
live provider output remain unaccepted. No checks ran.

The [transparency-disabled motion increment](motion-without-transparency-implementation.md)
skips liquid redraw and clears neutral revealage before sky motion when the
original OIT pass is disabled. Current scene/frame guards reject stale tail work.
Both option paths are connected in source; native pixels and provider operation
remain unverified. No checks ran.

The [controller hint consumer increment](controller-hint-consumers-implementation.md)
connects world-interaction glyphs and settings labels to owned snapshots, with weak
GUI refresh subscriptions and capture guards. The profile requires that group.
Visible hint/remapping acceptance, analog, overrides and packaging remain open.
No checks ran.

The [analog protocol/direction increment](analog-protocol-direction-implementation.md)
migrates the retained contract into an API-only input assembly and attaches its
original movement-vector consumer. Bootstrap binds first-party payload assemblies.
Control/speed arbitration, negotiation and ordinary companion attachment remain
unfinished. No checks ran.

The [analog client increment](analog-client-implementation.md) connects original
control speed/packet boundaries to retained arbitration, probes/acknowledgment and
encoded state refresh. Unacknowledged servers retain digital controls; world disposal
clears readiness/axes. The ordinary server companion and live acceptance remain
unfinished. No checks ran.

The [ordinary input companion increment](input-companion-implementation.md)
adds a graphics-independent server mod using public player/entity/network hooks
and an API movement seam. Negotiation, own-entity checks, retained speed/direction,
expiry and unload cleanup are in source. Packaging and live server behavior remain
unverified. No checks ran.

The [shader override policy increment](shader-override-policy-implementation.md)
uses actual asset origins/patch flags for replacement precedence and native/rewriter
selection. Required writer/shared-include overrides disable stock scene feature
publication pending specific adapters. Output/reload behavior, packaging, settings
UI and live port acceptance remain open. No checks ran.

The [ordinary mod control increment](ordinary-mod-control-implementation.md)
connects validated settings/save/reload and world lifecycle through framework/JSON
exports, with owner-boundary live updates and no second renderer. Status/reload
commands are in source. The full settings dialog, packaging and live acceptance
remain open. No checks ran.

The [renderer settings dialog increment](renderer-settings-dialog-implementation.md)
adds four ordinary GUI pages with draft Save/Cancel through the runtime bridge.
Controller/touch switches gate input; restart options and provider-specific quality
choices are shown. Main-menu/debug/effective-state presentation, packaging and
visible interaction acceptance remain open. No checks ran.

The [main-menu settings increment](main-menu-settings-implementation.md) adds an
active-profile sidebar button and parent-preserving screen, sharing one control
source with the ordinary dialog. Back/Cancel and visible save errors are wired.
Layout/navigation/resize, remaining presentation and packaging remain unverified.
No checks ran.

The [provider/debug presentation increment](provider-presentation-implementation.md)
adds post-Present effective-state snapshots, a shared Status page and retained
temporal debug views. Prepared FG inputs are distinct from SDK output counts.
FPS/all-vendor counters, visible output and packaging remain unverified. No checks ran.

The [FPS presentation increment](fps-presentation-implementation.md) wires
actual presentation observers to cumulative session counters and a shared bridge
snapshot. The ordinary HUD and settings switch consume real/SDK rates without
multiplier estimates. Visible output, vendor rates, lifecycle and packaging remain
unverified. No checks ran.

No complete product milestone is accepted yet. The following are verified steps toward the original full port, with their limits preserved.

| Area | Verified progress | Remaining work |
| --- | --- | --- |
| Early activation | Normal shortcut reached the native proxy and managed startup hook before `Main`; revised resolver passed build/tests/staging. [B0 result](validation-b0-2026-09-29-04.md). | Deploy and observe the current payload live; complete disablement, unsupported-profile, shutdown, and MFG coexistence cases. |
| SDL/Vulkan backend | All device partials, provider forwarders and support dependencies compile; latest CPU suite passed 75 tests. The three nullable warnings were repaired; subsequent clean compilation and four real facade clear/readback/present frames passed. Hidden indexed pixels/capture, offset mesh updates, segmented queries and red/green uniform snapshots also passed with present/teardown. [Device](validation-p0-device-native-2026-09-30-02.md), [capture](validation-p0-capture-boundary-2026-09-30-01.md), [queries](validation-p0-query-2026-09-30-01.md), [uniforms](validation-p0-uniform-draw-2026-09-30-01.md). | Successful game resource/state/draw routes, sampled facade draws, physical graph/feedback rendering, complete shader corpus, and remaining settings/host boundaries. |
| Native shader delivery | Repository-owned tool compiled one real `ui-compose` program into two SPIR-V modules and a manifest. [Shader result](validation-p0-shaders-2026-09-29-02.md). | Full corpus, game overrides/reload, pipeline variants, and packaged shader loading. |
| Startup binding and transaction | Official hashes/signatures and 418 operands pinned; 17 bootstrap tests verify guards/transaction rules. Sidecar and 16 core window-consumer prefixes compile; actual dormant Harmony install/remove and protected setters passed. [Routing](validation-g0-routing-2026-09-29-01.md), [cursor/window batch](validation-g0-cursor-clipboard-2026-09-30-01.md). | Complete mandatory startup/window/graphics/frame/shutdown groups and remaining consumers. Validate incoming Harmony instructions, partial-install rollback, live session ownership and first SDL window. |
| Game-facing SDL and graphics boundaries | Latest game suite passed 37 cases. Texture (11 targets), shader/uniform/sampler/UBO/disposal, mesh/SSBO/generic draw (10 targets), fixed state (19 targets) and framebuffer setup/lifecycle/load/unload/clears (14 prefixes plus 8 call-site bodies) subsets compile and install. All 34 framebuffer selection/color-clear anchors matched original/incoming IL. UBO bytes, fixed-state/null binding, clear/selector dispatch and minimized/default/placeholder lifecycle CPU behavior passed. [Default setup/load](validation-g1-default-framebuffers-2026-09-30-01.md), [clears/selectors](validation-g1-framebuffer-clears-2026-09-30-01.md), [state](validation-g1-state-routing-2026-09-30-01.md), [UBO](validation-g1-ubo-integration-2026-09-30-02.md). | Successful native SDL/resource/draw routes; GUI/IME, frame/close/lifecycle and resize; positive framebuffer/shared-depth behavior and native clear/selection pixels; native setup/load/unload acceptance, query/capture and complete startup routing. Incomplete subsets cannot activate the mandatory graphics group. |
| Providers | Retained DLSS/FSR 3.1/XeSS SR, FSR 4 runtime/shared-resource modules, FG presenters/wrappers and real device forwarders compile. FSR 4/XeSS FG managed ABI checks passed; neutral latency policy passed 10 contract tests. Selected NGX/FSR 3/Streamline bridge groups have focused evidence. [Full compile](validation-p0-full-device-2026-09-30-01.md), [FSR 4](validation-p0-fsr4-interop-2026-09-30-01.md), [XeSS FG](validation-p0-xess-fg-2026-09-30-01.md), [latency](validation-p0-latency-selection-2026-09-30-01.md). | Renamed FSR 4/XeSS FG native bridge builds/exports, runtime delivery, all SR/FG/latency attachment to actual frames, SDL handles, settings and presentation. No enabled vendor feature has been evaluated in the new host. |
| Game rendering and release | Working baseline source and provenance retained. | Menu graphics, world/temporal/post rendering, complete controls, standard mod entry/settings, normal-shortcut release package, targeted third-party GL compatibility, and Linux acceptance. |

**Current priority:** finish compiled shader-mode publication and remaining motion producers, remaining scene/content routes and service factories; then compose and register the complete startup profile. Session-owned providers, temporal/post/UI and query/capture routing are already in source, without new validation. Default setup/load/unload compilation and exercised CPU lifecycle passed; native allocation/shared-depth and complete frame behavior remain unaccepted. The clear/draw-buffer batch passed compilation, original/incoming anchors and exercised CPU dispatch; positive/native target behavior and pixels remain open. Compose startup/window/graphics/frame/shutdown groups and attach the existing sidecar/live session owner before committing SDL/Vulkan routing and rendering menus. Full facade/forwarder compilation and standalone clear/readback/present have passed; game calls, renamed native bridge delivery and vendor execution remain open. The last live game check remained on vanilla OpenGL. The chronological records below retain each increment's original status and subsequent validation; later records supersede earlier pending/failure statements.

The [default framebuffer source increment](game-default-framebuffers-implementation.md)
copies retained allocation through the TAA/SR/UI/FG slots, adapts settings and
owner callbacks, and adds shared-list disposal plus both load/unload overloads.
Fourteen prefixes and seventeen distinct methods await compilation/signature/
installation validation. The CPU fixture covers minimized/default/placeholder
paths only; positive native allocation, shared depth and complete host wiring
remain open. No build/test/native/game ran in this implementation turn.

Its [first bounded batch](validation-g1-default-framebuffers-2026-09-30-01.md)
passed 37 game cases and a clean build. Fourteen prefix signatures/private
metadata and eight existing original/incoming call-site bodies matched;
expanded install/remove, minimized setup, default/scaled viewport transitions,
owned zero-ID placeholders, foreign-list rejection and repeated disposal passed.
Positive native allocation/shared depth, real host callbacks and pixels remain open.

The [clear/draw-buffer source increment](game-framebuffer-clears-implementation.md)
adds four clear prefixes and eight checked original method-body substitutions
to the framebuffer subset. Retained clear values/masks and motion-slot logic,
original private-array semantics and current target ownership are preserved.
Eight prefix targets and fourteen distinct methods await compilation/official
anchor/install validation. No new GPU or live game evidence exists.
Its [first bounded batch](validation-g1-framebuffer-clears-2026-09-30-01.md)
passed 36 game cases and a clean build. All 34 original/incoming selection/color
clear anchors matched and the expanded group installed/removed. Exercised
original clear-prefix and direct selector/post-clear wrapper CPU dispatch
passed; positive/native target behavior, complete method execution and pixels
remain open.

The [SDL sidecar implementation](sdl-sidecar-implementation.md) now supplies original-platform association, guarded frame/event dispatch, close/focus/resize behavior, and lifecycle callbacks. This latest increment is source-only; no startup groups or live session caller are attached yet.

[Input behavior fixtures](input-fixtures-implementation.md) now target the real cached bindings and transplanted arbitration/touch algorithms. They are source-only pending one bounded validation batch; existing bootstrap tests do not cover these behaviors.

The [2026-09-30 input batch](validation-g0-input-behavior-2026-09-30-01.md) compiled the SDL sidecar, but the test project failed on a private settings constructor; no behavior tests ran. Fix the wheel-sensitivity dependency explicitly in the next implementation turn before validating again.

That wheel dependency now has a [concrete source repair](input-fixtures-implementation.md#wheel-dependency-repair--2026-09-30): a per-event sensitivity callback keeps game settings in the sidecar and removes settings-static access from fixtures. The repair is unvalidated; the previous failure stays recorded.

The repair passed [the next bounded batch](validation-g0-input-behavior-2026-09-30-02.md): 14 behavior tests, a clean profile build, and official startup/binding checks. The earlier compile failure is resolved. Native SDL sidecar dispatch and live startup remain unverified.

The [GUI text coordinator](gui-text-port-implementation.md) is now adapted in source, with cached private field access, focused-field/dialog discovery, IME area/caret positioning, stale-target rejection, and sidecar composition revision handling. The five new metadata contracts and coordinator compilation await the next validation turn; controller overlays and live menu/world integration remain open.

Its [first validation batch](validation-g0-gui-text-2026-09-30-01.md) passed compilation, all 14 input regressions, and official GUI metadata checks. Focus discovery/native IME and actual GUI accessors remain unexercised; no live startup route is active.

The [core window-consumer Harmony group](window-consumers-implementation.md) is now implemented for 13 original operations, with explicit dormant routing and owner-specific removal. A real patch install/pass-through/missing-adapter/removal fixture is added but unvalidated. Remaining consumers, startup producers and graphics routing are still required before live activation.

The group passed [its first bounded validation](validation-g0-window-consumers-2026-09-30-01.md): 15 tests, including actual Harmony installation/removal, and official target-signature checks. Native SDL replacements and complete window coverage are still unproved.

The [cursor/clipboard adapter increment](cursor-clipboard-port-implementation.md) adds retained bitmap cursor operations, expands the core Harmony group to 16 targets, and attaches a dormant-aware OS wrapper through original protected setters. Source fixtures cover forwarding/owner checks/setter execution. This increment is unvalidated; full startup/graphics activation remains pending.

Its [first validation](validation-g0-cursor-clipboard-2026-09-30-01.md) passed 18 tests, a clean build and official contracts, including real protected setters and 16-prefix installation/removal. Successful native cursor/clipboard routes and live startup remain unverified.

The [retained mesh manager increment](mesh-port-implementation.md) now adds allocation/upload/bind/indexed/indirect resource code to the backend compile list, with neutral layout metadata and game-boundary conversion. Existing draw-range fixtures and a native mapped-buffer/SSBO check are supplied. This increment is unvalidated and does not yet route game mesh calls or draw mesh pixels.

Its [first batch](validation-p0-mesh-2026-09-30-01.md) passed 41 backend cases and 19 game cases but failed game-enum attribute discovery. Fix the topology fixture's inline data in a later implementation turn; native buffer validation was skipped. Mesh drawing and game resource routing remain open.

The [next mesh source increment](indexed-mesh-implementation.md) fixes that discovery boundary and adds `--sdl-mesh`, using retained position/index allocation, actual vertex-layout pipeline selection, indexed draw, and the existing submit/present chain. Both the repair and new path are unvalidated; pixels and game routing remain unverified.

It passed [the next bounded batch](validation-p0-mesh-2026-09-30-02.md): 41 backend cases, 22 game cases, a clean build, mapped-buffer/SSBO checks and hidden indexed mesh draw/present. The earlier discovery failure is resolved. Pixel contents, game mesh/resource calls and world/provider rendering remain unverified.

The [readback source increment](readback-port-implementation.md) adds retained timeline/arena/image-copy code, a shared neutral texel-size helper, original channel-order regression and alignment cases. `--sdl-mesh-readback` now compares triangle/background pixels before presenting. This increment is unvalidated; full game capture and renderer/provider routing remain open.

Its [first bounded validation](validation-p0-readback-2026-09-30-01.md) passed 47 backend cases and both native RGBA8 pixel assertions, then presented and tore down. Selected indexed backend pixels now have direct evidence; game shaders/frames, complete captures and providers remain unverified.

The [query-ring increment](query-port-implementation.md) adds retained per-slot occlusion queries and a segmented indexed/empty-scope query preflight with pixel checks. This source-only increment awaits validation; game glare/culling and the full graphics route remain open.

Its [first validation](validation-p0-query-2026-09-30-01.md) passed 47 backend cases and the native query/pixel/present path. Segmented draw-versus-empty behavior now has focused evidence; complete lifecycle and game query routing remain unverified.

The [uniform/indirect state extraction](uniform-state-port-implementation.md) now brings retained client UBO shadows/bindings/snapshot allocation and the non-wrapping indirect ring into the backend compile list. Staged facade calls delegate to the same implementation; four source fixtures cover the changed boundary. This increment is unvalidated; full device/game shader binding and provider attachment remain open.

Its [first bounded validation](validation-p0-uniform-state-2026-09-30-01.md) passed 51 backend tests and a clean build. CPU shadow/binding and indirect ring semantics are checked; live GPU uniform snapshots, staged facade compilation and game/provider routing remain open.

The [uniform draw source increment](uniform-draw-implementation.md) now binds retained client-block snapshots to a native shader across a partial frame submission and compares distinct red/green draw pixels. `--sdl-uniform-snapshots` is source-only pending validation. Game named-block routing, full descriptor state and the device/provider facade remain open.

Its [first validation](validation-p0-uniform-draw-2026-09-30-01.md) passed 51 backend cases and the native red/green snapshot path across partial submission. This directly checks the exercised arena/descriptor/draw lifetime; game shader routing and the complete facade/providers remain unverified.

The [shader-definition boundary](shader-definition-port-implementation.md) now separates original game shader identity from renderer-owned stage/program data and adapts the staged program facade. Five source fixtures cover reload identity and stage tokens. This increment is unvalidated, and the full facade plus actual game shader patches remain open.

Its [first validation](validation-g1-shader-definition-2026-09-30-01.md) passed 27 game cases, a clean profile build and official contracts. Stage identity/text/token conversion is checked; include capture, the excluded facade and actual compile/link routing remain unverified.

The [neutral mesh upload increment](mesh-upload-port-implementation.md) extracts retained create/update operations into compiled backend source, adapts official mesh data at the game boundary and removes the injected buffer-part selector. A capture fixture and nonzero-offset native buffer scenario are source-only pending validation. Actual game resources, full facade and world/provider routing remain open.

Its [first validation](validation-p0-mesh-upload-2026-09-30-01.md) passed 51 backend and 28 game cases, clean compilation and helper-driven native buffer/offset checks. Other upload paths, the excluded facade and actual game resource/world/provider routing remain unverified.

The [texture/resource boundary increment](texture-resource-boundary-implementation.md) removes game enum imports and the old platform logger reference from the staged resource facade. Neutral formats/attachments and a game logger adapter are supplied; existing resource and vendor log behavior is retained. This increment is unvalidated, and complete facade/resource/provider routing remains open.

Its [first validation](validation-p0-texture-boundary-2026-09-30-01.md) passed 51 backend and 28 game cases plus a clean profile build. Compiled contracts/adapters have regression evidence; the excluded resource/device/provider partials and actual game routes remain open.

The [neutral capture increment](capture-boundary-implementation.md) removes the injected readback type and game imports from the staged readback partial, compiles the retained dump helper and adds decoder fixtures. Native pixel checks now consume decoded capture data. This increment is unvalidated; actual screenshots/AVI, full facade and game/provider routes remain open.

Its [first validation](validation-p0-capture-boundary-2026-09-30-01.md) passed 54 backend tests, clean compilation and decoded native pixels/presentation. File writers, actual game captures, excluded facade and provider routes remain unverified.

The [latency selection/listener increment](latency-selection-port-implementation.md) replaces the staged device's Optimum latency config reads and old platform listener with neutral contracts while retaining mode/FG policy and SDK calls. The source and seven contract fixtures are unvalidated; host settings, complete facade/provider compilation and live Reflex/PCL acceptance remain open.

Its [first validation](validation-p0-latency-selection-2026-09-30-01.md) passed 10 contracts, 54 backend and 28 game cases plus a clean tool build. Mode/FG selection policy is checked; the excluded device and live Reflex/PCL/game/provider acceptance remain open.

The [native/graph source increment](native-graph-port-implementation.md) brings retained pipeline/pass declarations, transient backing and feedback-copy pools into backend compilation with original CPU fixtures. This increment is unvalidated; physical graph draws, complete facade and game/provider routing remain open.

Its [first validation](validation-p0-native-graph-2026-09-30-01.md) passed 67 backend cases and clean compilation. Planning/transient lifetime and feedback retirement are checked; physical graph draws, full facade and game/provider routes remain open.

The [SR provider source increment](sr-provider-port-implementation.md) adds retained DLSS/FSR 3.1/XeSS modules to the compile group through the real renderer host interface, with staged device forwarders and disabled-DLSS fixture. This increment is unvalidated; FSR 4 interop, complete facade and actual SDK/world/provider activation remain open.

Its [first validation](validation-p0-sr-provider-2026-09-30-01.md) passed 68 backend cases and clean compilation. Provider bodies and disabled-DLSS early-out are checked; excluded real-device forwarding, FSR 4 interop and actual SDK/game/world activation remain unverified.

## Preserve the working baseline

The [framebuffer source increment](game-framebuffer-routing-implementation.md)
ports creation/disposal and public/private current-target setters with cached
original field access, retained viewport rules and owned target lifetime.
Four signatures and null-binding fixture await validation; full default target
set, shared-depth lifecycle and native attachment/pixel acceptance remain open.
Its [first batch](validation-g1-framebuffer-routing-2026-09-30-01.md) passed
35 game cases and a clean build, including four target installations and
successful original-field/null-binding CPU behavior. Native attachments,
positive binding/shared-depth lifetime and pixels remain open.

The [fixed-state source increment](game-state-routing-implementation.md) adds
nineteen original targets populating shared retained draw state, with explicit
blend enum conversion and frame-owned SSAO/motion override flags. Successful
patched CPU-state fixture and signatures await validation; pipeline/game pixels remain open.
Its [first batch](validation-g1-state-routing-2026-09-30-01.md) passed 34 game
cases and a clean build, including nineteen bindings and successful original
patched CPU state calls. Pipeline/game pixels and full blend-factor policy remain open.

The [generic draw source increment](game-draw-routing-implementation.md) adds
four original ordinary/instanced/multidraw/fullscreen targets through retained
StatedDraw and mod-owned state. Ten mesh targets await expanded validation;
native pixels and all dedicated scene/temporal routes remain required.
Its [first batch](validation-g1-draw-routing-2026-09-30-01.md) passed 33 game
cases and a clean build, with ten mesh target installations and ownerless draw
rejection. Successful generic/dedicated draws and full game acceptance remain open.

The [SSBO source increment](game-ssbo-routing-implementation.md) ports original
allocation/update and extracts the retained face-packing loop with its original
offset/byte count and stream ordering. Six mesh signatures/packing fixtures
await validation; actual native bytes/draws and full game routing remain open.
Its [first batch](validation-g1-ssbo-routing-2026-09-30-01.md) passed 33 game
cases and a clean build, including six mesh targets and CPU packed-face layout/
quad semantics. Native bytes/draws and remaining packing branches remain open.

The [mesh resource source increment](game-mesh-routing-implementation.md)
ports original allocation/upload/update/delete and VAO disposal with retained
metadata and mod-owned ownership. Signatures/anchors/install and native resource
bytes/retirement await validation; draw/SSBO integration remains open.
Its [first batch](validation-g1-mesh-routing-2026-09-30-01.md) passed 32 game
cases and a clean build, including mesh/VAO bindings and installation/removal.
Successful native mesh bytes/retirement and draw/SSBO integration remain open.

The [shader disposal source increment](game-shader-disposal-implementation.md)
replaces original GL detach/stage/sampler/program release calls while retaining
disposed-state/control flow and renderer-owned module lifetime. Compilation/
anchor/install checks and real linked-resource retirement await validation.
Its [first batch](validation-g1-shader-disposal-2026-09-30-01.md) passed
31 game cases and a clean build, including disposal release anchors and
installation/ownership/removal. Real linked-resource retirement remains open.

The [generic UBO handle repair](game-ubo-handle-repair-implementation.md) now
observes helper tokens in the owned UBO scope and pins payloads only during
shadow copy, preserving raw pointers and original helper cleanup. Source-only;
the failed exact-byte fixture and expanded cleanup checks await validation.
The [repaired UBO integration batch](validation-g1-ubo-integration-2026-09-30-02.md)
passed 31 game cases and a clean build, including exact whole/range/object CPU
bytes, observation cleanup and repeated disposal. GPU snapshot/pixels remain open.

The [successful UBO fixture increment](game-ubo-integration-fixture-implementation.md)
adds original patched creation/generic/object updates/disposal checks against
retained CPU shadow bytes, including offset/neighbor preservation and repeated
disposal. It is source-only; CPU dispatch and GPU snapshot/pixel acceptance remain open.
Its [first batch](validation-g1-ubo-integration-2026-09-30-01.md) passed 30
regressions but failed the new generic byte check: the original helper supplies
a GCHandle token, not a pinned payload pointer. Scoped pin/lifetime adaptation
is required; successful range/disposal and GPU results remain open.

The [generic UBO source increment](game-generic-ubo-routing-implementation.md)
keeps Update<T> bodies intact and intercepts their exact pointer uploads through
the thread's owned bound UBO. Original size/pinning semantics are retained;
signature discovery/entry-point patch validation and successful bytes await validation.
The [generic UBO batch](validation-g1-generic-ubo-routing-2026-09-30-01.md)
passed 30 game cases and a clean build. Upload discovery/install and original
size/missing-owner guards passed; successful owned bytes/pixels remain open.

The [UBO lifecycle source increment](game-ubo-routing-implementation.md) ports
creation/bind/unbind/object-range update/disposal using original UBO identities
and mod-owned metadata. Generic Update<T> still needs its raw GL upload route;
expanded compilation/bindings and native bytes/lifetimes await validation.
Its [first batch](validation-g1-ubo-routing-2026-09-30-01.md) passed 30 game
cases and a clean build, including UBO signatures/disposal anchor/install and
ownerless Bind rejection. Native bytes/lifetimes and generic updates remain open.

The [sampler/binding source increment](game-sampler-routing-implementation.md)
ports original sampler creation, shader 2D/cube bindings and Stop's sampler
unbind call, and transplants retained binding state/generic native-draw helper.
Compilation/anchors/patch validation and successful sampled pixels remain open.
Its [first batch](validation-g1-sampler-routing-2026-09-30-01.md) stopped at
game compilation because the transplanted state needs an explicit Graph alias.
No tests ran; preserve redirect semantics when fixing the namespace.
The Graph alias is now repaired in source without changing redirect behavior;
expanded sampler group compilation/patch validation awaits the next bounded batch.
The [repaired sampler batch](validation-g1-sampler-routing-2026-09-30-02.md)
passed 30 game cases and a clean build, including expanded signatures/Stop
anchor and installation. Successful native bindings/draw pixels remain open.

The [array/matrix uniform source increment](game-array-matrix-routing-implementation.md)
adds eight checked routes (eighteen uniform methods total), preserving receiver
program IDs, element counts, false-transpose convention and Matrix4 field order.
Expanded compilation/anchor/patch validation and native write pixels await validation.
Its [first batch](validation-g1-array-matrix-routing-2026-09-30-01.md) passed
30 game cases and a clean build, including all eighteen uniform anchors and
installations plus array/matrix ownership/removal. Successful writes/pixels remain open.

The [scalar/vector uniform source increment](game-uniform-routing-implementation.md)
adds ten checked call-site routes on original ShaderProgramBase overloads,
retaining active-shader guards, location lookup and integer/float conversions.
Expanded compilation/anchor/patch validation and successful uniform pixels remain open.
The [first uniform batch](validation-g1-uniform-routing-2026-09-30-01.md) passed
30 game cases and a clean profile build. Ten original/incoming call anchors and
transpiler installations passed; successful uniform writes/pixels remain open.

The [shader routing source increment](game-shader-routing-implementation.md)
ports compile/link/uniform lookup/program selection through neutral definitions
and mod-owned state, with four dormant Harmony targets and explicit settings/log
callbacks. Expanded compilation and fixture execution await validation.
The [first shader batch](validation-g1-shader-routing-2026-09-30-01.md) compiled
but failed the fourth target because `UseShaderProgram` is absent from the
official platform. Repair the original shader Use/Stop selection call sites;
29 regressions passed, shader installation and profile build did not complete.
The injected target is now replaced in source by checked GL.UseProgram call-site
transpilers on official ShaderProgramBase.Use/Stop, retaining surrounding logic.
The repaired fixture and five real targets await one bounded validation batch.
The [repair batch](validation-g1-shader-routing-2026-09-30-02.md) failed compilation
on the selection wrapper's missing ScreenManager namespace. No tests ran;
qualify/import the original Vintagestory.Client namespace before the next batch.

The [Cairo/cubemap source increment](game-cairo-cube-routing-implementation.md)
ports creation/update/six-face cube operations and expands dormant texture
targets to eleven, with official non-copying Cairo references and explicit
conditional error-check setting. Compilation/signatures and native pixels await validation.
Its [first batch](validation-g1-cairo-cube-routing-2026-09-30-01.md) passed
29 game cases and a clean profile build, including all eleven target bindings
and installation. Native Cairo/cubemap calls and pixels remain unverified.

The [bitmap/atlas routing source increment](game-bitmap-routing-implementation.md)
transplants five retained upload operations and expands the dormant texture group
to eight targets. Pinning, external buffers and deferred mipmap allocation are
retained; expanded signatures/compilation and native upload pixels await validation.
Its [first validation](validation-g1-bitmap-routing-2026-09-30-01.md) passed
29 game cases and clean compilation, including installation of all eight typed
targets. Successful native bitmap/atlas/mipmap operations remain open.

The [game texture routing increment](game-texture-routing-implementation.md)
starts the original-platform graphics bridge with mod-owned renderer association
and three dormant Harmony targets for raw creation, mipmaps and deletion.
The incomplete subset cannot satisfy the mandatory graphics group; source
fixtures and integration compilation await validation.
Its [first batch](validation-g1-texture-routing-2026-09-30-01.md) compiled the
adapter but failed fixture compilation due to a missing direct official Harmony
reference in the test project. No game tests ran; the profile build was skipped.
The missing reference is now repaired in source with the official non-copying
Harmony reference; the next bounded game-test/profile-build batch is pending.
The [repaired batch](validation-g1-texture-routing-2026-09-30-02.md) passed
29 game tests and a clean profile build, including three target signatures and
actual texture-prefix installation/missing-owner rejection/removal. Native
successful texture calls and complete graphics transaction remain open.

The [device preflight source increment](device-preflight-implementation.md)
repairs the three nullable warning sites and adds `--sdl-device` to exercise
the full facade's initialization, four distinct clear/readback/present frames
and device-before-window teardown. Its [first batch](validation-p0-device-native-2026-09-30-01.md)
passed 75 tests and warning-free compilation, but native pixels failed because
the harness supplied 0 instead of the native default target sentinel -1.
That harness repair and successful facade presentation remain open.
The harness argument is now repaired in source using `PassDeclaration.DefaultFramebuffer`;
one bounded tool-build/native run awaits the next validation turn.
That [corrected batch](validation-p0-device-native-2026-09-30-02.md) passed
warning-free compilation, four successive clear/readback/present frames and
normal full-device teardown before SDL. The harness failure is resolved;
game routing, facade shader/mesh draws and vendor execution remain unaccepted.

The [full-device source increment](full-device-port-implementation.md) now
includes all retained facade partials and their remaining GTAO/timestamp/FG/XeLL
support files, with embedded GTAO shaders and copied timestamp fixtures.
Its [first validation](validation-p0-full-device-2026-09-30-01.md) compiled the
complete group and passed 75 backend cases, with three nullable warnings still
requiring source repairs. Actual facade/GTAO execution, live game and vendor acceptance remain open.

The [XeSS FG presenter source increment](xess-fg-port-implementation.md) adds
retained interop requirements/runtime/presenter to compilation, renames its own
native bridge and supplies the 216-byte ABI fixture. The staged device now reads
a host-owned multiplier instead of injected game configuration. This increment
is unvalidated; full facade, native SDK/proxy execution and game integration remain open.

Source repository: `D:\Coding\VulkanStory`.

Source revision inspected for this plan: `386e0d05386d0b228b439d09aeca851428f7bbf3`. The source checkout was clean when this revision was recorded.

The existing renderer and SDL implementation are the working reference. **Direct source transplantation is the default.** Carry over tests, fixtures, captures, and recorded validation alongside the implementation. Do not redesign rendering algorithms, synchronization, shaders, temporal math, resource layout, or SDK integration as part of moving them into the mod.

The substantial new work is the native bootstrap and the adapter between unchanged game assemblies and the existing graphics implementation. Code currently living in an injected game member can often move into a mod-owned helper with the same algorithm and explicit state/accessor arguments. It does not automatically need a fresh implementation.

Existing validation remains evidence for that baseline. New acceptance work checks the changed integration and any actual behavior changes; hardware/platform gaps in the existing records stay visible. A renamed method or moved file alone is not a reason to repeat the full suite.

## Migration categories

| Existing area | Destination | Expected treatment |
| --- | --- | --- |
| `Optimum.Render.Vulkan/Core` | Vulkan backend | Direct copy; replace namespace and isolate the small host/configuration seams. Preserve allocator, descriptors, layouts, caches, and resource lifetimes. |
| `Graph`, `Frame`, `Transfer` | Vulkan backend | Direct copy, retaining synchronization/submission behavior and existing tests. |
| `VulkanDevice*.cs` | Vulkan backend | Direct copy with explicit host callbacks/settings in place of game/platform references. Keep native draw and provider methods. |
| `Shaders` and `tools/shader-compiler` | Backend shader library and build tool | Direct copy; change package/cache roots and new names. Preserve compilation, reflection, rewrite, and manifest semantics. |
| `sources/shaders-vk`, GTAO sources, supported compatibility shader sources | New shader source tree | Preserve content and variants initially, including notices. No aesthetic or arithmetic changes during migration. |
| `AmbientOcclusion`, temporal resolve/sharpen implementations | Render/backend source | Direct copy. Extract orchestration from game/platform state through a thin adapter. |
| `Upscale`, `Present`, `Latency` | Backend/provider modules | Direct copy first. Adapt native runtime paths, configuration, logging, and frame/window interfaces. |
| `native/optimum-*` | New native bridge directories | Source copy; rename own exports/libraries consistently only where needed. Keep vendor entry points, ABI, constants, and SDK behavior intact. |
| `Core/SdlVulkanWindowHost`, `Platform/SdlEventPump`, coordinate/key/touch helpers | SDL platform | Direct copy with neutral events and surface/logging interfaces. |
| Controller algorithms, profiles, button/axis arbitration, gyro/haptics | SDL/input and game integration | Keep algorithms; separate game GUI/input calls from SDL device handling. Preserve default mappings. |
| `VulkanClientPlatform.*` overrides | `GamePlatformAdapter` and mod-owned graphics facade | Preserve rendering method bodies wherever possible; replace inheritance, injected member calls, and base-state access with bound helpers. |
| TAA/post-chain/frame setup logic embedded in `ClientPlatformWindows` patches | Mod-owned frame/post-chain helpers | Extract the renderer-specific logic and state. Keep pass order, history decisions, and frame semantics. |
| Optimum temporal/motion/render-pass contract files | VulkanStory contracts and game bridge | Move needed types and behavior; remove injected game-API identities and unrelated configuration. |
| Essentials/Survival rendering source patches and `VulkanForkGraphics` | Versioned built-in-mod Harmony adapters | Rewrite how the code is reached. Reuse the draw/state operations; leave official mod assemblies on disk unchanged. |
| `OptimumAnalogMovement` plus associated input/server hooks | Optional input companion | Preserve negotiated analog control behavior with ordinary mod/Harmony integration. No renderer dependency on a remote server. |
| Configuration and settings UI | VulkanStory mod | Keep renderer/controller setting meanings and controls; replace storage, ownership, labels, and availability wiring. |
| Shader/mod compatibility scanner | Compatibility coordinator | Reuse applicable classification logic; replace launcher invocation and treat scanner output as evidence, not proof of support. |
| CPU/GPU renderer tests and fixtures | New test projects | Copy and mechanically update references; preserve expected values and regression cases. |
| Source-text/Cecil shipping assertions | New patch binding/integration tests where relevant | Keep the intent; tests for removed donor/patch distribution mechanics stay behind. |
| Optimum launcher, installer, donors, delta runtime, project-wide config | Excluded | No project/runtime references or copied build pipeline. |
| Worldgen, server scheduling, networking/database optimizations, gameplay changes | Excluded unless directly required for a retained input/render feature | Keep the port's purpose narrow. |

Do not copy the old `optimum-api-contracts` project wholesale. It mixes graphics contracts with unrelated optimization systems. Extract only required files, keep their algorithms, and replace the few host-specific dependencies.

### Migration record

For each source group record the original path/revision, destination, associated fixtures/evidence, and one of:

- **Unchanged:** byte-identical source/artifact content where practical.
- **Mechanical:** names, references, visibility in our own assemblies, paths, or logging only.
- **Adapter:** explicit accessors/callbacks replace game inheritance or injected state; algorithm retained.
- **Behavior change:** explanation and a targeted acceptance case required.

Keep shader source/binary digests and provider SDK versions with the record. Do not invent portability percentages before the dependency extraction is complete. Preserve file-level attribution and the existing license/provenance records; source copied from unlisted or mixed-license paths must not be relabeled by a blanket new license.

## Development workflow

The new project references a developer-supplied official game installation through a local `VintageStoryPath` or equivalent property. Use the matching game's API, client library, built-in mod assemblies, and shipped Harmony/OpenTK versions as compile-time references with copy disabled. Do not reference the old `build` donor tree, old deployed `.vanilla` directory, or Optimum contracts as the new runtime baseline.

The initial game profile targets 1.22.7. Verify its official runtime and assembly identities before freezing the first project files; the official current mod template uses net10.0. Keep SDK/NuGet dependencies pinned and native vendor SDK discovery local to development. Players receive built binaries and runtime dependencies.

Developers use three paths, all owned by this repository:

1. **Core development:** build/inspect renderer code and reuse standalone CPU/GPU fixtures without launching the game.
2. **Managed integration development:** activate the same managed bootstrap through a process-local .NET startup hook when debugging, so native proxy changes are not required for every managed edit. This is a developer convenience only.
3. **Product validation:** launch the original executable through the normal shortcut with the actual native bootstrap package installed.

Deploy only VulkanStory-owned output into a developer-selected game installation. Avoid generating or maintaining a second full game tree as part of the toolchain. Keep saves/configuration selected explicitly for live checks. A clean official reference installation is needed to prevent accidental dependence on Optimum's modified assemblies.

### Turn discipline

Implementation turns inspect/edit source and may create test fixtures, but do not run builds, tests, probes, packages, or the game. Validation turns define one bounded batch beforehand, execute it once, record the entire result, and stop. A failure is diagnosed from those logs/source; its fix occurs in a later implementation turn. Do not use automatic continuation to repeat a full suite or speculative live trials.

Validation scope follows changed code and the concrete remaining integration risk. The existing renderer tests are reused, not replaced with a new blanket testing project. Full feature acceptance is assembled from recorded milestone evidence rather than rerunning every previously accepted subsystem after each edit.

## Milestones

All acceptance milestones below remain open. Verified substeps are recorded above and in their linked batches; the latest backend native evidence is [the corrected device batch](validation-p0-device-native-2026-09-30-02.md), and the latest game evidence is [the default framebuffer/load/unload batch](validation-g1-default-framebuffers-2026-09-30-01.md). Each milestone has separate implementation and validation turns.

### B0 — Prove the product's activation route

**Progress:** pre-`Main` activation through the normal shortcut is proved. The revised resolver passed focused build/test/staging, but the installed older payload failed before Harmony observation. Live acceptance with the current observer remains required.

Implement only the native/managed bootstrap, dependency binding, pass-through behavior, and ordered startup markers. Inspect the exact existing MFG Enabler package when selecting coexistence behavior.

Acceptance: original shortcut; activation before the game startup targets execute; safe disablement; supported proxy coexistence; no duplicate CLR or game entry; correct paths and process filtering. See [the full B0 evidence list](../bootstrap-and-installation.md#b0-acceptance-evidence).

If this fails, do not transplant more rendering code to hide the unresolved install constraint. Diagnose the native loading order and revise the bootstrap in the next implementation turn. If the required loading point cannot be obtained, report the precise delivery tradeoff.

### P0 — Transplant the renderer and SDL source

**Progress:** all retained device partials, provider forwarders and support dependencies compile; the latest backend CPU suite passed 75 tests. Subsequent clean compilation and four real facade clear/readback/present frames passed. Focused hidden GPU checks also cover indexed pixels, offset uploads, queries and uniform snapshots through present/teardown. Full P0 acceptance still requires remaining host/configuration/input boundaries, native runtime delivery and full retained feature operation from this checkout.

Copy the known working implementation, shaders, native bridges, and applicable tests into new project boundaries. Record mechanical changes and extract only the required graphics/input contracts. Use small host interfaces to remove references to `OptimumConfig`, injected game methods, and the old platform subclass. Keep providers, shaders, algorithms, and internal layout unchanged.

Acceptance: the new projects build solely from this checkout, official game references, and declared SDK dependencies. Reused standalone tests cover the areas affected by dependency extraction. There is no runtime reference to Optimum, a donor assembly, a patched game copy, or the old source checkout.

This is a source migration milestone; it does not require writing a second Vulkan renderer or a second SDL implementation.

### G0 — Bind the original game and own startup/windowing

**Progress:** official IL guard matched 418 operands across seven targets; 17 bootstrap tests passed. Input/touch, GUI coordinator, cursor/clipboard wrapper and 16 dormant window-consumer prefixes compile. Actual Harmony installation/removal, cached bindings and protected setters have focused evidence; the latest game suite passed 37 cases including graphics CPU fixtures. Complete mandatory startup/frame/shutdown routing and a live session owner remain pending. Successful native SDL replacements, GUI/IME behavior, visible first-window ownership and game frames remain unproved.

Implement the original-platform sidecar, explicit private-member accessors, patch registry/profile, SDL startup substitutions, and frame/close/shutdown delegates. Move the existing SDL input-to-game glue into the adapter. Preserve argument parsing, data paths, audio, login, and single-player server startup.

Acceptance: one visible SDL window from the first graphics startup; no mandatory GLFW/GL context path on the successful route; correctly ordered game frames; initial input/resize/focus/close handling; failure before graphics commitment cleanly retains normal startup. A bounded clear-frame or minimal UI slice is sufficient here.

### G1 — Route the ordinary graphics API and menus

**Progress:** full device partials compile. Texture, shader/uniform/sampler/UBO/disposal, mesh/SSBO/generic draw, fixed-state and framebuffer setup/lifecycle/load/unload/clear subsets compile and install; 37 game tests passed. All 34 framebuffer selection/color-clear anchors matched. Original patched UBO bytes, fixed-state/null binding and exercised clear/selector dispatch have CPU evidence. Standalone backend draw/query/uniform/capture and facade clear/present have focused GPU evidence. Positive/native framebuffer/clear/selection operations, native default setup/load/unload acceptance, query/capture routes, full activation and usable game menus remain open.

Move the old platform rendering bodies into the sidecar/facade. Cover texture/mesh/framebuffer lifecycle, shaders/uniforms/samplers, buffer operations, queries, clipping/blending/depth state, GUI composition, readback, and capture. Complete all mandatory raw-GL routes used during startup and menus. Port the existing runtime shader rewriter and packaged native shader path.

Acceptance: usable main menu, settings and text; stable resize/fullscreen/minimize; shader reload and screenshot orientation/alpha; correct resource lifetimes; no mandatory game call reaching a missing GL context.

### G2 — Preserve complete world rendering and temporal behavior

**Progress:** temporal camera/jitter ownership, UI separation, TAA/AO/post/final blit, terrain/shadow/OIT, sky/celestial, particles/decals, clouds/cloud-map and entity routes are migrated in source. Object, previous-animation, held-item and instance histories are also migrated. Liquid velocity redraw now precedes sky motion in source; mechanical instance producers are now attached in source; compiled shader-mode publication, remaining content routes and complete scene composition remain open. No world rendering or temporal behavior has been accepted in the new mod.

Map all scene producers and built-in mod rendering hooks to the transplanted renderer: terrain/shadows/OIT/liquids, entities/hands, particles/decals, sky/weather/clouds, inventory/item/world-map/mechanical rendering. Extract the already-working temporal and frame orchestration into mod-owned state. Enable the migrated TAA, GTAO/SSAO, scaling/sharpening, and separate scene/UI resources through the new settings bridge.

Acceptance: representative world scenes and retained temporal regressions; world join/leave/rejoin and return to menu; consistent motion/depth/jitter and late overlays; no need to modify official Essentials/Survival assemblies. Choose existing fixtures/captures relevant to the new routing rather than rebuilding every test from scratch.

### G3 — Attach retained vendor features and complete SDL controls

**Progress:** retained SR modules, FSR 4 runtime/shared-resource modules, FG presenters/wrappers and device forwarders compile. Disabled-DLSS early-out, neutral latency policy, FSR 4/XeSS FG managed ABI and selected bridge groups passed focused checks. Renamed FSR 4/XeSS FG native bridge builds/exports and runtime delivery remain open. Enabled SDK evaluation, actual frame/presentation ownership, settings and complete controller/touch/IME integration remain unaccepted in the new host.

Wire the existing DLSS/XeSS/FSR SR and FG paths plus Reflex/PCL/XeLL/Anti-Lag to the new configuration, frame identity, native-runtime paths, and presentation ownership. Preserve implemented provider switching and teardown. Complete controller UI, navigation, physical/controller arbitration, touch, IME, gyro/haptics, and optional analog movement support.

Acceptance focuses on changed boundaries: packaged native DLL loading, real SDL native handles, actual SDK frame/present identities, live switching, focus/resize transitions, UI separation, and clean shutdown. Carry forward existing hardware coverage records and add evidence for the new host path; unavailable hardware remains an explicit gap.

Do not redesign vendor algorithms or update SDK versions at the same time as attaching them to the new host.

### C0 — Extend compatibility to third-party GL usage

The compatibility coordinator and discovery boundary exist earlier, but broad third-party translation comes after native game parity. Start with one selected, version-pinned mod and a documented set of OpenTK operations. Share the renderer's resource registry/state bridge; apply its patches before that mod initializes rendering. Reuse the shader rewriter for its supported shader subset.

Acceptance: the selected mod's real resource creation/update/disposal and rendering work, including shader reload and world transitions. Patch ordering with the mod's own Harmony owner is recorded. Unsupported operations produce an actionable report. Expand only with another concrete support profile and retained regression case.

This is where early activation pays off for future compatibility: operations can be redirected before GL resources ever exist. It is not a promise to translate all possible OpenGL/native APIs automatically.

### R0 — Release the normal-shortcut package

Package the verified proxy variant, payload, shaders, native redistributables, standard mod entry, and notices. Implement the existing-proxy conflict flow and the supported update/removal behavior. Confirm renderer disablement and package/game version mismatch handling. Publish the actual supported game/platform/provider matrix.

Acceptance: a user with only the official game can install and run from the normal shortcut; no SDK/decompiler/alternate game directory; supported MFG coexistence; removal leaves ordinary game startup intact. Release checks are one bounded validation batch for the release candidate, with earlier milestone evidence retained.

### B0-L / R0-L — Linux activation and package

Keep Linux code paths compiling through the migration. Before calling the Linux product ready, settle normal-shortcut activation for the supported launch script/package and validate it against actual native Linux windowing. Preserve provider availability differences. A Windows proxy under Wine does not satisfy this milestone.

## Feature retention checklist

| Feature group | Required destination behavior |
| --- | --- |
| Core Vulkan | Native scene/GUI draws, shader variants, pipeline/cache behavior, bindless resources, graph synchronization, transfers/readback, captures, and diagnostics. |
| Temporal/post | Existing TAA, motion writers, resets, world/hand cameras, GTAO/SSAO, scale/sharpen, bloom/god-rays/final composition, and separate UI. |
| Super resolution | DLSS SR, XeSS SR, FSR 3.1 SR, FSR 4 where supported, settings/presets, failure handling, and live switches. |
| Frame generation | DLSS-G, XeSS-FG, FSR 3 FG, real provider limits, proxy swapchain ownership, resource lifetime, and UI composition. |
| Latency | Shared frame identity, pacing authority, pre-input sleep, input/simulation/render/present markers, Reflex/PCL, XeLL, Anti-Lag. |
| SDL host | First visible window, lifecycle, scaling, fullscreen/minimize/restore, cursor capture, clipboard, IME, file drop, focus, and close cancellation. |
| Controls | Keyboard/mouse/touch, controller navigation/profiles/remapping, gyro/haptics, on-screen keyboard, input arbitration, and negotiated analog movement. |
| Integration | Standard game RenderAPI and registered renderers, built-in content renderer hooks, shader overrides/reload, screenshots/video path, return to menu/rejoin. |
| Extension | Render pass/motion writer API and later targeted third-party OpenGL adapters. |

### Analog movement boundary

The current controller implementation already negotiates analog movement with a compatible server and uses digital movement without acknowledgment. Preserve that behavior. Package a small ordinary input companion, separate from the renderer/bootstrap, for integrated single-player server support and optional remote-server installation. It contains no Vulkan/SDL/native loading and no unrelated server optimizations. Renderer use must not require a multiplayer server to install it. Exact mod metadata and packet compatibility are implementation details to pin before G3.

## Next implementation and acceptance steps

1. Finish remaining dense motion producers and per-frame coverage publication. Retained shader-source routing and compiled mode publication are now in source. Mechanical instance construction/fill/device identity and liquid velocity redraw are now migrated in source. Connect actual shader compilation modes to motion/AO owners; motion coverage is still unpublished and provider motion validity remains unavailable.
2. Finish remaining built-in content renderers, shader rewrite/override delivery and window/controller consumers. Assemble a complete graphics-scene group; existing scene subsets do not satisfy that requirement. The session service owner is concrete in source; assemble and register the startup plan once all mandatory groups are complete.
3. Finish settings UI/world attachment, renamed native bridge/runtime delivery and the player package for the existing game installation. Preserve provider algorithms, SDK versions, target ownership and resource layouts.
4. After implementation is complete, close the revised B0 live gate and accept SDL first-window/menu/world rendering, temporal inputs, vendor presentation, controls and shutdown in bounded validation turns. The older B0 payload still occupies the official installation; automatic approval review previously rejected its removal, and the revised-resolver batch did not deploy there.
5. Complete normal-shortcut install/update/removal acceptance, then Linux and targeted third-party compatibility. New test writing and expansion remain deferred until integration is finished.

The roadmap remains the full requested port. The original renderer is the working baseline; narrow preflights and passing tests certify only the boundaries they exercise.

Shader namespace repair (2026-09-30): the wrapper now explicitly qualifies
`Vintagestory.Client.ScreenManager` in source. The repaired official selection
group awaits a bounded game-test/profile-build validation batch.
The [next batch](validation-g1-shader-routing-2026-09-30-03.md) passed 30 game
cases and a clean profile build, including original/incoming selection anchors
and actual Use/Stop transpiler installation/removal. Successful shaders and
the remaining raw GL uniform/sampler/UBO/disposal/draw routes stay open.
