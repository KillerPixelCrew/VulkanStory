# Extended SDK state and hidden-window result — 2026-10-01

One bounded native/Bootstrap/Game build -> fresh bundle/stage -> isolated snapshot
capture batch, run once. No tests, installed deployment, fixes or second run.
Artifacts: `artifacts/validation/headless-20261001-161108/`.

Matching bridge/backend/SDL builds passed with zero managed warnings/errors; bundle
and stage passed. PID 47888 loaded foggy village story's isolated SQLite snapshot,
captured frames 180/210/240 plus attachments/AO, and automatically closed. Result
reports success, hidden=true, focused=false. Periodic records also show actual
SDL visible=false/focused=false throughout recorded samples. Device drain and
successful NGX shutdown captured. Installed game/user settings untouched.

## SDK state

Final sample: query result/status 0/0; maximum generated frames 1; minimum dimension
100; VSync support false; dynamic MFG support false. Current camera/motion valid.
234 successful DLSS SR evaluations, 232 FG-prepared frames and 231 SDK-reported
presents. The extended query uses the same one-per-frame state read and does not
consume output counts again for diagnostics.

No camera-constant warning or NGX feature-release error. Three supplied SDK hook
warnings remain. Hidden interpolation above one output per prepared frame is still
not proven; available SDK status does not diagnose why. Keep preparation, supported
count and actual presents separate. No physical scanout or generated image captured.

Visibility/focus evidence strengthens the harness's non-interruption contract,
not simultaneous-user GPU coexistence or all runtime paths. No commands/SSIM,
other vendors/platforms, settings/input/resize/proxy coexistence or release acceptance
claimed. This matching native bundle is the latest staging input; older bundles
lack the Details export. Full port goal remains open. No repair or rerun here.
