# DLSS reported-present gain — 2026-10-04

DIRECT validation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
One bounded Game/Mod build → fresh stage → isolated DLSS child ran once.
Both builds passed, including controller entry and protocol checks. Game retained
one CS8600 harness warning; Mod was clean. A shell payload parse error occurred
before execution; corrected construction launched the only batch. No source
fix, second batch, tests, native builds or deployment ran.

Artifacts: [dlss-gain-20261004-004502](../../artifacts/validation/dlss-gain-20261004-004502).
Saved run-batch.ps1 records inputs: current managed outputs, October-4 matching
FSR3/Streamline bridges, October-1 other native/notices/full shaders, isolated
foggy village story snapshot, hidden/unfocused window and async pipelines.
Session de37d1d858d2424cae1b72f6db788f7c; owned child PID 104352.
Scenario SHA256: `67d1e6bca9f742e1d5907aa0a634d210befea13215be31601f9fcb164501df3d`.
Game DLL SHA256: `5BEAAA46838EA56E9C584E124BE759BA217BEEDF18CE873D3022D45F79E0656E`.
Mod DLL SHA256: `6320C2EEB6260F3D6682147123A199E8F4A4BDBE25CF2F603A3A8F93D19B4446`.

## FG-gain assertion FAIL

Checkpoint tick 20/frame 967: realPresents=967, sdkReportedPresents=18.
Final tick 200/frame 1147: realPresents=1147, SDK presents=198. Both deltas equal
180. The assertion required at least 320 SDK presents over 180 rendered frames,
below the expected 360 for 2×; it failed. Preparation/effective-SR/real-progress
assertions passed. Six actions and one paired capture executed. Verifier/scenario
success=false. Child exited 0 without timeout; normal world/server/NGX close.

Final sample: motion ready, SR evaluated, FG inputs prepared, one generated frame
configured, SDK query/status=0, 201 successful upscales and 199 prepared frames.
No checked token/Reflex failure surfaced. Log reports 120 SDK presents over 120
rendered frames. Required added-present gain is absent in this hidden child,
despite preparation/configuration. This does not establish whether visible-window
behavior differs or identify the SDK/proxy cause. The speculative token/Reflex
failure is not supported by this run. Keep the strict failure; do not inflate
telemetry or weaken the assertion. No Options/controller UI or injected failure
ran. Installed game/settings/save unchanged; fresh stage is a development artifact.
