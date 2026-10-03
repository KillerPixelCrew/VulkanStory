# DLSS-only input capture — 2026-10-01

One bounded Game build -> fresh stage -> isolated hidden capture with retained
attachment dump. Artifacts:
`artifacts/validation/dlss-input-capture-20261001-212255/`.
Game/full dependencies passed with zero warnings/errors. No tests, installed
deployment, source repairs or second batch.

Used a snapshot of foggy village story, DLSS quality SR, FG off, asynchronous
ordinary pipelines, one scheduled frame and parity attachment dump at world
frame 240. Result reports success, hidden/unfocused, exact isolated Mod DLL,
worldReady=true and one requested/written PNG/PPM pair. Normal world/server close
and NGX shutdown succeeded. Main client/server logs contain no error/exception;
three known unsupported Streamline hook warnings remain.

The new final diagnostic sample reports render 1707x1019, display 2560x1528,
jitter (-0.4375, 0.3888889), temporalReset=false, CPU gameDitherSeed=241 and
gameFrameWidth=2560. DLSS SR evaluated successfully; FG remained off with zero
prepared/generated-provider counters. These CPU fields are not GPU block readback.

The original-resolution final PNG shows the horizontal sky pattern with FG off.
That rules out active frame generation as a necessary cause in this capture; it
does not yet distinguish source shading from SR/post processing. There are 51
attachment files (680,292,380 bytes), including Primary color0 and Slot22 SR output
float images. Preserve these existing artifacts for analysis before another run.
No raw-input visual/numerical comparison was completed in this batch.

The frame HUD reports about 29 real FPS before dump; final sampled FPS drops during
attachment readback/file I/O. This diagnostic run is not a performance benchmark.
No rendering algorithm was changed and no broad visual parity is claimed. Sky
localization, controller UI/glyphs, AVI output and full feature acceptance remain
open. User installation/settings/original save were not changed or controlled.
