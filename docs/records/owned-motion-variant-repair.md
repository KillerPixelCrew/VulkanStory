# Owned motion shader variant repair — 2026-10-01

Implementation turn: source inspection and edits only. No builds, tests, probes,
packages or runtime launch. The preceding exact-frame capture was successful but
still showed the sky pattern; this repair has not been compiled or run.

Found a concrete adapter mismatch. NativeShaderLibrary.TryAxisValues selects its
GBUFFER axis from SSAOLEVEL > 0. Owned sky and liquid motion programs supplied
TAAMOTION=1 and TAAMOTIONLOCATION, but omitted SSAOLEVEL. Native taa-skymotion and
chunkliquidmotion therefore selected GBUFFER=0 and wrote output2 even when the
scene's motion attachment was4. Their passes allowed only output4; output2 was
excluded. A successful draw submission could consequently certify a producer
that never wrote its intended attachment. This is consistent with the earlier
sky crop retaining OIT reactive B while its writer alpha stayed zero.

Both owned programs now use MotionProgramPrefix, deriving SSAOLEVEL=1 for the
fourth motion attachment and0 for the second. This is only the native manifest's
binary layout selector, not a change to the player's AO quality. TAAMOTION and
TAAMOTIONLOCATION remain explicit for the rewritten path. Existing location-change
reloads keep cached owned programs aligned when framebuffer modes change.

The sky pass checks reflected WrittenFragmentOutputs for its intended motion
attachment and refuses coverage with one error message if absent. Exact-frame
metadata now also records the selected written outputs. The GLSL reprojection,
jitter convention, matrices, resource formats and NGX motion scale are unchanged.

Pending later bounded validation: compile, confirm output4 and sky alpha1 in the
same captured frame and assess the final sky image. Source mismatch is repaired;
removal of the visible DLSS sky pattern is unproven. Full renderer/SDL/provider
acceptance remains open. No tests were written.