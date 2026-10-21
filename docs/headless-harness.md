# Headless renderer harness

`scripts/dev/headless-capture.ps1` runs the official client with the VulkanStory
runtime from a staged package. It uses an isolated data directory and a snapshot of
an existing world, captures frames and attachments, and verifies the result.
Rendering uses a real SDL window, Vulkan swapchain and provider presentation. The
user's installation, settings and any running game are not touched.

Requirements: Windows x64, the official 1.22.7 installation (default
`%APPDATA%\Vintagestory`), `dotnet.exe` (.NET 10) on `PATH`, Python with `sqlite3`,
and a staged package from `scripts/stage-runtime.ps1`
([building a package](bootstrap-and-installation.md#building-a-package)).

## Running

```powershell
./scripts/dev/headless-capture.ps1 -PackageDirectory artifacts\headless-stage `
  -OutputDirectory artifacts\headless-run-001 -Upscaler dlss -First 180 -Count 3 -Stride 30 -ParityDump
```

| Parameter | Default | Meaning |
| --- | --- | --- |
| `-OutputDirectory` | required | Run directory; it must not exist yet |
| `-World` | `foggy village story` | Save name under `<SourceDataDirectory>\Saves` |
| `-First` / `-Count` / `-Stride` | 180 / 3 / 30 | Capture schedule in world frames; `-Frames '180,240'` gives an explicit list instead |
| `-FixedDt` | 0.0166667 | Simulation step in seconds; `0` uses wall time |
| `-Upscaler` | `off` | `dlss`, `fsr3`, `fsr4`, `xess` |
| `-FrameGeneration` | `off` | `dlss`, `fsr3`, `xess` |
| `-Commands`, `-CommandFrame` | —, 30 | Unscheduled lines run at that frame; `@<frame>` lines run at their declared world frame |
| `-Scenario` | — | Scenario JSON (see below) |
| `-ParityDump`, `-ParityFrame` | off, `-First` | Dump every framebuffer attachment at one frame |
| `-AoOutputs` | off | Also dump AO working/edges/depth/output |
| `-AsyncPipelines` | off | Asynchronous pipeline creation; the default compiles synchronously so captures are deterministic |
| `-CompanionMode` | `present` | `absent` leaves out the input companion |
| `-Visible`, `-KeepOpen` | off | Show a focused window; `-KeepOpen` keeps the client running after capture |
| `-MainMenuOptions` | off | Open the Options dialog from the main menu without loading a world (`-OptionsPage`, `-MainOptionsAction open\|save\|cancel`) |
| `-SettingsOverrides` | — | JSON file of renderer settings merged into the isolated `vulkanstory.json` before launch, for settings read at world load (for example `WorldgenWorkers`). Renderer, upscaler, frame-generation and input switches stay under their own parameters |
| `-MaxWorldgenThreads` | 0 | Above 0, copies the source `servermagicnumbers.json` with the game's own `MaxWorldgenThreads` set, as the vanilla multi-threaded worldgen reference |
| `-TimeoutSeconds` | 300 | After this, the launcher kills its own child process |
| `-PreflightOnly` | off | Validate inputs and print JSON without starting a run |

**Command scripts:** lines starting with `.` go to the client command dispatcher
(for example `.vulkanstory set Upscaler xess`). Other lines are sent as chat, so
server `/` commands work too. Lines starting with `#` are comments. A line written
as `@<frame> <command>` runs at that world frame instead of `-CommandFrame`. Harness
directives reach paths no stock command does. Through the integrated server,
`!sand <dx> <dy> <dz>` places sand at that offset from the player and wakes it so it
falls, and `!drop <dx> <dz> <count>` spawns dropped flint on the surface at that offset.
`!occluder <distance>` builds a granite wall across the camera's view at that
distance, with four traders behind it and two in front of it (an entity occlusion
scene). On the client, `!fallingsource` makes every later falling block act as a mod block
that supplies its own falling mesh (`IFallingBlockMeshSource`), which no stock block
does.

**Scenarios** (`scripts/dev/scenarios/schema-v1.json`): up to 128 actions, each with
an `id`, a `tick` and a `kind`. The kinds are `settings` (apply renderer settings),
`options` (dialog operations), `camera` (world yaw/mode), `resize` (drawable dimensions),
`world-cycle` (leave/re-enter the save), `capture` (a named frame, optionally with
attachments), `checkpoint`, and `assert`
(check a runtime field such as `hidden`, `focused`, `worldReady`,
`effectiveUpscaler` or `realPresents`). `native-baseline.v1.json` is the reference
example.

Options actions use `operation` (`open`, `toggle-taa`, `save`, `cancel`, `back`,
`resume`, `close`) and optionally `page` when opening. Camera actions use
`yawDeltaDegrees` in -180..180 and optionally `mode` (`FirstPerson`, `ThirdPerson`,
`Overhead`); resize actions use `width`/`height` in 128..8192.

`world-cycle` accepts only `id`, `tick` and `kind`, requires world context, and
must be the sole action at its tick. It exits through the official menu path,
waits for server shutdown and detached world state, then reconnects the same
isolated save in the existing process. Scheduled ticks/captures pause during the
transition; the global timeout stays active. Completion requires a fresh client
on the same platform before the remaining actions resume.
The launcher validates inputs before starting a child. Controller/touch enable
flags require the deferred child-owned input profile and are refused when it is absent.

## Isolation

- **World:** `snapshot-world.py` copies the save with SQLite's backup API, opening
  the source read-only. The copy includes committed WAL content but not unsaved
  in-memory changes. The world can stay open in the game. Use the foggy village
  story save for world loading and representative rendering.
- **Settings:** a copy of `clientsettings.json` with all six audio levels at 0
  (recorded in `audio-mute.json`), VSync, fullscreen and pause-on-focus-loss off,
  and only `vulkanstory`/`vulkanstoryinput` removed from `disabledMods`. Renderer
  settings go to the isolated `ModConfig/vulkanstory.json`. Controller and touch
  input are off.
- **Mods:** the source user `Mods` and `ModConfig` contents are copied into the
  isolated data directory, then the staged `vulkanstory` and `vulkanstoryinput`
  replace their corresponding folders. Copied user mod paths point at the isolated
  folder; disabled choices are preserved. The game's installed VulkanStory folders
  are removed from discovery. Base game modules still load from the installation.
- **Process:** `dotnet exec` runs the official `Vintagestory.dll` with its own
  runtimeconfig and deps files, `--dataPath <run>/data`, and `DOTNET_STARTUP_HOOKS`
  set to the staged bootstrap. The installed `hostfxr.dll` proxy is not used. The
  child runs at BelowNormal priority but shares the GPU with everything else.
- **No fallback:** with `VULKANSTORY_HEADLESS=1`, a bootstrap failure, a disabled
  renderer or a window failure ends the run with an error. The client never falls
  back to a visible OpenGL window or the crash-reporter GUI.

## Window modes

- **Hidden (default):** the window stays hidden. Show, raise, fullscreen,
  minimize/restore, mouse capture and warp, cursor changes and IME are suppressed.
  Frames are throttled to about 30 fps.
- **`-Visible`:** the window is shown and focused, with no throttling. DLSS frame
  generation presents generated frames only to a visible, focused window, so run
  `-FrameGeneration dlss` checks in this mode.

## Simulated stereo

`-Stereo` runs the two-eye render path against a simulated XR runtime with fixed
eye poses and asymmetric FOVs. No headset is needed and nothing is displayed on a
headset; the desktop mirror window still presents and stays hidden by default.
`-StereoEyeSize '1440x1600'` selects each eye's dimensions (128..8192 per axis);
omit it or pass an empty string for the renderer default. These options set
`VULKANSTORY_HEADLESS_STEREO` and `VULKANSTORY_HEADLESS_STEREO_SIZE`. Stereo works
with world scenarios, command scripts and settings overrides, and cannot combine
with `-MainMenuOptions`.

World frames, timed commands, scenario ticks and completion advance once per
stereo frame. Captured PNG/PPM files contain the window-sized desktop mirror;
stats and scenario `renderWidth`/`renderHeight` describe the eye render targets,
while `displayWidth`/`displayHeight` describe the mirror window. Scene draw counters
include both eye passes. Stereo uses raster rendering with desktop temporal
providers and frame generation inactive. The game's `MaxFps`/`FrameSleep` frame
cap is not applied while XR is running; the hidden harness's 33ms throttle remains.

## Outputs

| Path | Contents |
| --- | --- |
| `frames/frame-NNNNNN.ppm`, `.png` | Scheduled frames. PPM is bottom-up (GL row order), PNG is top-down |
| `frames/headless-result.json` | Completion record; scenario runs add `scenario-result.json` and `scenario-events.jsonl` |
| `frames/scenario-checkpoints.jsonl` | Named scenario checkpoints, with observed fields and their frame/session provenance |
| `attachments/` | `<slot>-<name>-<color<i>\|depth>-<format>.{ppm,pgm,pfm}`. Names come from `EnumFrameBuffer`, `SlotN` for extra targets, slots 40–43 for AO |
| `status/runtime-<pid>.jsonl` | Provider counters (SR frames, prepared FG frames, SDK-reported presents), game frame work-time percentiles (`frameWorkMs`) and performance-port counters (`performance`) |
| `bootstrap.jsonl`, `stdout.log`, `stderr.log` | Bootstrap events and child output |
| `data/` | Isolated data path |

After the child stops and result checks finish (including failures), cleanup removes only `data/Saves`, `data/Backups` and `data/BackupSaves`; use `-KeepSave` to retain them for save-related diagnostics.

`verify-headless-result.ps1` runs after the child exits. It checks the exit code,
timeout, frame counts, that the staged mod copy was the one loaded, world readiness
and the window state. If any check fails, the launcher fails. Passing means the
artifacts are complete, not that the images are correct, so inspect the PNGs. Later
frames show the world after chunk streaming has finished.

`python scripts/dev/ssim.py <dir-a> <dir-b> [--threshold 0.98] [--allowlist f.md]`
compares two attachment dumps by SSIM and mean absolute difference (requires
numpy).

## Resident scene observations

`ResidentTerrain` and `ResidentEntities` default on. Scenario `settings` actions
can toggle either live to compare against the stock per-draw compat lane.
`NativeShaders=false` selects translated/compat rendering at launch; refused
content and foreign-patched policies also stay in that lane. Resident terrain
requires `shaderFloat64`; an unavailable feature selects compat terrain rather
than ending the run.

Scenario checkpoints and assertions expose cumulative renderer fields:

| Field | Meaning |
| --- | --- |
| `sceneDrawCommandsTerrain`, `sceneDrawCommandsEntity`, `sceneDrawCommandsParticle` | Vulkan commands recorded in the named scene category, including its compat draws |
| `sceneDrawCommandsCompat` | Commands outside those three categories, not all content refused by resident admission |
| `entityResidentRowsCaptured` | Successful row captures, including repeated captures of the same entity/submesh |
| `entityResidentCaptureRefusals` | Candidate mesh draws that continued without completing resident capture |
| `terrainParityChecks`, `terrainParityMismatches` | Completed stock-boundary visibility comparisons and disagreements |

Terrain parity is enabled by `VULKANSTORY_TERRAIN_PARITY=1`, or automatically
when the scenario contains an assertion on either parity field. The adapter
evaluates the game's `IsVisible` on private copies of the same frozen inputs and
compares GPU decisions at the stock-visibility and applicable historical-depth
boundaries. Comparisons are collected only when the owning frame slot completes.
A zero mismatch count with zero checks does not establish parity.

Scene draw counters count commands, not indirect entries, instances or visible
objects. A populated resident group can record a command whose GPU culling keeps
no entries. Neither these counts nor the parity fields measure GPU execution time.
See [performance](performance.md#resident-scene-submission) for the submission
and fallback behavior.

## Performance captures

Command scripts can use the packaged collector without adding a separate game launcher:

```text
@60 .vulkanstory profile start 10
@180 .vulkanstory set CloudCoordinateReuse false
@300 .vulkanstory set CloudCoordinateReuse true
@800 .vulkanstory profile status
```

Keep the session alive beyond the requested capture duration (for example, capture
world frame 900). Reports go to `%LOCALAPPDATA%\VulkanStory\Performance`, not the
isolated run directory. The collector writes `capture.nettrace`, `report.json`,
`report.html`, `capture.speedscope.json`, process metrics and target identity.

Hidden frames deliberately wait up to a 33ms cadence. Screenshots, injected chat
commands and settings changes can also create frame-time spikes. Warm asset loading
and chunk streaming before comparing steady-state performance, use equivalent
camera/workload conditions, and distinguish those harness costs from game work.
Nested timing spans overlap; allocation ticks are estimates. Monitor events report
elapsed waits, while external/native stack samples are not CPU milliseconds.
No GPU execution timing is collected.
