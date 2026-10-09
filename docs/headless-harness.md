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
| `-Commands`, `-CommandFrame` | —, 30 | Command script run once at that frame |
| `-Scenario` | — | Scenario JSON (see below) |
| `-ParityDump`, `-ParityFrame` | off, `-First` | Dump every framebuffer attachment at one frame |
| `-AoOutputs` | off | Also dump AO working/edges/depth/output |
| `-AsyncPipelines` | off | Asynchronous pipeline creation; the default compiles synchronously so captures are deterministic |
| `-CompanionMode` | `present` | `absent` leaves out the input companion |
| `-Visible`, `-KeepOpen` | off | Show a focused window; `-KeepOpen` keeps the client running after capture |
| `-MainMenuOptions` | off | Open the Options dialog from the main menu without loading a world (`-OptionsPage`, `-MainOptionsAction open\|save\|cancel`) |
| `-TimeoutSeconds` | 300 | After this, the launcher kills its own child process |
| `-PreflightOnly` | off | Validate inputs and print JSON without starting a run |

**Command scripts:** lines starting with `.` go to the client command dispatcher
(for example `.vulkanstory set Upscaler xess`). Other lines are sent as chat, so
server `/` commands work too. Lines starting with `#` are comments.

**Scenarios** (`scripts/dev/scenarios/schema-v1.json`): up to 128 actions, each with
an `id`, a `tick` and a `kind`. The kinds are `settings` (apply renderer settings),
`capture` (a named frame, optionally with attachments), `checkpoint`, and `assert`
(check a runtime field such as `hidden`, `focused`, `worldReady`,
`effectiveUpscaler` or `realPresents`). `native-baseline.v1.json` is the reference
example.

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
- **Mods:** the staged `Mods/vulkanstory` and `Mods/vulkanstoryinput` are copied
  into `<run>/data/Mods` and added with `--addModPath`. The game's installed copies
  are removed from discovery. Other installed mods load normally.
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

## Outputs

| Path | Contents |
| --- | --- |
| `frames/frame-NNNNNN.ppm`, `.png` | Scheduled frames. PPM is bottom-up (GL row order), PNG is top-down |
| `frames/headless-result.json` | Completion record; scenario runs add `scenario-result.json` and `scenario-events.jsonl` |
| `attachments/` | `<slot>-<name>-<color<i>\|depth>-<format>.{ppm,pgm,pfm}`. Names come from `EnumFrameBuffer`, `SlotN` for extra targets, slots 40–43 for AO |
| `status/runtime-<pid>.jsonl` | Provider counters: SR frames, prepared FG frames, SDK-reported presents |
| `bootstrap.jsonl`, `stdout.log`, `stderr.log` | Bootstrap events and child output |
| `data/` | Isolated data path |

`verify-headless-result.ps1` runs after the child exits. It checks the exit code,
timeout, frame counts, that the staged mod copy was the one loaded, world readiness
and the window state. If any check fails, the launcher fails. Passing means the
artifacts are complete, not that the images are correct, so inspect the PNGs. Later
frames show the world after chunk streaming has finished.

`python scripts/dev/ssim.py <dir-a> <dir-b> [--threshold 0.98] [--allowlist f.md]`
compares two attachment dumps by SSIM and mean absolute difference (requires
numpy).
