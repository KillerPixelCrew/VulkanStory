# Focused motion-gate runtime — 2026-10-04

DIRECT validation turn in `D:/Coding/VulkanStory-Rewrite` (no Git repository).
One bounded Game Release build → fresh stage → isolated off-to-FSR3 child ran.
No source fix, second batch, new native build, tests or installed deployment ran.

Artifacts: [motion-gate-20261004-002743](../../artifacts/validation/motion-gate-20261004-002743).
Saved run-batch.ps1 records the commands. Game build exited 0 with zero errors and
the existing CS8600 scenario warning. Fresh stage used current managed outputs,
the October-4 FSR3/Streamline bridges and the October-1 delivery-refresh native/
notices/shader inputs. The scenario intentionally contained only off and FSR3;
it retained the preparation assertion and added no weakened acceptance rule.

Scenario SHA256: `d88bfd1aa91a2b5c579e520a5d093977053e51b8981a56333b55b117fdf52e4e`.
Game DLL SHA256: `DB35BD4B0C136846C8F5FDDFD1A51DBA1AC0382A5DA541914FB47B1FA6E7F7F2`.
Session `39b4287868a242c18d08a67b9dc5d775`, owned child PID 99448. World was an
isolated consistent foggy village story snapshot. Async pipelines remained enabled.

## Scoped PASS; earlier defect unresolved

Child and batch exited 0, no timeout. Scenario completed 20/20 actions and 2/2
paired captures; result verifier pass=true. Hidden=true, focused=false,
worldReady=true and stagedModLoaded=true. Off capture tick 160; FSR3 tick 320,
completed frame 1084. The FSR3 PNG was inspected: world, sky, hand and HUD visible.

After selection, one sampled frame reported temporal reset requested. Subsequent
samples reported motionReadiness=ready, MotionValid=true and compiled motion
location 4 matching the target. Last sample: 141 successful SR frames and 139
prepared FG frames. Normal world/server close and NGX shutdown completed.

This does not explain or repair the earlier black output with invalid motion in
[provider-world-20261004-002041](validation-provider-world-2026-10-04.md). Production
changes between runs were diagnostics; a passing run is not evidence that they
fixed the cause. Keep the earlier failure, timing/async sensitivity and cold-path
transition correctness open. The focused child did not reach DLSS/XeSS, FSR4,
Options navigation, injected teardown failures or moving-scene acceptance.
Prepared counts and this pre-FG scene PNG do not certify visible interpolation or
display pacing. The fresh stage is a development artifact, not a release candidate.
Installed game, settings and original save were unchanged.
