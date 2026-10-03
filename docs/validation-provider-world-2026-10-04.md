# Current provider world batch — 2026-10-04

DIRECT validation turn in `D:/Coding/VulkanStory-Rewrite` (no Git repository).
One fresh stage and one isolated hidden client ran. No builds, source fixes,
reruns, installed deployment or user-game control occurred.

Artifacts: [provider-world-20261004-002041](../artifacts/validation/provider-world-20261004-002041/).
Saved run-batch.ps1 contains literal stage/launch inputs. Current managed outputs
and FSR3/Streamline bridges came from [the preceding build](validation-provider-release-2026-10-04.md).
Other native libraries/notices/full shaders came from the October-1 delivery-refresh
stage, not the mismatched provider-handoff bundle. Fresh staging recomputed its
inventory and verified shader hashes. This is a development stage, not an accepted
release candidate. Installed game/settings/original save were unchanged.

Scenario SHA256: `db040384b1114bafe89459ac04878eee43e4afdb050324ba729cbdc10e72dbe3`.
Session `38b93cbd20ea40c38363c8fe74bd141c`, owned child PID 86592. Requested
off → FSR3 → DLSS → XeSS with capture/assert checkpoints at ticks 160/320/500/680;
each switch preceded capture by 140 ticks. Consistent isolated foggy village story
snapshot, async pipelines, hidden=true, focused=false and staged Mod loaded.

## FAIL

Stage passed. Child exited 0 without timeout. The verifier failed because FSR3
inputsPreparedThisFrame was false at tick 320/completed frame 1107. Scenario
executed 20/42 actions with 2/4 paired captures; DLSS/XeSS phases did not execute.
Scenario/headless success=false. Subsequent verifier counts/manifest errors follow
the early terminal failure; they are not additional vendor failures.

Inspected both captures: off at tick 160/frame 947 shows world/sky/hand/HUD;
FSR3 at tick 320/frame 1107 is black. Runtime samples after switching consistently
show WorldCaptured=true, HasCamera=true, MotionValid=false, CanGenerate=false,
successfulUpscaleFrames=0 and preparedFrames=0, waiting for complete current
world camera/motion inputs. Provider strings report fsr3, but presentation reports
evaluated off; strings alone do not prove SDK operation. Render allocation became
1706×1018 for 2560×1528 output.

Logs record target/shader loading and FSR3 selection, then the assertion failure,
normal world/server close and successful NGX shutdown. Three known unsupported
Streamline-hook warnings remain. No disable failure was injected. Options startup
patch routing executed, but no Options tab was opened or interacted with.

Next implementation: trace off-to-temporal target/shader-mode transition, compiled
motion location, producer certification and final composition. SR requires a valid
motion attachment; FG requires certified current camera/motion. The first failed
producer/mode is not yet established. Do not force motion valid or weaken the
assertion. The black image is a scoped rendering defect, not an established vendor
SDK cause. REN-03, SDK-01/02, UI-01 and vendor/hardware acceptance remain open.
