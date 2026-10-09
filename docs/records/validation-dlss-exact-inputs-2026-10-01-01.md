# Exact-frame DLSS input capture — 2026-10-01

One bounded validation batch: Game build, fresh stage and isolated hidden world
capture. No tests, implementation edits, deployment or repeated runtime batch.
Artifacts: artifacts/validation/dlss-exact-inputs-20261001-215401.

Build passed with zero warnings/errors, including the earlier NGX rendered-delta
input and exact-frame metadata source changes. Staging reused the matching native
bundle from provider-handoff-20261001-210651 and the retained compiled shader corpus.

The foggy village story snapshot loaded and wrote one PNG/PPM at world frame 240
plus raw attachments and frame-inputs.json. Result success=true, worldReady=true,
hidden=true, focused=false, stagedModLoaded=true at its isolated Mod path.
World/server exit and NGX shutdown completed normally; process exited.

Capture metadata: device, temporal and sky producer IDs all 767; render1707x1019,
display2560x1528; jitter(-.4375,.3888889), reset=false, delta16.666698ms,
ditherSeed241, gameWidth2560. Sky draw submitted=true, previousWorldCaptured=true,
motion slot4. Native matrix uniform offsets are16 and80 in the program record.
These are exact CPU producer inputs at the capture boundary, not GPU record bytes
or a guarantee that the draw covered any particular pixel.

DLSS quality SR succeeded; FG off, zero prepared/SDK-present counters. Main log
has no error/exception event; ordinary rewriter fallback warnings remain for
sleepoverlay, machinegear, galaxy and rift (six startup entries including repeats).
Terrain, vegetation and HUD are visible. The light-sky horizontal pattern remains
visible in the final capture: adding NGX rendered delta did not remove it in this
run. No visual parity or performance acceptance is claimed (dump I/O affects FPS).

Preserve this exact-frame input/attachment set for source diagnosis; no additional
capture was launched. Sky motion per-pixel coverage and SR artifact cause remain
open, as do broad renderer/provider/SDL and installation acceptance requirements.