# FSR3 to DLSS provider handoff — 2026-10-01

One bounded Game build -> fresh matching stage -> isolated hidden world batch.
Artifacts: `artifacts/validation/provider-handoff-20261001-210651/`.
Game/full dependencies passed with zero warnings/errors, confirming the unused
callback source correction. Fresh FSR3/Streamline bridges came from the preceding
provider-lifetime compile batch. No tests, installed deployment or second batch.

The harness used a snapshot of foggy village story, asynchronous ordinary
pipelines, initial FSR3 SR/FG, then command-frame 90 settings to DLSS SR/FG.
FSR3 disable/drain and swapchain ownership handoff completed without error.
DLSS quality first evaluated 1707x1019 to 2560x1528. DLSS-G configured one generated
frame within the SDK limit. Three PNG/PPM pairs at world frames 240/270/300 were
written, with success=true, hidden=true, focused=false, worldReady=true and exact
isolated staged Mod DLL location.

Final diagnostics report effective DLSS SR/FG, current camera/motion, 300 aggregate
successful SR frames and 297 aggregate prepared FG frames. DLSS query result/status
are both zero, maximum generated=1, minimum dimension=100, VSync support=0 and
dynamic MFG support=0. DLSS-reported presents are 207; the runtime log reports
120 presents over 120 DLSS rendered frames. Hidden-window interpolation remains
unproved. Aggregate mixed-provider counters are 835 real/919 SDK-reported presents;
these include preceding FSR3 output and must not be read as DLSS output counts.

The final image was inspected: terrain, vegetation, sky, hand, map and HUD visible.
Faint horizontal patterning is visible in light sky regions; visual parity is not
claimed from this capture and that observation needs source/capture investigation.
The immediately issued status command displays the preceding FSR3 frame, as
designed for queued settings. Normal world/server close and NGX shutdown succeeded.
No client/server main-log error/exception; known three unsupported Streamline hook
warnings remain. No disable/drain failure was injected, so failure retention itself
has compile/source evidence only. Controller UI/glyph/AVI and full port acceptance
remain open. Original user game, settings and save were not changed or controlled.
