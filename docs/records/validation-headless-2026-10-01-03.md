# First completed isolated headless capture — 2026-10-01

One bounded Bootstrap/Game build -> fresh stage -> isolated capture batch, run
once. No tests, installed deployment, source repairs or second batch. Artifacts:
`artifacts/validation/headless-20261001-155347/`.

Bootstrap and Game/backend/SDL builds passed with zero warnings/errors. Stage
passed. The runner backed up foggy village story through SQLite into run/data,
loaded the existing official client through dotnet exec with the staged startup
hook, and used isolated config/cache/logs. No installed payload or user settings
were changed, and no user game process was opened/focused/closed.

PID 42772 captured world frames 180, 210 and 240, then automatically shut down.
frames/headless-result.json reports 3 requested/3 written, success. Outputs include
3 PPM frames, 12 AO debug files, and 55 attachment files in run/attachments.
Bootstrap recorded runtime.stopped/device drain before SDL teardown. No new game
render exception or crash reporter. Source/native provenance and private package
paths appear in the captured logs. Commands/SSIM comparison were not exercised.

## Image/provider evidence

Inspected run/status/world-rendered-42772.png (2560x1528): terrain, vegetation,
sky, first-person geometry and HUD/minimap are visible. The earlier black capture
was not reproduced in this later captured frame. This is source-frame evidence,
not scanout/generated-frame capture or complete renderer parity.

DLSS SR first successful evaluation: 1707x1019 -> 2560x1528. Final periodic sample:
234 successful upscale frames, 232 FG-prepared frames, current camera/motion,
231 SDK-reported presents. This short hidden run does not prove interpolation
above one reported present per prepared frame; hidden-surface/SDK behavior remains
a separate uncertainty. Do not substitute preparation counts for generated frames.

## SDK errors and remaining work

Three known SDK hook-map warnings remain. A new constants warning identifies
cameraPinholeOffset left invalid. At shutdown Streamline logs NGX release feature
failed 0xbad00004. Normal process exit/device-drain marker is not proof of complete
SDK cleanup; these must be investigated in a later implementation turn.

Headless snapshot/capture/attachment/AO/autoclose path now has bounded runtime
proof. Permanent invisibility guards are in source; concurrent user-session/GPU
coexistence and actual scanout remain unverified. Command scripts, SSIM, other
vendors/platforms, input/settings, resize/coexistence and release archives remain
open. Installed payload remains the older diagnostic live candidate; current
harness lives in this staged build. No repair or rerun this turn.
