# Motion variant capture — 2026-10-01

One bounded validation batch: Game build -> fresh stage -> hidden isolated snapshot
of foggy village story -> attachment/metadata and final PNG inspection. No tests,
source repairs, installed deployment or second runtime batch.
Artifacts: artifacts/validation/motion-variant-20261001-220117.

Game and dependencies compiled with zero warnings/errors. Stage succeeded using
the retained native bundle/shader corpus. Capture result success=true, hidden=true,
focused=false, worldReady=true, exact isolated ordinary Mod loaded; one PNG/PPM
at world frame240. Game/server closed and NGX shutdown succeeded normally.
Client/server main logs contain no error/exception event. Ordinary shader rewriter
fallback warnings remain; those are not native motion output errors.

Exact metadata agrees at device/temporal/sky frame762. Sky submitted=true,
previousWorldCaptured=true, motionAttachment4, writtenFragmentOutputs=[4].
DLSS quality SR evaluated, FG off. Jitter(-.4375,.3888889), resetfalse,
render1707x1019, rendered delta16.666698ms.

Raw PFM analysis covers normalized x.12-.22/y.18-.22, inclusive integer bounds
x204..375/y183..224, 7,224 pixels (slightly wider than the earlier 7,011-pixel ROI).
All are finite; writer alpha and scene depth are1 throughout. MotionX max absolute
.000213504 render pixels; MotionY .000828266. ReactiveB range.148315-.602539.
Results: run/sky-analysis/motion-depth-results.json. This verifies sky output in
this static-camera region; it does not verify moving-camera/liquid coverage across
the scene. The shared liquid prefix compiled but no dedicated liquid ROI was read.

The corrected writer alpha, previously zero in the earlier sky crop, supports the
identified native variant mismatch repair. Final image still shows horizontal
structure in light sky. Do not claim that producer fix removed the visual artifact
or extrapolate the capture to broad renderer/provider parity. Existing artifacts
remain available for source diagnosis; no further run was started.