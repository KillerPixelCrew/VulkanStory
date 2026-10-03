# Current DLSS → XeSS runtime — 2026-10-04

DIRECT validation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
One bounded Game/Mod Release build → fresh stage → isolated child batch ran.
Both builds passed; Game retained one CS8600 harness warning, Mod was clean.
No implementation changes, native rebuilds, tests, second batch or deployment ran.

Artifacts: [dlss-xess-20261004-003244](../artifacts/validation/dlss-xess-20261004-003244/).
Saved run-batch.ps1 records inputs. Fresh stage uses current managed outputs,
October-4 matching FSR3/Streamline bridges and October-1 delivery-refresh other
native libraries/notices/full shaders. World: isolated foggy village story snapshot.
Async pipelines enabled. Initial DLSS SR/FG; switch to XeSS SR/FG at tick 180;
capture/assert at ticks 160 and 320. Retained input-preparation assertions.

Session b82d0758b79e432299a538838431b187, owned child PID 10584. Scenario SHA256:
`9e21cec00129bcf3befcf8f6b6e4997c858a933738cb4803a7149dd5f76c0eaf`.
Game DLL: `46D83396852183BD27458DB7E3D5A6FDD237EE28D6C1A0985B6D5828FC5C2EA2`.
Mod DLL: `C6F2F9FA0765DBB8769EBAE149DEAD60AC2884E1FD0053FB7B6857882FC53790`.

## Scoped routing/input PASS; actual FG output still open

Child/batch exited 0 without timeout. Verifier pass=true, 21/21 actions completed,
2/2 paired captures, hidden=true, focused=false, worldReady/stagedModLoaded=true.
Both PNGs were inspected and show world/sky/hand/HUD. No black output appeared.
Normal world/server close and NGX shutdown completed.

Last DLSS sample at frame 1136: motion ready, SR evaluated, FG inputs prepared,
161 successful upscales and 159 prepared frames; SDK state query/status=0,
maximum generated=1 and 158 reported presents. Last XeSS sample at frame 1296:
motion ready, SR evaluated and FG prepared; mixed-provider cumulative totals
320 successful upscales and 317 prepared frames, SDK reported presents=452.
The log explicitly records the first successful XeSS quality evaluation at
1506×899 → 2560×1528. Do not treat cumulative totals as XeSS-only counts.

DLSS's captured HUD reports about 27 real FPS and 27 FG-output FPS; XeSS about
28 real and 57 reported output FPS. These are telemetry observations, not display
pacing measurements. The local sl_dlss_g.h describes numFramesActuallyPresented
as presents since the previous state query. DLSS's near-real reported cadence
does not establish the requested interpolation gain and needs investigation.
Scene PNGs are pre-FG captures and cannot certify generated-frame quality.
DLSS sky quality, moving content, FSR4/other hardware, missing-runtime/failure
paths and the earlier off→FSR3 black-frame failure remain open.

The Options callback repair compiled in both assemblies and startup routing ran;
no Options composer was opened or interacted with. UI-01 acceptance remains open.
Fresh stage is a development artifact. Installed game/settings/original save unchanged.
