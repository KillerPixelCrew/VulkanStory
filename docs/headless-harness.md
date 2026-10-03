# Headless renderer harness

Ported and compiled on 2026-10-01; bounded captures and staged-mod lifecycle are recorded in validation-headless-modpath-2026-10-01-01.md. Full feature/visual acceptance remains open. This replaces interactive
renderer checks as the default development path. It does not deploy anything to
or close the user's game installation.

## What it preserves

- A real SDL/Vulkan surface and swapchain in a permanently hidden window, including
  real provider presentation. This is not the surface-free backend preflight.
- Explicit frame lists or count/stride/first cadence, fixed simulation timestep,
  local/server command scripts, PPM frames, PPM/PGM/PFM attachment and AO outputs.
- Render-thread completion/timeout requests, with normal device drain and game save.
- Existing SSIM comparison script (numpy required for comparison only).

The frame planner, command parser and image writers are migrated from
`optimum-render-device.cs`; session tick/commands/capture are migrated from
ClientPlatformWindows's existing harness at baseline
386e0d05386d0b228b439d09aeca851428f7bbf3. No modified API/game assemblies, Optimum
launcher, copied game binaries or installed-setting edits are required.

## Run

First build Bootstrap and Game/backend/SDL and stage a complete runtime package
using the existing scripts. Do this in a validation turn. Do not deploy that stage
into the official installation for a harness run.

```powershell
./scripts/dev/headless-capture.ps1 `
  -PackageDirectory 'D:\Coding\VulkanStory-Rewrite\artifacts\headless-stage' `
  -OutputDirectory 'D:\Coding\VulkanStory-Rewrite\artifacts\headless-run-001' `
  -World 'foggy village story' -Upscaler dlss -FrameGeneration dlss `
  -First 180 -Count 3 -Stride 30 -ParityDump
```

Supply an existing staged package and a fresh output directory. `-Frames '180,240'`
selects explicit frames. `-Commands <file> -CommandFrame 30` runs the retained
camera/weather/chat script. `-FixedDt 0` leaves simulation on wall time. Attachment
capture can be scheduled later than the last frame with `-ParityFrame`; shutdown
waits for both artifacts. `-AoOutputs` adds working AO/edges/depth/output images.
Default lifetime is bounded by `-TimeoutSeconds 300`, with a render-thread timeout
and a final launcher timeout acting only on its own child handle.

Requires Windows x64, the matching official .NET 10 game and Python with standard
sqlite3 support. The launcher uses `dotnet exec` on the existing official client
with its original runtimeconfig/deps, and a process-local startup hook from the
staged package. Bootstrap permits that path only for explicit headless mode and
the expected client entry assembly, then still verifies official game hashes.
Bootstrap errors fail closed instead of falling back to a visible client.

## Isolation and avoiding interruptions

The launcher takes a SQLite backup of the user's existing world, including committed
WAL content, into `<run>/data/Saves`. It reads the source database without writing
to it, so the user can keep their original world open. No raw live database-file
copy and no development superflat. Simulated/world changes stay in the snapshot.
The snapshot reads the last committed database state, not unsaved in-memory edits.

Client settings are copied to the isolated data directory; sound/music, focus
pause and VSync are disabled there. Controller/touch input is disabled in the
harness settings. Output/config/cache/log files use the run directory. SDL guards
prevent show/raise/fullscreen/restore/minimize, mouse capture/warp, cursor selection
and IME activation. The harness cannot open its crash-reporter GUI. Its child runs
BelowNormal and frames are capped to roughly 30 per second by wall-time throttling.
GPU/memory use is real and shared; lower CPU priority is not GPU isolation.

The headless-only collector excludes the standard installed Mods/vulkanstory folder before assembly discovery. The runner always copies the staged ModSystem into isolated Mods; mod development no longer requires replacing or matching the installed DLL. Other mods follow normal discovery. Isolated module loading is verified. The installed native apphost shim is bypassed; proxy/MFG-enabler coexistence remains
its own live acceptance gate. The harness currently selects Vulkan; an unmodified
OpenGL reference capture is not claimed by this port.

## Evidence and remaining limits

Frames use the original bottom-up Netpbm row convention for comparison. Attachment
names use official framebuffer enum names or SlotN for extra targets; migrated AO
outputs use slots 40–43. `frames/headless-result.json` reports artifact completion,
not visual correctness. Nonzero child exit, missing result, failed counts or timeout
fail the launcher. Periodic provider/status records go to `<run>/status`, with
successful SR, prepared FG and SDK-reported presents kept distinct.

No new tests were written. Builds, bounded hidden captures, snapshots, scripted
settings, attachment outputs and provider execution are recorded in the validation
documents. Concurrent user-session performance and full visual parity remain open.
Use later scheduled PNGs to inspect world rendering after chunk streaming.

## First runtime checkpoint

The first completed run is recorded in [validation-headless-2026-10-01-03.md](validation-headless-2026-10-01-03.md): clean build, isolated snapshot, three frames and attachment/AO files, automatic shutdown. World source pixels are visible. SDK constants/release errors and remaining command/concurrency/visual gates stay open. No installed deployment occurred.

## Normal asynchronous pipeline mode

Pass -AsyncPipelines to keep ordinary scene pipeline creation asynchronous even during captures. Required final/luma/UI draws still wait for their own pipelines. The default retains deterministic synchronous capture behavior. Bounded asynchronous runs have completed successfully.

## Scheduled PNG sidecars

Each scheduled frame also writes a top-down PNG from the same BGRA readback. Original PPM files and SSIM conventions remain unchanged. Inspect those later PNGs for world completeness; the one-off status screenshot may precede completed chunk streaming.

## Isolated module discovery checkpoint

The launcher explicitly adds run/data/Mods. The latest checkpoint confirms the loaded DLL is the isolated staged copy and the ordinary world-ready callback arrives. Completion requires both. No installed module is replaced.

## Scripted settings checkpoint

[The latest run](validation-headless-commands-2026-10-01-02.md) dispatched sequential
`.vulkanstory set` commands at world frame 90, switching FSR3 SR/FG to XeSS balanced
SR/FG. All changes persisted and executed; three later frame captures completed.
One Streamline options warning during the transition remains unresolved.

## Disablement and startup-failure isolation

The runner enables only vulkanstory/vulkanstoryinput in its copied disabledMods
list; all other mod choices and the original user settings remain unchanged.
This keeps a renderer check usable when the user has disabled the normal mod.
An unexpected disabled renderer during a headless launch now fails instead of
returning to original visible OpenGL window creation. Window preparation failures
also propagate in headless mode instead of entering the normal visible fallback.
These source corrections are unbuilt/unrun. The preceding successful capture
does not exercise a disabled or failed-startup branch.
