# XeSS SR/FG isolated world checkpoint — 2026-10-01

One bounded Bootstrap/Game build -> fresh stage -> isolated XeSS capture batch,
run once. No tests, native rebuild, installed deployment, source fixes or second
run. Artifacts: `artifacts/validation/headless-xess-20261001-182957/`.

Both builds and staging passed. Reused matching native bundle from
headless-20261001-161108. PID 11336 loaded the isolated SQLite snapshot of foggy
village story, captured all three frames (180/210/240) and attachment/AO outputs,
and shut down automatically. Result: success, hidden=true, focused=false.
Bootstrap recorded device drain before SDL teardown. No new render/SDK teardown
error; the known Streamline hook-map warnings persist. User game/settings untouched.

## Provider and image evidence

XeSS quality first successful upscale: 1506x899 -> 2560x1528. Final periodic sample
records 233 successful SR frames, 231 prepared FG frames, 459 SDK-reported presents
across 230 SDK reports. Current camera/motion valid. Sample FPS is 25 real/49 SDK
output. These SDK counts show additional outputs above prepared real frames in
this bounded hidden run; no generated-frame image or pacing certification follows.

Inspected source-frame PNG shows world terrain/vegetation/sky and first-person/HUD
output. Some distant terrain is absent compared with earlier DLSS captures; snapshot
state and streaming/capture timing differ, so a parity conclusion cannot be drawn
from this unsynchronized image. Visual completeness remains open. The scheduled
PPMs and attachment files are retained for stronger diagnosis/comparison.

All source changes compile, but this fixed-quality run does not exercise the
new input-layout replacement branch. Quality/resize transitions and command scripts
remain unverified. Other provider/platform, input/settings, DLSS hidden interpolation,
SDK hooks/coexistence and release acceptance gates stay open. No source repair or
rerun occurred in this validation turn.
