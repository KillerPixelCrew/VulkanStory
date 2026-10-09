# DLSS execution and first source-frame capture — 2026-10-01

One bounded validation batch, run once. No tests, native/shader rebuilds, archives,
source repairs or second launch. Artifacts: `artifacts/validation/runtime-dlss-20261001-153708/`.

Game/backend build passed, zero warnings/errors; stage/deploy passed. Existing
foggy village story save/WAL/SHM were backed up. Temporary renderer config selected
DLSS quality SR, DLSS FG 2x, Streamline and FPS display. The original absent config
state was restored by moving the used config into settings-used.json. Diagnostic
environment was restored. No development superflat was used.

PID 38796 ran through the full 90-second bound. Requested close exited within
10 seconds, code 0, with device-drain/SDL teardown marker and world save. No new
render exception or crash reporter. Three Streamline SDK hook warnings remain.

## Provider evidence

First successful DLSS quality evaluation logged at 15:37:50: 1707x1067 -> 2560x1600.
The final periodic sample at frame 3049 recorded 1712 successful upscale frames,
1712 prepared DLSS-G frames, current camera/motion validity, and 3168 cumulative
SDK-reported presents. These are actual successful evaluation/preparation and SDK
state observations, not requested-multiplier estimates. SDK presents include the
SDK's reported output; they are not a separate count of generated-only images.
No visual/pacing quality certification is implied.

## Captured pixels and unresolved failure

frames/world-rendered-38796.png is a 2560x1600 composed source-frame capture,
frame 1195. Inspection shows a black scene with visible HUD/minimap/chat/FPS.
It is not accepted as correct world rendering. Capture occurred at 15:37:47,
before the first successful SR evaluation at 15:37:50. Therefore this early image
cannot establish the visual result of the later successful provider frames or
prove whether the black scene persists. No later source image/scanout/generated
frame was captured in this single batch.

The current capture gate counts attached-world frames after LevelFinalize, even
before a complete current world/provider frame. Next implementation should gate
capture on actual current scene completion (and successful SR when selected),
then inspect the scene/composition routing using existing source. Do not treat
HUD pixels, runtime survival or SDK counters as full rendering parity.

Installed payload now includes diagnostic source. Provider execution has concrete
bounded evidence; visuals, input/settings, other providers/platforms, resize,
coexistence, full parity and current release archives remain open. No fix or rerun
in this validation turn.
